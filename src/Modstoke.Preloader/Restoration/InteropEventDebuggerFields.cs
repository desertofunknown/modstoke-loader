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
    internal static class InteropEventDebuggerFields
    {
        internal static bool IsOwner(TypeDefinition type) =>
            type?.DeclaringModule.Assembly.Name == "UnityEngine.UIElementsModule" &&
            type.FullName == "UnityEngine.UIElements.EventDebugger";

        internal static bool HasOwner(TypeDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver) =>
            IsOwner(source) && source.DeclaringModule.Mvid == new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb") &&
            source.MetadataToken.ToInt32() == 0x020001AB && !source.IsValueType && !source.IsInterface &&
            !source.IsAbstract && source.GenericParameters.Count == 0 && source.Interfaces.Count == 0 &&
            source.BaseType?.FullName == "System.Object" &&
            owner.OriginalType is { IsValueType: false, IsInterface: false, IsAbstract: false } native &&
            native.FullName == source.FullName && native.BaseType?.FullName == "System.Object" &&
            native.Interfaces.Count == 0 && native.GenericParameters.Count == 0 &&
            native.Fields.All(field => field.IsStatic) && owner.ClassPointerFieldRef != null &&
            owner.NewType.BaseType?.FullName == "Il2CppSystem.Object" &&
            owner.NewType.DeclaringModule == imports.Module && owner.NewType.GenericParameters.Count == 0 &&
            resolver.ResolveRaw(source.ToTypeSignature())?.Resolve() == owner.NewType &&
            !owner.NewType.Fields.Any(field => source.Fields.Any(original => original.Name == field.Name));

        internal static bool TryDescribe(FieldDefinition field,
            out (string Getter, string Setter, int Collection) access)
        {
            access = default;
            if (field == null || !IsOwner(field.DeclaringType) ||
                field.DeclaringModule.Mvid != new Guid("b6e504b1-20f0-43e3-9923-7045b32ad8bb")) return false;
            var token = field.MetadataToken.ToInt32();
            (string Name, string Type, string Getter, string Setter) expected = token switch
            {
                0x04000610 => ("<panel>k__BackingField", "UnityEngine.UIElements.IPanel",
                    nameof(NativeEventDebuggerState.GetPanel), nameof(NativeEventDebuggerState.SetPanel)),
                0x04000611 => ("<isReplaying>k__BackingField", "System.Boolean",
                    nameof(NativeEventDebuggerState.GetIsReplaying), nameof(NativeEventDebuggerState.SetIsReplaying)),
                0x04000612 => ("<playbackSpeed>k__BackingField", "System.Single",
                    nameof(NativeEventDebuggerState.GetPlaybackSpeed), nameof(NativeEventDebuggerState.SetPlaybackSpeed)),
                0x04000613 => ("<isPlaybackPaused>k__BackingField", "System.Boolean",
                    nameof(NativeEventDebuggerState.GetIsPlaybackPaused), nameof(NativeEventDebuggerState.SetIsPlaybackPaused)),
                0x04000614 => ("m_EventCalledObjects", DictionaryOf("System.Collections.Generic.List`1<UnityEngine.UIElements.EventDebuggerCallTrace>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x04000615 => ("m_EventDefaultActionObjects", DictionaryOf("System.Collections.Generic.List`1<UnityEngine.UIElements.EventDebuggerDefaultActionTrace>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x04000616 => ("m_EventPathObjects", DictionaryOf("System.Collections.Generic.List`1<UnityEngine.UIElements.EventDebuggerPathTrace>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x04000617 => ("m_EventProcessedEvents", DictionaryOf("System.Collections.Generic.List`1<UnityEngine.UIElements.EventDebuggerTrace>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x04000618 => ("m_StackOfProcessedEvent", DictionaryOf("System.Collections.Generic.Stack`1<UnityEngine.UIElements.EventDebuggerTrace>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x04000619 => ("m_EventTypeProcessedCount", DictionaryOf("System.Collections.Generic.Dictionary`2<System.Int64,System.Int32>"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x0400061A => ("m_ModificationCount", DictionaryOf("System.Int64"),
                    nameof(NativeEventDebuggerState.GetCollection), nameof(NativeEventDebuggerState.SetCollection)),
                0x0400061B => ("m_Log", "System.Boolean",
                    nameof(NativeEventDebuggerState.GetLog), nameof(NativeEventDebuggerState.SetLog)),
                0x0400061C => ("<suspended>k__BackingField", "System.Boolean",
                    nameof(NativeEventDebuggerState.GetSuspended), nameof(NativeEventDebuggerState.SetSuspended)),
                _ => default
            };
            var attributes = FieldAttributes.Private |
                (token is 0x0400061A or 0x0400061B ? FieldAttributes.InitOnly : 0);
            if (expected.Name == null || field.Name != expected.Name || field.Attributes != attributes ||
                field.Signature.FieldType.FullName != expected.Type) return false;
            access = (expected.Getter, expected.Setter,
                token is >= 0x04000614 and <= 0x0400061A ? token - 0x04000614 : -1);
            return true;
        }

        private static string DictionaryOf(string value) =>
            "System.Collections.Generic.Dictionary`2<UnityEngine.UIElements.IPanel," + value + ">";

        internal static IMethodDescriptor ImportAccessor(InteropRuntimeReferences imports, string name)
        {
            // Inspect metadata without asking reflection to load the generated Unity assemblies.
            var helpers = imports.Module.DefaultImporter.ImportType(typeof(NativeEventDebuggerState)).Resolve();
            return imports.Module.DefaultImporter.ImportMethod(helpers.Methods.Single(method =>
                method.Name == name && method.IsPublic && method.IsStatic));
        }

        internal static void EmitOwnedCast(CilMethodBody methodBody, InteropRuntimeReferences imports,
            TypeSignature sourceType, TypeSignature resultType)
        {
            var body = methodBody.Instructions;
            var root = new CilLocalVariable(sourceType);
            methodBody.LocalVariables.Add(root);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Stloc, root);
            var cast = new CilInstruction(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                imports.Il2CppObjectBase_Cast.Value.MakeGenericInstanceMethod(resultType)));
            var finish = new CilInstruction(CilOpCodes.Nop);
            body.Add(CilOpCodes.Dup);
            body.Add(CilOpCodes.Brtrue, cast.CreateLabel());
            body.Add(CilOpCodes.Pop);
            body.Add(CilOpCodes.Ldnull);
            body.Add(CilOpCodes.Br, finish.CreateLabel());
            body.Add(cast);
            // Cast may return a pooled weak wrapper; the field result needs its own native root.
            var copy = (IMethodDefOrRef)imports.Module.DefaultImporter.ImportMethod(typeof(InteropNativeReferenceIdentity)
                .GetMethod(nameof(InteropNativeReferenceIdentity.CopyOwned)));
            body.Add(CilOpCodes.Call, copy.MakeGenericInstanceMethod(resultType));
            body.Add(CilOpCodes.Ldloc, root);
            body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                typeof(GC).GetMethod(nameof(GC.KeepAlive), new[] { typeof(object) })));
            body.Add(finish);
        }
    }
}
