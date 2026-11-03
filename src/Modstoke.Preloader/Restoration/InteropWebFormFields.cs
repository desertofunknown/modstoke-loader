// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal static class InteropWebFormFields
    {
        private const string ModuleName = "UnityEngine.UnityWebRequestModule";
        private const string OwnerName = "UnityEngine.WWWForm";
        private static readonly string[] StaticFields =
        {
            "dDash", "crlf", "contentTypeHeader", "dispositionHeader", "endQuote", "fileNameField", "ampersand", "equal"
        };

        internal static bool IsOwner(TypeDefinition type) =>
            type?.DeclaringModule.Assembly.Name == ModuleName && type.FullName == OwnerName;

        internal static bool HasOwner(TypeDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver)
        {
            if (!IsOwner(source) || source.DeclaringModule.Mvid != new Guid("e3e15492-8f90-4bae-ba37-bfac955d6ba2") ||
                source.MetadataToken.ToInt32() != 0x02000003 || source.Attributes != (TypeAttributes.Public | TypeAttributes.BeforeFieldInit) ||
                source.DeclaringType != null || source.GenericParameters.Count != 0 || source.Interfaces.Count != 0 ||
                source.CustomAttributes.Count != 0 || source.NestedTypes.Count != 0 || source.ClassLayout != null ||
                source.BaseType?.FullName != "System.Object" ||
                owner.OriginalType is not { IsValueType: false, IsInterface: false, IsAbstract: false } native ||
                !IsOwner(native) || native.DeclaringType != null || native.BaseType?.FullName != "System.Object" ||
                native.GenericParameters.Count != 0 || native.Interfaces.Count != 0 || owner.ClassPointerFieldRef == null ||
                owner.ComputedTypeSpecifics != InteropTypeContext.TypeSpecifics.ReferenceType ||
                owner.NewType.FullName != OwnerName || owner.NewType.DeclaringType != null ||
                owner.NewType.IsValueType || owner.NewType.IsInterface || owner.NewType.GenericParameters.Count != 0 ||
                owner.NewType.BaseType?.FullName != "Il2CppSystem.Object" || owner.NewType.DeclaringModule != imports.Module ||
                resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() != owner.NewType ||
                native.Fields.Count != StaticFields.Length || native.Fields.Any(field => !field.IsStatic || !IsByteArray(field.Signature?.FieldType)) ||
                !native.Fields.Select(field => (string)field.Name).OrderBy(name => name).SequenceEqual(StaticFields.OrderBy(name => name)) ||
                native.Methods.Count != 2 || native.Methods.Any(method => !method.IsStatic || method.IsVirtual ||
                    method.GenericParameters.Count != 0 || (string)method.Name is not ("get_DefaultEncoding" or ".cctor")) ||
                native.Methods.Count(method => method.Name == "get_DefaultEncoding") != 1)
                return false;

            var fields = source.Fields.Where(field => !field.IsStatic).ToArray();
            if (source.Fields.Count != 14 || fields.Length != 6 || fields.Any(field => !TryDescribe(field, out _)) ||
                owner.NewType.Fields.Any(field => fields.Any(original => original.Name == field.Name)) ||
                owner.NewType.Properties.Any(property => fields.Any(original => original.Name == property.Name))) return false;
            foreach (var field in source.Fields.Where(field => field.IsStatic))
            {
                var mapped = owner.TryGetFieldByUnityAssemblyField(field);
                if (!StaticFields.Contains((string)field.Name) || !IsByteArray(field.Signature?.FieldType) ||
                    mapped?.PointerField == null || mapped.OriginalField.DeclaringType != native || !mapped.OriginalField.IsStatic)
                    return false;
            }

            var methods = source.Methods.Where(method => !method.IsStatic).ToArray();
            if (source.Methods.Count != 11 || methods.Length != 9 || methods.Any(method => !IsInstanceBody(method)) ||
                methods.Select(method => method.MetadataToken.ToInt32()).Distinct().Count() != 9 ||
                methods.Any(method => owner.TryGetMethodByUnityAssemblyMethod(method) != null)) return false;
            var constructor = methods.Single(method => method.MetadataToken.ToInt32() == 0x06000007);
            if (constructor.Name != ".ctor" || constructor.Attributes != (MethodAttributes.Public | MethodAttributes.HideBySig |
                    MethodAttributes.SpecialName | MethodAttributes.RuntimeSpecialName) ||
                constructor.Signature.ParameterTypes.Count != 0 ||
                constructor.Signature.ReturnType is not CorLibTypeSignature { ElementType: ElementType.Void }) return false;

            var context = owner.AssemblyContext.GlobalContext;
            var list = context.TryGetTypeByName("mscorlib", "System.Collections.Generic.List`1");
            if (list?.OriginalType is not { IsValueType: false, IsInterface: false } originalList ||
                originalList.FullName != "System.Collections.Generic.List`1" || originalList.DeclaringModule.Assembly.Name != "mscorlib" ||
                originalList.GenericParameters.Count != 1 || list.ClassPointerFieldRef == null ||
                list.NewType.FullName != "Il2CppSystem.Collections.Generic.List`1" || list.NewType.GenericParameters.Count != 1 ||
                list.NewType.IsValueType || list.NewType.IsInterface) return false;
            var bytes = new GenericInstanceTypeSignature(imports.Il2CppStructArray.ToTypeDefOrRef(), false,
                imports.Module.CorLibTypeFactory.Byte);
            var plan = InteropRestorationPlan.For(context);
            foreach (var field in fields)
            {
                TryDescribe(field, out var access);
                TypeSignature expected = access.Index switch
                {
                    0 => new GenericInstanceTypeSignature(list.NewType, false, bytes),
                    >= 1 and <= 3 => new GenericInstanceTypeSignature(list.NewType, false, imports.Module.CorLibTypeFactory.String),
                    4 => bytes,
                    _ => imports.Module.CorLibTypeFactory.Boolean
                };
                if (plan.ContainsManagedOnlyStorage(field.Signature.FieldType) ||
                    plan.GetNativeIdentityRejection(field.Signature.FieldType) != null ||
                    !SignatureComparer.Default.Equals(resolver.Resolve(field.Signature.FieldType), expected)) return false;
            }

            // Private state is lowered only for the original instance bodies; native statics remain native fields.
            foreach (var method in source.DeclaringModule.GetAllTypes().SelectMany(type => type.Methods))
            {
                if (method.CilMethodBody == null) continue;
                foreach (var instruction in method.CilMethodBody.Instructions)
                {
                    if (instruction.OpCode.Code == CilCode.Ldstr && instruction.Operand is string literal &&
                        fields.Any(field => field.Name == literal)) return false;
                    if (instruction.Operand is not IFieldDescriptor reference || reference.DeclaringType?.FullName != OwnerName ||
                        !fields.Any(field => field.Name == reference.Name)) continue;
                    if (method.DeclaringType != source || !IsInstanceBody(method) ||
                        instruction.OpCode.Code is not (CilCode.Ldfld or CilCode.Stfld) ||
                        reference.Resolve() is not { } resolvedField || resolvedField.DeclaringType != source || !TryDescribe(resolvedField, out _) ||
                        !SignatureComparer.Default.Equals(reference.Signature, resolvedField.Signature) ||
                        !SignatureComparer.Default.Equals(reference.DeclaringType.ToTypeSignature(), source.ToTypeSignature())) return false;
                }
            }
            return true;
        }

        internal static bool TryDescribe(FieldDefinition field, out (string Getter, string Setter, int Index) access)
        {
            access = default;
            if (field == null || !IsOwner(field.DeclaringType) ||
                field.DeclaringModule.Mvid != new Guid("e3e15492-8f90-4bae-ba37-bfac955d6ba2") ||
                field.DeclaringType.MetadataToken.ToInt32() != 0x02000003 || field.Attributes != FieldAttributes.Private ||
                field.Signature == null || field.Signature.HasThis || field.Constant != null || field.FieldRva != null ||
                field.FieldOffset != null || field.HasFieldMarshal || field.MarshalDescriptor != null || field.CustomAttributes.Count != 0)
                return false;
            var index = field.MetadataToken.ToInt32() - 0x04000002;
            var name = index switch
            {
                0 => "formData", 1 => "fieldNames", 2 => "fileNames", 3 => "types", 4 => "boundary", 5 => "containsFiles", _ => null
            };
            if (name == null || field.Name != name) return false;
            var type = field.Signature.FieldType;
            if (index < 4)
            {
                if (type is not GenericInstanceTypeSignature { IsValueType: false } collection ||
                    collection.GenericType.FullName != "System.Collections.Generic.List`1" ||
                    InteropRestoredTypeSupport.GetAssemblyName(collection.GenericType.ToTypeSignature()) != "netstandard" ||
                    collection.TypeArguments.Count != 1 || (index == 0 ? !IsByteArray(collection.TypeArguments[0]) :
                        collection.TypeArguments[0] is not CorLibTypeSignature { ElementType: ElementType.String })) return false;
            }
            else if (index == 4 ? !IsByteArray(type) : type is not CorLibTypeSignature { ElementType: ElementType.Boolean }) return false;
            access = index == 5
                ? (nameof(NativeWebFormState.GetContainsFiles), nameof(NativeWebFormState.SetContainsFiles), -1)
                : (nameof(NativeWebFormState.GetReference), nameof(NativeWebFormState.SetReference), index);
            return true;
        }

        internal static IMethodDescriptor ImportAccessor(InteropRuntimeReferences imports, string name)
        {
            var helpers = imports.Module.DefaultImporter.ImportType(typeof(NativeWebFormState)).Resolve();
            return imports.Module.DefaultImporter.ImportMethod(helpers.Methods.Single(method =>
                method.Name == name && method.IsPublic && method.IsStatic));
        }

        private static bool IsByteArray(TypeSignature type) =>
            type is SzArrayTypeSignature { BaseType: CorLibTypeSignature { ElementType: ElementType.U1 } };

        private static bool IsInstanceBody(MethodDefinition method) =>
            method.MetadataToken.ToInt32() is >= 0x06000007 and <= 0x0600000F &&
            !method.IsStatic && !method.IsVirtual && !method.IsAbstract && !method.IsPInvokeImpl &&
            method.IsPublic && method.ImplAttributes == MethodImplAttributes.IL && method.CilMethodBody != null &&
            method.GenericParameters.Count == 0 && InteropDelegateContracts.HasStandardConvention(method.Signature) &&
            method.Signature.HasThis && method.Signature.GenericParameterCount == 0;
    }
}
