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
        private bool TryRestorePropagationPathsCopy(MethodDefinition source, MethodDefinition constructor,
            InteropTypeContext owner, InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver,
            InteropRestorationEntry entry)
        {
            entry.Set("signature-rejected", "propagation-copy-source-contract",
                "The copy requires the original Object base call and three ordered, independently allocated native lists.");
            if (!TryGetPropagationPathsCopy(source, out var code)) return false;

            entry.Set("signature-rejected", "propagation-copy-native-storage",
                "The native class must retain all three list fields and its pointer-wrapping constructor.");
            var original = owner.OriginalType;
            var pointerSignature = MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void,
                imports.Module.CorLibTypeFactory.IntPtr);
            var pointerConstructors = owner.NewType.Methods.Where(method => method.Name == ".ctor" &&
                SignatureComparer.Default.Equals(method.Signature, pointerSignature)).ToArray();
            if (original.BaseType?.FullName != "System.Object" || original.IsAbstract || original.Interfaces.Count != 0 ||
                original.Fields.Count(field => !field.IsStatic) != 3 ||
                original.Methods.Any(method => !method.IsStatic && method.Name == "Finalize") ||
                pointerConstructors.Length != 1 || pointerConstructors[0].CilMethodBody == null ||
                !pointerConstructors[0].IsPublic || pointerConstructors[0].GenericParameters.Count != 0)
                return false;
            for (var index = 0; index < 3; index++)
                if (!HasPropagationPathField(((IFieldDescriptor)code[4 + index * 5].Operand).Resolve(),
                    owner, imports, resolver)) return false;

            var originalBody = source.CilMethodBody;
            var copyBody = new CilMethodBody();
            foreach (var instruction in code.Skip(2))
                copyBody.Instructions.Add(new CilInstruction(instruction.OpCode, instruction.Operand));
            try
            {
                // Translate the source copies, including native IEnumerable conversion, allocation and write barriers.
                source.CilMethodBody = copyBody;
                if (!InteropMethodTranslator.TranslateMethod(source, constructor, owner, imports, out var failure))
                {
                    entry.Set("body-rejected", failure?.Reason, failure?.Detail);
                    entry.Failure = failure;
                    return false;
                }
            }
            finally { source.CilMethodBody = originalBody; }

            var body = constructor.CilMethodBody.Instructions;
            var start = body[0];
            var finish = new CilInstruction(CilOpCodes.Ret);
            foreach (var instruction in body.Where(instruction => instruction.OpCode.Code == CilCode.Ret))
            {
                instruction.OpCode = CilOpCodes.Leave;
                instruction.Operand = finish.CreateLabel();
            }
            var keepAlive = imports.Module.DefaultImporter.ImportMethod(
                typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) }));
            var cleanup = body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, keepAlive);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Call, keepAlive);
            body.Add(CilOpCodes.Endfinally);
            body.Add(finish);
            constructor.CilMethodBody.ExceptionHandlers.Add(new CilExceptionHandler
            {
                HandlerType = CilExceptionHandlerType.Finally,
                TryStart = start.CreateLabel(), TryEnd = cleanup.CreateLabel(),
                HandlerStart = cleanup.CreateLabel(), HandlerEnd = finish.CreateLabel()
            });

            // Object::.ctor has no instance initialization. Allocate/root the native object once,
            // then keep the source's copy order; calling the default ctor would allocate three extra lists.
            CilInstruction[] prefix =
            [
                new(CilOpCodes.Ldarg_0),
                new(CilOpCodes.Ldsfld, owner.ClassPointerFieldRef),
                new(CilOpCodes.Call, imports.IL2CPP_il2cpp_object_new.Value),
                new(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(pointerConstructors[0]))
            ];
            for (var index = 0; index < prefix.Length; index++) body.Insert(index, prefix[index]);

            owner.NewType.Methods.Add(constructor);
            owner.AddRestoredReferenceConstructor(source, constructor);
            completedBodies.Add((constructor, entry));
            entry.Set("native-reference-copy-constructor-adapter", null,
                "Fresh native storage and three ordered list copies; no default-list allocation, pool borrowing or source-list aliasing.");
            return true;
        }

        private static bool TryGetPropagationPathsCopy(MethodDefinition source, out CilInstruction[] code)
        {
            code = null;
            var type = source.DeclaringType;
            var module = source.DeclaringModule;
            if (module.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.MetadataToken.ToInt32() != 0x06000F9B || type.MetadataToken.ToInt32() != 0x02000224 ||
                type.BaseType?.FullName != "System.Object" || type.IsValueType || type.IsInterface || type.IsAbstract ||
                type.GenericParameters.Count != 0 || type.Interfaces.Count != 0 ||
                type.Fields.Count(field => !field.IsStatic) != 3 ||
                !source.IsConstructor || source.IsStatic || !source.IsPublic || source.GenericParameters.Count != 0 ||
                source.ImplAttributes != MethodImplAttributes.IL ||
                !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                source.Signature.GenericParameterCount != 0 || !source.Signature.HasThis ||
                source.Signature.ReturnType.ElementType != ElementType.Void || source.Signature.ParameterTypes.Count != 1 ||
                !SignatureComparer.Default.Equals(source.Signature.ParameterTypes[0], type.ToTypeSignature()) ||
                source.CilMethodBody is not { } body || body.LocalVariables.Count != 0 || body.ExceptionHandlers.Count != 0)
                return false;
            code = body.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            if (code.Length != 18 || code[0].OpCode.Code != CilCode.Ldarg_0 || code[1].OpCode.Code != CilCode.Call ||
                code[^1].OpCode.Code != CilCode.Ret || code[1].Operand is not IMethodDescriptor baseConstructor ||
                baseConstructor is MethodSpecification || baseConstructor.Name != ".ctor" ||
                baseConstructor.DeclaringType is not ITypeDefOrRef baseType || baseType.FullName != "System.Object" ||
                !InteropArraySegmentLowering.HasSourceContract(baseType) ||
                !SignatureComparer.Default.Equals(baseConstructor.Signature,
                    MethodSignature.CreateInstance(module.CorLibTypeFactory.Void))) return false;

            string[] names = ["trickleDownPath", "targetElements", "bubbleUpPath"];
            for (var index = 0; index < names.Length; index++)
            {
                var start = 2 + index * 5;
                if (code[start].OpCode.Code != CilCode.Ldarg_0 || code[start + 1].OpCode.Code != CilCode.Ldarg_1 ||
                    code[start + 2].OpCode.Code != CilCode.Ldfld || code[start + 3].OpCode.Code != CilCode.Newobj ||
                    code[start + 4].OpCode.Code != CilCode.Stfld ||
                    code[start + 2].Operand is not IFieldDescriptor read || read.Resolve() is not { } field ||
                    code[start + 4].Operand is not IFieldDescriptor write || write.Resolve() != field ||
                    field.DeclaringType != type || field.Name != names[index] ||
                    field.MetadataToken.ToInt32() != 0x040006F3 + index ||
                    field.Attributes != (FieldAttributes.Public | FieldAttributes.InitOnly) ||
                    field.FieldRva != null || field.HasFieldMarshal || field.Constant != null ||
                    !SignatureComparer.Default.Equals(read.Signature, field.Signature) ||
                    !SignatureComparer.Default.Equals(write.Signature, field.Signature) ||
                    field.Signature.FieldType is not GenericInstanceTypeSignature list || list.IsValueType ||
                    list.GenericType.FullName != "System.Collections.Generic.List`1" ||
                    !InteropArraySegmentLowering.HasSourceContract(list.GenericType) || list.TypeArguments.Count != 1 ||
                    list.TypeArguments[0].FullName != "UnityEngine.UIElements.VisualElement" ||
                    list.TypeArguments[0].Resolve()?.DeclaringModule != module ||
                    code[start + 3].Operand is not IMethodDescriptor copy || copy is MethodSpecification ||
                    copy.Name != ".ctor" || !SignatureComparer.Default.Equals(copy.DeclaringType?.ToTypeSignature(), list) ||
                    !InteropDelegateContracts.HasStandardConvention(copy.Signature) ||
                    copy.Signature is not { HasThis: true, GenericParameterCount: 0 } ||
                    copy.Signature.ReturnType.ElementType != ElementType.Void || copy.Signature.ParameterTypes.Count != 1 ||
                    copy.Signature.ParameterTypes[0].InstantiateGenericTypes(GenericContext.FromMethod(copy))
                        is not GenericInstanceTypeSignature enumerable || enumerable.IsValueType ||
                    enumerable.GenericType.FullName != "System.Collections.Generic.IEnumerable`1" ||
                    !InteropArraySegmentLowering.HasSourceContract(enumerable.GenericType) || enumerable.TypeArguments.Count != 1 ||
                    !SignatureComparer.Default.Equals(enumerable.TypeArguments[0], list.TypeArguments[0]))
                    return false;
            }
            return true;
        }

        private static bool HasPropagationPathField(FieldDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver)
        {
            var field = owner.TryGetFieldByUnityAssemblyField(source);
            if (field == null || field.DeclaringType != owner || field.PointerField == null ||
                field.OriginalField.DeclaringType != owner.OriginalType || field.OriginalField.IsStatic ||
                !field.OriginalField.IsInitOnly || field.OriginalField.IsLiteral ||
                owner.NewType.Fields.Any(candidate => candidate.Name == source.Name || candidate.Name == field.UnmangledName))
                return false;
            var fieldType = resolver.Resolve(source.Signature.FieldType);
            var properties = owner.NewType.Properties.Where(property => property.Name == field.UnmangledName).ToArray();
            if (fieldType == null || properties.Length != 1) return false;
            var getter = properties[0].GetMethod;
            var setter = properties[0].SetMethod;
            return IsAccessor(getter) && IsAccessor(setter) &&
                SignatureComparer.Default.Equals(getter.Signature, MethodSignature.CreateInstance(fieldType)) &&
                SignatureComparer.Default.Equals(setter.Signature,
                    MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, fieldType));

            bool IsAccessor(MethodDefinition method) => method is { IsPublic: true, IsStatic: false, IsVirtual: false, IsAbstract: false } &&
                method.DeclaringType == owner.NewType && method.CilMethodBody != null && method.GenericParameters.Count == 0 &&
                InteropDelegateContracts.HasStandardConvention(method.Signature);
        }
    }
}
