// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Collections;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private readonly Dictionary<CilInstruction, TypeSignature> arrayLocalConversions =
            new(ReferenceEqualityComparer.Instance);

        private CilInstruction TranslateArrayAccess(bool setter, TypeSignature explicitElement = null)
        {
            if (!TryGetArrayOperand(currentInstruction, setter ? 2 : 1, out var sourceArray,
                    out var rewrittenArray, out var element) ||
                !TryGetStackValue(currentInstruction, setter ? 1 : 0, out var index))
                return FailInstruction("unsupported-array-element-signature",
                    "The actual vector and index types are not proven on every incoming stack path.");

            var indexType = NumericStackValue(index.Type).Type;
            if (indexType?.ElementType is not (ElementType.I4 or ElementType.I))
                return FailInstruction("unsupported-array-index-signature", "Array index is not a proven Int32 or native integer.");
            if (explicitElement != null && !SignatureComparer.Default.Equals(explicitElement, element))
                return FailInstruction("unsupported-array-element-signature",
                    "The typed element operand differs from the actual vector element type.");

            var code = currentInstruction.OpCode.Code;
            var storage = ArrayStorageType(sourceArray.BaseType);
            if (!ArrayOpcodeMatches(code, sourceArray.BaseType, storage))
                return FailInstruction("unsupported-array-element-signature",
                    "The array element storage is incompatible with this opcode: " + FormatDiagnostic(sourceArray.BaseType));

            StackValue value = default;
            TypeSignature valueType = null;
            MethodDefinition nativeStore = null;
            MethodDefinition stringConversion = null;
            TypeSignature nativeObject = null;
            var referenceStore = setter && !element.IsValueType;
            var boxedStore = referenceStore && (sourceArray.BaseType is GenericParameterSignature || sourceArray.BaseType.IsValueType);
            if (setter)
            {
                if (!TryGetStackValue(currentInstruction, 0, out value))
                    return FailInstruction("unsupported-array-store-value", "The stored value type is not proven.");
                valueType = value.IsNull ? element : resolver.Resolve(value.Type);
                if (valueType == null)
                    return FailInstruction("unsupported-array-store-value", "The stored value type cannot be resolved.");
                if (referenceStore)
                {
                    if (boxedStore ? value.IsNull || !SignatureComparer.Default.Equals(value.Type, sourceArray.BaseType) :
                        value.Type is GenericParameterSignature || (!value.IsNull &&
                        valueType.FullName != "System.String" && !IsNativeArrayReference(valueType)))
                        return FailInstruction("unsupported-array-reference-store",
                            "The store requires an exact generic/value element or a proven native reference or string.");
                    var identityFailure = InteropRestorationPlan.For(context).GetNativeIdentityRejection(sourceArray.BaseType);
                    if (identityFailure != null)
                        return FailInstruction("native-array-element-unavailable", identityFailure);
                    if (!TryGetNativeArrayStore(out nativeStore, out nativeObject))
                        return FailInstruction("unsupported-array-native-store",
                            "No existing native System.Array.SetValue(Object, Int32) wrapper is available.");
                    if (!boxedStore && valueType.FullName == "System.String")
                    {
                        stringConversion = nativeObject.Resolve()?.Methods.SingleOrDefault(method =>
                            method.Name == "op_Implicit" && method.IsStatic &&
                            method.Signature.ParameterTypes.Count == 1 &&
                            method.Signature.ParameterTypes[0].FullName == "System.String" &&
                            SignatureComparer.Default.Equals(method.Signature.ReturnType, nativeObject));
                        if (stringConversion == null)
                            return FailInstruction("unsupported-array-reference-store", "Native string conversion is unavailable.");
                    }
                }
                else if (!ArrayValueMatches(value, sourceArray.BaseType, code))
                    return FailInstruction("unsupported-array-store-value", "The stored value has an incompatible evaluation-stack type.");
            }

            // Spill only this operation's operands, preserving any lower stack values (including dup).
            // Check native-sized indices before narrowing them for the Int32 accessor.
            var arrayLocal = AddArrayLocal(rewrittenArray);
            var indexLocal = AddArrayLocal(indexType);
            var valueLocal = setter ? AddArrayLocal(valueType) : null;
            var first = setter ? body.Add(CilOpCodes.Stloc, valueLocal) : body.Add(CilOpCodes.Stloc, indexLocal);
            if (setter)
                body.Add(CilOpCodes.Stloc, indexLocal);
            body.Add(CilOpCodes.Stloc, arrayLocal);
            EmitArrayBoundsCheck(arrayLocal, indexLocal);

            if (referenceStore)
                EmitNativeReferenceStore(arrayLocal, indexLocal, valueLocal, nativeStore, nativeObject, stringConversion, boxedStore);
            else
            {
                body.Add(CilOpCodes.Ldloc, arrayLocal);
                body.Add(CilOpCodes.Ldloc, indexLocal);
                body.Add(CilOpCodes.Conv_I4);
                if (setter)
                {
                    body.Add(CilOpCodes.Ldloc, valueLocal);
                    EmitArrayStoreConversion(storage);
                }
                EmitArrayAccessor(setter, element);
                if (!setter)
                    EmitArrayLoadConversion(code);
            }
            return first;
        }

        private CilLocalVariable AddArrayLocal(TypeSignature type)
        {
            var variable = new CilLocalVariable(imports.Module.DefaultImporter.ImportTypeSignature(type));
            target.CilMethodBody.LocalVariables.Add(variable);
            return variable;
        }

        private void EmitArrayBoundsCheck(CilLocalVariable array, CilLocalVariable index)
        {
            var length = AddArrayLocal(imports.Module.CorLibTypeFactory.Int32);
            body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(imports.Il2CppArrayBase_get_Length.Value));
            body.Add(CilOpCodes.Stloc, length);
            body.Add(CilOpCodes.Ldloc, index);
            body.Add(CilOpCodes.Conv_U8);
            body.Add(CilOpCodes.Ldloc, length);
            body.Add(CilOpCodes.Conv_U8);
            var inRange = new CilInstructionLabel();
            body.Add(CilOpCodes.Blt_Un, inRange);
            EmitArrayException(typeof(IndexOutOfRangeException));
            inRange.Instruction = body.Add(CilOpCodes.Nop);
        }

        private void EmitArrayException(Type exception)
        {
            body.Add(CilOpCodes.Newobj, imports.Module.DefaultImporter.ImportMethod(exception.GetConstructor(Type.EmptyTypes)));
            body.Add(CilOpCodes.Throw);
        }

        private bool TryGetNativeArrayStore(out MethodDefinition store, out TypeSignature nativeObject)
        {
            var corlib = context.TryGetAssemblyByName("mscorlib");
            var array = corlib?.TryGetTypeByName("System.Array");
            nativeObject = corlib?.TryGetTypeByName("System.Object")?.NewType.ToTypeSignature();
            // Use a metadata-backed wrapper, never a restored declaration or a guessed native handle.
            store = array?.Methods.SingleOrDefault(method =>
                method.OriginalMethod.Name == "SetValue" && !method.OriginalMethod.IsStatic &&
                method.OriginalMethod.Signature.ParameterTypes.Count == 2 &&
                method.OriginalMethod.Signature.ParameterTypes[0].FullName == "System.Object" &&
                method.OriginalMethod.Signature.ParameterTypes[1].FullName == "System.Int32" &&
                method.NonGenericMethodInfoPointerField != null &&
                method.NewMethod.CilMethodBody?.Instructions.Count > 0)?.NewMethod;
            return store != null && nativeObject != null;
        }

        private bool IsNativeArrayReference(TypeSignature type)
        {
            if (type is GenericInstanceTypeSignature generic &&
                (SignatureComparer.Default.Equals(generic.GenericType, imports.Il2CppArrayBase.ToTypeDefOrRef()) ||
                 SignatureComparer.Default.Equals(generic.GenericType, imports.Il2CppReferenceArray.ToTypeDefOrRef()) ||
                 SignatureComparer.Default.Equals(generic.GenericType, imports.Il2CppStructArray.ToTypeDefOrRef())))
                return true;
            if (SignatureComparer.Default.Equals(type, imports.Il2CppStringArray))
                return true;
            var visited = new System.Collections.Generic.HashSet<TypeDefinition>();
            while (type != null)
            {
                if (SignatureComparer.Default.Equals(type, imports.Il2CppObjectBase))
                    return true;
                var definition = type.Resolve();
                if (definition == null || !visited.Add(definition))
                    return false;
                type = definition.BaseType?.ToTypeSignature();
            }
            return false;
        }

        private void EmitNativeReferenceStore(CilLocalVariable array, CilLocalVariable index, CilLocalVariable value,
            MethodDefinition store, TypeSignature nativeObject, MethodDefinition stringConversion, bool boxedStore)
        {
            var nativeValue = AddArrayLocal(nativeObject);
            body.Add(CilOpCodes.Ldloc, value);
            if (boxedStore)
                body.Add(CilOpCodes.Call,
                    ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                        .GetMethod(nameof(InteropGenericValueSupport.Box))))
                    .MakeGenericInstanceMethod(value.VariableType, nativeObject));
            else if (stringConversion != null)
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(stringConversion));
            else
            {
                var nonNull = new CilInstructionLabel();
                var converted = new CilInstructionLabel();
                body.Add(CilOpCodes.Brtrue, nonNull);
                body.Add(CilOpCodes.Ldnull);
                body.Add(CilOpCodes.Br, converted);
                nonNull.Instruction = body.Add(CilOpCodes.Ldloc, value);
                body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                    imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(nativeObject)));
                converted.Instruction = body.Add(CilOpCodes.Nop);
            }
            body.Add(CilOpCodes.Stloc, nativeValue);

            // SetValue owns the native write and its GC barrier. Check covariance here to preserve
            // stelem.ref's ArrayTypeMismatchException rather than SetValue's cast exception.
            var compatible = new CilInstructionLabel();
            body.Add(CilOpCodes.Ldloc, nativeValue);
            body.Add(CilOpCodes.Brfalse, compatible);
            body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value));
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_il2cpp_object_get_class.Value));
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_class_get_element_class))));
            body.Add(CilOpCodes.Ldloc, nativeValue);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_Il2CppObjectBaseToPtrNotNull.Value));
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(imports.IL2CPP_il2cpp_object_get_class.Value));
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(IL2CPP).GetMethod(nameof(IL2CPP.il2cpp_class_is_assignable_from))));
            body.Add(CilOpCodes.Brtrue, compatible);
            EmitArrayException(typeof(ArrayTypeMismatchException));
            compatible.Instruction = body.Add(CilOpCodes.Ldloc, array);
            body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(
                imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(store.DeclaringType.ToTypeSignature())));
            body.Add(CilOpCodes.Ldloc, nativeValue);
            body.Add(CilOpCodes.Ldloc, index);
            body.Add(CilOpCodes.Conv_I4);
            body.Add(CilOpCodes.Callvirt, imports.Module.DefaultImporter.ImportMethod(store));
        }

        private static ElementType? ArrayStorageType(TypeSignature type)
        {
            if (type == null)
                return null;
            if (type is CorLibTypeSignature)
                return type.ElementType;
            if (type is GenericParameterSignature or PointerTypeSignature or ByReferenceTypeSignature)
                return null;
            var definition = type.Resolve();
            return definition?.IsEnum == true
                ? definition.Fields.SingleOrDefault(field => field.Name == "value__")?.Signature.FieldType.ElementType
                : null;
        }

        private static bool ArrayOpcodeMatches(CilCode code, TypeSignature element, ElementType? storage) => code switch
        {
            CilCode.Ldelem or CilCode.Stelem => true,
            CilCode.Ldelem_Ref or CilCode.Stelem_Ref => IsStackReference(element),
            CilCode.Ldelem_I1 or CilCode.Ldelem_U1 or CilCode.Stelem_I1 =>
                storage is ElementType.Boolean or ElementType.I1 or ElementType.U1,
            CilCode.Ldelem_I2 or CilCode.Ldelem_U2 or CilCode.Stelem_I2 =>
                storage is ElementType.Char or ElementType.I2 or ElementType.U2,
            CilCode.Ldelem_I4 or CilCode.Ldelem_U4 or CilCode.Stelem_I4 => storage is ElementType.I4 or ElementType.U4,
            CilCode.Ldelem_I8 or CilCode.Stelem_I8 => storage is ElementType.I8 or ElementType.U8,
            CilCode.Ldelem_I or CilCode.Stelem_I => storage is ElementType.I or ElementType.U,
            CilCode.Ldelem_R4 or CilCode.Stelem_R4 => storage == ElementType.R4,
            CilCode.Ldelem_R8 or CilCode.Stelem_R8 => storage == ElementType.R8,
            _ => false
        };

        private bool ArrayValueMatches(StackValue value, TypeSignature element, CilCode code)
        {
            if (value.Type == null)
                return false;
            if (ArrayStorageType(element) is ElementType.R4 or ElementType.R8 &&
                ArrayStorageType(value.Type) is ElementType.R4 or ElementType.R8)
                return true;
            if (code == CilCode.Stelem)
                return SignatureComparer.Default.Equals(value.Type, element) ||
                    (ArrayStorageType(element) != null &&
                     SameStackValue(NumericStackValue(value.Type), NumericStackValue(element)));
            var expected = NumericStackValue(element);
            return expected.Type != null && SameStackValue(NumericStackValue(value.Type), expected);
        }

        private void EmitArrayStoreConversion(ElementType? storage)
        {
            switch (storage)
            {
                case ElementType.I1: body.Add(CilOpCodes.Conv_I1); break;
                case ElementType.Boolean:
                case ElementType.U1: body.Add(CilOpCodes.Conv_U1); break;
                case ElementType.I2: body.Add(CilOpCodes.Conv_I2); break;
                case ElementType.Char:
                case ElementType.U2: body.Add(CilOpCodes.Conv_U2); break;
                case ElementType.I4: body.Add(CilOpCodes.Conv_I4); break;
                case ElementType.U4: body.Add(CilOpCodes.Conv_U4); break;
                case ElementType.I8: body.Add(CilOpCodes.Conv_I8); break;
                case ElementType.U8: body.Add(CilOpCodes.Conv_U8); break;
                case ElementType.I: body.Add(CilOpCodes.Conv_I); break;
                case ElementType.U: body.Add(CilOpCodes.Conv_U); break;
                case ElementType.R4: body.Add(CilOpCodes.Conv_R4); break;
                case ElementType.R8: body.Add(CilOpCodes.Conv_R8); break;
            }
        }

        private void EmitArrayLoadConversion(CilCode code)
        {
            switch (code)
            {
                case CilCode.Ldelem_I1: body.Add(CilOpCodes.Conv_I1); break;
                case CilCode.Ldelem_U1: body.Add(CilOpCodes.Conv_U1); break;
                case CilCode.Ldelem_I2: body.Add(CilOpCodes.Conv_I2); break;
                case CilCode.Ldelem_U2: body.Add(CilOpCodes.Conv_U2); break;
                case CilCode.Ldelem_I4: body.Add(CilOpCodes.Conv_I4); break;
                case CilCode.Ldelem_U4: body.Add(CilOpCodes.Conv_U4); break;
                case CilCode.Ldelem_I8: body.Add(CilOpCodes.Conv_I8); break;
                case CilCode.Ldelem_I: body.Add(CilOpCodes.Conv_I); break;
            }
        }

        private bool ValidateArrayAssignment(CilInstruction instruction)
        {
            if (instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt or CilCode.Newobj &&
                instruction.Operand is IMethodDescriptor method && method.Signature != null)
            {
                var parameters = method.Signature.ParameterTypes;
                for (var index = 0; index < parameters.Count; index++)
                    if (!ValidateArrayAssignment(instruction, parameters[index].InstantiateGenericTypes(GenericContext.FromMethod(method)),
                            parameters.Count - index - 1))
                        return false;
            }
            TypeSignature destination = instruction.OpCode.Code switch
            {
                CilCode.Stloc when instruction.Operand is CilLocalVariable variable => variable.VariableType,
                CilCode.Starg when instruction.Operand is Parameter argument => argument.ParameterType,
                CilCode.Stfld or CilCode.Stsfld when instruction.Operand is IFieldDescriptor field =>
                    field.Signature?.FieldType.InstantiateGenericTypes(GenericContext.FromField(field)),
                CilCode.Stobj when instruction.Operand is ITypeDefOrRef stored => stored.ToTypeSignature(),
                CilCode.Stind_Ref when TryGetStackValue(instruction, 1, out var address) &&
                    address.Type is ByReferenceTypeSignature reference => reference.BaseType,
                CilCode.Ret => sourceReturnType,
                _ => null
            };
            return ValidateArrayAssignment(instruction, destination, 0);
        }

        private bool ValidateArrayAssignment(CilInstruction instruction, TypeSignature destination, int depth)
        {
            if (destination is not SzArrayTypeSignature)
                return true;
            if (TryGetStackValue(instruction, depth, out var value))
            {
                if (value.IsNull || SignatureComparer.Default.Equals(value.Type, destination) ||
                    InteropManagedVectorLowering.CanAssign(context, value.Type, destination))
                    return true;
                if (instruction.OpCode.Code == CilCode.Stloc && depth == 0 &&
                    instruction.Operand is CilLocalVariable &&
                    TryGetNativeReferenceVectorUpcast(value.Type, destination, out var rewritten))
                {
                    arrayLocalConversions.Add(instruction, rewritten);
                    return true;
                }
            }
            return Fail("unsupported-array-assignment", "Array assignment needs the same proven vector signature " +
                "or a proven closed native reference-vector upcast at stloc; native generic wrappers cannot use " +
                "CLR array covariance implicitly.", instruction);
        }

        private bool TryGetNativeReferenceVectorUpcast(TypeSignature source, TypeSignature destination,
            out TypeSignature rewrittenDestination)
        {
            rewrittenDestination = null;
            if (source is not SzArrayTypeSignature from || destination is not SzArrayTypeSignature to ||
                !TryGetClosedNativeReferenceElement(from.BaseType, out var definition) ||
                !TryGetClosedNativeReferenceElement(to.BaseType, out _))
                return false;

            // Prove a class upcast in the source metadata, not assignability between invariant CLR wrappers.
            var visited = new HashSet<TypeDefinition> { definition };
            var parent = definition.BaseType?.ToTypeSignature();
            while (TryGetClosedNativeReferenceElement(parent, out definition) && visited.Add(definition))
            {
                if (SignatureComparer.Default.Equals(parent, to.BaseType))
                {
                    var rewrittenFrom = resolver.Resolve(from);
                    var rewrittenTo = resolver.Resolve(to);
                    if (!IsNativeReferenceVectorWrapper(rewrittenFrom) || !IsNativeReferenceVectorWrapper(rewrittenTo))
                        return false;
                    rewrittenDestination = rewrittenTo;
                    return true;
                }
                parent = definition.BaseType?.ToTypeSignature();
            }
            return false;
        }

        private bool TryGetClosedNativeReferenceElement(TypeSignature element, out TypeDefinition definition)
        {
            definition = null;
            if (element is not (TypeDefOrRefSignature or CorLibTypeSignature) || element.FullName == "System.String")
                return false;
            definition = element.Resolve();
            return definition is { IsValueType: false, IsInterface: false } && definition.GenericParameters.Count == 0 &&
                InteropRestorationPlan.For(context).GetNativeIdentityRejection(element) == null;
        }

        private bool IsNativeReferenceVectorWrapper(TypeSignature type) =>
            type is GenericInstanceTypeSignature wrapper && wrapper.TypeArguments.Count == 1 &&
            SignatureComparer.Default.Equals(wrapper.GenericType, imports.Il2CppReferenceArray.ToTypeDefOrRef()) &&
            IsNativeArrayReference(wrapper.TypeArguments[0]);

        private CilInstruction EmitArrayLocalConversion(CilLocalVariable local, TypeSignature rewritten)
        {
            // The checked helper preserves null and wraps the same native pointer without copying the array.
            var cast = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropGenericValueSupport)
                .GetMethod(nameof(InteropGenericValueSupport.CastReference)));
            var first = body.Add(CilOpCodes.Call, cast.MakeGenericInstanceMethod(rewritten));
            body.Add(CilOpCodes.Stloc, local);
            return first;
        }
    }
}
