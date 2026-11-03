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
        private bool TryRestoreWebFormConstructor(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "web-form-constructor-contract",
                "Web forms require the original parameterless constructor, sidecar field contract and pointer-wrapping constructor.");
            if (!InteropWebFormFields.HasOwner(source.DeclaringType, owner, imports, resolver) ||
                source.DeclaringModule.Mvid != new Guid("e3e15492-8f90-4bae-ba37-bfac955d6ba2") ||
                source.DeclaringType.MetadataToken.ToInt32() != 0x02000003 ||
                source.MetadataToken.ToInt32() != 0x06000007 ||
                source.Attributes != (MethodAttributes.Public | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) ||
                source.ImplAttributes != MethodImplAttributes.IL || !IsReferenceConstructorDeclaration(source) ||
                source.Parameters.Count != 0 || source.Signature.ParameterTypes.Count != 0 ||
                source.CilMethodBody is not { } sourceBody || !sourceBody.InitializeLocals ||
                sourceBody.ExceptionHandlers.Count != 0 || sourceBody.LocalVariables.Count != 5 ||
                !sourceBody.LocalVariables.Select(local => local.VariableType.ElementType)
                    .SequenceEqual(new[] { ElementType.I4, ElementType.I4, ElementType.Boolean,
                        ElementType.Boolean, ElementType.Boolean }) ||
                !IsReferenceConstructorDeclaration(constructor) || !constructor.IsPublic ||
                constructor.Signature.ParameterTypes.Count != 0 || constructor.Parameters.Count != 0 ||
                constructor.CilMethodBody is not { } constructorBody || constructorBody.Instructions.Count != 0 ||
                constructorBody.ExceptionHandlers.Count != 0 || constructorBody.LocalVariables.Count != 0)
                return false;

            var code = sourceBody.Instructions;
            if (code.Count < 6 || code[0].Offset != 0 || code[0].OpCode.Code != CilCode.Ldarg_0 ||
                code[1].Offset != 1 || code[1].OpCode.Code != CilCode.Ldc_I4_0 ||
                code[2].Offset != 2 || code[2].OpCode.Code != CilCode.Stfld ||
                code[2].Operand is not IFieldDescriptor containsFiles || containsFiles.Name != "containsFiles" ||
                containsFiles.Resolve() is not { } field || field.DeclaringType != source.DeclaringType || field.IsStatic ||
                field.Signature.FieldType is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                !SignatureComparer.Default.Equals(containsFiles.Signature, field.Signature) ||
                code[3].Offset != 7 || code[3].OpCode.Code != CilCode.Ldarg_0 ||
                code[4].Offset != 8 || code[4].OpCode.Code != CilCode.Call ||
                code[4].Operand is not IMethodDescriptor baseCall || baseCall is MethodSpecification ||
                baseCall.Name != ".ctor" || baseCall.DeclaringType?.FullName != "System.Object" ||
                !SignatureComparer.Default.Equals(baseCall.DeclaringType, source.DeclaringType.BaseType) ||
                InteropRestoredTypeSupport.GetAssemblyName(baseCall.DeclaringType.ToTypeSignature()) is not ("mscorlib" or "netstandard") ||
                !InteropDelegateContracts.HasStandardConvention(baseCall.Signature) ||
                !SignatureComparer.Default.Equals(baseCall.Signature,
                    MethodSignature.CreateInstance(source.DeclaringModule.CorLibTypeFactory.Void)) ||
                code[^1].OpCode.Code != CilCode.Ret ||
                code.Any(instruction => instruction != code[4] &&
                    instruction.OpCode.Code is CilCode.Call or CilCode.Callvirt &&
                    instruction.Operand is IMethodDescriptor call && call.Name == ".ctor"))
                return false;

            var pointerSignature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                imports.Module.CorLibTypeFactory.IntPtr);
            var pointers = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, pointerSignature)).ToArray();
            if (pointers.Length != 1 || !pointers[0].IsPublic || !IsReferenceConstructorDeclaration(pointers[0]) ||
                pointers[0].CilMethodBody == null || owner.ClassPointerFieldRef == null)
                return false;

            const string name = "__InitializeWebForm_06000007";
            if (owner.NewType.Methods.Any(method => method.Name == name || method.Name == ".ctor" &&
                    SignatureComparer.Default.Equals(method.Signature, constructor.Signature)) ||
                owner.TryGetRestoredBody(source) != null || owner.TryGetRestoredReferenceConstructor(source) != null)
            {
                entry.Set("signature-rejected", "web-form-constructor-collision",
                    "A constructor, initializer or restored constructor mapping already exists.");
                return false;
            }

            var initializer = new MethodDefinition(name, MethodAttributes.Family | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
            {
                CilMethodBody = new CilMethodBody()
            };
            owner.NewType.Methods.Add(constructor);
            owner.NewType.Methods.Add(initializer);
            owner.AddRestoredReferenceConstructor(source, constructor, initializer);
            owner.AddRestoredBody(source, constructor);
            referenceConstructorBodies.Add(constructor, initializer);
            // Translation preserves the original list allocations, field writes and random boundary generation order.
            pendingBodies.Add((source, initializer, owner, imports, entry));
            entry.Set("body-pending");

            var body = constructorBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef);
            body.Add(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value);
            // Root the single native allocation before the initializer can allocate or populate sidecar fields.
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(pointers[0]));
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(initializer));
            body.Add(CilOpCodes.Ret);
            return true;
        }
    }
}
