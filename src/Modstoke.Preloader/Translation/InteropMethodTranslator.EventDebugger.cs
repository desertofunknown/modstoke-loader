// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodTranslator
    {
        private bool TryTranslateEventDebuggerField(CilOpCode code, IFieldDescriptor reference,
            out CilInstruction translated)
        {
            translated = null;
            if (reference.DeclaringType?.FullName != "UnityEngine.UIElements.EventDebugger") return false;
            var source = reference.DeclaringType?.Resolve();
            if (!InteropEventDebuggerFields.IsOwner(source)) return false;

            var field = reference.Resolve();
            if (source != sourceMethod.DeclaringType || field?.DeclaringType != source ||
                !InteropEventDebuggerFields.HasOwner(source, ownerType, imports, resolver) ||
                !InteropEventDebuggerFields.TryDescribe(field, out var access) ||
                !SignatureComparer.Default.Equals(reference.Signature, field.Signature))
            {
                translated = FailInstruction("event-debugger-field-contract",
                    "The debugger state requires the original private field and unchanged native owner.");
                return true;
            }
            if (code.Code is not (CilCode.Ldfld or CilCode.Stfld))
            {
                translated = FailInstruction("event-debugger-field-address",
                    "Native debugger state cannot expose a CLR field address or token.");
                return true;
            }
            var setter = code.Code == CilCode.Stfld;
            if (setter && field.IsInitOnly && (!sourceMethod.IsConstructor || sourceMethod.IsStatic))
            {
                translated = FailInstruction("event-debugger-readonly-field",
                    "Readonly debugger state can only be assigned by its constructor.");
                return true;
            }
            var valueType = resolver.Resolve(field.Signature.FieldType);
            if (valueType == null || InteropRestorationPlan.For(context)
                    .GetNativeIdentityRejection(field.Signature.FieldType) != null ||
                InteropManagedCollectionLowering.IsStorage(context, field.Signature.FieldType))
            {
                translated = FailInstruction("event-debugger-native-field-identity",
                    "Debugger state must retain native objects, not CLR collection payloads.");
                return true;
            }
            var helper = InteropEventDebuggerFields.ImportAccessor(imports, setter ? access.Setter : access.Getter);
            if (access.Collection >= 0)
                translated = body.Add(CilOpCodes.Ldc_I4, access.Collection);
            var call = body.Add(CilOpCodes.Call, helper);
            translated ??= call;
            if (!setter && !valueType.IsValueType)
                InteropEventDebuggerFields.EmitOwnedCast(target.CilMethodBody, imports,
                    helper.Signature.ReturnType, valueType);
            return true;
        }
    }
}
