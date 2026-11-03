// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using MethodImplAttributes = AsmResolver.PE.DotNet.Metadata.Tables.MethodImplAttributes;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateNativeSliceUtility(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (method is not MethodSpecification specification || specification.Signature.TypeArguments.Count != 1 ||
                method.DeclaringType?.FullName is not ("Unity.Collections.NativeSliceExtensions" or
                    "Unity.Collections.LowLevel.Unsafe.NativeSliceUnsafeUtility") ||
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) != InteropNativeArrayLowering.CoreModule ||
                !InteropNativeArrayLowering.IsStrippedHandle(context, specification.Signature.TypeArguments[0])) return false;
            var source = method.Resolve();
            var signature = method.Signature;
            if (code.Code != CilCode.Call || source == null || !source.IsPublic || !source.IsStatic || source.IsAbstract ||
                source.IsPInvokeImpl || !source.HasMethodBody || source.ImplAttributes != MethodImplAttributes.IL ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.HasThis ||
                signature.GenericParameterCount != 1 || source.GenericParameters.Count != 1)
                return RejectNativeSliceUtility(out translated);
            var substitution = GenericContext.FromMethod(method);
            var parameters = signature.ParameterTypes.Select(parameter => parameter.InstantiateGenericTypes(substitution)).ToArray();
            var result = signature.ReturnType.InstantiateGenericTypes(substitution);
            if (method.DeclaringType.FullName == "Unity.Collections.NativeSliceExtensions")
            {
                if (method.Name != "Slice" || parameters.Length is < 1 or > 3 ||
                    parameters.Skip(1).Any(parameter => parameter is not CorLibTypeSignature { ElementType: ElementType.I4 }) ||
                    !InteropNativeArrayLowering.TryGetSliceElement(context, result, out var resultElement) ||
                    !SignatureComparer.Default.Equals(resultElement, specification.Signature.TypeArguments[0]) ||
                    !InteropNativeArrayLowering.TryGetStorage(context, parameters[0], out var sourceElement, out var sourceHelper) ||
                    (sourceHelper != typeof(InteropNativeArrayStorage<>) && sourceHelper != typeof(InteropNativeSliceStorage<>)) ||
                    !SignatureComparer.Default.Equals(sourceElement, resultElement)) return RejectNativeSliceUtility(out translated);
                var owner = resolver.Resolve(result) as GenericInstanceTypeSignature;
                var arguments = parameters.Select(parameter => resolver.Resolve(parameter)).ToArray();
                if (owner == null || arguments.Any(argument => argument == null)) return RejectNativeSliceUtility(out translated);
                // The one-argument slice extension returns the existing descriptor unchanged.
                if (sourceHelper == typeof(InteropNativeSliceStorage<>) && arguments.Length == 1)
                {
                    translated = body.Add(CilOpCodes.Nop);
                    return true;
                }
                IMethodDescriptor selected = null;
                foreach (var constructor in typeof(InteropNativeSliceStorage<>).GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                {
                    var imported = imports.Module.DefaultImporter.ImportMethod(constructor);
                    IMethodDescriptor candidate = new MemberReference(owner.ToTypeDefOrRef(), imported.Name, imported.Signature);
                    var targetContext = GenericContext.FromMethod(candidate);
                    if (candidate.Signature.ParameterTypes.Count != arguments.Length ||
                        !arguments.Select((argument, index) => SignatureComparer.Default.Equals(argument,
                            candidate.Signature.ParameterTypes[index].InstantiateGenericTypes(targetContext))).All(matches => matches)) continue;
                    if (selected != null) return RejectNativeSliceUtility(out translated);
                    selected = candidate;
                }
                if (selected == null) return RejectNativeSliceUtility(out translated);
                translated = body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(selected));
                return true;
            }

            var wrap = method.Name == "ConvertExistingDataToNativeSlice";
            var array = wrap ? result : parameters.FirstOrDefault();
            if (!InteropNativeArrayLowering.TryGetSliceElement(context, array, out var element) ||
                !SignatureComparer.Default.Equals(element, specification.Signature.TypeArguments[0]))
                return RejectNativeSliceUtility(out translated);
            var targetOwner = resolver.Resolve(array) as GenericInstanceTypeSignature;
            if (targetOwner == null) return RejectNativeSliceUtility(out translated);
            if (wrap)
            {
                if (parameters.Length != 3 || !IsStorageVoidPointer(parameters[0]) ||
                    parameters.Skip(1).Any(parameter => parameter is not CorLibTypeSignature { ElementType: ElementType.I4 }))
                    return RejectNativeSliceUtility(out translated);
                var imported = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeSliceStorage<>)
                    .GetMethod(nameof(InteropNativeSliceStorage<int>.FromExistingData)));
                translated = body.Add(CilOpCodes.Call,
                    new MemberReference(targetOwner.ToTypeDefOrRef(), imported.Name, imported.Signature));
                return true;
            }
            if ((string)method.Name is not ("GetUnsafePtr" or "GetUnsafeReadOnlyPtr") ||
                parameters.Length != 1 || !IsStorageVoidPointer(result)) return RejectNativeSliceUtility(out translated);
            var local = AddArrayLocal(targetOwner);
            translated = body.Add(CilOpCodes.Stloc, local);
            body.Add(CilOpCodes.Ldloca, local);
            var getter = imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeSliceStorage<>)
                .GetMethod(nameof(InteropNativeSliceStorage<int>.GetUnsafePtr)));
            body.Add(CilOpCodes.Call, new MemberReference(targetOwner.ToTypeDefOrRef(), getter.Name, getter.Signature));
            body.Add(CilOpCodes.Conv_U);
            return true;
        }

        private bool RejectNativeSliceUtility(out CilInstruction translated)
        {
            translated = FailInstruction("native-slice-utility-contract",
                "This closed slice operation has no exact public borrowed-storage contract.");
            return true;
        }
    }
}
