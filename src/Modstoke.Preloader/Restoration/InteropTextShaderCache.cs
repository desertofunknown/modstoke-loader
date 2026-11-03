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
    internal static class InteropTextShaderCache
    {
        private const string ModuleName = "UnityEngine.TextCoreTextEngineModule";
        private const string OwnerName = "UnityEngine.TextCore.Text.TextShaderUtilities";
        private const string FieldName = "k_ShaderRef_Sprite";
        private const string PropertyName = "ShaderRef_Sprite";

        internal static bool CanRestoreField(FieldDefinition field, InteropTypeContext owner,
            InteropUnstripTypeResolver resolver)
        {
            var source = field?.DeclaringType;
            if (source == null || source.FullName != OwnerName || source.MetadataToken.ToInt32() != 0x0200004D ||
                source.DeclaringModule.Assembly.Name != ModuleName || source.DeclaringModule.Assembly.Modules.Count != 1 ||
                source.DeclaringModule.Mvid != new Guid("f7d4b6ec-b22d-4f6f-94d8-914fe3ebc785") ||
                source.Attributes != (TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed) ||
                source.DeclaringType != null || source.GenericParameters.Count != 0 || source.Interfaces.Count != 0 ||
                source.BaseType?.FullName != "System.Object" || source.CustomAttributes.Count != 1 ||
                source.CustomAttributes[0].Constructor?.DeclaringType?.FullName != "UnityEngine.Internal.ExcludeFromDocsAttribute" ||
                field.MetadataToken.ToInt32() != 0x04000403 || field.Name != FieldName ||
                field.Attributes != (FieldAttributes.Private | FieldAttributes.Static) ||
                field.Signature is not { HasThis: false } || !IsUnityType(field.Signature.FieldType, "UnityEngine.Shader") ||
                field.Constant != null || field.FieldRva != null || field.FieldOffset != null || field.HasFieldMarshal ||
                field.MarshalDescriptor != null || field.CustomAttributes.Count != 0 ||
                !HasNativeOwner(owner, OwnerName, ModuleName) ||
                resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != owner.NewType ||
                owner.TryGetFieldByUnityAssemblyField(field) != null ||
                owner.OriginalType.Fields.Any(candidate => candidate.Name == FieldName || candidate.Name == PropertyName) ||
                owner.NewType.Fields.Any(candidate => candidate.Name == FieldName || candidate.Name == PropertyName) ||
                owner.OriginalType.Properties.Any(candidate => candidate.Name == FieldName || candidate.Name == PropertyName) ||
                owner.NewType.Properties.Any(candidate => candidate.Name == FieldName || candidate.Name == PropertyName) ||
                owner.OriginalType.Methods.Any(candidate => candidate.Name == "get_ShaderRef_Sprite") ||
                owner.NewType.Methods.Any(candidate => candidate.Name == "get_ShaderRef_Sprite"))
                return false;

            var shader = owner.AssemblyContext.GlobalContext.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.Shader");
            if (!HasNativeOwner(shader, "UnityEngine.Shader", "UnityEngine.CoreModule") ||
                resolver.Resolve(field.Signature.FieldType)?.Resolve() != shader.NewType) return false;
            var getter = source.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == 0x0600023F);
            var initializer = source.Methods.FirstOrDefault(method => method.MetadataToken.ToInt32() == 0x06000240);
            if (!HasMethod(getter, "get_ShaderRef_Sprite", MethodAttributes.Assembly | MethodAttributes.SpecialName) ||
                !SignatureComparer.Default.Equals(getter.Signature.ReturnType, field.Signature.FieldType) ||
                owner.TryGetMethodByUnityAssemblyMethod(getter) != null ||
                !HasMethod(initializer, ".cctor", MethodAttributes.Private | MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) ||
                initializer.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                !HasGetterBody(getter, owner, resolver)) return false;
            var properties = source.Properties.Where(property => property.Name == PropertyName).ToArray();
            if (properties.Length != 1 || properties[0].Attributes != 0 || properties[0].CustomAttributes.Count != 0 ||
                properties[0].Constant != null || properties[0].GetMethod != getter ||
                properties[0].SetMethod != null || properties[0].Semantics.Count != 1 ||
                properties[0].Signature is not { HasThis: false } || properties[0].Signature.ParameterTypes.Count != 0 ||
                !SignatureComparer.Default.Equals(properties[0].Signature.ReturnType, field.Signature.FieldType)) return false;

            // Only this restored getter owns the CLR cache; all other shader caches keep their native storage.
            var references = 0;
            foreach (var method in source.DeclaringModule.GetAllTypes().SelectMany(type => type.Methods))
            {
                if (method.CilMethodBody == null) continue;
                foreach (var instruction in method.CilMethodBody.Instructions)
                {
                    if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string literal &&
                        literal.Contains(FieldName, StringComparison.Ordinal)) return false;
                    if (instruction.Operand is not IFieldDescriptor reference || reference.Name != FieldName ||
                        reference.DeclaringType?.FullName != OwnerName) continue;
                    if (method != getter || reference.Resolve() != field ||
                        !SignatureComparer.Default.Equals(reference.Signature, field.Signature) ||
                        !SignatureComparer.Default.Equals(reference.DeclaringType.ToTypeSignature(), source.ToTypeSignature())) return false;
                    var allowed = (instruction.Offset, instruction.OpCode.Code) switch
                    {
                        (0x01 or 0x20 or 0x3E or 0x5D, CilCode.Ldsfld) => true,
                        (0x1B or 0x39 or 0x57, CilCode.Stsfld) => true,
                        _ => false
                    };
                    if (!allowed) return false;
                    references++;
                }
            }
            return references == 7;
        }

        private static bool HasGetterBody(MethodDefinition getter, InteropTypeContext owner, InteropUnstripTypeResolver resolver)
        {
            var body = getter.CilMethodBody;
            if (!body.InitializeLocals || body.ExceptionHandlers.Count != 0 || body.LocalVariables.Count != 4 ||
                body.LocalVariables.Take(3).Any(local => local.VariableType is not CorLibTypeSignature { ElementType: ElementType.Boolean }) ||
                !IsUnityType(body.LocalVariables[3].VariableType, "UnityEngine.Shader")) return false;
            CilCode[] expected =
            [
                CilCode.Nop, CilCode.Ldsfld, CilCode.Ldnull, CilCode.Call, CilCode.Stloc_0, CilCode.Ldloc_0, CilCode.Brfalse_S,
                CilCode.Nop, CilCode.Ldstr, CilCode.Call, CilCode.Stsfld, CilCode.Ldsfld, CilCode.Ldnull, CilCode.Call,
                CilCode.Stloc_1, CilCode.Ldloc_1, CilCode.Brfalse_S, CilCode.Ldstr, CilCode.Call, CilCode.Stsfld,
                CilCode.Ldsfld, CilCode.Ldnull, CilCode.Call, CilCode.Stloc_2, CilCode.Ldloc_2, CilCode.Brfalse_S,
                CilCode.Ldstr, CilCode.Call, CilCode.Stsfld, CilCode.Nop, CilCode.Ldsfld, CilCode.Stloc_3,
                CilCode.Br_S, CilCode.Ldloc_3, CilCode.Ret
            ];
            var code = body.Instructions;
            if (!code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected) ||
                code[8].Operand is not "TextMeshPro/Sprite" || code[17].Operand is not "Text/Sprite" ||
                code[26].Operand is not "Hidden/TextCore/Sprite" ||
                !Targets(code[6], code[30]) || !Targets(code[16], code[20]) ||
                !Targets(code[25], code[29]) || !Targets(code[32], code[33])) return false;

            foreach (var index in new[] { 3, 9, 13, 18, 22, 27 })
            {
                var find = index is 9 or 18 or 27;
                if (code[index].Operand is not IMethodDescriptor call || call is MethodSpecification ||
                    call.Name != (find ? "Find" : "op_Equality") ||
                    !IsUnityType(call.DeclaringType?.ToTypeSignature(), find ? "UnityEngine.Shader" : "UnityEngine.Object") ||
                    !InteropDelegateContracts.HasStandardConvention(call.Signature) || call.Signature.HasThis ||
                    call.Signature.GenericParameterCount != 0 || call.Signature.ParameterTypes.Count != (find ? 1 : 2)) return false;
                if (find ? !IsUnityType(call.Signature.ReturnType, "UnityEngine.Shader") ||
                    call.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.String }
                    : call.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                    call.Signature.ParameterTypes.Any(parameter => !IsUnityType(parameter, "UnityEngine.Object"))) return false;
                var nativeOwner = owner.AssemblyContext.GlobalContext.TryGetTypeByName("UnityEngine.CoreModule", call.DeclaringType.FullName);
                if (!HasNativeOwner(nativeOwner, call.DeclaringType.FullName, "UnityEngine.CoreModule") ||
                    call.Resolve() is not { } original || original.DeclaringType.FullName != call.DeclaringType.FullName ||
                    !SignatureComparer.Default.Equals(original.Signature, call.Signature)) return false;
                var mapped = nativeOwner.TryGetMethodByUnityAssemblyMethod(original);
                if (mapped?.NonGenericMethodInfoPointerField == null || mapped.OriginalMethod.DeclaringType != nativeOwner.OriginalType ||
                    !mapped.OriginalMethod.IsStatic || mapped.NewMethod.DeclaringType != nativeOwner.NewType || !mapped.NewMethod.IsStatic ||
                    mapped.NewMethod.GenericParameters.Count != 0 ||
                    mapped.NewMethod.Signature.ParameterTypes.Count != call.Signature.ParameterTypes.Count ||
                    !SignatureComparer.Default.Equals(mapped.NewMethod.Signature.ReturnType, resolver.Resolve(call.Signature.ReturnType)) ||
                    !mapped.NewMethod.Signature.ParameterTypes.Select((parameter, position) =>
                        SignatureComparer.Default.Equals(parameter, resolver.Resolve(call.Signature.ParameterTypes[position]))).All(matches => matches)) return false;
            }
            return true;
        }

        private static bool HasNativeOwner(InteropTypeContext owner, string name, string module) =>
            owner?.OriginalType is { IsValueType: false, IsInterface: false, DeclaringType: null } original &&
            original.FullName == name && original.DeclaringModule.Assembly.Name == module && original.GenericParameters.Count == 0 &&
            owner.ClassPointerFieldRef != null && owner.ComputedTypeSpecifics == InteropTypeContext.TypeSpecifics.ReferenceType &&
            owner.NewType.FullName == name && owner.NewType.DeclaringType == null && !owner.NewType.IsValueType &&
            !owner.NewType.IsInterface && owner.NewType.GenericParameters.Count == 0 &&
            owner.NewType.DeclaringModule == owner.AssemblyContext.Imports.Module;

        private static bool HasMethod(MethodDefinition method, string name, MethodAttributes attributes) =>
            method != null && method.Name == name && method.Attributes == (attributes | MethodAttributes.Static | MethodAttributes.HideBySig) &&
            method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null && method.ImplementationMap == null &&
            method.GenericParameters.Count == 0 && method.CustomAttributes.Count == 0 &&
            InteropDelegateContracts.HasStandardConvention(method.Signature) && !method.Signature.HasThis &&
            method.Signature.GenericParameterCount == 0 && method.Signature.ParameterTypes.Count == 0 && method.ParameterDefinitions.Count == 0;

        private static bool IsUnityType(TypeSignature type, string name) =>
            type is TypeDefOrRefSignature { IsValueType: false } && type.FullName == name &&
            InteropRestoredTypeSupport.GetAssemblyName(type) == "UnityEngine.CoreModule";

        private static bool Targets(CilInstruction branch, CilInstruction target) =>
            branch.Operand is CilInstructionLabel label && label.Instruction == target;
    }
}
