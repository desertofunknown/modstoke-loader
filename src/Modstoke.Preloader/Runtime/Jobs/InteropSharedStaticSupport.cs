// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal static class InteropSharedStaticSupport
    {
        private const string StorageName = "Unity.Collections.LowLevel.Unsafe.BurstLike/SharedStatic`1";

        internal static bool TryDescribeFactoryCall(IMethodDescriptor method,
            out GenericInstanceTypeSignature storage, out TypeSignature[] contexts)
        {
            storage = null;
            contexts = null;
            if (method is not MethodSpecification instance || method.Name != "GetOrCreate" ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount is not (1 or 2) ||
                instance.Signature.TypeArguments.Count != method.Signature.GenericParameterCount ||
                method.Signature.ParameterTypes.Count != 1 ||
                method.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.U4 } ||
                method.DeclaringType?.ToTypeSignature() is not GenericInstanceTypeSignature declaring ||
                declaring.GenericType.FullName != StorageName || declaring.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(declaring) != "UnityEngine.CoreModule")
                return false;
            var definition = instance.Method.Resolve();
            if (definition == null || !IsFactoryDefinition(definition)) return false;
            if (!HasStorageField(definition.DeclaringType) ||
                !SignatureComparer.Default.Equals(method.Signature.ReturnType.InstantiateGenericTypes(
                    GenericContext.FromType(declaring)), declaring))
                return false;
            storage = declaring;
            contexts = instance.Signature.TypeArguments.ToArray();
            return true;
        }

        private static bool IsFactoryDefinition(MethodDefinition method) =>
            method.DeclaringModule.Assembly.Name == "UnityEngine.CoreModule" &&
            method.DeclaringType.FullName == StorageName && method.Name == "GetOrCreate" &&
            method.IsStatic && !method.Signature.HasThis &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) &&
            method.Signature.GenericParameterCount is 1 or 2 &&
            method.GenericParameters.Count == method.Signature.GenericParameterCount &&
            method.GenericParameters.All(parameter => parameter.Attributes == 0 && parameter.Constraints.Count == 0) &&
            method.Signature.ParameterTypes.Count == 1 &&
            method.Signature.ParameterTypes[0] is CorLibTypeSignature { ElementType: ElementType.U4 } &&
            method.Signature.ReturnType is GenericInstanceTypeSignature result &&
            result.GenericType.FullName == StorageName && result.TypeArguments.Count == 1 &&
            result.TypeArguments[0] is GenericParameterSignature { ParameterType: GenericParameterType.Type, Index: 0 };

        private static bool HasStorageField(TypeDefinition type)
        {
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 1 && fields[0].Name == "_buffer" &&
                fields[0].Signature.FieldType is PointerTypeSignature
                    { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } };
        }

        internal static bool TryFillBody(MethodDefinition original, MethodDefinition target,
            InteropTypeContext type, InteropRuntimeReferences imports)
        {
            if (original.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                original.DeclaringType.FullName != StorageName || type.OriginalType == null ||
                type.NewType.IsValueType || type.NewType.GenericParameters.Count != 1 ||
                !InteropDelegateContracts.HasStandardConvention(original.Signature) ||
                !InteropDelegateContracts.HasStandardConvention(target.Signature))
                return false;
            if (!HasStorageField(original.DeclaringType))
                return false;

            var getter = original.Name == "get_Data" && !original.IsStatic && original.Signature.HasThis &&
                target.Signature.HasThis && original.Signature.GenericParameterCount == 0 &&
                target.Signature.GenericParameterCount == 0 && original.Signature.ParameterTypes.Count == 0 &&
                target.Signature.ParameterTypes.Count == 0 &&
                original.Signature.ReturnType is ByReferenceTypeSignature { BaseType: GenericParameterSignature
                    { ParameterType: GenericParameterType.Type, Index: 0 } } &&
                target.Signature.ReturnType is ByReferenceTypeSignature { BaseType: GenericParameterSignature
                    { ParameterType: GenericParameterType.Type, Index: 0 } };
            var contexts = original.Signature.GenericParameterCount;
            var factory = IsFactoryDefinition(original) && !target.Signature.HasThis &&
                target.Signature.GenericParameterCount == contexts && target.GenericParameters.Count == contexts &&
                target.Signature.ParameterTypes.Count == 1 &&
                target.Signature.ParameterTypes[0] is CorLibTypeSignature { ElementType: ElementType.U4 } &&
                SignatureComparer.Default.Equals(target.Signature.ReturnType, type.SelfSubstitutedRef.ToTypeSignature());
            if (!getter && !factory) return false;

            var cil = target.CilMethodBody;
            cil.Instructions.Clear();
            cil.LocalVariables.Clear();
            cil.ExceptionHandlers.Clear();
            var body = cil.Instructions;
            var importer = imports.Module.DefaultImporter;
            var storage = type.SelfSubstitutedRef.ToTypeSignature();
            var value = new GenericParameterSignature(imports.Module, GenericParameterType.Type, 0);
            if (getter)
            {
                // Reflection invocation need not preserve a byref return's storage identity.
                body.Add(CilOpCodes.Ldarg_0);
                var accessor = (IMethodDefOrRef)importer.ImportMethod(typeof(NativeSharedMemory)
                    .GetMethod(nameof(NativeSharedMemory.GetSharedStaticValue)));
                body.Add(CilOpCodes.Call, accessor.MakeGenericInstanceMethod(storage, value));
            }
            else
            {
                var hash = (IMethodDefOrRef)importer.ImportMethod(typeof(NativeSharedMemory)
                    .GetMethod(nameof(NativeSharedMemory.GetNativeContextHash)));
                for (var index = 0; index < contexts; index++)
                    body.Add(CilOpCodes.Call, hash.MakeGenericInstanceMethod(
                        new GenericParameterSignature(imports.Module, GenericParameterType.Method, index)));
                if (contexts == 1) body.Add(CilOpCodes.Ldc_I8, 0L);
                body.Add(CilOpCodes.Ldarg_0);
                var create = (IMethodDefOrRef)importer.ImportMethod(typeof(NativeSharedMemory)
                    .GetMethod(nameof(NativeSharedMemory.CreateSharedStatic)));
                body.Add(CilOpCodes.Call, create.MakeGenericInstanceMethod(storage, value));
            }
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
