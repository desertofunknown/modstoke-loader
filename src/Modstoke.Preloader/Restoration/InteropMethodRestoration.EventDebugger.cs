// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using AsmResolver.PE.DotNet.Metadata.Tables;
using Il2CppInterop.Runtime.Injection;

namespace Modstoke.Preloader
{
    internal sealed partial class InteropMethodRestoration
    {
        private static bool TryRestoreEventDebuggerProperty(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                source.DeclaringType.FullName != "UnityEngine.UIElements.EventDebugger") return false;
            (int Method, int Field, string Property, string Helper) contract = (string)source.Name switch
            {
                "get_panel" => (100666562, 67110416, "panel", nameof(NativeEventDebuggerState.GetPanel)),
                "set_panel" => (100666563, 67110416, "panel", nameof(NativeEventDebuggerState.SetPanel)),
                "get_isReplaying" => (100666564, 67110417, "isReplaying", nameof(NativeEventDebuggerState.GetIsReplaying)),
                "set_isReplaying" => (100666565, 67110417, "isReplaying", nameof(NativeEventDebuggerState.SetIsReplaying)),
                "get_playbackSpeed" => (100666566, 67110418, "playbackSpeed", nameof(NativeEventDebuggerState.GetPlaybackSpeed)),
                "set_playbackSpeed" => (100666567, 67110418, "playbackSpeed", nameof(NativeEventDebuggerState.SetPlaybackSpeed)),
                "get_isPlaybackPaused" => (100666568, 67110419, "isPlaybackPaused", nameof(NativeEventDebuggerState.GetIsPlaybackPaused)),
                "set_isPlaybackPaused" => (100666569, 67110419, "isPlaybackPaused", nameof(NativeEventDebuggerState.SetIsPlaybackPaused)),
                "get_suspended" => (100666591, 67110428, "suspended", nameof(NativeEventDebuggerState.GetSuspended)),
                "set_suspended" => (100666592, 67110428, "suspended", nameof(NativeEventDebuggerState.SetSuspended)),
                _ => default
            };
            if (contract.Method == 0) return false;

            entry.Set("signature-rejected", "event-debugger-property-contract",
                "Debugger properties require the exact source field access and unchanged native owner identity.");
            var setter = source.IsSetMethod;
            var type = source.DeclaringType;
            var fieldTypeName = contract.Property switch
            {
                "panel" => "UnityEngine.UIElements.IPanel",
                "playbackSpeed" => "System.Single",
                _ => "System.Boolean"
            };
            if (source.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") ||
                source.MetadataToken.ToInt32() != contract.Method || type.MetadataToken.ToInt32() != 33554859 ||
                type.IsValueType || type.IsInterface || type.IsAbstract || type.GenericParameters.Count != 0 ||
                type.Interfaces.Count != 0 || type.BaseType?.FullName != "System.Object" ||
                source.IsStatic || (!setter && !source.IsGetMethod) || source.GenericParameters.Count != 0 ||
                source.IsVirtual || source.IsAbstract || source.ImplAttributes != MethodImplAttributes.IL ||
                !InteropDelegateContracts.HasStandardConvention(source.Signature) ||
                source.Signature is not { HasThis: true, GenericParameterCount: 0 } ||
                source.Signature.ParameterTypes.Count != (setter ? 1 : 0) ||
                source.Signature.ReturnType.FullName != (setter ? "System.Void" : fieldTypeName) ||
                setter && source.Signature.ParameterTypes[0].FullName != fieldTypeName ||
                source.CilMethodBody is not { } original || original.LocalVariables.Count != 0 || original.ExceptionHandlers.Count != 0)
                return true;
            var code = original.Instructions.Where(instruction => instruction.OpCode.Code != CilCode.Nop).ToArray();
            if (code.Length != (setter ? 4 : 3) || code[0].OpCode.Code != CilCode.Ldarg_0 ||
                code[^1].OpCode.Code != CilCode.Ret || setter && code[1].OpCode.Code != CilCode.Ldarg_1 ||
                code[^2].OpCode.Code != (setter ? CilCode.Stfld : CilCode.Ldfld) ||
                code[^2].Operand is not IFieldDescriptor access || access.Resolve() is not { } field ||
                field.DeclaringType != type || field.MetadataToken.ToInt32() != contract.Field ||
                field.Name != "<" + contract.Property + ">k__BackingField" || field.Attributes != FieldAttributes.Private ||
                field.Signature.FieldType.FullName != fieldTypeName ||
                !SignatureComparer.Default.Equals(access.Signature, field.Signature) ||
                !SignatureComparer.Default.Equals(field.Signature.FieldType,
                    setter ? source.Signature.ParameterTypes[0] : source.Signature.ReturnType)) return true;
            if (contract.Property == "panel"
                ? field.Signature.FieldType.Resolve()?.DeclaringModule != source.DeclaringModule
                : field.Signature.FieldType is not CorLibTypeSignature primitive ||
                  primitive.ElementType != (contract.Property == "playbackSpeed" ? ElementType.R4 : ElementType.Boolean))
                return true;

            if (!InteropEventDebuggerFields.HasOwner(type, owner, imports, resolver)) return true;
            var valueType = resolver.Resolve(field.Signature.FieldType);
            if (valueType == null || contract.Property == "panel" &&
                (resolver.ResolveRaw(field.Signature.FieldType) == null ||
                 owner.AssemblyContext.GlobalContext.TryGetTypeByName("UnityEngine.UIElementsModule", fieldTypeName)
                     is not { OriginalType.IsInterface: true, ClassPointerFieldRef: not null })) return true;
            var signature = setter ? MethodSignature.CreateInstance(imports.Module.CorLibTypeFactory.Void, valueType) :
                MethodSignature.CreateInstance(valueType);
            if (owner.NewType.Methods.Any(method => method.Name == source.Name &&
                SignatureComparer.Default.Equals(method.Signature, signature))) return true;

            var target = new MethodDefinition(source.Name, source.Attributes, MethodSignature.CreateInstance(signature.ReturnType))
                { CilMethodBody = new CilMethodBody() };
            if (setter) InteropMethodDeclarations.AddParameter(target, valueType, source.Parameters[0].Name,
                source.Parameters[0].Definition?.Attributes ?? default);
            var helper = InteropEventDebuggerFields.ImportAccessor(imports, contract.Helper);
            var body = target.CilMethodBody.Instructions;
            body.Add(CilOpCodes.Ldarg_0);
            if (setter) body.Add(CilOpCodes.Ldarg_1);
            body.Add(CilOpCodes.Call, helper);
            if (contract.Property == "panel" && !setter)
                InteropEventDebuggerFields.EmitOwnedCast(target.CilMethodBody, imports,
                    helper.Signature.ReturnType, valueType);
            body.Add(CilOpCodes.Ret);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            var property = GetOrCreateProperty(source, target);
            if (setter) property.SetMethod = target;
            else property.GetMethod = target;
            entry.Set("native-event-debugger-property-adapter", null,
                "Native weak-key state shared with direct field access; reference results retain independent native roots.");
            return true;
        }
    }
}
