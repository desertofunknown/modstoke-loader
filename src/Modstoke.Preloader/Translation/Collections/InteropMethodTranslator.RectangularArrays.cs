// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, CilInstruction> rectangularAddressConsumers =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly HashSet<CilInstruction> consumedRectangularAddresses =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        private readonly Dictionary<CilInstruction, Dictionary<int, TypeSignature>> rectangularConversions =
            new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

        private void FindRectangularArrayOperands(CilMethodBody original)
        {
            var boundaries = new HashSet<CilInstruction>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var instruction in original.Instructions)
            {
                if (instruction.Operand is ICilLabel label) AddBoundary(label);
                else if (instruction.Operand is IReadOnlyList<ICilLabel> labels)
                    foreach (var targetLabel in labels) AddBoundary(targetLabel);
            }
            foreach (var handler in original.ExceptionHandlers)
            {
                AddBoundary(handler.TryStart);
                AddBoundary(handler.TryEnd);
                AddBoundary(handler.HandlerStart);
                AddBoundary(handler.HandlerEnd);
                AddBoundary(handler.FilterStart);
            }
            for (var index = 0; index + 1 < original.Instructions.Count; index++)
            {
                var call = original.Instructions[index];
                var consumer = original.Instructions[index + 1];
                if (call.OpCode.Code is CilCode.Call or CilCode.Callvirt &&
                    call.Operand is IMethodDescriptor method && method.Name == "Address" &&
                    method.DeclaringType?.ToTypeSignature() is ArrayTypeSignature && !boundaries.Contains(consumer))
                    rectangularAddressConsumers.Add(call, consumer);
            }

            void AddBoundary(ICilLabel label)
            {
                if (label is CilInstructionLabel { Instruction: not null } entry)
                    boundaries.Add(entry.Instruction);
            }
        }

        // Hook before ordinary instruction translation. A true result with a null instruction is a rejection.
        private bool TryTranslateRectangularInstruction(CilInstruction instruction, out CilInstruction translated)
        {
            translated = null;
            if (referenceProducers.TryGetValue(instruction, out var reference) && reference.RectangularRank != 0)
                return TryTranslateScopedReference(instruction, out translated);
            if (rectangularConversions.Remove(instruction, out var conversions))
            {
                var first = EmitRectangularConversions(instruction, conversions);
                if (first != null && TranslateInstruction(instruction) != null)
                    translated = first;
                return true;
            }
            if (consumedRectangularAddresses.Contains(instruction))
            {
                translated = body.Add(CilOpCodes.Nop);
                return true;
            }
            if (instruction.OpCode.Code is CilCode.Castclass or CilCode.Isinst or CilCode.Unbox_Any &&
                instruction.Operand is ITypeDefOrRef castType && castType.ToTypeSignature() is ArrayTypeSignature castArray)
            {
                translated = TranslateRectangularCast(instruction, castArray);
                return true;
            }
            if (instruction.Operand is IMethodDescriptor method &&
                method.DeclaringType?.ToTypeSignature() is ArrayTypeSignature array)
            {
                translated = TranslateRectangularMethod(instruction.OpCode, method, array);
                return true;
            }
            if (instruction.OpCode.Code == CilCode.Ldlen && TryGetStackValue(instruction, 0, out var value) &&
                value.Type is ArrayTypeSignature shape)
            {
                if (!InteropRectangularArraySupport.TryResolve(context, imports, shape, out _, out var reason))
                    translated = FailInstruction("unsupported-rectangular-array", reason);
                else
                {
                    translated = body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                        InteropRectangularArraySupport.NativeMethod(context, "get_Length", false)));
                    body.Add(CilOpCodes.Conv_U);
                }
                return true;
            }
            return false;
        }

        private CilInstruction TranslateRectangularCast(CilInstruction instruction, ArrayTypeSignature array)
        {
            if (!InteropRectangularArraySupport.TryResolve(context, imports, array, out var rewritten, out var reason))
                return FailInstruction("unsupported-rectangular-array", reason);
            if (!TryGetStackValue(instruction, 0, out var value) || (!value.IsNull &&
                !IsNativeArrayReference(resolver.Resolve(value.Type))))
                return FailInstruction("unsupported-rectangular-cast", "Rectangular casts require a native object, not CLR-only storage.");
            var first = body.Add(CilOpCodes.Dup);
            var nonNull = new CilInstructionLabel();
            var done = new CilInstructionLabel();
            body.Add(CilOpCodes.Brtrue, nonNull);
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Ldnull);
            body.Add(CilOpCodes.Br, done);
            var native = AddArrayLocal(imports.Il2CppObjectBase);
            var nativeClass = AddArrayLocal(imports.Module.CorLibTypeFactory.IntPtr);
            var mismatch = new CilInstructionLabel();
            nonNull.Instruction = body.Add(CilOpCodes.Stloc, native);
            body.Add(CilOpCodes.Ldloc, native);
            body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
            body.Add(CilOpCodes.Stloc, nativeClass);
            body.Add(CilOpCodes.Ldloc, nativeClass);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_rank))));
            body.Add(CilOpCodes.Ldc_I4, array.Rank);
            body.Add(CilOpCodes.Bne_Un, mismatch);
            body.Add(CilOpCodes.Ldloc, nativeClass);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_class_get_type.Value);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_type_get_type))));
            body.Add(CilOpCodes.Ldc_I4, (int)ElementType.Array);
            body.Add(CilOpCodes.Bne_Un, mismatch);
            if (!InteropRectangularArraySupport.Find(context, rewritten).IsReferenceElement)
            {
                body.Add(CilOpCodes.Ldloc, nativeClass);
                body.Add(CilOpCodes.Ldsfld, imports.Module.DefaultImporter.ImportField(new MemberReference(
                    new GenericInstanceTypeSignature(imports.Il2CppClassPointerStore.ToTypeDefOrRef(), false, rewritten).ToTypeDefOrRef(),
                    "NativeClassPtr", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr))));
                body.Add(CilOpCodes.Bne_Un, mismatch);
            }
            else
            {
                body.Add(CilOpCodes.Ldloc, nativeClass);
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_get_element_class))));
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    typeof(Il2CppInterop.Runtime.IL2CPP).GetMethod(nameof(Il2CppInterop.Runtime.IL2CPP.il2cpp_class_is_valuetype))));
                body.Add(CilOpCodes.Brtrue, mismatch);
            }
            var cast = instruction.OpCode.Code == CilCode.Isinst ? imports.Il2CppObjectBase_TryCast.Value :
                imports.Il2CppObjectBase_Cast.Value;
            body.Add(CilOpCodes.Ldloc, native);
            body.Add(CilOpCodes.Call,
                imports.Module.DefaultImporter.ImportMethod(cast.MakeGenericInstanceMethod(rewritten)));
            body.Add(CilOpCodes.Br, done);
            mismatch.Instruction = body.Add(CilOpCodes.Nop);
            if (instruction.OpCode.Code == CilCode.Isinst)
                body.Add(CilOpCodes.Ldnull);
            else
                EmitArrayException(typeof(System.InvalidCastException));
            done.Instruction = body.Add(CilOpCodes.Nop);
            return first;
        }

        private CilInstruction TranslateRectangularMethod(CilOpCode code, IMethodDescriptor method, ArrayTypeSignature array)
        {
            if (!InteropRectangularArraySupport.TryResolve(context, imports, array, out var type, out var reason))
                return FailInstruction("unsupported-rectangular-array", reason);
            var wrapper = InteropRectangularArraySupport.Find(context, type);
            var signature = method.Signature;
            if (wrapper == null || signature == null || !signature.HasThis || signature.GenericParameterCount != 0 ||
                code.Code is not (CilCode.Newobj or CilCode.Call or CilCode.Callvirt))
                return FailInstruction("unsupported-rectangular-method", "Only concrete rectangular array construction and calls are supported.");

            var count = signature.ParameterTypes.Count;
            var name = method.Name?.ToString();
            if (name == ".ctor" && code.Code == CilCode.Newobj &&
                signature.ReturnType.ElementType == ElementType.Void &&
                signature.ParameterTypes.All(parameter => parameter.ElementType == ElementType.I4))
            {
                var constructor = count == wrapper.Rank ? wrapper.LengthConstructor :
                    count == wrapper.Rank * 2 ? wrapper.BoundsConstructor : null;
                return constructor == null
                    ? FailInstruction("unsupported-rectangular-constructor", "Expected lengths or interleaved lower-bound/length pairs.")
                    : body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(constructor));
            }

            if (code.Code == CilCode.Newobj)
                return FailInstruction("unsupported-rectangular-constructor", "The rectangular constructor signature is not supported.");
            if (!TryGetStackValue(currentInstruction, count, out var receiver) ||
                (!receiver.IsNull && !CanConvertRectangularShape(receiver.Type, array)))
                return FailInstruction("unsupported-rectangular-receiver", "The receiver must retain the same element type and rank.");

            var setter = name == "Set";
            var indices = setter ? count - 1 : count;
            if (name is "Get" or "Set" or "Address")
            {
                if (indices != wrapper.Rank || signature.ParameterTypes.Take(indices).Any(parameter =>
                        parameter.ElementType != ElementType.I4) ||
                    (setter && (!SignatureComparer.Default.Equals(signature.ParameterTypes[count - 1], array.BaseType) ||
                                signature.ReturnType.ElementType != ElementType.Void)) ||
                    (name == "Get" && !SignatureComparer.Default.Equals(signature.ReturnType, array.BaseType)) ||
                    (name == "Address" && (signature.ReturnType is not ByReferenceTypeSignature address ||
                                           !SignatureComparer.Default.Equals(address.BaseType, array.BaseType))))
                    return FailInstruction("unsupported-rectangular-accessor", "The accessor signature does not match its rectangular element and rank.");
                if (name == "Address")
                    return TranslateRectangularAddress(wrapper, array);
                return body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(setter ? wrapper.Set : wrapper.Get));
            }

            // Some compilers put inherited shape members on the array TypeSpec instead of System.Array.
            var shapeQuery = (count == 0 && name is "get_Length" or "get_Rank") ||
                (count == 1 && signature.ParameterTypes[0].ElementType == ElementType.I4 &&
                 name is "GetLength" or "GetLowerBound" or "GetUpperBound");
            if (shapeQuery && signature.ReturnType.ElementType == ElementType.I4)
                return body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                    InteropRectangularArraySupport.NativeMethod(context, name, false,
                        count == 0 ? System.Array.Empty<string>() : new[] { "System.Int32" })));
            return FailInstruction("unsupported-rectangular-method", "The rectangular method has no verified native lowering: " + name);
        }

        private CilInstruction TranslateRectangularAddress(InteropRectangularArraySupport.Wrapper wrapper, ArrayTypeSignature array)
        {
            if (!rectangularAddressConsumers.TryGetValue(currentInstruction, out var consumer))
                return FailInstruction("unsupported-rectangular-address-escape", "A native element alias requires a proven owner lifetime; it cannot escape.");
            var code = consumer.OpCode.Code;
            var typed = consumer.Operand is ITypeDefOrRef operand &&
                SignatureComparer.Default.Equals(operand.ToTypeSignature(), array.BaseType);
            if (code == CilCode.Initobj && typed)
            {
                var first = EmitRectangularReferenceCoordinates(wrapper.Type.ToTypeSignature(), wrapper.Rank,
                    out var owner, out var indices, out _);
                body.Add(CilOpCodes.Ldloc, owner);
                body.Add(CilOpCodes.Call, imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value);
                body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_get_class.Value);
                var classStore = imports.Il2CppClassPointerStore.MakeGenericInstanceType(owner.VariableType);
                body.Add(CilOpCodes.Ldsfld, imports.Module.DefaultImporter.ImportField(new MemberReference(
                    classStore.ToTypeDefOrRef(), "NativeClassPtr", new FieldSignature(imports.Module.CorLibTypeFactory.IntPtr))));
                var exact = new CilInstructionLabel();
                body.Add(CilOpCodes.Beq, exact);
                body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(
                    typeof(System.ArrayTypeMismatchException).GetConstructor(System.Type.EmptyTypes)));
                body.Add(CilOpCodes.Throw);
                // Address acquisition must complete before default construction can allocate or fail.
                var zero = AddArrayLocal(wrapper.Element);
                exact.Instruction = body.Add(CilOpCodes.Ldloca, zero);
                var initialize = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropGenericValueSupport).GetMethod(nameof(InteropGenericValueSupport.InitializeDefault)));
                body.Add(CilOpCodes.Call, initialize.MakeGenericInstanceMethod(wrapper.Element));
                body.Add(CilOpCodes.Ldloc, owner);
                foreach (var index in indices) body.Add(CilOpCodes.Ldloc, index);
                body.Add(CilOpCodes.Ldloc, zero);
                body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(wrapper.AddressSet));
                consumedRectangularAddresses.Add(consumer);
                return first;
            }
            var load = code switch
            {
                CilCode.Ldind_I1 => CilCode.Ldelem_I1,
                CilCode.Ldind_U1 => CilCode.Ldelem_U1,
                CilCode.Ldind_I2 => CilCode.Ldelem_I2,
                CilCode.Ldind_U2 => CilCode.Ldelem_U2,
                CilCode.Ldind_I4 => CilCode.Ldelem_I4,
                CilCode.Ldind_U4 => CilCode.Ldelem_U4,
                CilCode.Ldind_I8 => CilCode.Ldelem_I8,
                CilCode.Ldind_I => CilCode.Ldelem_I,
                _ => CilCode.Nop
            };
            var storage = ArrayStorageType(array.BaseType);
            if ((code == CilCode.Ldobj && typed) || (load != CilCode.Nop && ArrayOpcodeMatches(load, array.BaseType, storage)) ||
                (code == CilCode.Ldind_R4 && storage == ElementType.R4) || (code == CilCode.Ldind_R8 && storage == ElementType.R8) ||
                (code == CilCode.Ldind_Ref && wrapper.IsReferenceElement))
            {
                var first = body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(wrapper.AddressGet));
                EmitArrayLoadConversion(load);
                consumedRectangularAddresses.Add(consumer);
                return first;
            }
            return FailInstruction("unsupported-rectangular-address-escape",
                "Only an immediate, same-region typed load or representable initobj can consume a rectangular element address.");
        }

        private static bool SameRectangularShape(TypeSignature left, TypeSignature right) =>
            left is ArrayTypeSignature a && right is ArrayTypeSignature b && a.Rank == b.Rank &&
            SignatureComparer.Default.Equals(a.BaseType, b.BaseType);

        private bool CanConvertRectangularShape(TypeSignature source, TypeSignature destination) =>
            SameRectangularShape(source, destination) ||
            source is ArrayTypeSignature from && destination is ArrayTypeSignature to && from.Rank == to.Rank &&
            InteropRectangularArraySupport.IsReferenceElement(context, from.BaseType) &&
            InteropRectangularArraySupport.IsReferenceElement(context, to.BaseType) &&
            InteropRectangularArraySupport.CanRepresent(context, from, out _) &&
            InteropRectangularArraySupport.CanRepresent(context, to, out _);

        // Hook alongside vector assignment validation; CLR reference assignability cannot substitute for native rank.
        private bool ValidateRectangularArrayAssignment(CilInstruction instruction)
        {
            if (instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt or CilCode.Newobj &&
                instruction.Operand is IMethodDescriptor method && method.Signature != null)
            {
                for (var index = 0; index < method.Signature.ParameterTypes.Count; index++)
                    if (!ValidateRectangularDestination(instruction,
                            method.Signature.ParameterTypes[index].InstantiateGenericTypes(GenericContext.FromMethod(method)),
                            method.Signature.ParameterTypes.Count - index - 1))
                        return false;
                if (instruction.OpCode.Code != CilCode.Newobj && method.Signature.HasThis &&
                    method.DeclaringType?.ToTypeSignature() is ArrayTypeSignature receiver)
                {
                    if (!ValidateRectangularDestination(instruction, receiver, method.Signature.ParameterTypes.Count))
                        return false;
                    if (method.Name == "Set" && InteropRectangularArraySupport.TryResolve(context, imports, receiver,
                            out var rewritten, out _))
                    {
                        var wrapper = InteropRectangularArraySupport.Find(context, rewritten);
                        if (!TryGetStackValue(instruction, 0, out var value))
                            return Fail("unsupported-rectangular-value", "The stored element type is not proven.", instruction);
                        if (wrapper.IsReferenceElement)
                        {
                            if (!value.IsNull && (value.Type is GenericParameterSignature ||
                                (!InteropRectangularArraySupport.IsReferenceElement(context, value.Type) &&
                                 !(value.Type is ArrayBaseTypeSignature && IsNativeArrayReference(resolver.Resolve(value.Type))))))
                                return Fail("unsupported-rectangular-value", "A reference store needs a concrete native reference or string.", instruction);
                            AddRectangularConversion(instruction, 0, wrapper.Element);
                        }
                        else if (!ArrayValueMatches(value, receiver.BaseType, CilCode.Stelem))
                            return Fail("unsupported-rectangular-value", "The stored value does not match the native value element.", instruction);
                    }
                }
            }
            TypeSignature destination = instruction.OpCode.Code switch
            {
                CilCode.Stloc when instruction.Operand is CilLocalVariable local => local.VariableType,
                CilCode.Starg when instruction.Operand is Parameter argument => argument.ParameterType,
                CilCode.Stfld or CilCode.Stsfld when instruction.Operand is IFieldDescriptor field =>
                    field.Signature?.FieldType.InstantiateGenericTypes(GenericContext.FromField(field)),
                CilCode.Stobj when instruction.Operand is ITypeDefOrRef stored => stored.ToTypeSignature(),
                CilCode.Stind_Ref when TryGetStackValue(instruction, 1, out var address) && address.Type is ByReferenceTypeSignature reference => reference.BaseType,
                CilCode.Ret => sourceReturnType,
                _ => null
            };
            return ValidateRectangularDestination(instruction, destination, 0);
        }

        private bool ValidateRectangularDestination(CilInstruction instruction, TypeSignature destination, int depth)
        {
            if (destination is not ArrayTypeSignature)
                return true;
            if (TryGetStackValue(instruction, depth, out var value))
            {
                if (value.IsNull || SameRectangularShape(value.Type, destination))
                    return true;
                if (CanConvertRectangularShape(value.Type, destination) &&
                    InteropRectangularArraySupport.TryResolve(context, imports, (ArrayTypeSignature)destination,
                        out var rewritten, out _))
                {
                    AddRectangularConversion(instruction, depth, rewritten);
                    return true;
                }
            }
            return Fail("unsupported-rectangular-assignment", "Rectangular assignment requires the same native rank and value element, or checked reference covariance.", instruction);
        }

        private void AddRectangularConversion(CilInstruction instruction, int depth, TypeSignature targetType)
        {
            if (!rectangularConversions.TryGetValue(instruction, out var conversions))
                rectangularConversions.Add(instruction, conversions = new Dictionary<int, TypeSignature>());
            conversions[depth] = imports.Module.DefaultImporter.ImportTypeSignature(targetType);
        }

        private CilInstruction EmitRectangularConversions(CilInstruction instruction, Dictionary<int, TypeSignature> conversions)
        {
            var depth = conversions.Keys.Max();
            var spilled = new CilLocalVariable[depth + 1];
            for (var index = 0; index <= depth; index++)
            {
                if (!TryGetStackValue(instruction, index, out var value) ||
                    value.Type is ByReferenceTypeSignature or PointerTypeSignature)
                    return FailInstruction("unsupported-rectangular-conversion-stack",
                        "Covariance conversion cannot spill an unproven stack value or native alias.");
                if (value.IsNull)
                    continue;
                var rewritten = resolver.Resolve(value.Type);
                if (rewritten == null)
                    return FailInstruction("unsupported-rectangular-conversion-stack", "The conversion stack type is unavailable.");
                spilled[index] = AddArrayLocal(rewritten);
            }
            CilInstruction first = null;
            for (var index = 0; index <= depth; index++)
            {
                var spill = spilled[index] == null ? body.Add(CilOpCodes.Pop) : body.Add(CilOpCodes.Stloc, spilled[index]);
                first ??= spill;
            }
            for (var index = depth; index >= 0; index--)
            {
                if (spilled[index] == null)
                    body.Add(CilOpCodes.Ldnull);
                else
                    body.Add(CilOpCodes.Ldloc, spilled[index]);
                if (conversions.TryGetValue(index, out var targetType))
                {
                    var method = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport).GetMethod(
                        nameof(InteropGenericValueSupport.CastReference)));
                    body.Add(CilOpCodes.Call, method.MakeGenericInstanceMethod(targetType));
                }
            }
            return first;
        }
    }
}
