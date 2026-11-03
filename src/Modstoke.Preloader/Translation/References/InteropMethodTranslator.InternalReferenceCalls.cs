// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanInternalReferenceCall(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var signature = method.Signature;
            if (instruction.OpCode.Code is not (CilCode.Call or CilCode.Callvirt) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || signature.GenericParameterCount != 0 ||
                method.Resolve() is not { IsConstructor: false, IsVirtual: false, IsAbstract: false } original ||
                (original.ImplAttributes & MethodImplAttributes.InternalCall) == 0 ||
                original.DeclaringType.GenericParameters.Count != 0 ||
                original.IsStatic != !signature.HasThis || original.IsStatic && instruction.OpCode.Code != CilCode.Call)
                return false;
            var sourceOwner = method.DeclaringType?.ToTypeSignature();
            var owner = resolver.ResolveRaw(sourceOwner);
            var definition = owner?.Resolve();
            var type = context.TryGetAssemblyByName(original.DeclaringModule.Assembly.Name)
                ?.TryGetTypeByName(original.DeclaringType.FullName);
            if (definition == null || type == null || type.NewType != definition) return false;
            var bridge = type.TryGetRestoredInternalCall(original);
            var offset = original.IsStatic ? 0 : 1;
            var count = signature.ParameterTypes.Count;
            if (bridge == null || consumed.Length != count + offset || bridge.Target.Parameters.Count != count ||
                bridge.Target.IsStatic != original.IsStatic || definition.GenericParameters.Count != 0)
                return false;
            if (offset != 0 && (consumed[0] != null || InteropTypeReferences.IsValueType(sourceOwner) ||
                !IsNativeArrayReference(owner) || !NativeValueArgumentMatches(instruction, count, sourceOwner)))
                return false;
            var call = new ReferenceCall
            {
                Owner = owner,
                InternalCall = bridge,
                Parameters = new TypeSignature[count],
                References = new bool[count],
                NativeReferences = new bool[count]
            };
            if (signature.ReturnType.ElementType != ElementType.Void &&
                !TryGetAddressElement(signature.ReturnType, out call.Result)) return false;
            if (!SignatureComparer.Default.Equals(bridge.Target.Signature.ReturnType,
                    call.Result ?? imports.Module.CorLibTypeFactory.Void)) return false;
            var hasNativeLocation = false;
            for (var index = 0; index < count; index++)
            {
                var parameter = signature.ParameterTypes[index];
                var reference = parameter as ByReferenceTypeSignature;
                if (!TryGetAddressElement(reference?.BaseType ?? parameter, out call.Parameters[index])) return false;
                call.References[index] = reference != null;
                var expected = reference == null ? call.Parameters[index] : call.Parameters[index].MakeByReferenceType();
                if (!SignatureComparer.Default.Equals(bridge.Target.Parameters[index].ParameterType, expected)) return false;
                var origins = consumed[index + offset];
                if (origins != null)
                {
                    if (reference == null || !TryGetWritableCallLocation(origins, reference.BaseType, out _)) return false;
                    call.NativeReferences[index] = true;
                    hasNativeLocation = true;
                }
                else if (!NativeValueArgumentMatches(instruction, count - index - 1, parameter)) return false;
            }
            if (!hasNativeLocation) return false;
            use = new ReferenceUse { Call = call };
            return true;
        }

        private CilInstruction EmitScopedInternalCall(ReferenceCall call)
        {
            var importer = imports.Module.DefaultImporter;
            var location = importer.ImportTypeSignature(typeof(InteropReferenceLocation));
            var arguments = new CilLocalVariable[call.Parameters.Length];
            var first = body.Add(CilOpCodes.Nop);
            for (var index = arguments.Length - 1; index >= 0; index--)
            {
                var parameter = call.NativeReferences[index] ? location : call.References[index]
                    ? call.Parameters[index].MakeByReferenceType() : call.Parameters[index];
                arguments[index] = AddArrayLocal(parameter);
                if (!call.References[index]) EmitArrayStoreConversion(ArrayStorageType(call.Parameters[index]));
                body.Add(CilOpCodes.Stloc, arguments[index]);
            }
            CilLocalVariable receiver = null;
            if (!call.InternalCall.Original.IsStatic)
            {
                receiver = AddArrayLocal(call.Owner);
                body.Add(CilOpCodes.Stloc, receiver);
                body.Add(CilOpCodes.Ldloc, receiver);
            }
            foreach (var argument in arguments) body.Add(CilOpCodes.Ldloc, argument);
            body.Add(CilOpCodes.Call, importer.ImportMethod(call.InternalCall.GetScopedAdapter(call.NativeReferences)));
            if (receiver != null) EmitAddressKeepAlive(receiver);
            return first;
        }
    }
}
