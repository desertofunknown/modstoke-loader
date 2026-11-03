// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;

namespace Modstoke.Sons;

/// <summary>
/// Exact managed field and property declarations only. Never reads a value, invokes an accessor,
/// initializes the declaring type or accesses native memory. A generated property may wrap a
/// native field; the inspected declaration does not establish its native meaning or access safety.
/// </summary>
public static class FieldInspection
{
    public const int MaximumIndexParameterCount = 32;

    private const BindingFlags DeclarationFlags = BindingFlags.DeclaredOnly | BindingFlags.Public
        | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    /// <summary>
    /// Matches one field by ordinal name, staticness and exact managed type, including nonpublic fields.
    /// </summary>
    /// <remarks>
    /// Does not search base classes, fall back to properties or read constants or pointer values.
    /// Generic declaring types, open field types and custom modifiers are unsupported.
    /// True means one managed declaration was inspected, not that the field is usable at runtime.
    /// </remarks>
    public static bool TryInspectField(Type declaringType, string name, bool isStatic, Type fieldType,
        out FieldDeclaration declaration, out FieldInspectionStatus status)
    {
        declaration = null;
        status = FieldInspectionStatus.InvalidSignature;
        if (declaringType == null || string.IsNullOrEmpty(name) || fieldType == null)
            return false;
        try
        {
            if (declaringType.IsGenericType || declaringType.ContainsGenericParameters || fieldType.ContainsGenericParameters)
            {
                status = FieldInspectionStatus.UnsupportedSignature;
                return false;
            }

            FieldInfo match = null;
            foreach (var field in declaringType.GetFields(DeclarationFlags))
            {
                if (field.DeclaringType != declaringType || !string.Equals(field.Name, name, StringComparison.Ordinal)
                    || field.IsStatic != isStatic || field.FieldType != fieldType)
                    continue;
                if (match != null)
                {
                    status = FieldInspectionStatus.Ambiguous;
                    return false;
                }
                match = field;
            }
            if (match == null)
            {
                status = FieldInspectionStatus.MemberMissing;
                return false;
            }
            if (match.GetRequiredCustomModifiers().Length != 0 || match.GetOptionalCustomModifiers().Length != 0)
            {
                status = FieldInspectionStatus.UnsupportedSignature;
                return false;
            }

            declaration = new FieldDeclaration(match);
            status = FieldInspectionStatus.Inspected;
            return true;
        }
        catch (Exception)
        {
            status = FieldInspectionStatus.InspectionFailed;
            return false;
        }
    }

    /// <summary>
    /// Matches one property by ordinal name, staticness, exact managed type and ordered index types.
    /// </summary>
    /// <remarks>
    /// Pass an empty array for a non-indexed property. At most MaximumIndexParameterCount index types
    /// are accepted; excess is rejected, never truncated. No base search, field fallback, optional
    /// argument expansion or type coercion occurs. Nonpublic accessors are included as metadata only.
    /// Generic declaring types, open signature types, varargs and custom modifiers are unsupported.
    /// True establishes neither a native field mapping nor a callable getter or setter.
    /// </remarks>
    public static bool TryInspectProperty(Type declaringType, string name, bool isStatic, Type propertyType,
        Type[] indexParameterTypes, out PropertyDeclaration declaration,
        out FieldInspectionStatus status)
    {
        declaration = null;
        status = FieldInspectionStatus.InvalidSignature;
        if (declaringType == null || string.IsNullOrEmpty(name) || propertyType == null || indexParameterTypes == null)
            return false;
        if (indexParameterTypes.Length > MaximumIndexParameterCount)
        {
            status = FieldInspectionStatus.SignatureLimitExceeded;
            return false;
        }
        try
        {
            var expected = (Type[])indexParameterTypes.Clone();
            foreach (var type in expected)
            {
                if (type == null)
                    return false;
            }
            if (declaringType.IsGenericType || declaringType.ContainsGenericParameters || propertyType.ContainsGenericParameters)
            {
                status = FieldInspectionStatus.UnsupportedSignature;
                return false;
            }
            foreach (var type in expected)
            {
                if (type.ContainsGenericParameters)
                {
                    status = FieldInspectionStatus.UnsupportedSignature;
                    return false;
                }
            }

            PropertyInfo match = null;
            MethodInfo matchGetter = null;
            MethodInfo matchSetter = null;
            ParameterInfo[] matchParameters = null;
            foreach (var property in declaringType.GetProperties(DeclarationFlags))
            {
                if (property.DeclaringType != declaringType || !string.Equals(property.Name, name, StringComparison.Ordinal)
                    || property.PropertyType != propertyType)
                    continue;
                var parameters = property.GetIndexParameters();
                if (parameters.Length != expected.Length)
                    continue;
                var exact = true;
                for (var i = 0; i < expected.Length; i++)
                    exact &= parameters[i].ParameterType == expected[i];
                if (!exact)
                    continue;

                var getter = property.GetGetMethod(nonPublic: true);
                var setter = property.GetSetMethod(nonPublic: true);
                if ((getter == null && setter == null)
                    || (getter != null && setter != null && getter.IsStatic != setter.IsStatic))
                {
                    status = FieldInspectionStatus.UnsupportedSignature;
                    return false;
                }
                if ((getter ?? setter).IsStatic != isStatic)
                    continue;
                if (match != null)
                {
                    status = FieldInspectionStatus.Ambiguous;
                    return false;
                }
                match = property;
                matchGetter = getter;
                matchSetter = setter;
                matchParameters = parameters;
            }
            if (match == null)
            {
                status = FieldInspectionStatus.MemberMissing;
                return false;
            }
            if (match.GetRequiredCustomModifiers().Length != 0 || match.GetOptionalCustomModifiers().Length != 0
                || HasUnsupportedAccessor(matchGetter) || HasUnsupportedAccessor(matchSetter))
            {
                status = FieldInspectionStatus.UnsupportedSignature;
                return false;
            }
            foreach (var parameter in matchParameters)
            {
                if (HasModifiers(parameter))
                {
                    status = FieldInspectionStatus.UnsupportedSignature;
                    return false;
                }
            }

            declaration = new PropertyDeclaration(match, matchGetter, matchSetter, matchParameters);
            status = FieldInspectionStatus.Inspected;
            return true;
        }
        catch (Exception)
        {
            status = FieldInspectionStatus.InspectionFailed;
            return false;
        }
    }

    private static bool HasModifiers(ParameterInfo parameter) =>
        parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0;

    private static bool HasUnsupportedAccessor(MethodInfo method)
    {
        if (method == null)
            return false;
        if (method.IsGenericMethod || (method.CallingConvention & CallingConventions.VarArgs) != 0
            || HasModifiers(method.ReturnParameter))
            return true;
        foreach (var parameter in method.GetParameters())
        {
            if (HasModifiers(parameter))
                return true;
        }
        return false;
    }
}

public enum FieldInspectionStatus
{
    Inspected,
    InvalidSignature,
    UnsupportedSignature,
    SignatureLimitExceeded,
    MemberMissing,
    Ambiguous,
    InspectionFailed
}

/// <summary>
/// Copied managed field metadata, retaining Type references but no receiver or native handle.
/// Field flags describe the wrapper declaration, not native mutability, lifetime or authority.
/// </summary>
public sealed class FieldDeclaration
{
    internal FieldDeclaration(FieldInfo field)
    {
        DeclaringType = field.DeclaringType;
        Name = field.Name;
        FieldType = field.FieldType;
        Attributes = field.Attributes;
        ModuleVersionId = field.Module.ModuleVersionId;
        ManagedMetadataToken = field.MetadataToken;
    }

    public Type DeclaringType { get; }
    public string Name { get; }
    public Type FieldType { get; }
    public FieldAttributes Attributes { get; }
    public bool IsStatic => (Attributes & FieldAttributes.Static) != 0;
    /// <summary>The managed InitOnly flag; not a native read-only or write-safety verdict.</summary>
    public bool IsInitOnly => (Attributes & FieldAttributes.InitOnly) != 0;
    public bool IsLiteral => (Attributes & FieldAttributes.Literal) != 0;
    public Guid ModuleVersionId { get; }
    /// <summary>Token in the inspected managed module, NOT an original native IL2CPP field token.</summary>
    public int ManagedMetadataToken { get; }
}

/// <summary>
/// Copied managed property metadata, retaining Type references but no receiver or native handle.
/// Accessor presence and visibility are declarations only, never permission or proof of usable access.
/// </summary>
public sealed class PropertyDeclaration
{
    internal PropertyDeclaration(PropertyInfo property, MethodInfo getter, MethodInfo setter, ParameterInfo[] parameters)
    {
        DeclaringType = property.DeclaringType;
        Name = property.Name;
        PropertyType = property.PropertyType;
        Attributes = property.Attributes;
        IsStatic = (getter ?? setter).IsStatic;
        GetterAttributes = getter?.Attributes;
        SetterAttributes = setter?.Attributes;
        var indexTypes = new Type[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            indexTypes[i] = parameters[i].ParameterType;
        IndexParameterTypes = Array.AsReadOnly(indexTypes);
        ModuleVersionId = property.Module.ModuleVersionId;
        ManagedMetadataToken = property.MetadataToken;
    }

    public Type DeclaringType { get; }
    public string Name { get; }
    public Type PropertyType { get; }
    public PropertyAttributes Attributes { get; }
    public bool IsStatic { get; }
    /// <summary>A getter is declared, including a nonpublic getter. It is never invoked by inspection.</summary>
    public bool CanRead => GetterAttributes.HasValue;
    /// <summary>A setter is declared, including a nonpublic setter. It is never invoked by inspection.</summary>
    public bool CanWrite => SetterAttributes.HasValue;
    /// <summary>Raw managed accessor flags; null when no getter is declared.</summary>
    public MethodAttributes? GetterAttributes { get; }
    /// <summary>Raw managed accessor flags; null when no setter is declared.</summary>
    public MethodAttributes? SetterAttributes { get; }
    public IReadOnlyList<Type> IndexParameterTypes { get; }
    public Guid ModuleVersionId { get; }
    /// <summary>Token in the inspected managed module, NOT an original native IL2CPP property or field token.</summary>
    public int ManagedMetadataToken { get; }
}
