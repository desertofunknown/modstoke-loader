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
        private bool TryTranslateNativeArrayValue(CilOpCode code, IMethodDescriptor method, out CilInstruction translated)
        {
            translated = null;
            if ((string)method.Name is not ("ReinterpretLoad" or "ReinterpretStore") ||
                method is not MethodSpecification specification || specification.Signature.TypeArguments.Count != 1 ||
                !InteropNativeArrayLowering.TryGetElement(context, method.DeclaringType?.ToTypeSignature(), out _, out var readOnly) ||
                readOnly || !TryGetNativeValueWrapper(specification.Signature.TypeArguments[0], out var proxy)) return false;
            var source = method.Resolve();
            var signature = method.Signature;
            var store = method.Name == "ReinterpretStore";
            if (code.Code != CilCode.Call || source == null || !source.IsPublic || source.IsStatic || source.IsAbstract ||
                source.IsPInvokeImpl || !source.HasMethodBody || source.ImplAttributes != MethodImplAttributes.IL ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 1 || source.GenericParameters.Count != 1 ||
                signature.ParameterTypes.Count != (store ? 2 : 1) ||
                signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(specification.Signature.TypeArguments[0]) != null)
                return RejectNativeArrayValue(out translated);
            var substitution = GenericContext.FromMethod(method);
            var owner = resolver.Resolve(method.DeclaringType.ToTypeSignature()) as GenericInstanceTypeSignature;
            var result = resolver.Resolve(signature.ReturnType.InstantiateGenericTypes(substitution));
            var parameters = signature.ParameterTypes.Select(parameter =>
                resolver.Resolve(parameter.InstantiateGenericTypes(substitution))).ToArray();
            if (owner == null || result == null || parameters.Any(parameter => parameter == null))
                return RejectNativeArrayValue(out translated);
            var candidate = typeof(InteropNativeArrayStorage<>).GetMethod(store ? "ReinterpretStoreProxy" : "ReinterpretLoadProxy",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var imported = imports.Module.DefaultImporter.ImportMethod(candidate);
            IMethodDescriptor reference = new MemberReference(owner.ToTypeDefOrRef(), imported.Name, imported.Signature)
                .MakeGenericInstanceMethod(proxy);
            if (!MatchesSubstitutedCallSignature(reference, result, parameters))
                return RejectNativeArrayValue(out translated);
            translated = body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(reference));
            return true;
        }

        private bool RejectNativeArrayValue(out CilInstruction translated)
        {
            translated = FailInstruction("native-array-value-contract",
                "This array value operation has no exact native value-proxy and byte-storage contract.");
            return true;
        }
    }
}
