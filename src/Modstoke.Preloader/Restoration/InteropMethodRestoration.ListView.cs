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
        private bool TryRestoreListViewMultiEdit(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                source.DeclaringType.FullName != "UnityEngine.UIElements.BaseListView" ||
                source.Name != "SetOverMaxMultiEditLimit") return false;

            entry.Set("signature-rejected", "list-view-multi-edit-source-contract",
                "Multi-edit restoration requires the exact source body and assembly-wide exclusive count store/read.");
            if (!TryGetListViewMultiEditSource(source, out var code)) return true;

            entry.Set("signature-rejected", "list-view-multi-edit-native-contract",
                "Multi-edit restoration requires native BaseListView identity, Boolean/string setters and no count storage.");
            if (owner.OriginalType == null || owner.ClassPointerFieldRef == null ||
                owner.OriginalType.FullName != source.DeclaringType.FullName || owner.OriginalType.IsValueType ||
                owner.OriginalType.IsInterface || owner.OriginalType.GenericParameters.Count != 0 ||
                owner.OriginalType.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                owner.NewType.FullName != source.DeclaringType.FullName || owner.NewType.DeclaringModule != imports.Module ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.GenericParameters.Count != 0 ||
                resolver.ResolveRaw(source.DeclaringType.ToTypeSignature())?.Resolve() != owner.NewType ||
                owner.OriginalType.Fields.Any(field => field.Name == "m_MaxMultiEditCount") ||
                owner.NewType.Fields.Any(field => field.Name == "m_MaxMultiEditCount") ||
                owner.NewType.Properties.Any(property => property.Name == "m_MaxMultiEditCount") ||
                !HasListViewMultiEditSetter((IFieldDescriptor)code[2].Operand, owner, imports, resolver) ||
                !HasListViewMultiEditSetter((IFieldDescriptor)code[12].Operand, owner, imports, resolver))
                return true;

            var target = new MethodDefinition(source.Name, MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void))
                { CilMethodBody = new CilMethodBody() };
            foreach (var parameter in source.Parameters)
                InteropMethodDeclarations.AddParameter(target, resolver.Resolve(parameter.ParameterType), parameter.Name, default);
            if (owner.NewType.Methods.Any(method => method.Name == target.Name &&
                SignatureComparer.Default.Equals(method.Signature, target.Signature)))
            {
                entry.Set("signature-rejected", "list-view-multi-edit-target-conflict",
                    "A matching declaration already exists; the adapter does not replace native wrappers.");
                return true;
            }

            var originalBody = source.CilMethodBody;
            var reducedBody = new CilMethodBody();
            var body = reducedBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Stfld, (IFieldDescriptor)code[2].Operand);
            body.Add(CilOpCodes.Ldarg_0);
            body.Add(CilOpCodes.Ldstr, (string)code[7].Operand);
            body.Add(CilOpCodes.Ldarg_2);
            body.Add(CilOpCodes.Box, (ITypeDefOrRef)code[10].Operand);
            body.Add(CilOpCodes.Call, (IMethodDescriptor)code[11].Operand);
            body.Add(CilOpCodes.Stfld, (IFieldDescriptor)code[12].Operand);
            body.Add(CilOpCodes.Ret);
            try
            {
                // Translate fresh instructions so macro expansion cannot mutate the qualified source body.
                // Retain native boxing/formatting and setter semantics, including the first null check.
                source.CilMethodBody = reducedBody;
                if (!InteropMethodTranslator.TranslateMethod(source, target, owner, imports, out var failure))
                {
                    entry.Set("body-rejected", failure?.Reason, failure?.Detail);
                    entry.Failure = failure;
                    return true;
                }
            }
            finally
            {
                source.CilMethodBody = originalBody;
            }

            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            completedBodies.Add((target, entry));
            entry.Set("native-list-view-multi-edit-adapter", null,
                "Exact-source count store/read eliminated; native Boolean update, original formatting and native string update retained in order.");
            return true;
        }

        private static bool TryGetListViewMultiEditSource(MethodDefinition source, out CilInstruction[] code)
        {
            code = null;
            var type = source.DeclaringType;
            var module = source.DeclaringModule;
            if (module.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                module.Assembly.Modules.Count != 1 || source.MetadataToken.ToInt32() != 0x060003C0 ||
                type.MetadataToken.ToInt32() != 0x0200005B ||
                type.Attributes != (TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.BeforeFieldInit) ||
                type.DeclaringType != null || type.GenericParameters.Count != 0 || type.CustomAttributes.Count != 0 ||
                type.BaseType?.FullName != "UnityEngine.UIElements.BaseVerticalCollectionView" ||
                type.BaseType.Resolve()?.DeclaringModule != module ||
                source.Attributes != (MethodAttributes.Assembly | MethodAttributes.HideBySig) ||
                source.ImplAttributes != MethodImplAttributes.IL || source.GenericParameters.Count != 0 ||
                source.CustomAttributes.Count != 0 || source.Signature.GenericParameterCount != 0 ||
                !source.Signature.HasThis || !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                source.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void } ||
                source.Signature.ParameterTypes.Count != 2 || source.Parameters.Count != 2 ||
                source.Signature.ParameterTypes[0] is not CorLibTypeSignature { ElementType: ElementType.Boolean } ||
                source.Signature.ParameterTypes[1] is not CorLibTypeSignature { ElementType: ElementType.I4 } ||
                source.Parameters.Any(parameter => parameter.Definition == null || parameter.Definition.Attributes != 0 ||
                    parameter.Definition.MarshalDescriptor != null || parameter.Definition.Constant != null) ||
                source.CilMethodBody is not { } body || body.InitializeLocals || body.LocalVariables.Count != 0 ||
                body.ExceptionHandlers.Count != 0) return false;

            code = body.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            CilCode[] expected =
            [
                CilCode.Ldarg_0, CilCode.Ldarg_1, CilCode.Stfld,
                CilCode.Ldarg_0, CilCode.Ldarg_2, CilCode.Stfld,
                CilCode.Ldarg_0, CilCode.Ldstr, CilCode.Ldarg_0, CilCode.Ldfld,
                CilCode.Box, CilCode.Call, CilCode.Stfld, CilCode.Ret
            ];
            if (!code.Select(instruction => instruction.OpCode.Code).SequenceEqual(expected) ||
                !IsListViewMultiEditField(code[2], type, 0x04000136, "m_IsOverMultiEditLimit", ElementType.Boolean) ||
                !IsListViewMultiEditField(code[5], type, 0x04000137, "m_MaxMultiEditCount", ElementType.I4) ||
                !IsListViewMultiEditField(code[9], type, 0x04000137, "m_MaxMultiEditCount", ElementType.I4) ||
                !IsListViewMultiEditField(code[12], type, 0x04000151, "m_MaxMultiEditStr", ElementType.String) ||
                code[7].Operand is not string message ||
                message != "This field cannot display arrays with more than {0} elements when multiple objects are selected." ||
                code[10].Operand is not ITypeDefOrRef boxed ||
                !SignatureComparer.Default.Equals(boxed.ToTypeSignature(), module.CorLibTypeFactory.Int32) ||
                code[11].Operand is not IMethodDescriptor format || format.Name != "Format" ||
                format.DeclaringType?.FullName != "System.String" ||
                InteropRestoredTypeSupport.GetAssemblyName(format.DeclaringType.ToTypeSignature()) != "netstandard" ||
                !InteropDelegateContracts.HasStandardConvention(format.Signature) ||
                !SignatureComparer.Default.Equals(format.Signature, MethodSignature.CreateStatic(
                    module.CorLibTypeFactory.String, module.CorLibTypeFactory.String, module.CorLibTypeFactory.Object)))
                return false;

            var count = ((IFieldDescriptor)code[5].Operand).Resolve();
            var references = 0;
            foreach (var candidate in module.GetAllTypes().SelectMany(candidate => candidate.Methods))
            {
                if (candidate.CilMethodBody == null) continue;
                foreach (var instruction in candidate.CilMethodBody.Instructions)
                {
                    if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string literal &&
                        literal.Contains("m_MaxMultiEditCount", StringComparison.Ordinal)) return false;
                    if (instruction.Operand is not IFieldDescriptor field || field.Name != count.Name ||
                        field.DeclaringType?.FullName != type.FullName) continue;
                    if (field.Resolve() != count || candidate != source ||
                        instruction != code[5] && instruction != code[9]) return false;
                    references++;
                }
            }
            return references == 2;
        }

        private static bool IsListViewMultiEditField(CilInstruction instruction, TypeDefinition owner,
            int token, string name, ElementType element) =>
            instruction.Operand is IFieldDescriptor field && field.Resolve() is { } definition &&
            definition.DeclaringType == owner && definition.MetadataToken.ToInt32() == token && definition.Name == name &&
            definition.Attributes == FieldAttributes.Private && definition.CustomAttributes.Count == 0 &&
            definition.FieldOffset == null && definition.FieldRva == null && definition.Constant == null &&
            !definition.HasFieldMarshal && definition.Signature.FieldType is CorLibTypeSignature primitive &&
            primitive.ElementType == element && SignatureComparer.Default.Equals(field.Signature, definition.Signature);

        private static bool HasListViewMultiEditSetter(IFieldDescriptor source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver)
        {
            var field = owner.TryGetFieldByUnityAssemblyField(source.Resolve());
            if (field == null || field.DeclaringType != owner || field.PointerField == null ||
                field.OriginalField.DeclaringType != owner.OriginalType || field.OriginalField.IsStatic ||
                field.OriginalField.IsInitOnly || field.OriginalField.IsLiteral ||
                owner.NewType.Fields.Any(candidate => candidate.Name == source.Name || candidate.Name == field.UnmangledName))
                return false;
            var setters = owner.NewType.Properties.Where(property => property.Name == field.UnmangledName)
                .Select(property => property.SetMethod).ToArray();
            return setters.Length == 1 && setters[0] is { } setter && setter.DeclaringType == owner.NewType &&
                setter.IsPublic && !setter.IsStatic && !setter.IsVirtual && !setter.IsAbstract &&
                setter.CilMethodBody != null && setter.GenericParameters.Count == 0 &&
                InteropDelegateContracts.HasStandardConvention(setter.Signature) &&
                SignatureComparer.Default.Equals(setter.Signature, MethodSignature.CreateInstance(
                    imports.Module.CorLibTypeFactory.Void, resolver.Resolve(source.Signature.FieldType)));
        }
    }
}
