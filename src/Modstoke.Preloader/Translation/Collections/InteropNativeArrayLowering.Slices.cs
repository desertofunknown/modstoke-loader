// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static partial class InteropNativeArrayLowering
    {
        internal const string SliceName = "Unity.Collections.NativeSlice`1";
        private const string SliceEnumeratorName = SliceName + "/Enumerator";

        internal static bool TryGetSliceElement(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element)
        {
            element = null;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 || instance.GenericType.FullName != SliceName ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != CoreModule ||
                !IsStrippedHandle(context, instance.TypeArguments[0]) || !HasSliceLayout(instance.GenericType.Resolve()))
                return false;
            element = instance.TypeArguments[0];
            return true;
        }

        private static bool TryGetSliceStorage(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, out Type runtimeType)
        {
            runtimeType = null;
            if (TryGetSliceElement(context, source, out element))
            {
                runtimeType = typeof(InteropNativeSliceStorage<>);
                return true;
            }
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType ||
                instance.TypeArguments.Count != 1 || instance.GenericType.FullName != SliceEnumeratorName ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != CoreModule ||
                instance.GenericType.Resolve() is not { } definition || !HasSliceEnumeratorLayout(definition))
                return false;
            var owner = new GenericInstanceTypeSignature(definition.DeclaringType, true, instance.TypeArguments[0]);
            if (!TryGetSliceElement(context, owner, out element)) return false;
            runtimeType = typeof(InteropNativeSliceStorage<>.Enumerator);
            return true;
        }

        internal static bool HasSliceLayout(TypeDefinition type)
        {
            if (type == null || type.FullName != SliceName || type.DeclaringModule.Assembly.Name != CoreModule ||
                !type.IsValueType || type.IsEnum || !type.IsSequentialLayout || type.GenericParameters.Count != 1 ||
                type.ClassLayout is { } layout && (layout.ClassSize is not (0 or 16) || layout.PackingSize is not (0 or 8)) ||
                (type.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint))
                return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 3 && fields.All(field => !field.HasFieldMarshal && field.FieldRva == null) &&
                fields[0].Name == "m_Buffer" && fields[0].FieldOffset is null or 0 &&
                fields[0].Signature?.FieldType is PointerTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.U1 } } &&
                fields[1].Name == "m_Stride" && fields[1].FieldOffset is null or 8 &&
                fields[1].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 } &&
                fields[2].Name == "m_Length" && fields[2].FieldOffset is null or 12 &&
                fields[2].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 };
        }

        private static bool HasSliceEnumeratorLayout(TypeDefinition type)
        {
            if (!type.IsValueType || type.IsEnum || !type.IsSequentialLayout || type.GenericParameters.Count != 1 ||
                type.DeclaringType == null || !HasSliceLayout(type.DeclaringType) ||
                type.ClassLayout is { } layout && (layout.ClassSize is not (0 or 24) || layout.PackingSize is not (0 or 8)) ||
                (type.GenericParameters[0].Attributes & GenericParameterAttributes.SpecialConstraintMask) !=
                    (GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint))
                return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 2 && fields.All(field => !field.HasFieldMarshal && field.FieldRva == null) &&
                fields[0].Name == "m_Array" && fields[0].FieldOffset is null or 0 &&
                fields[0].Signature?.FieldType is GenericInstanceTypeSignature slice && slice.IsValueType &&
                SignatureComparer.Default.Equals(slice.GenericType, type.DeclaringType) &&
                slice.TypeArguments.Count == 1 && IsElementParameter(slice.TypeArguments[0]) &&
                fields[1].Name == "m_Index" && fields[1].FieldOffset is null or 16 &&
                fields[1].Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 };
        }
    }
}
