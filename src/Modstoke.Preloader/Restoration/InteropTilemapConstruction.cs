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
    internal static class InteropTilemapConstruction
    {
        private const string ModuleName = "UnityEngine.TilemapModule";
        private const string OwnerName = "UnityEngine.Tilemaps.ITilemap";
        private const string TilemapName = "UnityEngine.Tilemaps.Tilemap";

        internal static bool IsOwner(TypeDefinition source) =>
            source?.FullName == OwnerName && source.DeclaringModule.Assembly.Name == ModuleName;

        internal static bool TryGetNativeDefault(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, out MethodDefinition nativeDefault)
        {
            nativeDefault = null;
            var type = source?.DeclaringType;
            if (!IsOwner(type) || type.DeclaringModule.Mvid != new Guid("dd6f32f2-53d2-4004-9e03-88abd97a76b9") ||
                type.MetadataToken.ToInt32() != 0x02000007 || type.Attributes != (TypeAttributes.Public | TypeAttributes.BeforeFieldInit) ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || type.Interfaces.Count != 0 || type.ClassLayout != null ||
                !IsSystemType(type.BaseType, "System.Object") || type.Fields.Count != 5 ||
                type.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 2 ||
                type.Methods.Any(method => method.Name == "Finalize" || method.Name == "Dispose" ||
                    method.IsConstructor && method.Signature.ParameterTypes.Any(parameter => parameter.ElementType == ElementType.I)) ||
                source.MetadataToken.ToInt32() != 0x06000017 || !IsSourceConstructor(source, MethodAttributes.Public, 1) ||
                !IsTilemapType(source.Signature.ParameterTypes[0], type.DeclaringModule) ||
                source.Parameters[0].Definition is not { Attributes: 0, Constant: null, MarshalDescriptor: null } ||
                owner.OriginalType is not { IsValueType: false, IsInterface: false, IsAbstract: false } native ||
                !IsOwner(native) || native.BaseType?.FullName != "System.Object" || native.DeclaringType != null ||
                native.GenericParameters.Count != 0 || native.Interfaces.Count != 0 ||
                native.Fields.Count(field => !field.IsStatic) != 4 ||
                native.Methods.Any(method => !method.IsStatic && (method.Name == "Finalize" || method.Name == "Dispose")) ||
                owner.ClassPointerFieldRef == null || owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                owner.NewType.FullName != OwnerName || owner.NewType.DeclaringModule != imports.Module ||
                owner.NewType.DeclaringType != null || owner.NewType.GenericParameters.Count != 0 ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.BaseType?.FullName != "Il2CppSystem.Object" ||
                resolver.ResolveRaw(type.ToTypeSignature())?.Resolve() != owner.NewType ||
                owner.TryGetMethodByUnityAssemblyMethod(source) != null) return false;

            var tilemap = owner.AssemblyContext.GlobalContext.TryGetTypeByName(ModuleName, TilemapName);
            var parameter = resolver.Resolve(source.Signature.ParameterTypes[0]);
            if (tilemap?.OriginalType is not { IsValueType: false, IsInterface: false, DeclaringType: null } nativeTilemap ||
                nativeTilemap.FullName != TilemapName || nativeTilemap.DeclaringModule.Assembly.Name != ModuleName ||
                nativeTilemap.GenericParameters.Count != 0 || tilemap.ClassPointerFieldRef == null ||
                tilemap.NewType.FullName != TilemapName || tilemap.NewType.DeclaringModule != imports.Module ||
                tilemap.NewType.IsValueType || tilemap.NewType.IsInterface ||
                tilemap.NewType.GenericParameters.Count != 0 || parameter?.Resolve() != tilemap.NewType) return false;

            string[] fieldNames = ["s_Instance", "m_Tilemap", "m_AddToList", "m_RefreshCount", "m_RefreshPos"];
            FieldDefinition tilemapField = null;
            for (var index = 0; index < fieldNames.Length; index++)
            {
                var field = type.Fields[index];
                if (field.MetadataToken.ToInt32() != 0x04000014 + index || field.Name != fieldNames[index] ||
                    field.Attributes != (FieldAttributes.Assembly | (index == 0 ? FieldAttributes.Static : 0)) ||
                    field.Signature == null || field.Signature.HasThis || field.Constant != null || field.FieldRva != null ||
                    field.FieldOffset != null || field.HasFieldMarshal || field.MarshalDescriptor != null || field.CustomAttributes.Count != 0)
                    return false;
                var storage = field.Signature.FieldType;
                var valid = index switch
                {
                    0 => SignatureComparer.Default.Equals(storage, type.ToTypeSignature()),
                    1 => IsTilemapType(storage, type.DeclaringModule),
                    2 => storage is CorLibTypeSignature { ElementType: ElementType.Boolean },
                    3 => storage is CorLibTypeSignature { ElementType: ElementType.I4 },
                    _ => storage is GenericInstanceTypeSignature { IsValueType: true, TypeArguments.Count: 1 } array &&
                        array.GenericType.FullName == "Unity.Collections.NativeArray`1" &&
                        InteropRestoredTypeSupport.GetAssemblyName(array.GenericType.ToTypeSignature()) == "UnityEngine.CoreModule" &&
                        array.TypeArguments[0] is TypeDefOrRefSignature { IsValueType: true } vector &&
                        vector.FullName == "UnityEngine.Vector3Int" &&
                        InteropRestoredTypeSupport.GetAssemblyName(vector) == "UnityEngine.CoreModule"
                };
                if (!valid) return false;
                if (index == 0) continue;
                var mapped = owner.TryGetFieldByUnityAssemblyField(field);
                if (mapped?.PointerField == null || mapped.DeclaringType != owner || mapped.OriginalField.DeclaringType != native ||
                    mapped.OriginalField.IsStatic || mapped.OriginalField.IsLiteral || mapped.OriginalField.IsInitOnly ||
                    owner.NewType.Fields.Any(candidate => candidate.Name == field.Name || candidate.Name == mapped.UnmangledName)) return false;
                if (index != 1) continue;
                tilemapField = field;
                var properties = owner.NewType.Properties.Where(property => property.Name == mapped.UnmangledName).ToArray();
                if (properties.Length != 1 || properties[0].SetMethod is not { } setter || setter.DeclaringType != owner.NewType ||
                    setter.IsStatic || setter.IsVirtual || setter.IsAbstract || setter.IsPInvokeImpl || setter.CilMethodBody == null ||
                    setter.GenericParameters.Count != 0 || !setter.IsPublic ||
                    !SignatureComparer.Default.Equals(setter.Signature,
                        MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, parameter))) return false;
            }

            var sourceDefault = type.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == 0x06000016);
            if (!IsSourceConstructor(sourceDefault, MethodAttributes.Assembly, 0) ||
                sourceDefault.CilMethodBody.LocalVariables.Count != 0 || sourceDefault.CilMethodBody.ExceptionHandlers.Count != 0 ||
                sourceDefault.CilMethodBody.InitializeLocals) return false;
            var defaultCode = sourceDefault.CilMethodBody.Instructions;
            if (!defaultCode.Select(instruction => instruction.OpCode.Code).SequenceEqual(
                    new[] { CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Nop, CilCode.Ret }) ||
                !IsObjectConstructor(defaultCode[1])) return false;

            var body = source.CilMethodBody;
            CilCode[] expected =
            [
                CilCode.Ldarg_0, CilCode.Call, CilCode.Nop, CilCode.Nop, CilCode.Ldarg_1, CilCode.Ldnull,
                CilCode.Call, CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Brfalse_S, CilCode.Ldstr,
                CilCode.Newobj, CilCode.Throw, CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Stfld, CilCode.Ret
            ];
            var code = body.Instructions;
            if (!body.InitializeLocals || body.LocalVariables.Count != 1 || body.ExceptionHandlers.Count != 0 ||
                body.LocalVariables[0].VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                !code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected) || !IsObjectConstructor(code[1]) ||
                code[9].Operand is not CilInstructionLabel branch || branch.Instruction != code[13] || code[13].Offset != 0x1E ||
                code[10].Operand is not "Argument tilemap cannot be null" ||
                code[11].Operand is not IMethodDescriptor exception || exception is MethodSpecification || exception.Name != ".ctor" ||
                !IsSystemType(exception.DeclaringType, "System.ArgumentNullException") ||
                !SignatureComparer.Default.Equals(exception.Signature,
                    MethodSignature.CreateInstance(type.DeclaringModule.CorLibTypeFactory.Void, type.DeclaringModule.CorLibTypeFactory.String)) ||
                code[15].Operand is not IFieldDescriptor assignment || assignment.Resolve() != tilemapField ||
                !SignatureComparer.Default.Equals(assignment.Signature, tilemapField.Signature) ||
                !SignatureComparer.Default.Equals(assignment.DeclaringType.ToTypeSignature(), type.ToTypeSignature()) ||
                code[6].Operand is not IMethodDescriptor equality || equality is MethodSpecification || equality.Name != "op_Equality" ||
                equality.DeclaringType?.FullName != "UnityEngine.Object" ||
                InteropRestoredTypeSupport.GetAssemblyName(equality.DeclaringType.ToTypeSignature()) != "UnityEngine.CoreModule" ||
                !SignatureComparer.Default.Equals(equality.Signature, MethodSignature.CreateStatic(type.DeclaringModule.CorLibTypeFactory.Boolean,
                    equality.DeclaringType.ToTypeSignature(), equality.DeclaringType.ToTypeSignature()))) return false;

            var mappedDefault = owner.TryGetMethodByUnityAssemblyMethod(sourceDefault);
            var target = mappedDefault?.NewMethod;
            if (mappedDefault?.NonGenericMethodInfoPointerField == null || mappedDefault.OriginalMethod.DeclaringType != native ||
                !mappedDefault.OriginalMethod.IsConstructor || mappedDefault.OriginalMethod.IsStatic ||
                target == null || target.DeclaringType != owner.NewType || !target.IsConstructor || target.Name != ".ctor" ||
                target.IsStatic || target.IsVirtual || target.IsAbstract || target.IsPInvokeImpl || target.CilMethodBody == null ||
                target.GenericParameters.Count != 0 || !InteropDelegateContracts.HasStandardConvention(target.Signature) ||
                !SignatureComparer.Default.Equals(target.Signature, MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void)) ||
                owner.NewType.Methods.Count(method => method.Name == ".ctor" &&
                    SignatureComparer.Default.Equals(method.Signature, target.Signature)) != 1) return false;

            // This existing native default is the sole allocator; the source remainder retains null semantics and field assignment.
            nativeDefault = target;
            return true;
        }

        private static bool IsSourceConstructor(MethodDefinition method, MethodAttributes access, int parameters) =>
            method != null && method.Name == ".ctor" && method.Attributes == (access | MethodAttributes.HideBySig |
                MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) && method.ImplAttributes == MethodImplAttributes.IL &&
            method.CilMethodBody != null && method.ImplementationMap == null && method.GenericParameters.Count == 0 &&
            method.CustomAttributes.Count == 0 && InteropDelegateContracts.HasStandardConvention(method.Signature) &&
            method.Signature.HasThis && method.Signature.GenericParameterCount == 0 &&
            method.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void } &&
            method.Signature.ParameterTypes.Count == parameters && method.Parameters.Count == parameters;

        private static bool IsObjectConstructor(CilInstruction instruction) =>
            instruction.Operand is IMethodDescriptor call && call is not MethodSpecification && call.Name == ".ctor" &&
            IsSystemType(call.DeclaringType, "System.Object") && InteropDelegateContracts.HasStandardConvention(call.Signature) &&
            call.Signature.HasThis && call.Signature.GenericParameterCount == 0 && call.Signature.ParameterTypes.Count == 0 &&
            call.Signature.ReturnType is CorLibTypeSignature { ElementType: ElementType.Void };

        private static bool IsSystemType(ITypeDescriptor type, string name) =>
            type is ITypeDefOrRef reference && reference.FullName == name && InteropArraySegmentLowering.HasSourceContract(reference);

        private static bool IsTilemapType(TypeSignature type, ModuleDefinition module) =>
            type is TypeDefOrRefSignature { IsValueType: false } && type.FullName == TilemapName && type.Resolve()?.DeclaringModule == module;
    }
}
