// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    // Closed storage substitution only. Native generic definitions and identities are unchanged.
    internal static partial class InteropNativeArrayLowering
    {
        internal const string ArrayName = "Unity.Collections.NativeArray`1";
        internal const string ReadOnlyName = ArrayName + "/ReadOnly";
        private const string EnumeratorName = ArrayName + "/Enumerator";
        private const string ReadOnlyEnumeratorName = ReadOnlyName + "/Enumerator";
        internal const string CoreModule = "UnityEngine.CoreModule";
        internal const string AnimationModule = "UnityEngine.AnimationModule";
        internal const string SceneHandle = "UnityEngine.Animations.PropertySceneHandle";
        internal const string StreamHandle = "UnityEngine.Animations.PropertyStreamHandle";

        internal static bool TryGetElement(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out bool readOnly)
        {
            element = null;
            readOnly = false;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(instance.GenericType.ToTypeSignature()) != CoreModule ||
                instance.GenericType.FullName is not (ArrayName or ReadOnlyName)) return false;
            var argument = instance.TypeArguments[0];
            if (!IsStrippedHandle(context, argument)) return false;
            readOnly = instance.GenericType.FullName == ReadOnlyName;
            if (!HasArrayLayout(instance.GenericType.Resolve(), readOnly)) return false;
            element = argument;
            return true;
        }

        internal static bool IsStrippedHandle(InteropRewriteContext context, TypeSignature argument)
        {
            if (argument is not TypeDefOrRefSignature || !argument.IsValueType ||
                argument.FullName is not (SceneHandle or StreamHandle) ||
                InteropRestoredTypeSupport.GetAssemblyName(argument) != AnimationModule ||
                !HasHandleLayout(argument.Resolve(), argument.FullName)) return false;
            // A real native element identity always wins over this fallback.
            var existing = context.TryGetTypeByName(AnimationModule, argument.FullName);
            return existing?.OriginalType == null && existing?.ClassPointerFieldRef == null;
        }

        internal static bool TryGetStorage(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType)
        {
            runtimeType = null;
            if (TryGetSliceStorage(context, source, out element, out runtimeType)) return true;
            if (TryGetElement(context, source, out element, out var readOnly))
            {
                runtimeType = readOnly ? typeof(InteropNativeArrayStorage<>.ReadOnly) : typeof(InteropNativeArrayStorage<>);
                return true;
            }
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 ||
                instance.GenericType.FullName is not (EnumeratorName or ReadOnlyEnumeratorName) ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != CoreModule ||
                instance.GenericType.Resolve() is not { } definition ||
                !HasEnumeratorLayout(definition)) return false;
            var owner = new GenericInstanceTypeSignature(definition.DeclaringType, true, instance.TypeArguments[0]);
            if (!TryGetElement(context, owner, out element, out readOnly)) return false;
            runtimeType = readOnly ? typeof(InteropNativeArrayStorage<>.ReadOnly.Enumerator) :
                typeof(InteropNativeArrayStorage<>.Enumerator);
            return true;
        }

        internal static bool IsEnumerator(Type runtimeType) => runtimeType == typeof(InteropNativeArrayStorage<>.Enumerator) ||
            runtimeType == typeof(InteropNativeArrayStorage<>.ReadOnly.Enumerator);

        private static bool HasEnumeratorLayout(TypeDefinition type)
        {
            if (!type.IsValueType || type.IsEnum || !type.IsSequentialLayout || type.DeclaringType == null ||
                type.GenericParameters.Count != 1 ||
                type.ClassLayout is { } layout && (layout.ClassSize != 0 || layout.PackingSize is not (0 or 8))) return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 3 && fields.All(field => !field.HasFieldMarshal && field.FieldRva == null && field.FieldOffset == null) &&
                (type.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) ==
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) &&
                fields[0].Name == "m_Array" && fields[0].Signature?.FieldType is GenericInstanceTypeSignature array &&
                array.IsValueType && SignatureComparer.Default.Equals(array.GenericType, type.DeclaringType) &&
                array.TypeArguments.Count == 1 && IsElementParameter(array.TypeArguments[0]) &&
                fields[1].Name == "m_Index" && fields[1].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 } &&
                fields[2].Name == "value" && IsElementParameter(fields[2].Signature?.FieldType);
        }

        private static bool IsElementParameter(TypeSignature type) => type is GenericParameterSignature
            { ParameterType: GenericParameterType.Type, Index: 0 };

        internal static bool HasHandleLayout(TypeDefinition type, string name)
        {
            var count = name == SceneHandle ? 2 : name == StreamHandle ? 4 : 0;
            if (type == null || count == 0 || type.FullName != name ||
                type.DeclaringModule.Assembly.Name != AnimationModule || !type.IsValueType || type.IsEnum ||
                !type.IsSequentialLayout || type.GenericParameters.Count != 0 ||
                type.ClassLayout is { } layout && (layout.ClassSize != 0 && layout.ClassSize != count * 4 ||
                    layout.PackingSize is not (0 or 4 or 8))) return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            var names = count == 2 ? new[] { "valid", "handleIndex" } :
                new[] { "m_AnimatorBindingsVersion", "handleIndex", "valueArrayIndex", "bindType" };
            if (fields.Length != count) return false;
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                if (field.Name != names[index] || field.HasFieldMarshal || field.FieldRva != null ||
                    (field.FieldOffset != null && field.FieldOffset != index * 4) ||
                    field.Signature?.FieldType is not CorLibTypeSignature primitive ||
                    primitive.ElementType != (index == 0 ? ElementType.U4 : ElementType.I4)) return false;
            }
            return true;
        }

        internal static bool HasArrayLayout(TypeDefinition type, bool readOnly)
        {
            if (type == null || !type.IsValueType || type.IsEnum || !type.IsSequentialLayout ||
                type.GenericParameters.Count != 1 || type.DeclaringModule.Assembly.Name != CoreModule ||
                type.ClassLayout is { } layout && (layout.ClassSize is not (0 or 16) ||
                    layout.PackingSize is not (0 or 8))) return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != (readOnly ? 2 : 3) || fields.Any(field => field.HasFieldMarshal || field.FieldRva != null) ||
                fields[0].Name != "m_Buffer" || fields[0].FieldOffset is not (null or 0) ||
                fields[0].Signature?.FieldType is not PointerTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } } ||
                fields[1].Name != "m_Length" || fields[1].FieldOffset is not (null or 8) ||
                fields[1].Signature?.FieldType is not CorLibTypeSignature { ElementType: ElementType.I4 }) return false;
            if (readOnly) return HasArrayLayout(type.DeclaringType, false);
            var constraint = type.GenericParameters[0];
            if ((constraint.Attributes & GenericParameterAttributes.SpecialConstraintMask) !=
                (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint) ||
                fields[2].Name != "m_AllocatorLabel" || fields[2].FieldOffset is not (null or 12) ||
                !IsAllocationEnum(fields[2].Signature?.FieldType, "Unity.Collections.Allocator")) return false;
            return true;
        }

        internal static bool IsAllocationEnum(TypeSignature type, string name)
        {
            if (type is not TypeDefOrRefSignature || type.FullName != name ||
                InteropRestoredTypeSupport.GetAssemblyName(type) != CoreModule || type.Resolve() is not { IsEnum: true } definition)
                return false;
            var fields = definition.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 1 && fields[0].Name == "value__" &&
                fields[0].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 };
        }
    }
}
