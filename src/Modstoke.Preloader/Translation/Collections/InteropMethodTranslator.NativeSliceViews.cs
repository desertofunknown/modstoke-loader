// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

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
        private bool TryTranslateNativeSliceView(CilOpCode code, IMethodDescriptor method, out CilInstruction translated)
        {
            translated = null;
            if ((string)method.Name is not ("SliceConvert" or "SliceWithStride") ||
                method is not MethodSpecification specification || specification.Signature.TypeArguments.Count != 1 ||
                method.Signature == null) return false;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (sourceOwner is not GenericInstanceTypeSignature originalOwner || !originalOwner.IsValueType ||
                originalOwner.TypeArguments.Count != 1 || originalOwner.GenericType.FullName != InteropNativeArrayLowering.SliceName ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceOwner) != InteropNativeArrayLowering.CoreModule) return false;
            var substitution = GenericContext.FromMethod(method);
            var sourceResult = method.Signature.ReturnType.InstantiateGenericTypes(substitution);
            var fromManaged = InteropNativeArrayLowering.TryGetSliceElement(context, sourceOwner, out _);
            var toManaged = InteropNativeArrayLowering.TryGetSliceElement(context, sourceResult, out _);
            if (fromManaged == toManaged) return false;

            var source = method.Resolve();
            var signature = method.Signature;
            if (code.Code != CilCode.Call || source == null || !source.IsPublic || source.IsStatic || source.IsAbstract ||
                source.IsPInvokeImpl || !source.HasMethodBody || source.ImplAttributes != MethodImplAttributes.IL ||
                !signature.HasThis || !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.GenericParameterCount != 1 || source.GenericParameters.Count != 1 ||
                source.Name != method.Name || !SignatureComparer.Default.Equals(source.DeclaringType, originalOwner.GenericType) ||
                !SignatureComparer.Default.Equals(source.Signature, signature))
                return RejectNativeSliceView("The mixed slice view requires an exact public instance IL method with one generic argument and a direct call.", out translated);
            if (signature.ParameterTypes.Count > (method.Name == "SliceConvert" ? 0 : 1) ||
                signature.ParameterTypes.Any(parameter => parameter is not CorLibTypeSignature { ElementType: ElementType.I4 }) ||
                sourceResult is not GenericInstanceTypeSignature resultSlice || !resultSlice.IsValueType ||
                resultSlice.GenericType.FullName != InteropNativeArrayLowering.SliceName || resultSlice.TypeArguments.Count != 1 ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceResult) != InteropNativeArrayLowering.CoreModule ||
                !SignatureComparer.Default.Equals(resultSlice.TypeArguments[0], specification.Signature.TypeArguments[0]))
                return RejectNativeSliceView("SliceConvert requires no arguments; SliceWithStride accepts only an optional Int32 offset, and both must return the exact NativeSlice<U>.", out translated);

            var nativeSource = fromManaged ? sourceResult : sourceOwner;
            if (!TryResolveNativeSliceView(nativeSource, out var nativeSlice, out var nativeElement, out var valueProxy))
                return RejectNativeSliceView("The native slice view requires an exact slice layout, native identity, wrapper and native element representation.", out translated);
            var managedOwner = resolver.Resolve(fromManaged ? sourceOwner : sourceResult) as GenericInstanceTypeSignature;
            var result = resolver.Resolve(sourceResult);
            if (managedOwner == null || result == null)
                return RejectNativeSliceView("The mixed slice view has no resolved CLR storage owner or return type.", out translated);
            var parameters = signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToList();
            // Value-aware translation has already loaded the native receiver wrapper and
            // retains its lifetime. Exports keep the original CLR storage receiver byref.
            if (!fromManaged) parameters.Insert(0, nativeSlice);
            if (parameters.Any(parameter => parameter == null))
                return RejectNativeSliceView("The mixed slice view has an unresolved helper parameter.", out translated);
            var name = (string)method.Name + (valueProxy ? "Proxy" : "") + (fromManaged ? "ToNative" : "FromNative");
            IMethodDescriptor selected = null;
            foreach (var candidate in typeof(InteropNativeSliceStorage<>).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(candidate => candidate.Name == name && candidate.GetGenericArguments().Length == 2))
            {
                if (candidate.IsStatic == fromManaged) continue;
                var imported = imports.Module.DefaultImporter.ImportMethod(candidate);
                // The managed element belongs to the helper owner; its method arguments
                // describe the native side, which is the source only for imports.
                IMethodDescriptor reference = new MemberReference(managedOwner.ToTypeDefOrRef(), imported.Name, imported.Signature)
                    .MakeGenericInstanceMethod(nativeElement, nativeSlice);
                if (!MatchesSubstitutedCallSignature(reference, result, parameters)) continue;
                if (selected != null)
                    return RejectNativeSliceView("The mixed slice view matches more than one exact public declared helper.", out translated);
                selected = reference;
            }
            if (selected == null)
                return RejectNativeSliceView("The mixed slice view has no public declared helper with the exact substituted return and parameter types.", out translated);
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(selected));
            return true;
        }

        private bool TryResolveNativeSliceView(TypeSignature source,
            out TypeSignature slice, out TypeSignature element, out bool valueProxy)
        {
            slice = null;
            element = null;
            valueProxy = false;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType || instance.TypeArguments.Count != 1 ||
                instance.GenericType.FullName != InteropNativeArrayLowering.SliceName ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != InteropNativeArrayLowering.CoreModule ||
                !InteropNativeArrayLowering.HasSliceLayout(instance.GenericType.Resolve()) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(source) != null ||
                !TryGetNativeValueWrapper(source, out slice)) return false;
            element = resolver.Resolve(instance.TypeArguments[0]);
            if (element == null || slice is not GenericInstanceTypeSignature nativeSlice || nativeSlice.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(nativeSlice.TypeArguments[0], element)) return false;
            if (TryGetNativeValueWrapper(instance.TypeArguments[0], out var proxy))
            {
                valueProxy = SignatureComparer.Default.Equals(element, proxy);
                return valueProxy;
            }
            return SatisfiesSpecialConstraints(element,
                GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint);
        }

        private bool RejectNativeSliceView(string message, out CilInstruction translated)
        {
            translated = FailInstruction("native-slice-view-contract", message);
            return true;
        }
    }
}
