// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateWebFormField(CilOpCode code, IFieldDescriptor reference,
            out CilInstruction translated)
        {
            translated = null;
            if (reference.DeclaringType?.FullName != "UnityEngine.WWWForm") return false;
            var source = reference.DeclaringType.Resolve();
            if (!InteropWebFormFields.IsOwner(source)) return false;
            var field = reference.Resolve();
            // The surviving formatting arrays still use their native static accessors.
            if (field?.IsStatic == true) return false;
            if (source != sourceMethod.DeclaringType || field?.DeclaringType != source ||
                !InteropWebFormFields.HasOwner(source, ownerType, imports, resolver) ||
                !InteropWebFormFields.TryDescribe(field, out var access) ||
                !SignatureComparer.Default.Equals(reference.Signature, field.Signature))
            {
                translated = FailInstruction("web-form-field-contract",
                    "Form state requires the original private field and unchanged native owner.");
                return true;
            }
            if (code.Code is not (CilCode.Ldfld or CilCode.Stfld))
            {
                translated = FailInstruction("web-form-field-address",
                    "Native form state cannot expose a CLR field address or token.");
                return true;
            }
            var valueType = resolver.Resolve(field.Signature.FieldType);
            if (valueType == null || InteropRestorationPlan.For(context)
                    .GetNativeIdentityRejection(field.Signature.FieldType) != null ||
                InteropManagedCollectionLowering.IsStorage(context, field.Signature.FieldType))
            {
                translated = FailInstruction("web-form-native-field-identity",
                    "Form fields must preserve native collection and array identities.");
                return true;
            }
            var setter = code.Code == CilCode.Stfld;
            var helper = InteropWebFormFields.ImportAccessor(imports, setter ? access.Setter : access.Getter);
            if (access.Index >= 0)
                translated = body.Add(CilOpCodes.Ldc_I4, access.Index);
            var call = body.Add(CilOpCodes.Call, helper);
            translated ??= call;
            if (!setter && !valueType.IsValueType)
                InteropEventDebuggerFields.EmitOwnedCast(target.CilMethodBody, imports,
                    helper.Signature.ReturnType, valueType);
            return true;
        }

        private bool TryTranslateWebFormBaseConstructor(CilOpCode code, IMethodDescriptor method,
            out CilInstruction translated)
        {
            translated = null;
            if (code.Code != CilCode.Call || method.Name != ".ctor" ||
                !InteropWebFormFields.IsOwner(sourceMethod.DeclaringType) ||
                ownerType.TryGetRestoredReferenceInitializer(sourceMethod) != target)
                return false;
            if (!InteropWebFormFields.HasOwner(sourceMethod.DeclaringType, ownerType, imports, resolver) ||
                sourceMethod.MetadataToken.ToInt32() != 0x06000007 || method is MethodSpecification ||
                !InteropDelegateContracts.HasStandardConvention(method.Signature) ||
                method.Signature is not { HasThis: true, GenericParameterCount: 0 } ||
                method.Signature.ReturnType.ElementType != ElementType.Void || method.Signature.ParameterTypes.Count != 0 ||
                method.DeclaringType is not ITypeDefOrRef objectType || objectType.FullName != "System.Object" ||
                !InteropArraySegmentLowering.HasSourceContract(objectType))
            {
                translated = FailInstruction("web-form-base-constructor-contract",
                    "The form initializer requires its original parameterless Object base call.");
                return true;
            }
            // The pointer constructor already rooted this object; Object::.ctor performs no field initialization.
            translated = body.Add(CilOpCodes.Pop);
            return true;
        }
    }
}
