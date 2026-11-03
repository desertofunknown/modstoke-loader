// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryPlanIntPtrReferenceToInt32(CilInstruction instruction, IMethodDescriptor method,
            ReferenceOrigins[] consumed, out ReferenceUse use)
        {
            use = null;
            var signature = method.Signature;
            var source = sourceMethod.DeclaringModule.CorLibTypeFactory;
            var target = imports.Module.CorLibTypeFactory;
            if (IntPtr.Size != 8 || instruction.OpCode.Code != CilCode.Call || method.Name != "ToInt32" ||
                method.DeclaringType?.ToTypeSignature() is not CorLibTypeSignature { ElementType: ElementType.I } ||
                !SignatureComparer.Default.Equals(method.DeclaringType, source.IntPtr.Type) ||
                !InteropDelegateContracts.HasStandardConvention(signature) || !signature.HasThis ||
                signature.GenericParameterCount != 0 || signature.ParameterTypes.Count != 0 ||
                signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                !SignatureComparer.Default.Equals(signature.ReturnType.ToTypeDefOrRef(), source.Int32.Type) ||
                consumed.Length != 1 || !TryGetReferenceLocation(consumed[0], out var location, out _) ||
                !SignatureComparer.Default.Equals(location.SourceElement, source.IntPtr) ||
                !SignatureComparer.Default.Equals(location.Element, target.IntPtr) ||
                !location.UsesNativeValueAccess && !location.IsArray && location.Getter == null)
                return false;

            var type = context.CorLib.TryGetTypeByName("System.IntPtr");
            if (type?.OriginalType == null || !type.NewType.IsValueType || !type.NewType.IsExplicitLayout ||
                type.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                type.NewType.GenericParameters.Count != 0 ||
                type.NewType.ClassLayout is { ClassSize: > 0 } layout && layout.ClassSize != IntPtr.Size)
                return false;
            var fields = type.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != 1 || fields[0].FieldOffset != 0 ||
                !SignatureComparer.Default.Equals(fields[0].Signature?.FieldType, target.IntPtr))
                return false;

            InteropMethodContext mapped = null;
            foreach (var candidate in type.Methods)
            {
                var original = candidate.OriginalMethod;
                if (original.Name != "ToInt32" || original.IsStatic || original.IsVirtual || original.IsAbstract ||
                    !InteropDelegateContracts.HasStandardConvention(original.Signature) || !original.Signature.HasThis ||
                    original.Signature.GenericParameterCount != 0 || original.Signature.ParameterTypes.Count != 0 ||
                    original.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.I4 })
                    continue;
                if (mapped != null) return false;
                mapped = candidate;
            }
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.Rva == 0 ||
                mapped.NewMethod.IsStatic || !mapped.NewMethod.IsPublic || !mapped.NewMethod.Signature.HasThis ||
                mapped.NewMethod.Signature.GenericParameterCount != 0 ||
                mapped.NewMethod.Signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(mapped.NewMethod.Signature.ReturnType, target.Int32))
                return false;

            use = new ReferenceUse { Call = new ReferenceCall
            {
                IntPtrToInt32Location = location,
                IntPtrToInt32Method = mapped.NewMethod
            } };
            return true;
        }

        private CilInstruction EmitIntPtrReferenceToInt32(ReferenceCall call)
        {
            var importer = imports.Module.DefaultImporter;
            var receiver = importer.ImportTypeSignature(call.IntPtrToInt32Method.DeclaringType.ToTypeSignature());
            var carrier = AddArrayLocal(importer.ImportTypeSignature(typeof(InteropReferenceLocation)));
            var snapshot = AddArrayLocal(receiver);
            var first = body.Add(CilOpCodes.Stloc, carrier);
            EmitReferenceAccess(call.IntPtrToInt32Location, carrier, null);
            // Mono and CLR narrowing differ. Preserve every bit and call the game's native
            // conversion on the verified one-pointer value layout, without aliasing its field.
            var bitCast = (IMethodDefOrRef)importer.ImportMethod(typeof(Unsafe).GetMethod(nameof(Unsafe.BitCast)));
            body.Add(CilOpCodes.Call, bitCast.MakeGenericInstanceMethod(imports.Module.CorLibTypeFactory.IntPtr, receiver));
            body.Add(CilOpCodes.Stloc, snapshot);
            body.Add(CilOpCodes.Ldloca, snapshot);
            body.Add(CilOpCodes.Call, importer.ImportMethod(call.IntPtrToInt32Method));
            return first;
        }
    }
}
