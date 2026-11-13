// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    /// <summary>
    /// Picks the Unity value-math methods whose generated wrappers get Unity's own managed body instead. A generated
    /// wrapper reaches the native method through il2cpp_runtime_invoke and boxes the result on the IL2CPP heap, so
    /// every Vector3 sum, Quaternion product or Mathf.Lerp in a mod costs a native call and an allocation. Unity's
    /// managed bodies for these are plain arithmetic over the same blittable fields, which IL2CPP compiled from the
    /// same IL, so running them on the CLR gives the same results without either cost.
    /// </summary>
    internal static class InteropManagedMath
    {
        // Set to 1 to keep every math method on its native wrapper; the setting is part of the interop input hash.
        internal const string DisableVariable = "MODSTOKE_NATIVE_UNITY_MATH";

        private static readonly HashSet<string> Types = new(StringComparer.Ordinal)
        {
            "UnityEngine.Vector2", "UnityEngine.Vector3", "UnityEngine.Vector4",
            "UnityEngine.Vector2Int", "UnityEngine.Vector3Int", "UnityEngine.Quaternion",
            "UnityEngine.Mathf", "UnityEngine.Color", "UnityEngine.Rect", "UnityEngine.Bounds",
            "UnityEngine.Matrix4x4", "UnityEngine.Plane", "UnityEngine.Ray"
        };

        private static readonly HashSet<string> MathTypes = new(StringComparer.Ordinal) { "System.Math", "System.MathF" };

        internal static bool Enabled { get; } = Environment.GetEnvironmentVariable(DisableVariable) != "1";

        internal static bool Covers(TypeDefinition type) => Enabled && type != null && Types.Contains(type.FullName);

        /// <summary>
        /// True when the method's body only does arithmetic over the covered types, primitives and System.Math.
        /// Anything that reaches native state, allocates, formats text or throws keeps its native wrapper.
        /// </summary>
        internal static bool Qualifies(MethodDefinition original, InteropTypeContext type, out string reason)
        {
            reason = null;
            if (type.OriginalType == null || !type.NewType.IsValueType ||
                type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct)
                return Reject(out reason, "not-blittable");
            var body = original.CilMethodBody;
            if (body == null || original.IsAbstract || (original.ImplAttributes & MethodImplAttributes.InternalCall) != 0)
                return Reject(out reason, "no-managed-body");
            if (original.IsVirtual || original.GenericParameters.Count != 0 || (original.IsConstructor && original.IsStatic))
                return Reject(out reason, "dispatch-or-generic");
            if (original.Name?.Value is "ToString" or "GetHashCode" or "Equals")
                return Reject(out reason, "identity-or-formatting");
            if (body.ExceptionHandlers.Count != 0)
                return Reject(out reason, "exception-handlers");
            if (!Allowed(original.Signature.ReturnType) || !AllParametersAllowed(original.Signature))
                return Reject(out reason, "signature");
            foreach (var local in body.LocalVariables)
                if (!Allowed(local.VariableType))
                    return Reject(out reason, "local-" + local.VariableType.FullName);
            foreach (var instruction in body.Instructions)
                if (!Allowed(instruction, out var detail))
                    return Reject(out reason, instruction.OpCode.Mnemonic + (detail == null ? "" : " " + detail));
            return true;
        }

        private static bool Allowed(CilInstruction instruction, out string detail)
        {
            detail = null;
            var code = instruction.OpCode.Code;
            switch (instruction.OpCode.OperandType)
            {
                case CilOperandType.InlineString:
                case CilOperandType.InlineTok:
                case CilOperandType.InlineSig:
                case CilOperandType.InlinePhi:
                    return false;
                case CilOperandType.InlineMethod:
                    if (code is not (CilCode.Call or CilCode.Newobj) || instruction.Operand is not IMethodDescriptor method)
                        return false;
                    detail = method.FullName;
                    return AllowedCall(method);
                case CilOperandType.InlineField:
                    if (code is not (CilCode.Ldfld or CilCode.Stfld or CilCode.Ldflda or CilCode.Ldsfld) ||
                        instruction.Operand is not IFieldDescriptor field)
                        return false;
                    detail = field.FullName;
                    return field.DeclaringType != null && Types.Contains(field.DeclaringType.FullName) &&
                        Allowed(field.Signature?.FieldType);
                case CilOperandType.InlineType:
                    if (code is not (CilCode.Ldobj or CilCode.Stobj or CilCode.Initobj or CilCode.Cpobj or CilCode.Sizeof) ||
                        instruction.Operand is not ITypeDefOrRef operand)
                        return false;
                    detail = operand.FullName;
                    return Allowed(operand.ToTypeSignature());
                case CilOperandType.InlineNone:
                    // Arrays, exceptions, raw memory and varargs stay on the native path.
                    return code is not (CilCode.Throw or CilCode.Rethrow or CilCode.Ldlen or CilCode.Localloc or
                        CilCode.Endfinally or CilCode.Endfilter or CilCode.Arglist or CilCode.Refanytype or
                        CilCode.Cpblk or CilCode.Initblk or CilCode.Break) &&
                        !code.ToString().StartsWith("Ldelem", StringComparison.Ordinal) &&
                        !code.ToString().StartsWith("Stelem", StringComparison.Ordinal);
                default:
                    // Constants, branches, switches, locals and arguments.
                    return true;
            }
        }

        private static bool AllowedCall(IMethodDescriptor method)
        {
            var declaring = method.DeclaringType?.FullName;
            if (method is MethodSpecification || method.Signature == null || declaring == null)
                return false;
            if (MathTypes.Contains(declaring))
                return !method.Signature.HasThis && IsPrimitive(method.Signature.ReturnType) &&
                    method.Signature.ParameterTypes.All(IsPrimitive);
            if (!Types.Contains(declaring) || !Allowed(method.Signature.ReturnType) || !AllParametersAllowed(method.Signature))
                return false;
            // Calls into native methods would only move the native invoke into the managed body.
            var definition = method.Resolve();
            return definition != null && definition.CilMethodBody != null &&
                (definition.ImplAttributes & MethodImplAttributes.InternalCall) == 0;
        }

        private static bool AllParametersAllowed(MethodSignature signature) => signature.ParameterTypes.All(Allowed);

        private static bool Allowed(TypeSignature type) => type switch
        {
            null => false,
            ByReferenceTypeSignature reference => Allowed(reference.BaseType),
            CorLibTypeSignature corlib => corlib.ElementType == ElementType.Void || IsPrimitive(corlib),
            TypeDefOrRefSignature named => Types.Contains(named.FullName),
            _ => false
        };

        private static bool IsPrimitive(TypeSignature type) => type is CorLibTypeSignature
        {
            ElementType: >= ElementType.Boolean and <= ElementType.R8 or ElementType.I or ElementType.U
        };

        private static bool Reject(out string reason, string why)
        {
            reason = why;
            return false;
        }
    }
}
