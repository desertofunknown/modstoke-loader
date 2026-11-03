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
        private bool TryRestoreOverlayAccessor(MethodDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports, InteropUnstripTypeResolver resolver, InteropRestorationEntry entry)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                source.DeclaringType.FullName != "UnityEngine.UIElements.UIElementsRuntimeUtility" ||
                (string)source.Name is not ("add_onRepaintOverlayPanels" or "remove_onRepaintOverlayPanels")) return false;

            entry.Set("signature-rejected", "overlay-event-source-contract",
                "Overlay access requires the matched event accessors and player-loop registration contract.");
            if (!InteropOverlayRepaintContract.TryGetAccessor(source, out var sourceEvent, out var sourceRegistered,
                out var sourceInner, out var sourceRegistration)) return true;

            entry.Set("signature-rejected", "overlay-event-native-contract",
                "Overlay access requires native registration, callback identity and panel ownership accessors.");
            var action = resolver.Resolve(sourceEvent.Signature.FieldType);
            var field = owner.NewType.Fields.SingleOrDefault(candidate => candidate.Name == sourceEvent.Name);
            var inner = owner.TryGetRestoredBody(sourceInner);
            var registration = owner.TryGetMethodByUnityAssemblyMethod(sourceRegistration)?.NewMethod;
            var native = owner.AssemblyContext.TryGetTypeByName("UnityEngine.UIElements.UIElementsRuntimeUtilityNative");
            if (action == null || action.IsValueType || field == null ||
                !field.IsStatic || field.IsInitOnly || field.IsLiteral || field.FieldRva != null ||
                !SignatureComparer.Default.Equals(field.Signature.FieldType, action) ||
                inner == null || inner.DeclaringType != owner.NewType || !inner.IsStatic || inner.IsAbstract ||
                !SignatureComparer.Default.Equals(inner.Signature, MethodSignature.CreateStatic(
                    imports.Module.CorLibTypeFactory.Void, action)) ||
                registration == null || registration.DeclaringType != owner.NewType ||
                !registration.IsStatic || registration.IsAbstract ||
                !SignatureComparer.Default.Equals(registration.Signature,
                    MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void)) ||
                !TryGetOverlayStaticGetter(owner, sourceRegistered.Name, imports.Module.CorLibTypeFactory.Boolean, out var registered) ||
                !TryGetOverlayStaticGetter(native, "UpdateRuntimePanelsCallback", action, out var updateCallback))
                return true;

            var ownedCallback = owner.NewType.Fields.SingleOrDefault(candidate => candidate.Name == "repaintUpdateCallback");
            if (ownedCallback != null && (ownedCallback.Attributes != (FieldAttributes.Private | FieldAttributes.Static) ||
                !SignatureComparer.Default.Equals(ownedCallback.Signature.FieldType, action))) return true;

            var target = new MethodDefinition(source.Name,
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
                MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void))
                { CilMethodBody = new CilMethodBody() };
            InteropMethodDeclarations.AddParameter(target, action, source.Parameters[0].Name, default);
            if (owner.NewType.Methods.Any(method => method.Name == target.Name &&
                SignatureComparer.Default.Equals(method.Signature, target.Signature)))
            {
                entry.Set("signature-rejected", "overlay-event-target-conflict",
                    "The event adapter does not replace an existing declaration.");
                return true;
            }
            if (ownedCallback == null)
            {
                ownedCallback = new FieldDefinition("repaintUpdateCallback", FieldAttributes.Private | FieldAttributes.Static, action);
                owner.NewType.Fields.Add(ownedCallback);
            }

            var body = target.CilMethodBody.Instructions;
            var finish = new CilInstruction(CilOpCodes.Ret);
            if (source.Name == "add_onRepaintOverlayPanels")
            {
                var subscribe = new CilInstruction(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Ldsfld, field);
                body.Add(CilOpCodes.Brtrue, subscribe.CreateLabel());
                body.Add(CilOpCodes.Call, registered);
                body.Add(CilOpCodes.Brtrue, subscribe.CreateLabel());
                body.Add(CilOpCodes.Call, updateCallback);
                body.Add(CilOpCodes.Brtrue, subscribe.CreateLabel());
                body.Add(CilOpCodes.Call, registration);
                body.Add(CilOpCodes.Call, updateCallback);
                body.Add(CilOpCodes.Stsfld, ownedCallback);
                body.Add(subscribe);
                body.Add(CilOpCodes.Call, inner);
            }
            else
            {
                var release = new CilInstruction(CilOpCodes.Ldnull);
                body.Add(CilOpCodes.Ldarg_0);
                body.Add(CilOpCodes.Call, inner);
                body.Add(CilOpCodes.Ldsfld, field);
                body.Add(CilOpCodes.Brtrue, finish.CreateLabel());
                body.Add(CilOpCodes.Ldsfld, ownedCallback);
                body.Add(CilOpCodes.Brfalse, finish.CreateLabel());
                body.Add(CilOpCodes.Call, registered);
                body.Add(CilOpCodes.Brtrue, release.CreateLabel());
                body.Add(CilOpCodes.Ldsfld, ownedCallback);
                body.Add(CilOpCodes.Call, updateCallback);
                body.Add(CilOpCodes.Call, imports.Module.DefaultImporter.ImportMethod(
                    typeof(InteropNativeReferenceIdentity).GetMethod(nameof(InteropNativeReferenceIdentity.AreEqual))));
                body.Add(CilOpCodes.Brfalse, release.CreateLabel());
                body.Add(CilOpCodes.Call, registration);
                body.Add(release);
                body.Add(CilOpCodes.Stsfld, ownedCallback);
            }
            body.Add(finish);
            owner.NewType.Methods.Add(target);
            owner.AddRestoredManagedMethod(source, target);
            completedBodies.Add((target, entry));
            entry.Set("native-overlay-event-adapter", null,
                "Atomic event storage retained; existing panel and replacement callbacks keep their player-loop ownership.");
            return true;
        }

        private static bool TryGetOverlayStaticGetter(InteropTypeContext owner, string name,
            TypeSignature value, out MethodDefinition getter)
        {
            getter = null;
            if (owner?.OriginalType == null || owner.ClassPointerFieldRef == null || owner.NewType.GenericParameters.Count != 0 ||
                owner.OriginalType.IsValueType || owner.NewType.IsValueType || owner.NewType.Fields.Any(field => field.Name == name))
                return false;
            var field = owner.Fields.SingleOrDefault(candidate => candidate.OriginalField.Name == name);
            if (field?.PointerField == null || !field.OriginalField.IsStatic || field.OriginalField.IsLiteral) return false;
            getter = owner.NewType.Properties.SingleOrDefault(property => property.Name == field.UnmangledName)?.GetMethod;
            return getter != null && getter.IsStatic && !getter.IsAbstract && getter.CilMethodBody != null &&
                getter.DeclaringType == owner.NewType && getter.GenericParameters.Count == 0 &&
                InteropDelegateContracts.HasStandardConvention(getter.Signature) &&
                SignatureComparer.Default.Equals(getter.Signature, MethodSignature.CreateStatic(value));
        }

        private static void RestoreOverlayEvents(TypeDefinition source, InteropTypeContext owner,
            InteropRuntimeReferences imports)
        {
            if (source.DeclaringModule.Assembly.Name != "UnityEngine.UIElementsModule" ||
                source.FullName != "UnityEngine.UIElements.UIElementsRuntimeUtility" ||
                owner?.NewType.Fields.SingleOrDefault(field => field.Name == "s_onRepaintOverlayPanels") is not { } storage)
                return;
            foreach (var sourceEvent in source.Events.Where(candidate =>
                (string)candidate.Name is "s_onRepaintOverlayPanels" or "onRepaintOverlayPanels"))
            {
                if (sourceEvent.AddMethod == null || sourceEvent.RemoveMethod == null) continue;
                var add = owner.TryGetRestoredManagedMethod(sourceEvent.AddMethod) ?? owner.TryGetRestoredBody(sourceEvent.AddMethod);
                var remove = owner.TryGetRestoredManagedMethod(sourceEvent.RemoveMethod) ?? owner.TryGetRestoredBody(sourceEvent.RemoveMethod);
                if (add == null || remove == null || add.DeclaringType != owner.NewType || remove.DeclaringType != owner.NewType ||
                    !add.IsStatic || !remove.IsStatic || sourceEvent.Attributes != 0 ||
                    !SignatureComparer.Default.Equals(add.Signature,
                        MethodSignature.CreateStatic(imports.Module.CorLibTypeFactory.Void, storage.Signature.FieldType)) ||
                    !SignatureComparer.Default.Equals(add.Signature, remove.Signature) ||
                    owner.NewType.Events.Any(candidate => candidate.Name == sourceEvent.Name)) continue;
                if (sourceEvent.Name == storage.Name)
                {
                    // Direct subscriptions must not bypass the outer event's player-loop ownership.
                    storage.IsPrivate = true;
                    add.IsPrivate = true;
                    remove.IsPrivate = true;
                }
                owner.NewType.Events.Add(new EventDefinition(sourceEvent.Name, sourceEvent.Attributes,
                    storage.Signature.FieldType.ToTypeDefOrRef()) { AddMethod = add, RemoveMethod = remove });
            }
        }
    }
}
