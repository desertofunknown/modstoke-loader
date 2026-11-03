// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal static class InteropVfxSpawnerConstruction
    {
        private const string ModuleName = "UnityEngine.VFXModule";
        private const string OwnerName = "UnityEngine.VFX.VFXSpawnerState";

        internal static bool IsOwner(TypeDefinition type) =>
            type?.FullName == OwnerName && type.DeclaringModule.Assembly.Name == ModuleName;

        internal static bool TryGetPointerConstructor(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, out MethodDefinition pointerConstructor)
        {
            pointerConstructor = null;
            var type = source?.DeclaringType;
            if (!IsOwner(type) || type.DeclaringModule.Mvid != new Guid("f5a4ec05-7e52-44a2-a253-4bb50ede2990") ||
                type.MetadataToken.ToInt32() != 0x02000018 || type.Attributes != (TypeAttributes.Public | TypeAttributes.Sealed |
                    TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit) || type.ClassLayout != null ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || !IsSystemType(type.BaseType, "System.Object") ||
                type.Interfaces.Count != 1 || !IsSystemType(type.Interfaces[0].Interface, "System.IDisposable") || type.Fields.Count != 3 ||
                source.MetadataToken.ToInt32() != 0x0600008A ||
                !HasSourceMethod(source, ".ctor", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName, false,
                    type.DeclaringModule.CorLibTypeFactory.Void) || source.CilMethodBody.LocalVariables.Count != 0 ||
                source.CilMethodBody.ExceptionHandlers.Count != 0 ||
                owner.OriginalType is not { IsValueType: false, IsInterface: false, IsAbstract: false, IsSealed: true } native ||
                !IsOwner(native) || native.DeclaringType != null || native.GenericParameters.Count != 0 ||
                native.BaseType?.FullName != "System.Object" || native.Interfaces.Count != 1 ||
                native.Interfaces[0].Interface?.FullName != "System.IDisposable" || native.Fields.Count != 3 ||
                owner.ClassPointerFieldRef == null || owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                owner.NewType.FullName != OwnerName || owner.NewType.DeclaringType != null || !owner.NewType.IsSealed ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.GenericParameters.Count != 0 ||
                owner.NewType.DeclaringModule != imports.Module || owner.NewType.BaseType?.FullName != "Il2CppSystem.Object" ||
                resolver.ResolveRaw(type.ToTypeSignature())?.Resolve() != owner.NewType ||
                owner.TryGetMethodByUnityAssemblyMethod(source) != null ||
                owner.NewType.Methods.Any(method => method.Name == ".ctor" && method.Signature.ParameterTypes.Count == 0)) return false;

            var corlib = type.DeclaringModule.CorLibTypeFactory;
            var bound = FindMethod(type, 0x0600008B);
            var create = FindMethod(type, 0x0600008C);
            var release = FindMethod(type, 0x06000091);
            var finalizer = FindMethod(type, 0x06000092);
            var dispose = FindMethod(type, 0x06000093);
            if (!HasSourceMethod(bound, ".ctor", MethodAttributes.Assembly | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName,
                    false, corlib.Void, corlib.IntPtr, corlib.Boolean) ||
                !HasSourceMethod(release, "Release", MethodAttributes.Private, false, corlib.Void) ||
                !HasSourceMethod(finalizer, "Finalize", MethodAttributes.Family | MethodAttributes.Virtual, false, corlib.Void) ||
                !HasSourceMethod(dispose, "Dispose", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                    false, corlib.Void) || create == null || create.Name != "Internal_Create" ||
                create.Attributes != (MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                create.ImplAttributes != MethodImplAttributes.InternalCall || create.CilMethodBody != null ||
                create.GenericParameters.Count != 0 || create.CustomAttributes.Count != 0 ||
                !SignatureComparer.Default.Equals(create.Signature, MethodSignature.CreateStatic(corlib.IntPtr))) return false;

            string[] names = ["m_Ptr", "m_Owner", "m_WrapEventAttribute"];
            for (var index = 0; index < names.Length; index++)
            {
                var field = type.Fields[index];
                if (field.MetadataToken.ToInt32() != 0x0400011A + index || field.Name != names[index] ||
                    field.Attributes != FieldAttributes.Private || field.Signature == null || field.Signature.HasThis ||
                    field.Constant != null || field.FieldRva != null || field.FieldOffset != null || field.HasFieldMarshal ||
                    field.MarshalDescriptor != null || field.CustomAttributes.Count != 0) return false;
                var storage = field.Signature.FieldType;
                if (index == 0 ? storage is not CorLibTypeSignature { ElementType: ElementType.I } : index == 1
                    ? storage is not CorLibTypeSignature { ElementType: ElementType.Boolean }
                    : storage is not TypeDefOrRefSignature { IsValueType: false } ||
                        storage.FullName != "UnityEngine.VFX.VFXEventAttribute" || storage.Resolve()?.DeclaringModule != type.DeclaringModule) return false;
                var mapped = owner.TryGetFieldByUnityAssemblyField(field);
                if (mapped?.PointerField == null || mapped.DeclaringType != owner || mapped.OriginalField.DeclaringType != native ||
                    mapped.OriginalField.Attributes != field.Attributes ||
                    mapped.OriginalField.Signature?.FieldType.FullName != storage.FullName || resolver.Resolve(storage) == null ||
                    owner.NewType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == mapped.UnmangledName)) return false;
            }

            var code = WithoutNops(source);
            var boundCode = WithoutNops(bound);
            if (!HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldc_I4_1, CilCode.Call, CilCode.Ret]) ||
                !Calls(code[1], create) || !Calls(code[3], bound) ||
                bound.CilMethodBody.LocalVariables.Count != 0 || bound.CilMethodBody.ExceptionHandlers.Count != 0 ||
                !HasOpcodes(boundCode, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Stfld,
                    CilCode.Ldarg_0, CilCode.Ldarg_2, CilCode.Stfld, CilCode.Ret]) ||
                !CallsSystem(boundCode[1], "System.Object", ".ctor", true, corlib.Void) ||
                !UsesField(boundCode[4], type.Fields[0]) || !UsesField(boundCode[7], type.Fields[1]) ||
                !HasCleanupBodies(type, release, finalizer, dispose) ||
                !HasNativeMethod(bound, owner, resolver) || !HasNativeMethod(release, owner, resolver) ||
                !HasNativeMethod(finalizer, owner, resolver) || !HasNativeMethod(dispose, owner, resolver)) return false;
            var nativeFinalizer = owner.TryGetMethodByUnityAssemblyMethod(finalizer).OriginalMethod;
            if (!nativeFinalizer.IsVirtual || native.Methods.Count(method => method.Name == "Finalize" && !method.IsStatic) != 1) return false;

            var signature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, signature)).ToArray();
            if (pointers.Length != 1 || pointers[0] is not { IsPublic: true, IsStatic: false, IsVirtual: false, IsAbstract: false, IsConstructor: true } pointer ||
                pointer.IsPInvokeImpl || pointer.ImplAttributes != MethodImplAttributes.IL || pointer.GenericParameters.Count != 0 ||
                pointer.CilMethodBody == null || pointer.CilMethodBody.LocalVariables.Count != 0 || pointer.CilMethodBody.ExceptionHandlers.Count != 0) return false;
            var pointerCode = WithoutNops(pointer);
            if (!HasOpcodes(pointerCode, [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Call, CilCode.Ret]) ||
                pointerCode[2].Operand is not IMethodDescriptor baseCall || baseCall is MethodSpecification || baseCall.Name != ".ctor" ||
                !SignatureComparer.Default.Equals(baseCall.DeclaringType, owner.NewType.BaseType) ||
                !SignatureComparer.Default.Equals(baseCall.Signature, signature)) return false;
            pointerConstructor = pointer;
            return true;
        }

        private static bool HasCleanupBodies(TypeDefinition type, MethodDefinition release, MethodDefinition finalizer, MethodDefinition dispose)
        {
            var corlib = type.DeclaringModule.CorLibTypeFactory;
            var code = WithoutNops(release);
            if (!release.CilMethodBody.InitializeLocals || release.CilMethodBody.LocalVariables.Count != 1 ||
                release.CilMethodBody.LocalVariables[0].VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                release.CilMethodBody.ExceptionHandlers.Count != 0 ||
                !HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Ldsfld, CilCode.Call, CilCode.Brfalse_S,
                    CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Br_S, CilCode.Ldc_I4_0, CilCode.Stloc_0, CilCode.Ldloc_0,
                    CilCode.Brfalse_S, CilCode.Ldarg_0, CilCode.Ldfld, CilCode.Call, CilCode.Ldarg_0, CilCode.Ldsfld,
                    CilCode.Stfld, CilCode.Ldarg_0, CilCode.Ldnull, CilCode.Stfld, CilCode.Ret]) ||
                !UsesField(code[1], type.Fields[0]) || !UsesField(code[6], type.Fields[1]) || !UsesField(code[13], type.Fields[0]) ||
                !UsesField(code[17], type.Fields[0]) || !UsesField(code[20], type.Fields[2]) ||
                !IsZero(code[2]) || !IsZero(code[16]) ||
                !CallsSystem(code[3], "System.IntPtr", "op_Inequality", false, corlib.Boolean, corlib.IntPtr, corlib.IntPtr) ||
                !Targets(code[4], code[8]) || !Targets(code[7], code[9]) || !Targets(code[11], code[15]) ||
                code[14].Operand is not IMethodDescriptor destroy || destroy is MethodSpecification || destroy.Name != "Internal_Destroy" ||
                destroy.DeclaringType?.Resolve() != type ||
                !SignatureComparer.Default.Equals(destroy.Signature, MethodSignature.CreateStatic(corlib.Void, corlib.IntPtr)) ||
                destroy.Resolve() is not { IsStatic: true, IsInternalCall: true } destroyMethod || destroyMethod.DeclaringType != type) return false;

            code = WithoutNops(finalizer);
            if (!finalizer.CilMethodBody.InitializeLocals || finalizer.CilMethodBody.LocalVariables.Count != 0 ||
                finalizer.CilMethodBody.ExceptionHandlers.Count != 1 ||
                !HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Call, CilCode.Leave_S, CilCode.Ldarg_0, CilCode.Call, CilCode.Endfinally, CilCode.Ret]) ||
                !Calls(code[1], release) || !Targets(code[2], code[6]) ||
                !CallsSystem(code[4], "System.Object", "Finalize", true, corlib.Void)) return false;
            var handler = finalizer.CilMethodBody.ExceptionHandlers[0];
            if (handler.HandlerType != CilExceptionHandlerType.Finally || handler.ExceptionType != null || handler.FilterStart != null ||
                handler.TryStart?.Offset != 0x01 || handler.TryEnd?.Offset != 0x0B ||
                handler.HandlerStart?.Offset != 0x0B || handler.HandlerEnd?.Offset != 0x13) return false;

            code = WithoutNops(dispose);
            return dispose.CilMethodBody.LocalVariables.Count == 0 && dispose.CilMethodBody.ExceptionHandlers.Count == 0 &&
                HasOpcodes(code, [CilCode.Ldarg_0, CilCode.Call, CilCode.Ldarg_0, CilCode.Call, CilCode.Ret]) &&
                Calls(code[1], release) && CallsSystem(code[3], "System.GC", "SuppressFinalize", false, corlib.Void, corlib.Object);
        }

        private static bool HasNativeMethod(MethodDefinition source, InteropTypeContext owner, InteropUnstripTypeResolver resolver)
        {
            var mapped = owner.TryGetMethodByUnityAssemblyMethod(source);
            if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.DeclaringType != owner.OriginalType ||
                mapped.OriginalMethod.Attributes != source.Attributes || mapped.OriginalMethod.GenericParameters.Count != 0 ||
                mapped.NewMethod.DeclaringType != owner.NewType || mapped.NewMethod.IsStatic || mapped.NewMethod.IsAbstract ||
                mapped.NewMethod.IsPInvokeImpl || mapped.NewMethod.CilMethodBody == null || mapped.NewMethod.GenericParameters.Count != 0 ||
                owner.NewType.Methods.Count(method => method.Name == mapped.NewMethod.Name &&
                    SignatureComparer.Default.Equals(method.Signature, mapped.NewMethod.Signature)) != 1) return false;
            var arguments = source.Signature.ParameterTypes.Select(resolver.Resolve).ToArray();
            return arguments.All(argument => argument != null) && SignatureComparer.Default.Equals(mapped.NewMethod.Signature,
                MethodSignature.CreateInstance(resolver.Resolve(source.Signature.ReturnType), arguments));
        }

        private static bool HasSourceMethod(MethodDefinition method, string name, MethodAttributes attributes, bool isStatic,
            TypeSignature result, params TypeSignature[] parameters) =>
            method != null && method.Name == name && method.Attributes == (attributes | MethodAttributes.HideBySig | (isStatic ? MethodAttributes.Static : 0)) &&
            method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null && method.ImplementationMap == null &&
            method.GenericParameters.Count == 0 && method.CustomAttributes.Count == 0 &&
            SignatureComparer.Default.Equals(method.Signature, isStatic ? MethodSignature.CreateStatic(result, parameters) : MethodSignature.CreateInstance(result, parameters));

        private static MethodDefinition FindMethod(TypeDefinition type, int token) =>
            type.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == token);

        private static CilInstruction[] WithoutNops(MethodDefinition method) =>
            method.CilMethodBody.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();

        private static bool HasOpcodes(CilInstruction[] code, CilCode[] expected) =>
            code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected);

        private static bool Calls(CilInstruction instruction, MethodDefinition target) =>
            instruction.Operand is IMethodDescriptor call && call is not MethodSpecification && call.Resolve() == target &&
            SignatureComparer.Default.Equals(call.Signature, target.Signature);

        private static bool UsesField(CilInstruction instruction, FieldDefinition target) =>
            instruction.Operand is IFieldDescriptor field && field.Resolve() == target && SignatureComparer.Default.Equals(field.Signature, target.Signature);

        private static bool CallsSystem(CilInstruction instruction, string type, string name, bool hasThis,
            TypeSignature result, params TypeSignature[] parameters) =>
            instruction.Operand is IMethodDescriptor call && call is not MethodSpecification && call.Name == name && IsSystemType(call.DeclaringType, type) &&
            SignatureComparer.Default.Equals(call.Signature, hasThis ? MethodSignature.CreateInstance(result, parameters) : MethodSignature.CreateStatic(result, parameters));

        private static bool IsSystemType(ITypeDescriptor type, string name) =>
            type is ITypeDefOrRef reference && reference.FullName == name && InteropArraySegmentLowering.HasSourceContract(reference);

        private static bool IsZero(CilInstruction instruction) =>
            instruction.Operand is IFieldDescriptor field && field.Name == "Zero" && IsSystemType(field.DeclaringType, "System.IntPtr") &&
            field.Signature?.FieldType is CorLibTypeSignature { ElementType: ElementType.I };

        private static bool Targets(CilInstruction branch, CilInstruction target) =>
            branch.Operand is CilInstructionLabel label && label.Instruction == target;
    }
}
