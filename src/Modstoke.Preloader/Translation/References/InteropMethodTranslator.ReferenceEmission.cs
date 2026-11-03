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
        private bool TryTranslateScopedReference(CilInstruction instruction, out CilInstruction copy)
        {
            copy = null;
            if (referenceFailure != null)
            {
                Fail("unsupported-scoped-reference", referenceFailureDetail, referenceFailure);
                return true;
            }
            if (referenceProducers.TryGetValue(instruction, out var location))
            {
                copy = location.IsScopedParameter
                    ? body.Add(CilOpCodes.Ldarg, target.Parameters[location.ScopedParameterIndex])
                    : EmitReferenceProducer(location);
                return true;
            }
            if (referencePrefixes.Contains(instruction))
            {
                copy = body.Add(CilOpCodes.Nop);
                return true;
            }
            if (!referenceUses.TryGetValue(instruction, out var use)) return false;
            copy = EmitReferenceUse(instruction, use);
            return true;
        }

        private CilInstruction EmitReferenceProducer(AddressOperation location)
        {
            if (location.RectangularRank != 0) return EmitRectangularReferenceProducer(location);
            if (location.IsNestedField)
                return EmitNestedReferenceProducer(location);
            // Checks happen at the original address acquisition, including when its only use
            // is pop or a later store whose value producer throws. Each acquisition is immutable.
            CilInstruction first = null;
            if (location.ReturnedOwnerField is { } ownerField)
            {
                if (location.ReturnedMethodPointerName != null)
                {
                    first = body.Add(CilOpCodes.Ldstr, location.ReturnedMethodPointerName);
                    body.Add(CilOpCodes.Ldstr, ownerField.NativeFieldPointerName);
                    body.Add(CilOpCodes.Ldstr, location.NativeFieldPointerName);
                    var acquireField = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropNativeReferenceReturn).GetMethod(nameof(InteropNativeReferenceReturn.Acquire)));
                    body.Add(CilOpCodes.Call, acquireField.MakeGenericInstanceMethod(
                        ownerField.OwnerType, ownerField.Element, location.Element));
                    return first;
                }
                first = EmitNestedReferenceProducer(ownerField);
                var read = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceObject).GetMethod(nameof(InteropNativeReferenceObject.Read)));
                body.Add(CilOpCodes.Call, read.MakeGenericInstanceMethod(ownerField.Element));
            }
            var acquire = EmitAddressOwner(location);
            first ??= acquire;
            if (location.Owner == null) body.Add(CilOpCodes.Ldnull);
            else body.Add(CilOpCodes.Ldloc, location.Owner);
            if (location.IsArray)
            {
                body.Add(CilOpCodes.Ldloc, location.Index);
                body.Add(IsNativeAggregateLocation(location) ? CilOpCodes.Conv_Ovf_I4 : CilOpCodes.Conv_I4);
            }
            else body.Add(CilOpCodes.Ldc_I4, 0);
            if (location.NativeFieldPointerName == null)
            {
                body.Add(CilOpCodes.Ldnull);
                body.Add(CilOpCodes.Ldnull);
            }
            else
            {
                EmitReferenceRuntimeType(location.OwnerType ?? location.StaticOwnerType);
                body.Add(CilOpCodes.Ldstr, location.NativeFieldPointerName);
            }
            body.Add(CilOpCodes.Ldc_I4, location.ReadOnly ? 1 : 0);
            body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropReferenceLocation).GetConstructor(new[]
                    { typeof(object), typeof(int), typeof(System.Type), typeof(string), typeof(bool) })));
            if (location.SourceElement is GenericParameterSignature)
            {
                var validate = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceStorage).GetMethod(nameof(InteropNativeReferenceStorage.Validate)));
                body.Add(CilOpCodes.Call, validate.MakeGenericInstanceMethod(location.Element));
            }
            else if (IsNativeAggregateLocation(location))
            {
                var validate = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceAggregate).GetMethod(nameof(InteropNativeReferenceAggregate.Validate)));
                body.Add(CilOpCodes.Call, validate.MakeGenericInstanceMethod(location.Element));
            }
            return first;
        }

        private CilInstruction EmitNestedReferenceProducer(AddressOperation location)
        {
            var first = body.Add(CilOpCodes.Ldstr, location.NativeFieldPointerName);
            body.Add(CilOpCodes.Ldc_I4, location.ReadOnly ? 1 : 0);
            var factory = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                typeof(InteropNativeReferenceStorage).GetMethod(nameof(InteropNativeReferenceStorage.Field)));
            body.Add(CilOpCodes.Call, factory.MakeGenericInstanceMethod(location.OwnerType, location.Element));
            return first;
        }

        private CilInstruction EmitReferenceUse(CilInstruction instruction, ReferenceUse use)
        {
            if (use.Call != null) return EmitReferenceCall(use.Call);
            if (use.NestedFieldRead != null || use.NestedFieldWrite != null)
            {
                var field = use.NestedFieldWrite ?? use.NestedFieldRead;
                var firstAccess = body.Add(CilOpCodes.Nop);
                CilLocalVariable storedValue = null;
                if (use.NestedFieldWrite != null)
                {
                    storedValue = AddArrayLocal(field.Element);
                    EmitArrayStoreConversion(ArrayStorageType(field.SourceElement));
                    body.Add(CilOpCodes.Stloc, storedValue);
                }
                EmitNestedReferenceProducer(field);
                var child = AddArrayLocal(imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation)));
                body.Add(CilOpCodes.Stloc, child);
                EmitReferenceAccess(field, child, storedValue);
                return firstAccess;
            }
            var code = instruction.OpCode.Code;
            var location = use.Location;
            var carrierType = imports.Module.DefaultImporter.ImportTypeSignature(typeof(InteropReferenceLocation));
            var carrier = AddArrayLocal(carrierType);
            CilLocalVariable value = null;
            var first = body.Add(CilOpCodes.Nop);
            if (IsNativeAggregateLocation(location) && (use.CopySource != null || code == CilCode.Initobj))
            {
                CilLocalVariable source = null;
                if (use.CopySource != null)
                {
                    source = AddArrayLocal(carrierType);
                    body.Add(CilOpCodes.Stloc, source);
                }
                body.Add(CilOpCodes.Stloc, carrier);
                body.Add(CilOpCodes.Ldloc, carrier);
                if (source != null) body.Add(CilOpCodes.Ldloc, source);
                var method = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceAggregate).GetMethod(source == null
                        ? nameof(InteropNativeReferenceAggregate.Initialize) : nameof(InteropNativeReferenceAggregate.Copy)));
                body.Add(CilOpCodes.Call, method.MakeGenericInstanceMethod(location.Element));
                return first;
            }
            if (use.CopySource != null)
            {
                var source = AddArrayLocal(carrierType);
                body.Add(CilOpCodes.Stloc, source);
                body.Add(CilOpCodes.Stloc, carrier);
                EmitReferenceAccess(use.CopySource, source, null);
                value = AddArrayLocal(location.Element);
                body.Add(CilOpCodes.Stloc, value);
            }
            else
            {
                if (IsScopedReferenceWrite(code))
                {
                    value = AddArrayLocal(location.Element);
                    if (code != CilCode.Initobj)
                    {
                        EmitArrayStoreConversion(ArrayStorageType(location.SourceElement));
                        body.Add(CilOpCodes.Stloc, value);
                    }
                }
                body.Add(CilOpCodes.Stloc, carrier);
                if (code == CilCode.Initobj)
                {
                    body.Add(CilOpCodes.Ldloca, value);
                    body.Add(CilOpCodes.Initobj, location.Element.ToTypeDefOrRef());
                }
            }
            EmitReferenceAccess(location, carrier, value);
            if (code == CilCode.Ldfld)
            {
                var snapshot = AddArrayLocal(location.Element);
                body.Add(CilOpCodes.Stloc, snapshot);
                body.Add(location.Element.IsValueType ? CilOpCodes.Ldloca : CilOpCodes.Ldloc, snapshot);
                if (TranslateField(CilOpCodes.Ldfld, (AsmResolver.DotNet.IFieldDescriptor)instruction.Operand) == null)
                    return null;
                if (!location.Element.IsValueType) EmitAddressKeepAlive(snapshot);
            }
            else if (value == null)
                EmitReferenceLoadConversion(code);
            return first;
        }

        private void EmitReferenceAccess(AddressOperation location, CilLocalVariable carrier, CilLocalVariable value)
        {
            if (IsNativeAggregateLocation(location))
            {
                body.Add(CilOpCodes.Ldloc, carrier);
                if (value != null) body.Add(CilOpCodes.Ldloc, value);
                var accessor = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceAggregate).GetMethod(value == null
                        ? nameof(InteropNativeReferenceAggregate.Read) : nameof(InteropNativeReferenceAggregate.Write)));
                body.Add(CilOpCodes.Call, accessor.MakeGenericInstanceMethod(location.Element));
                return;
            }
            if (location.UsesNativeValueAccess)
            {
                body.Add(CilOpCodes.Ldloc, carrier);
                if (value != null) body.Add(CilOpCodes.Ldloc, value);
                var helper = IsNativeReferenceValue(location) ? typeof(InteropNativeReferenceObject) : typeof(InteropNativeReferenceValue);
                var accessor = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    helper.GetMethod(value == null ? nameof(InteropNativeReferenceValue.Read) : nameof(InteropNativeReferenceValue.Write)));
                body.Add(CilOpCodes.Call, accessor.MakeGenericInstanceMethod(location.Element));
                return;
            }
            if (location.OwnerType != null)
            {
                body.Add(CilOpCodes.Ldloc, carrier);
                body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropReferenceLocation).GetProperty(nameof(InteropReferenceLocation.Owner)).GetMethod));
                // This is the CLR wrapper captured above, not an IL2CPP object cast.
                body.Add(CilOpCodes.Castclass, location.OwnerType.ToTypeDefOrRef());
            }
            if (location.IsArray)
            {
                body.Add(CilOpCodes.Ldloc, carrier);
                body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropReferenceLocation).GetProperty(nameof(InteropReferenceLocation.Index)).GetMethod));
            }
            if (value != null) body.Add(CilOpCodes.Ldloc, value);
            if (IsNativeReferenceArrayAddress(location))
            {
                if (value == null)
                {
                    body.Add(CilOpCodes.Ldloc, carrier);
                    body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                        typeof(InteropReferenceLocation).GetProperty(nameof(InteropReferenceLocation.IsReadOnly)).GetMethod));
                }
                EmitReferenceArrayAccessor(value != null, location.Element);
            }
            else if (location.IsArray) EmitArrayAccessor(value != null, location.Element);
            else body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                value == null ? location.Getter : location.Setter));
            // The descriptor roots the owner throughout access. Accessors own any native pin
            // cleanup; no raw native pointer survives the invocation.
            EmitAddressKeepAlive(carrier);
            if (value != null && !location.Element.IsValueType) EmitAddressKeepAlive(value);
        }
    }
}
