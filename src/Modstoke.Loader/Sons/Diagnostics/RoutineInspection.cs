// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using Il2CppInterop.Common;

namespace Modstoke.Sons;

/// <summary>Exact managed declaration inspection; never invokes or qualifies a native game routine.</summary>
public static class RoutineInspection
{
    /// <summary>
    /// Matches one declaration by ordinal name, staticness, return type and ordered parameter types.
    /// </summary>
    /// <remarks>
    /// Does not search base classes, expand optional arguments, coerce types, initialize the declaring
    /// type or read a native pointer. Use MakeByRefType for by-reference parameters; ref and out share
    /// that type identity, so inspect Method.GetParameters() when their direction matters.
    /// Generic declarations, varargs and required/optional custom modifiers are unsupported.
    /// True means a unique managed declaration was inspected, not that it can be called or hooked.
    /// </remarks>
    public static bool TryInspect(Type declaringType, string name, bool isStatic, Type returnType,
        Type[] parameterTypes, out RoutineDeclaration declaration,
        out RoutineInspectionStatus status)
    {
        declaration = null;
        status = RoutineInspectionStatus.InvalidSignature;
        if (declaringType == null || string.IsNullOrEmpty(name) || returnType == null || parameterTypes == null)
            return false;
        try
        {
            var expected = (Type[])parameterTypes.Clone();
            foreach (var type in expected)
            {
                if (type == null)
                    return false;
            }
            if (declaringType.IsGenericType || declaringType.ContainsGenericParameters || returnType.ContainsGenericParameters)
            {
                status = RoutineInspectionStatus.UnsupportedSignature;
                return false;
            }
            foreach (var type in expected)
            {
                if (type.ContainsGenericParameters)
                {
                    status = RoutineInspectionStatus.UnsupportedSignature;
                    return false;
                }
            }

            MethodInfo match = null;
            ParameterInfo[] matchParameters = null;
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance;
            foreach (var method in declaringType.GetMethods(flags))
            {
                if (method.DeclaringType != declaringType || !string.Equals(method.Name, name, StringComparison.Ordinal)
                    || method.IsStatic != isStatic || method.ReturnType != returnType || method.IsGenericMethod)
                    continue;
                var parameters = method.GetParameters();
                if (parameters.Length != expected.Length)
                    continue;
                var exact = true;
                for (var i = 0; i < expected.Length; i++)
                    exact &= parameters[i].ParameterType == expected[i];
                if (!exact)
                    continue;
                if (match != null)
                {
                    status = RoutineInspectionStatus.Ambiguous;
                    return false;
                }
                match = method;
                matchParameters = parameters;
            }
            if (match == null)
            {
                status = RoutineInspectionStatus.MethodMissing;
                return false;
            }
            if ((match.CallingConvention & CallingConventions.VarArgs) != 0 || HasModifiers(match.ReturnParameter))
            {
                status = RoutineInspectionStatus.UnsupportedSignature;
                return false;
            }
            foreach (var parameter in matchParameters)
            {
                if (HasModifiers(parameter))
                {
                    status = RoutineInspectionStatus.UnsupportedSignature;
                    return false;
                }
            }

            var body = match.GetMethodBody();
            var kind = RoutineBodyKind.BodyUnavailable;
            string mappingField = null;
            if (body != null)
            {
                if (IsUnstrippingFailure(match, body))
                    kind = RoutineBodyKind.UnsupportedUnstrippingStub;
                else
                {
                    // Resolve field metadata only. GetValue would run the wrapper's native initializer.
                    var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(match);
                    mappingField = field?.Name;
                    kind = field == null ? RoutineBodyKind.OtherManagedBody
                        : RoutineBodyKind.GeneratedMethodMapping;
                }
            }
            declaration = new RoutineDeclaration(match, kind, mappingField);
            status = RoutineInspectionStatus.Inspected;
            return true;
        }
        catch (Exception)
        {
            status = RoutineInspectionStatus.InspectionFailed;
            return false;
        }
    }

    private static bool HasModifiers(ParameterInfo parameter) =>
        parameter.GetRequiredCustomModifiers().Length != 0 || parameter.GetOptionalCustomModifiers().Length != 0;

    private static bool IsUnstrippingFailure(MethodInfo method, MethodBody body)
    {
        if (body.ExceptionHandlingClauses.Count != 0)
            return false;
        var il = body.GetILAsByteArray();
        if (il == null)
            return false;
        var offset = 0;
        // Recognize only the generator's unconditional ldstr/newobj/throw body, not incidental throws.
        if (!Take(0x72) || il.Length - offset < 4)
            return false;
        var message = method.Module.ResolveString(BitConverter.ToInt32(il, offset));
        offset += 4;
        if (message != "Method unstripping failed" && !message.StartsWith("Method unstripping failed:", StringComparison.Ordinal))
            return false;
        if (!Take(0x73) || il.Length - offset < 4)
            return false;
        var constructor = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset));
        offset += 4;
        var parameters = constructor?.GetParameters();
        if (constructor is not ConstructorInfo || constructor.DeclaringType != typeof(NotSupportedException)
            || parameters.Length != 1 || parameters[0].ParameterType != typeof(string) || !Take(0x7A))
            return false;
        SkipNops();
        if (offset < il.Length && il[offset] == 0x2A)
            offset++;
        SkipNops();
        return offset == il.Length;

        void SkipNops()
        {
            while (offset < il.Length && il[offset] == 0)
                offset++;
        }

        bool Take(byte opcode)
        {
            SkipNops();
            return offset < il.Length && il[offset++] == opcode;
        }
    }
}

public enum RoutineInspectionStatus
{
    Inspected,
    InvalidSignature,
    UnsupportedSignature,
    MethodMissing,
    Ambiguous,
    InspectionFailed
}

public enum RoutineBodyKind
{
    BodyUnavailable,
    UnsupportedUnstrippingStub,
    /// <summary>A mapping field exists in the managed body; its native value and identity are unverified.</summary>
    GeneratedMethodMapping,
    /// <summary>Includes restored methods, field accessors and icall wrappers; not a callable/safe verdict.</summary>
    OtherManagedBody
}

/// <summary>Managed metadata only. No native entry, receiver, authority or detour safety is established.</summary>
public sealed class RoutineDeclaration
{
    internal RoutineDeclaration(MethodInfo method, RoutineBodyKind bodyKind, string mappingField)
    {
        Method = method;
        BodyKind = bodyKind;
        NativeMappingFieldName = mappingField;
        ModuleVersionId = method.Module.ModuleVersionId;
        ManagedMetadataToken = method.MetadataToken;
    }

    /// <summary>The exact managed declaration. Invoking it remains the caller's separate responsibility.</summary>
    public MethodInfo Method { get; }
    public RoutineBodyKind BodyKind { get; }
    public string NativeMappingFieldName { get; }
    public Guid ModuleVersionId { get; }
    /// <summary>Token in the generated managed module, NOT the original native IL2CPP method token.</summary>
    public int ManagedMetadataToken { get; }
}
