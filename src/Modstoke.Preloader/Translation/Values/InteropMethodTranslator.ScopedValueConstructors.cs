// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private CilInstruction EmitScopedNativeCall(ReferenceCall call)
        {
            var location = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            var arguments = new CilLocalVariable[call.Parameters.Length];
            var first = body.Add(CilOpCodes.Nop);
            for (var index = arguments.Length - 1; index >= 0; index--)
            {
                var parameter = call.NativeReferences[index] ? location : call.References[index]
                    ? call.Parameters[index].MakeByReferenceType() : call.Parameters[index];
                arguments[index] = AddArrayLocal(parameter);
                if (call.NativeInterfaceArguments?[index] == true)
                    body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.CastReference))))
                        .MakeGenericInstanceMethod(call.Parameters[index]));
                if (!call.References[index]) EmitArrayStoreConversion(ArrayStorageType(call.Parameters[index]));
                body.Add(CilOpCodes.Stloc, arguments[index]);
            }
            var mapped = call.ValueMethod ?? call.MappedMethod;
            CilLocalVariable receiver = null;
            if (!mapped.OriginalMethod.IsStatic)
            {
                receiver = AddArrayLocal(call.ReferenceReceiver ? location : call.Owner);
                if (call.CreatesReceiver)
                {
                    // Allocate zeroed native value backing without running any source default constructor.
                    body.Add(CilOpCodes.Ldnull);
                    body.Add(CilOpCodes.Stloc, receiver);
                    body.Add(CilOpCodes.Ldloca, receiver);
                    EmitValueStorageCall(nameof(InteropNativeValueStorage.Receiver), call.Owner);
                }
                else if (!call.ReferenceReceiver && call.MappedMethod == null)
                    EmitValueStorageCall(nameof(InteropNativeValueStorage.Receiver), call.Owner);
                body.Add(CilOpCodes.Stloc, receiver);
                body.Add(CilOpCodes.Ldloc, receiver);
            }
            foreach (var argument in arguments) body.Add(CilOpCodes.Ldloc, argument);
            var initializer = InteropMethodBodies.GetScopedNativeCallAdapter(
                mapped, call.ReferenceReceiver, call.NativeReferences);
            var reference = new MemberReference(call.Owner.ToTypeDefOrRef(), initializer.Name, initializer.Signature);
            IMethodDescriptor invocation = call.MethodArguments == null ? reference :
                reference.MakeGenericInstanceMethod(call.MethodArguments);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(invocation));
            if (receiver != null) EmitAddressKeepAlive(receiver);
            if (call.CreatesReceiver) body.Add(CilOpCodes.Ldloc, receiver);
            return first;
        }
    }
}
