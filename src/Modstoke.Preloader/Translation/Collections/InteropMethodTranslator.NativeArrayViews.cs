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
        private bool TryTranslateNativeArrayView(CilOpCode code, IMethodDescriptor method, out CilInstruction translated)
        {
            translated = null;
            if (method.Name != "Reinterpret" || method is not MethodSpecification specification ||
                specification.Signature.TypeArguments.Count != 1 || method.Signature == null) return false;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            if (sourceOwner is not GenericInstanceTypeSignature originalOwner ||
                originalOwner.GenericType.FullName is not (InteropNativeArrayLowering.ArrayName or InteropNativeArrayLowering.ReadOnlyName) ||
                InteropRestoredTypeSupport.GetAssemblyName(sourceOwner) != InteropNativeArrayLowering.CoreModule) return false;
            var substitution = GenericContext.FromMethod(method);
            var sourceResult = method.Signature.ReturnType.InstantiateGenericTypes(substitution);
            var fromManaged = InteropNativeArrayLowering.TryGetElement(context, sourceOwner, out _, out var ownerReadOnly);
            var toManaged = InteropNativeArrayLowering.TryGetElement(context, sourceResult, out _, out var resultReadOnly);
            if (fromManaged == toManaged) return false;

            var readOnly = fromManaged ? ownerReadOnly : resultReadOnly;
            var source = method.Resolve();
            var signature = method.Signature;
            if (code.Code != CilCode.Call || source == null || !source.IsPublic || source.IsStatic || source.IsAbstract ||
                source.IsPInvokeImpl || !source.HasMethodBody || source.ImplAttributes != MethodImplAttributes.IL ||
                !signature.HasThis || !InteropDelegateContracts.HasStandardConvention(signature) ||
                signature.GenericParameterCount != 1 || source.GenericParameters.Count != 1 ||
                signature.ParameterTypes.Count > (readOnly ? 0 : 1) ||
                signature.ParameterTypes.Any(parameter => parameter is not CorLibTypeSignature { ElementType: ElementType.I4 }) ||
                sourceResult is not GenericInstanceTypeSignature resultArray || resultArray.TypeArguments.Count != 1 ||
                !SignatureComparer.Default.Equals(resultArray.TypeArguments[0], specification.Signature.TypeArguments[0]))
                return RejectNativeArrayView(out translated);

            var nativeSource = fromManaged ? sourceResult : sourceOwner;
            if (!TryResolveNativeArrayView(nativeSource, readOnly, out var nativeArray, out var nativeElement, out var valueProxy))
                return RejectNativeArrayView(out translated);
            var managedOwner = resolver.Resolve(fromManaged ? sourceOwner : sourceResult) as GenericInstanceTypeSignature;
            var result = resolver.Resolve(sourceResult);
            if (managedOwner == null || result == null) return RejectNativeArrayView(out translated);
            var parameters = signature.ParameterTypes.Select(parameter => resolver.Resolve(parameter)).ToList();
            // Value-aware translation has already loaded the boxed native receiver. The
            // static import helper consumes that wrapper; the export helper keeps CLR this byref.
            if (!fromManaged) parameters.Insert(0, nativeArray);
            if (parameters.Any(parameter => parameter == null)) return RejectNativeArrayView(out translated);
            var helper = readOnly ? typeof(InteropNativeArrayStorage<>.ReadOnly) : typeof(InteropNativeArrayStorage<>);
            var name = valueProxy
                ? fromManaged ? "ReinterpretProxyToNative" : "ReinterpretProxyFromNative"
                : fromManaged ? "ReinterpretToNative" : "ReinterpretFromNative";
            IMethodDescriptor selected = null;
            foreach (var candidate in helper.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(candidate => candidate.Name == name && candidate.GetGenericArguments().Length == 2))
            {
                if (candidate.IsStatic == fromManaged) continue;
                var imported = imports.Module.DefaultImporter.ImportMethod(candidate);
                IMethodDescriptor reference = new MemberReference(managedOwner.ToTypeDefOrRef(), imported.Name, imported.Signature)
                    .MakeGenericInstanceMethod(nativeElement, nativeArray);
                if (!MatchesSubstitutedCallSignature(reference, result, parameters)) continue;
                if (selected != null) return RejectNativeArrayView(out translated);
                selected = reference;
            }
            if (selected == null) return RejectNativeArrayView(out translated);
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(selected));
            return true;
        }

        private bool TryResolveNativeArrayView(TypeSignature source, bool readOnly,
            out TypeSignature array, out TypeSignature element, out bool valueProxy)
        {
            array = null;
            element = null;
            valueProxy = false;
            if (source is not GenericInstanceTypeSignature instance || !instance.IsValueType || instance.TypeArguments.Count != 1 ||
                instance.GenericType.FullName != (readOnly ? InteropNativeArrayLowering.ReadOnlyName : InteropNativeArrayLowering.ArrayName) ||
                InteropRestoredTypeSupport.GetAssemblyName(source) != InteropNativeArrayLowering.CoreModule ||
                !InteropNativeArrayLowering.HasArrayLayout(instance.GenericType.Resolve(), readOnly) ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(source) != null ||
                !TryGetNativeValueWrapper(source, out array)) return false;
            element = resolver.Resolve(instance.TypeArguments[0]);
            if (TryGetNativeValueWrapper(instance.TypeArguments[0], out var proxy))
            {
                valueProxy = SignatureComparer.Default.Equals(element, proxy);
                return valueProxy;
            }
            return element != null && SatisfiesSpecialConstraints(element,
                GenericParameterAttributes.NotNullableValueTypeConstraint | GenericParameterAttributes.DefaultConstructorConstraint);
        }

        private bool RejectNativeArrayView(out CilInstruction translated)
        {
            translated = FailInstruction("native-array-view-contract",
                "This mixed array reinterpretation has no exact native element, descriptor or CLR value contract.");
            return true;
        }
    }
}
