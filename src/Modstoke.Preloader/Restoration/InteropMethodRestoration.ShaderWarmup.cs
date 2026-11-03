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
    internal sealed partial class InteropMethodRestoration
    {
        private const string ShaderWarmupOwner = "UnityEngine.Experimental.Rendering.ShaderWarmup";
        private const string ShaderWarmupSetup = "UnityEngine.Experimental.Rendering.ShaderWarmupSetup";

        // Consume rejected family members too: the CLR setup must never reach a generic native-call stub.
        private static bool TryRestoreShaderWarmup(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                source.DeclaringType.FullName != ShaderWarmupOwner ||
                (string)source.Name is not ("WarmupShader" or "WarmupShaderFromCollection" or
                    "WarmupShader_Injected" or "WarmupShaderFromCollection_Injected"))
                return false;

            entry.Set("signature-rejected", "shader-warmup-contract",
                "Shader warmup requires matched declarations and forwarding bodies, native shader and descriptor identities, and an exact CLR setup array field.");
            var module = source.DeclaringModule;
            var sourceOwner = source.DeclaringType;
            if (module.Mvid != new Guid("9b3c2d40-4437-435a-8a05-e8ae563b270c") ||
                sourceOwner.MetadataToken.ToInt32() != 0x020004D8 ||
                !HasShaderWarmupOwner(sourceOwner, owner, imports.Module, resolver)) return true;

            var sourceSetup = module.TopLevelTypes.FirstOrDefault(type => type.FullName == ShaderWarmupSetup);
            var setup = owner.AssemblyContext.TryGetTypeByName(ShaderWarmupSetup);
            if (sourceSetup == null || !HasShaderWarmupSetup(sourceSetup, true) ||
                sourceSetup.MetadataToken.ToInt32() != 0x020004D7 ||
                setup == null || setup.OriginalType != null || setup.ClassPointerFieldRef != null ||
                setup.NewType.DeclaringModule != imports.Module || !HasShaderWarmupSetup(setup.NewType, false) ||
                resolver.ResolveRaw(sourceSetup.ToTypeSignature())?.Resolve() != setup.NewType ||
                sourceSetup.Fields[0].Signature.FieldType is not SzArrayTypeSignature
                    { BaseType: TypeDefOrRefSignature { IsValueType: true } sourceVertexRef } ||
                sourceVertexRef.Resolve() is not { } sourceVertex || sourceVertex.DeclaringModule != module)
                return true;

            var context = owner.AssemblyContext.GlobalContext;
            var vertex = context.TryGetTypeByName("UnityEngine.CoreModule", "UnityEngine.Rendering.VertexAttributeDescriptor");
            if (!HasShaderWarmupVertex(sourceVertex, vertex, imports.Module, resolver)) return true;
            var vertexType = vertex.NewType.ToTypeSignature();
            var arrayType = new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false, vertexType);
            var field = setup.NewType.Fields[0];
            if (!SignatureComparer.Default.Equals(resolver.Resolve(sourceSetup.Fields[0].Signature.FieldType), arrayType) ||
                !SignatureComparer.Default.Equals(field.Signature.FieldType, arrayType)) return true;

            string[] referenceNames = ["UnityEngine.Shader", "UnityEngine.ShaderVariantCollection"];
            var sourceReferences = new TypeDefinition[referenceNames.Length];
            var references = new TypeSignature[referenceNames.Length];
            for (var index = 0; index < referenceNames.Length; index++)
            {
                var original = module.TopLevelTypes.FirstOrDefault(type => type.FullName == referenceNames[index]);
                var targetType = context.TryGetTypeByName("UnityEngine.CoreModule", referenceNames[index]);
                if (original == null || original.IsValueType || original.IsInterface || original.GenericParameters.Count != 0 ||
                    !HasParticleTriggerNativeReference(targetType, "UnityEngine.CoreModule", referenceNames[index], referenceNames[index], 0) ||
                    targetType.NewType.DeclaringModule != imports.Module ||
                    resolver.ResolveRaw(original.ToTypeSignature())?.Resolve() != targetType.NewType) return true;
                sourceReferences[index] = original;
                references[index] = targetType.NewType.ToTypeSignature();
            }

            string[] names = ["WarmupShader", "WarmupShaderFromCollection", "WarmupShader_Injected", "WarmupShaderFromCollection_Injected"];
            var methods = new MethodDefinition[names.Length];
            var signatures = new MethodSignature[names.Length];
            var setupType = setup.NewType.ToTypeSignature();
            for (var index = 0; index < names.Length; index++)
            {
                var declarations = sourceOwner.Methods.Where(method => method.Name == names[index]).ToArray();
                if (declarations.Length != 1) return true;
                var method = declarations[0];
                var collection = index % 2 != 0;
                var injected = index >= 2;
                if (!HasShaderWarmupDeclaration(method, sourceOwner, sourceSetup, sourceReferences,
                        0x06002AEC + index, collection, injected)) return true;
                TypeSignature value = injected ? setupType.MakeByReferenceType() : setupType;
                TypeSignature[] parameters = collection ? [references[1], references[0], value] : [references[0], value];
                for (var parameter = 0; parameter < parameters.Length; parameter++)
                    if (!SignatureComparer.Default.Equals(resolver.Resolve(method.Signature.ParameterTypes[parameter]), parameters[parameter]))
                        return true;
                var signature = MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void, parameters);
                var restored = owner.TryGetRestoredManagedMethod(method);
                if (owner.NewType.Methods.Any(candidate => candidate.Name == method.Name &&
                    (candidate != restored || candidate.Attributes != method.Attributes ||
                     candidate.ImplAttributes != MethodImplAttributes.IL || candidate.CilMethodBody == null ||
                     !SignatureComparer.Default.Equals(candidate.Signature, signature)))) return true;
                methods[index] = method;
                signatures[index] = signature;
            }
            if (!HasShaderWarmupBody(methods[0], methods[2], false) ||
                !HasShaderWarmupBody(methods[1], methods[3], true) ||
                owner.NewType.Methods.Any(method => method.Name == source.Name)) return true;
            var sourceIndex = Array.IndexOf(methods, source);
            if (sourceIndex < 0) return true;

            var target = new MethodDefinition(source.Name, source.Attributes,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void))
            {
                ImplAttributes = MethodImplAttributes.IL | MethodImplAttributes.Managed,
                CilMethodBody = new CilMethodBody()
            };
            for (var index = 0; index < source.Parameters.Count; index++)
                InteropMethodDeclarations.AddParameter(target, signatures[sourceIndex].ParameterTypes[index],
                    source.Parameters[index].Name, source.Parameters[index].Definition.Attributes);

            var body = target.CilMethodBody.Instructions;
            var last = target.Parameters.Count - 1;
            for (var index = 0; index < last; index++) body.Add(CilOpCodes.Ldarg, target.Parameters[index]);
            if (sourceIndex >= 2)
            {
                // The native setup slot is read-only; pass its array wrapper without copying or reinterpreting the CLR setup.
                body.Add(CilOpCodes.Ldarg, target.Parameters[last]);
                body.Add(CilOpCodes.Ldfld, field);
                var helper = sourceIndex == 2 ? nameof(InteropShaderWarmupSupport.Warmup) : nameof(InteropShaderWarmupSupport.WarmupFromCollection);
                body.Add(CilOpCodes.Call, ((IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropShaderWarmupSupport).GetMethod(helper))).MakeGenericInstanceMethod(vertexType));
            }
            else
            {
                body.Add(CilOpCodes.Ldarga, target.Parameters[last]);
                // The injected declaration may be restored later in the loop.
                body.Add(CilOpCodes.Call, new MemberReference(owner.NewType, methods[sourceIndex + 2].Name, signatures[sourceIndex + 2]));
            }
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            entry.Set("native-shader-warmup-adapter", null,
                "Typed native descriptor array extracted from CLR setup, with read-only setup semantics and preserved public forwarding.");
            return true;
        }

        private static bool HasShaderWarmupOwner(TypeDefinition source, InteropTypeContext target,
            ModuleDefinition module, InteropUnstripTypeResolver resolver) =>
            source.IsPublic && source.IsAbstract && source.IsSealed && !source.IsValueType && !source.IsInterface &&
            source.DeclaringType == null && source.GenericParameters.Count == 0 && source.Fields.Count == 0 &&
            HasScreenLayoutFrameworkType(source.BaseType, "System.Object") &&
            target.OriginalType == null && target.ClassPointerFieldRef == null &&
            target.NewType.FullName == ShaderWarmupOwner && target.NewType.IsPublic &&
            target.NewType.IsAbstract && target.NewType.IsSealed && !target.NewType.IsValueType && !target.NewType.IsInterface &&
            target.NewType.DeclaringType == null && target.NewType.GenericParameters.Count == 0 && target.NewType.Fields.Count == 0 &&
            target.NewType.DeclaringModule == module && module.Assembly.Name == "UnityEngine.CoreModule" &&
            SignatureComparer.Default.Equals(target.NewType.BaseType?.ToTypeSignature(), module.CorLibTypeFactory.Object) &&
            resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() == target.NewType;

        private static bool HasShaderWarmupSetup(TypeDefinition type, bool source)
        {
            if (type.FullName != ShaderWarmupSetup || !type.IsPublic || !type.IsSealed || !type.IsValueType || type.IsEnum ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || type.BaseType?.FullName != "System.ValueType" ||
                (source ? !type.IsSequentialLayout : !type.IsAutoLayout) || type.ClassLayout != null || type.Fields.Count != 1)
                return false;
            var field = type.Fields[0];
            return field.Name == "vdecl" && field.Attributes == FieldAttributes.Public && field.Signature != null &&
                field.FieldOffset == null && !field.HasFieldMarshal && field.MarshalDescriptor == null &&
                field.FieldRva == null && field.Constant == null &&
                (!source || field.MetadataToken.ToInt32() == 0x040010E4);
        }

        private static bool HasShaderWarmupVertex(TypeDefinition source, InteropTypeContext target,
            ModuleDefinition module, InteropUnstripTypeResolver resolver)
        {
            const string name = "UnityEngine.Rendering.VertexAttributeDescriptor";
            if (source.FullName != name || !source.IsValueType || source.IsEnum || !source.IsSequentialLayout ||
                !source.IsPublic || source.GenericParameters.Count != 0 || source.DeclaringType != null || source.Fields.Count != 4 ||
                target?.OriginalType == null || target.ClassPointerFieldRef == null ||
                target.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.BlittableStruct ||
                target.OriginalType.FullName != name || target.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.CoreModule" ||
                !target.OriginalType.IsValueType || target.OriginalType.IsEnum || target.OriginalType.GenericParameters.Count != 0 ||
                target.OriginalType.DeclaringType != null || target.NewType.FullName != name ||
                !target.NewType.IsValueType || target.NewType.IsEnum || !target.NewType.IsExplicitLayout ||
                !target.NewType.IsPublic || target.NewType.GenericParameters.Count != 0 || target.NewType.DeclaringType != null ||
                target.NewType.DeclaringModule != module || resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != target.NewType)
                return false;
            var fields = target.NewType.Fields.Where(field => !field.IsStatic).ToArray();
            if (fields.Length != 4) return false;
            string[] names = ["attribute", "format", "dimension", "stream"];
            string[] types = ["UnityEngine.Rendering.VertexAttribute", "UnityEngine.Rendering.VertexAttributeFormat", "System.Int32", "System.Int32"];
            for (var index = 0; index < fields.Length; index++)
            {
                var original = source.Fields[index];
                var field = fields[index];
                // Managed field names are normalized; native lookups keep the original angle-bracket names.
                if (original.Name != "<" + names[index] + ">k__BackingField" || original.Attributes != FieldAttributes.Private ||
                    original.Signature?.FieldType.FullName != types[index] || original.FieldOffset != null ||
                    original.HasFieldMarshal || original.MarshalDescriptor != null || original.FieldRva != null ||
                    field.Name != "_" + names[index] + "_k__BackingField" || field.Attributes != FieldAttributes.Public ||
                    field.FieldOffset != index * 4 || field.HasFieldMarshal || field.MarshalDescriptor != null || field.FieldRva != null ||
                    !SignatureComparer.Default.Equals(field.Signature?.FieldType, resolver.Resolve(original.Signature.FieldType)))
                    return false;
            }
            return true;
        }

        private static bool HasShaderWarmupDeclaration(MethodDefinition method, TypeDefinition owner, TypeDefinition setup,
            TypeDefinition[] references, int token, bool collection, bool injected)
        {
            var count = collection ? 3 : 2;
            if (method.DeclaringType != owner || method.MetadataToken.ToInt32() != token ||
                method.Attributes != ((injected ? MethodAttributes.Private : MethodAttributes.Public) | MethodAttributes.Static | MethodAttributes.HideBySig) ||
                method.ImplAttributes != (injected ? MethodImplAttributes.InternalCall : MethodImplAttributes.IL) ||
                method.HasMethodBody == injected || method.GenericParameters.Count != 0 ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) || method.Signature.HasThis ||
                method.Signature.GenericParameterCount != 0 ||
                method.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                method.Parameters.Count != count || method.Signature.ParameterTypes.Count != count ||
                method.ParameterDefinitions.Count != count ||
                method.Parameters.Any(parameter => parameter.Definition is not { } definition || definition.Attributes != 0 ||
                    definition.MarshalDescriptor != null || definition.Constant != null || definition.CustomAttributes.Count != 0))
                return false;
            for (var index = 0; index < count - 1; index++)
                if (method.Signature.ParameterTypes[index] is not TypeDefOrRefSignature { IsValueType: false } reference ||
                    reference.Resolve() != references[collection && index == 0 ? 1 : 0]) return false;
            var value = method.Signature.ParameterTypes[count - 1];
            if (injected)
            {
                if (value is not ByReferenceTypeSignature byReference) return false;
                value = byReference.BaseType;
            }
            return value is TypeDefOrRefSignature { IsValueType: true } setupRef && setupRef.Resolve() == setup &&
                (injected ? method.CilMethodBody == null : method.CilMethodBody != null);
        }

        private static bool HasShaderWarmupBody(MethodDefinition method, MethodDefinition callee, bool collection)
        {
            var body = method.CilMethodBody;
            var code = body.Instructions;
            CilCode[] expected = collection
                ? [CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Ldarga_S, CilCode.Call, CilCode.Ret]
                : [CilCode.Ldarg_0, CilCode.Ldarga_S, CilCode.Call, CilCode.Ret];
            var address = collection ? 2 : 1;
            return !body.InitializeLocals && body.LocalVariables.Count == 0 && body.ExceptionHandlers.Count == 0 &&
                code.Count == expected.Length && !code.Where((instruction, index) => instruction.OpCode.Code != expected[index]).Any() &&
                code[address].Operand == method.Parameters[address] &&
                code[address + 1].Operand is IMethodDescriptor call && call is not MethodSpecification &&
                call.Resolve() == callee && SignatureComparer.Default.Equals(call.Signature, callee.Signature);
        }
    }
}
