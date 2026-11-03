// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private CilInstruction EmitReferenceCall(ReferenceCall call)
        {
            if (call.ManagedAdapter != null)
                return body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(call.ManagedAdapter));
            if (call.ConstrainedReceiver != null)
            {
                var dispatch = body.Add(CilOpCodes.Ldstr, call.MethodPointerName);
                var helper = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                    .GetMethod(nameof(InteropGenericValueSupport.ConstrainedInterfaceReference)));
                body.Add(CilOpCodes.Call, helper.MakeGenericInstanceMethod(call.ConstrainedReceiver, call.Owner, call.Parameters[0]));
                return dispatch;
            }
            if (call.Atomic != null) return EmitAtomicReferenceCall(call.Atomic);
            if (call.Int32ToStringLocation != null) return EmitInt32ReferenceToString(call.Int32ToStringLocation);
            if (call.IntPtrToInt32Location != null) return EmitIntPtrReferenceToInt32(call);
            if (call.InternalCall != null) return EmitScopedInternalCall(call);
            if (call.ValueMethod != null || call.MappedMethod != null) return EmitScopedNativeCall(call);
            var importer = imports.Module.DefaultImporter;
            var carrier = importer.ImportTypeSignature(typeof(InteropReferenceLocation));
            var arguments = new CilLocalVariable[call.Parameters.Length];
            var first = body.Add(CilOpCodes.Nop);
            for (var index = arguments.Length - 1; index >= 0; index--)
            {
                arguments[index] = AddArrayLocal(call.References[index] ? carrier : call.Parameters[index]);
                if (!call.References[index]) EmitArrayStoreConversion(ArrayStorageType(call.Parameters[index]));
                body.Add(CilOpCodes.Stloc, arguments[index]);
            }
            CilLocalVariable receiver = null;
            if (call.ReferenceReceiver || call.Receiver != null)
            {
                receiver = AddArrayLocal(call.ReferenceReceiver ? carrier : call.Receiver);
                body.Add(CilOpCodes.Stloc, receiver);
            }
            var values = AddArrayLocal(importer.ImportTypeSignature(typeof(object[])));
            body.Add(CilOpCodes.Ldc_I4, arguments.Length);
            body.Add(CilOpCodes.Newarr, imports.Module.CorLibTypeFactory.Object.ToTypeDefOrRef());
            body.Add(CilOpCodes.Stloc, values);
            var box = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropGenericValueSupport)
                .GetMethod(nameof(InteropGenericValueSupport.Box)));
            var nativeObject = context.CorLib.GetTypeByName("System.Object").NewType.ToTypeSignature();
            for (var index = 0; index < arguments.Length; index++)
            {
                body.Add(CilOpCodes.Ldloc, values);
                body.Add(CilOpCodes.Ldc_I4, index);
                body.Add(CilOpCodes.Ldloc, arguments[index]);
                // Existing native boxes already carry exact value storage, including Nullable.
                // Reboxing them would copy unnecessarily and unwrap Nullable<T> to T or null.
                if (!call.References[index] && !IsNativeArrayReference(call.Parameters[index]))
                    body.Add(CilOpCodes.Call, box.MakeGenericInstanceMethod(call.Parameters[index], nativeObject));
                body.Add(CilOpCodes.Stelem_Ref);
            }
            EmitReferenceRuntimeType(call.Owner);
            body.Add(CilOpCodes.Ldstr, call.MethodPointerName);
            if (receiver == null) body.Add(CilOpCodes.Ldnull);
            else body.Add(CilOpCodes.Ldloc, receiver);
            if (!call.Constructor) body.Add(CilOpCodes.Ldc_I4, call.VirtualDispatch ? 1 : 0);
            body.Add(CilOpCodes.Ldloc, values);
            body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(InteropNativeReferenceCall)
                .GetMethod(call.Constructor ? nameof(InteropNativeReferenceCall.Construct) : nameof(InteropNativeReferenceCall.Invoke))));
            if (call.Constructor) return first;
            if (call.Result == null) body.Add(CilOpCodes.Pop);
            else
            {
                var unbox = (IMethodDefOrRef)importer.ImportMethod(typeof(InteropGenericValueSupport)
                    .GetMethod(nameof(InteropGenericValueSupport.UnboxAny)));
                body.Add(CilOpCodes.Call, unbox.MakeGenericInstanceMethod(call.Result));
            }
            return first;
        }

        private CilInstruction EmitInt32ReferenceToString(AddressOperation location)
        {
            var importer = imports.Module.DefaultImporter;
            var carrier = AddArrayLocal(importer.ImportTypeSignature(typeof(InteropReferenceLocation)));
            var snapshot = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
            var first = body.Add(CilOpCodes.Stloc, carrier);
            EmitReferenceAccess(location, carrier, null);
            body.Add(CilOpCodes.Stloc, snapshot);
            body.Add(CilOpCodes.Ldloca, snapshot);
            // Keep CLR's current-culture formatting; the native location never escapes or receives a writeback.
            body.Add(CilOpCodes.Call, importer.ImportMethod(typeof(int).GetMethod(nameof(int.ToString), Type.EmptyTypes)));
            return first;
        }

        private void EmitReferenceRuntimeType(TypeSignature type)
        {
            body.Add(CilOpCodes.Ldtoken, type.ToTypeDefOrRef());
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))));
        }
    }
}
