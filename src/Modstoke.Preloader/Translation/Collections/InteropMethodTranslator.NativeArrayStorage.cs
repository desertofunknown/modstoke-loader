// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using GenericParameterAttributes = AsmResolver.PE.DotNet.Metadata.Tables.GenericParameterAttributes;
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateNativeArrayStorageMethod(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (TryTranslateNativeSliceView(code, method, out translated)) return true;
            if (TryTranslateNativeSliceUtility(code, method, out translated)) return true;
            if (TryTranslateNativeArrayValue(code, method, out translated)) return true;
            if (TryTranslateNativeArrayView(code, method, out translated)) return true;
            if (TryTranslateNativeArrayPointer(code, method, out translated)) return true;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (!InteropNativeArrayLowering.TryGetStorage(context, sourceOwner, out _, out var helper)) return false;
            var source = method.Resolve();
            if (code.Code is not (CilCode.Call or CilCode.Newobj) || source == null || !source.IsPublic ||
                source.IsAbstract || source.IsPInvokeImpl || !source.HasMethodBody ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature))
            {
                translated = FailInstruction("native-array-storage-call", "This array operation has no direct CLR storage contract.");
                return true;
            }
            var owner = resolver.Resolve(sourceOwner) as GenericInstanceTypeSignature;
            if (owner == null) return RejectNativeArrayMethod(out translated);
            var substitution = GenericContext.FromMethod(method);
            var sourceReturn = ResolveStorageCallType(method.Signature.ReturnType.InstantiateGenericTypes(substitution));
            var parameters = method.Signature.ParameterTypes.Select(parameter =>
                ResolveStorageCallType(parameter.InstantiateGenericTypes(substitution))).ToArray();
            if (sourceReturn == null || parameters.Any(parameter => parameter == null))
                return RejectNativeArrayMethod(out translated);

            var genericArguments = method is MethodSpecification specification
                ? specification.Signature.TypeArguments.Select(argument => resolver.Resolve(argument)).ToArray()
                : Array.Empty<TypeSignature>();
            if (method.Name == "Dispose" && parameters.Length != 0)
            {
                if (code.Code != CilCode.Call || helper != typeof(InteropNativeArrayStorage<>) || source.IsStatic || !method.Signature.HasThis ||
                    source.ImplAttributes != MethodImplAttributes.IL || method is MethodSpecification ||
                    source.GenericParameters.Count != 0 || source.Signature.GenericParameterCount != 0 ||
                    method.Signature.GenericParameterCount != 0 || parameters.Length != 1 ||
                    source.Signature.ParameterTypes.Count != 1 ||
                    !IsStorageJobHandle(method.Signature.ParameterTypes[0]) ||
                    !IsStorageJobHandle(method.Signature.ReturnType) ||
                    !IsStorageJobHandle(source.Signature.ParameterTypes[0]) ||
                    !IsStorageJobHandle(source.Signature.ReturnType) ||
                    !HasStorageJobHandleLayout(parameters[0]) ||
                    !SignatureComparer.Default.Equals(sourceReturn, parameters[0]))
                    return RejectNativeArrayMethod(out translated);
                // Unity's overload is non-generic; bind the helper to the exact lowered handle.
                genericArguments = new[] { parameters[0] };
            }
            if (genericArguments.Any(argument => argument == null ||
                !SatisfiesSpecialConstraints(argument, GenericParameterAttributes.NotNullableValueTypeConstraint |
                    GenericParameterAttributes.DefaultConstructorConstraint))) return RejectNativeArrayMethod(out translated);

            var candidates = source.IsConstructor
                ? helper.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Cast<MethodBase>()
                : helper.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Where(candidate => candidate.Name == (string)method.Name).Cast<MethodBase>();
            IMethodDescriptor selected = null;
            foreach (var candidate in candidates)
            {
                if (candidate.IsStatic == method.Signature.HasThis ||
                    (candidate is MethodInfo info ? info.GetGenericArguments().Length : 0) != genericArguments.Length) continue;
                var imported = imports.Module.DefaultImporter.ImportMethod(candidate);
                IMethodDescriptor reference = new MemberReference(owner.ToTypeDefOrRef(), imported.Name, imported.Signature);
                if (genericArguments.Length != 0)
                    reference = ((IMethodDefOrRef)reference).MakeGenericInstanceMethod(genericArguments);
                if (!MatchesSubstitutedCallSignature(reference, sourceReturn, parameters)) continue;
                if (selected != null) return RejectNativeArrayMethod(out translated);
                selected = reference;
            }
            if (selected == null) return RejectNativeArrayMethod(out translated);
            translated = body.Add(code, imports.Module.DefaultImporter.ImportMethod(selected));
            return true;
        }

        private static bool IsStorageJobHandle(TypeSignature type) => type is TypeDefOrRefSignature &&
            type.IsValueType && type.FullName == "Unity.Jobs.JobHandle" &&
            InteropRestoredTypeSupport.GetAssemblyName(type) == InteropNativeArrayLowering.CoreModule;

        private static bool HasStorageJobHandleLayout(TypeSignature signature)
        {
            if (!IsStorageJobHandle(signature) || signature.Resolve() is not { } type ||
                !type.IsValueType || type.IsEnum || !type.IsExplicitLayout || type.GenericParameters.Count != 0)
                return false;
            var fields = type.Fields.Where(field => !field.IsStatic).ToArray();
            return fields.Length == 2 &&
                fields.Any(field => field.Name == "jobGroup" && field.FieldOffset == 0 &&
                    field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.U8 }) &&
                fields.Any(field => field.Name == "version" && field.FieldOffset == 8 &&
                    field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I4 });
        }

        private TypeSignature ResolveStorageCallType(TypeSignature source)
        {
            // Both Unity enums are exactly Int32; the helper stores allocator labels without boxing.
            if (InteropNativeArrayLowering.IsAllocationEnum(source, "Unity.Collections.Allocator") ||
                InteropNativeArrayLowering.IsAllocationEnum(source, "Unity.Collections.NativeArrayOptions"))
                return imports.Module.CorLibTypeFactory.Int32;
            return resolver.Resolve(source);
        }

        private bool RejectNativeArrayMethod(out CilInstruction translated)
        {
            translated = FailInstruction("native-array-storage-method-unavailable",
                "The closed array operation has no matching CLR storage signature. Native fallback is not valid for a stripped element.");
            return true;
        }

        private bool TryTranslateNativeArrayPointer(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (method is not MethodSpecification specification || specification.Signature.TypeArguments.Count != 1 ||
                method.DeclaringType?.FullName != "Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility" ||
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) != InteropNativeArrayLowering.CoreModule)
                return false;
            var source = method.Resolve();
            if (source == null) return false;
            var substitution = GenericContext.FromMethod(method);
            var parameters = method.Signature.ParameterTypes.Select(parameter => parameter.InstantiateGenericTypes(substitution)).ToArray();
            var result = method.Signature.ReturnType.InstantiateGenericTypes(substitution);
            var wrap = method.Name == "ConvertExistingDataToNativeArray";
            var array = wrap ? result : parameters.FirstOrDefault();
            if (!InteropNativeArrayLowering.TryGetElement(context, array, out _, out var readOnly)) return false;
            if (code.Code != CilCode.Call || !source.IsPublic || !source.IsStatic ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 1 || readOnly) return RejectNativeArrayMethod(out translated);
            var owner = resolver.Resolve(array) as GenericInstanceTypeSignature;
            if (owner == null) return RejectNativeArrayMethod(out translated);
            if (wrap)
            {
                if (parameters.Length != 3 || !IsStorageVoidPointer(parameters[0]) ||
                    parameters[1] is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                    !InteropNativeArrayLowering.IsAllocationEnum(parameters[2], "Unity.Collections.Allocator"))
                    return RejectNativeArrayMethod(out translated);
                var imported = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeArrayStorage<>)
                    .GetMethod(nameof(InteropNativeArrayStorage<int>.FromExistingData)));
                // void* and IntPtr have the same evaluation-stack representation; no boxing occurs.
                translated = body.Add(CilOpCodes.Call, new MemberReference(owner.ToTypeDefOrRef(), imported.Name, imported.Signature));
                return true;
            }
            if ((string)method.Name is not ("GetUnsafePtr" or "GetUnsafeReadOnlyPtr" or "GetUnsafeBufferPointerWithoutChecks") ||
                parameters.Length != 1 || !IsStorageVoidPointer(result)) return RejectNativeArrayMethod(out translated);
            var local = AddArrayLocal(owner);
            translated = body.Add(CilOpCodes.Stloc, local);
            body.Add(CilOpCodes.Ldloca, local);
            var getter = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeArrayStorage<>)
                .GetMethod(nameof(InteropNativeArrayStorage<int>.GetUnsafePtr)));
            body.Add(CilOpCodes.Call, new MemberReference(owner.ToTypeDefOrRef(), getter.Name, getter.Signature));
            body.Add(CilOpCodes.Conv_U);
            return true;
        }

        private static bool IsStorageVoidPointer(TypeSignature type) => type is PointerTypeSignature
            { BaseType: CorLibTypeSignature { ElementType: ElementType.Void } };
    }
}
