// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateNativeArrayConstrainedCall(CilInstruction prefix, TypeSignature receiver,
            IMethodDescriptor method, CilInstruction call, out CilInstruction translated)
        {
            translated = null;
            if (!InteropNativeArrayLowering.TryGetStorage(context, receiver, out _, out var helper)) return false;
            var source = receiver.Resolve();
            var slot = method.Resolve();
            if (method is MethodSpecification || source == null ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || !method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 || method.Signature.ParameterTypes.Count != 0 ||
                !TryGetStackValue(prefix, 0, out var value) || value.Type is not ByReferenceTypeSignature address ||
                !SignatureComparer.Default.Equals(address.BaseType, receiver)) return Reject(out translated);
            var hash = method.Name == "GetHashCode";
            if (hash)
            {
                var corlib = sourceMethod.DeclaringModule.CorLibTypeFactory;
                if ((helper != typeof(InteropNativeArrayStorage<>) && helper != typeof(InteropNativeSliceStorage<>)) ||
                    !SignatureComparer.Default.Equals(method.DeclaringType, corlib.Object.Type) ||
                    InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) is not
                        ("mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib") ||
                    !SignatureComparer.Default.Equals(method.Signature.ReturnType, corlib.Int32) ||
                    slot == null || !slot.IsPublic || !slot.IsVirtual || slot.IsStatic || slot.IsAbstract ||
                    slot.GenericParameters.Count != 0 || slot.Name != method.Name ||
                    !SignatureComparer.Default.Equals(slot.DeclaringType, corlib.Object.Type.Resolve()) ||
                    !SignatureComparer.Default.Equals(slot.Signature, method.Signature)) return Reject(out translated);
            }
            else if (method.Name != "Dispose" || method.DeclaringType?.FullName != "System.IDisposable" ||
                InteropRestoredTypeSupport.GetAssemblyName(method.DeclaringType.ToTypeSignature()) is not
                    ("mscorlib" or "netstandard" or "System.Runtime" or "System.Private.CoreLib") ||
                slot?.DeclaringType?.IsInterface != true || method.Signature.ReturnType.ElementType != ElementType.Void ||
                !source.Interfaces.Any(entry => SignatureComparer.Default.Equals(entry.Interface, method.DeclaringType)))
                return Reject(out translated);
            var candidates = source.Methods.Where(candidate => candidate.IsPublic && !candidate.IsStatic &&
                !candidate.IsAbstract && !candidate.IsPInvokeImpl && candidate.HasMethodBody &&
                candidate.Name == method.Name && candidate.GenericParameters.Count == 0 &&
                SignatureComparer.Default.Equals(candidate.Signature, method.Signature)).ToArray();
            if (candidates.Length != 1) return Reject(out translated);
            var implementation = candidates[0];
            if (hash && (!implementation.IsVirtual || implementation.IsNewSlot ||
                implementation.ImplAttributes != MethodImplAttributes.IL ||
                source.MethodImplementations.Any(entry => SignatureComparer.Default.Equals(entry.Declaration, method) ||
                    SignatureComparer.Default.Equals(entry.Body, implementation)))) return Reject(out translated);

            // Keep the original location. The normal storage-call router binds this direct call
            // to the helper; an enumerator's no-op Dispose must never dispose its backing array.
            constrainedValueCalls.Add(call, new MemberReference(receiver.ToTypeDefOrRef(),
                implementation.Name, implementation.Signature));
            translated = body.Add(CilOpCodes.Nop);
            return true;

            bool Reject(out CilInstruction result)
            {
                result = FailInstruction("native-array-storage-constrained-call",
                    "Only exact storage/enumerator Dispose and writable array/slice Object.GetHashCode contracts have constrained CLR adapters.");
                return true;
            }
        }
    }
}
