// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropAtomicBridge
    {
        internal static bool TryBind(IMethodDescriptor source, ModuleDefinition module,
            Func<TypeSignature, TypeSignature> rewriteElement, out IMethodDescriptor bridge)
        {
            bridge = null;
            if (!InteropRestoredUnmanagedDelegateSupport.IsSystemType(source.DeclaringType, "System.Threading.Interlocked") ||
                !InteropDelegateContracts.HasStandardConvention(source.Signature) || source.Signature.HasThis)
                return false;
            var signature = source.Signature;
            var name = (string)source.Name;
            if (name is not ("CompareExchange" or "Exchange" or "Increment" or "Decrement" or "Add" or "Read" or "MemoryBarrier"))
                return false;

            if (TryGetReferenceElement(source, out var sourceElement))
            {
                var element = rewriteElement(sourceElement);
                if (!InteropDelegateContracts.CanBeArgument(element, false)) return false;
                var helper = typeof(InteropManagedReferenceAtomic).GetMethod(name);
                bridge = ((IMethodDefOrRef)module.DefaultImporter.ImportMethod(helper)).MakeGenericInstanceMethod(element);
                return true;
            }

            if (signature.GenericParameterCount != 0 || source is MethodSpecification || signature.ParameterTypes.Count > 3)
                return false;
            var returnType = PrimitiveType(signature.ReturnType);
            if (returnType == null) return false;
            var parameters = new Type[signature.ParameterTypes.Count];
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = signature.ParameterTypes[index];
                var byReference = parameter as ByReferenceTypeSignature;
                var type = PrimitiveType(byReference?.BaseType ?? parameter);
                if (type == null || type == typeof(void)) return false;
                parameters[index] = byReference == null ? type : type.MakeByRefType();
            }
            var nativeEquivalent = typeof(Interlocked).GetMethod(name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.ExactBinding, null, parameters, null);
            if (nativeEquivalent == null || nativeEquivalent.IsGenericMethod || nativeEquivalent.ReturnType != returnType)
                return false;
            bridge = module.DefaultImporter.ImportMethod(nativeEquivalent);
            return true;
        }

        private static bool TryGetReferenceElement(IMethodDescriptor source, out TypeSignature element)
        {
            element = null;
            if (source.Name != "CompareExchange" && source.Name != "Exchange") return false;
            var signature = source.Signature;
            var count = source.Name == "CompareExchange" ? 3 : 2;
            if (signature.ParameterTypes.Count != count ||
                signature.ParameterTypes[0] is not ByReferenceTypeSignature reference)
                return false;
            var generic = signature.GenericParameterCount == 1 && IsMethodParameter(reference.BaseType);
            if (!generic && (signature.GenericParameterCount != 0 ||
                reference.BaseType is not CorLibTypeSignature { ElementType: ElementType.Object }))
                return false;
            if (!SignatureComparer.Default.Equals(reference.BaseType, signature.ReturnType)) return false;
            for (var index = 1; index < count; index++)
                if (!SignatureComparer.Default.Equals(reference.BaseType, signature.ParameterTypes[index])) return false;
            if (source is MethodSpecification instance)
            {
                if (!generic || instance.Signature?.TypeArguments.Count != 1) return false;
                element = instance.Signature.TypeArguments[0];
            }
            else element = reference.BaseType;
            return true;
        }

        private static bool IsMethodParameter(TypeSignature type) =>
            type is GenericParameterSignature { ParameterType: GenericParameterType.Method, Index: 0 };

        private static Type PrimitiveType(TypeSignature type) => type is not CorLibTypeSignature primitive ? null :
            primitive.ElementType switch
            {
                ElementType.Void => typeof(void),
                ElementType.I1 => typeof(sbyte),
                ElementType.U1 => typeof(byte),
                ElementType.I2 => typeof(short),
                ElementType.U2 => typeof(ushort),
                ElementType.I4 => typeof(int),
                ElementType.U4 => typeof(uint),
                ElementType.I8 => typeof(long),
                ElementType.U8 => typeof(ulong),
                ElementType.R4 => typeof(float),
                ElementType.R8 => typeof(double),
                ElementType.I => typeof(IntPtr),
                ElementType.U => typeof(UIntPtr),
                _ => null
            };

        internal static bool TryFillBody(MethodDefinition source, MethodDefinition target, InteropRuntimeReferences imports)
        {
            if (!TryBind(source, imports.Module, _ => target.Signature.ReturnType, out var bridge)) return false;
            FillBody(target, bridge);
            return true;
        }

        internal static void FillBody(MethodDefinition target, IMethodDescriptor bridge)
        {
            var genericContext = GenericContext.FromMethod(bridge);
            if (!target.IsStatic || target.Signature.ParameterTypes.Count != bridge.Signature.ParameterTypes.Count ||
                !SignatureComparer.Default.Equals(target.Signature.ReturnType,
                    bridge.Signature.ReturnType.InstantiateGenericTypes(genericContext)))
                throw new InvalidOperationException("The atomic bridge does not match the rewritten method signature.");
            for (var index = 0; index < target.Parameters.Count; index++)
                if (!SignatureComparer.Default.Equals(target.Parameters[index].ParameterType,
                    bridge.Signature.ParameterTypes[index].InstantiateGenericTypes(genericContext)))
                    throw new InvalidOperationException("The atomic bridge does not match the rewritten parameter type.");
            var body = target.CilMethodBody.Instructions;
            foreach (var parameter in target.Parameters) body.Add(CilOpCodes.Ldarg, parameter);
            body.Add(CilOpCodes.Call, bridge);
            body.Add(CilOpCodes.Ret);
        }
    }
}
