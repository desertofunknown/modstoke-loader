// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private readonly Dictionary<IntPtr, ItemInstanceModule> _pausedModules = new();
        private readonly HashSet<IntPtr> _unregisteredModules = new();
        private ItemInstanceModuleUpdater _detachedUpdater;
        private IntPtr _detachedUpdateEvent;
        private bool HoldsDetachedModules => _authority != null && _pausedModules.Count != 0;
        private static IntPtr _perishableWriter;
        private static int _perishableDepth;
        private static bool _perishableUpdating, _perishableUpdateEntered, _perishableStateEntered;

        /// <summary>
        /// This source still holds native inventory and passive-module suppression, including
        /// after Dispose/fault. Cancellation releases it only before retirement with exact readback.
        /// This suppresses autonomous writers; armed server decay has its own exact scoped writer.
        /// It is not permission for arbitrary battery use, cooking or module changes.
        /// </summary>
        public bool IsNativeGuardHeld => ReferenceEquals(_guard, this)
            && _load == SaveLifecycle.State.BeforeLoadCount;

        internal void RetainPausedModules(ItemInstance item)
        {
            if (item == null) return;
            if (item.WasCollected || item._modules == null || item._modules.WasCollected)
                throw new InvalidOperationException("Cannot retain an unavailable native module list.");
            foreach (var module in item._modules)
            {
                RequireKnownModule(module);
                var type = IL2CPP.il2cpp_object_get_class(module.Pointer);
                if ((type == Il2CppClassPointerStore<PerishableItemInstanceModule>.NativeClassPtr
                    || type == Il2CppClassPointerStore<VolumeContainerItemInstanceModule>.NativeClassPtr)
                    && !module._isInstantiated)
                    throw new InvalidOperationException("Passive inventory modules must finish native initialization before capture.");
                _pausedModules.TryAdd(module.Pointer, module);
            }
            // Detached server data never needs autonomous ModuleUpdater registration. Remove
            // that native root now, while the captured updater is still current; the scoped
            // authority Update and mutation guards remain active until consumption/disposal.
            if (_authority != null)
                foreach (var module in item._modules) UnregisterDetachedModule(module);
        }

        private void CaptureDetachedUpdater()
        {
            _detachedUpdater = ItemInstanceModuleUpdater._instance;
            if (_detachedUpdater == null || _detachedUpdater.WasCollected || !_detachedUpdater
                || !ItemInstanceModuleUpdater.HasInstance || _detachedUpdater.OnUpdate == null)
                throw new InvalidOperationException("Detached module updater is unavailable.");
            _detachedUpdateEvent = _detachedUpdater.OnUpdate.Pointer;
        }

        private void UnregisterDetachedModule(ItemInstanceModule module)
        {
            if (_authority == null || module == null || module.WasCollected)
                throw new InvalidOperationException("Only retained detached server modules may be unregistered.");
            if (_unregisteredModules.Contains(module.Pointer)) return;
            if (_detachedUpdater == null || _detachedUpdater.WasCollected || !_detachedUpdater
                || ItemInstanceModuleUpdater._instance?.Pointer != _detachedUpdater.Pointer
                || !ItemInstanceModuleUpdater.HasInstance || _detachedUpdater.OnUpdate?.Pointer != _detachedUpdateEvent)
                throw new InvalidOperationException("Detached module updater changed before listener removal; suppression retained.");
            UnregisterNativeModule(module, QualifyModuleUnregister());
            _unregisteredModules.Add(module.Pointer);
        }

        internal void ReleaseDetachedModules(ItemInstance item)
        {
            if (!OnThread || _authority == null)
                throw new InvalidOperationException("Release detached server modules only on their owning game thread.");
            if (item == null) return;
            if (item.WasCollected || item._modules == null || item._modules.WasCollected)
                throw new InvalidOperationException("Detached module list became unavailable before release.");
            foreach (var module in item._modules) ReleaseDetachedModule(module);
        }

        private void ReleaseDetachedModule(ItemInstanceModule module)
        {
            if (module == null || module.WasCollected)
                throw new InvalidOperationException("Detached module became unavailable before release.");
            if (!_pausedModules.ContainsKey(module.Pointer)) return;
            UnregisterDetachedModule(module);
            _pausedModules.Remove(module.Pointer);
            _unregisteredModules.Remove(module.Pointer);
        }

        private void ReleaseAllDetachedModules()
        {
            if (_authority == null) return; // Original client inventory remains guarded, never finalized here.
            string failure = null;
            foreach (var module in new List<ItemInstanceModule>(_pausedModules.Values))
            {
                try { ReleaseDetachedModule(module); }
                catch (Exception error) { failure ??= error.Message; }
            }
            if (failure != null)
            {
                Detail = "Detached module cleanup is incomplete; unresolved modules remain guarded: " + failure;
                Fail(Detail);
            }
            else { _detachedUpdater = null; _detachedUpdateEvent = IntPtr.Zero; }
        }

        // Detached server grant data needs native initialization before its first saved value is
        // selected. This never places the instance into an inventory or adopts observed custody.
        internal static void InitializeGrantModules(ItemInstance item)
        {
            if (item == null || item.WasCollected || item._modules == null)
                throw new InvalidOperationException("New grant modules are unavailable.");
            foreach (var module in item._modules) RequireKnownModule(module);
            foreach (var module in item._modules) module.Instantiate();
        }

        private static void RequireKnownModule(ItemInstanceModule module)
        {
            if (module == null || module.WasCollected) throw new InvalidOperationException("Native module unavailable.");
            var type = IL2CPP.il2cpp_object_get_class(module.Pointer);
            if (type != Il2CppClassPointerStore<RangedWeaponItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<PerishableItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<DismemberedLimbItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<ItemPlatingItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<VisualVariantItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<ArrowUpgradeItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<BloodRevealItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<GPSLocatorItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<StructureElementItemInstanceModule>.NativeClassPtr
                && type != Il2CppClassPointerStore<VolumeContainerItemInstanceModule>.NativeClassPtr)
                throw new InvalidOperationException("Unknown module timing cannot participate in a stable inventory capture.");
        }

        private bool RetainsModule(ItemInstanceModule module)
            => (_authority != null || _load == SaveLifecycle.State.BeforeLoadCount) && module != null && !module.WasCollected
                && _pausedModules.TryGetValue(module.Pointer, out var retained) && !retained.WasCollected;

        private static bool ModulePaused(ItemInstanceModule module)
        {
            if (_guard?.RetainsModule(module) == true) return true;
            // These are detached ledger representations, not local inventory custody. Pause
            // their autonomous updates too, so elapsed host time cannot rewrite an accepted baseline.
            foreach (var endpoint in Active)
                if (endpoint._authority != null && endpoint.RetainsModule(module)) return true;
            return false;
        }

        private static bool BeforePerishableUpdate(PerishableItemInstanceModule __instance, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            if (!ModulePaused(__instance)) return true;
            if (__runOriginal && __state && _perishableUpdating && !_perishableUpdateEntered
                && __instance.Pointer == _perishableWriter && _writerDepth == _perishableDepth + 1)
            { _perishableUpdateEntered = true; return true; }
            return false;
        }

        private static bool BeforePerishableState(PerishableItemInstanceModule __instance, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            if (!ModulePaused(__instance)) return true;
            if (__runOriginal && __state && !_perishableStateEntered && __instance.Pointer == _perishableWriter
                && _writerDepth == _perishableDepth + (_perishableUpdating ? 2 : 1)
                && (!_perishableUpdating || _perishableUpdateEntered))
            { _perishableStateEntered = true; return true; }
            return false;
        }

        private static bool BlockPerishableMutation(PerishableItemInstanceModule __instance, out bool __state)
        { EnterWriter(out __state); return !ModulePaused(__instance); }

        internal static PerishableItemInstanceModule FindPerishable(ItemInstance item)
        {
            if (item == null || item.WasCollected || item._modules == null || item._modules.WasCollected) return null;
            PerishableItemInstanceModule found = null;
            foreach (var module in item._modules)
            {
                RequireKnownModule(module);
                if (IL2CPP.il2cpp_object_get_class(module.Pointer) != Il2CppClassPointerStore<PerishableItemInstanceModule>.NativeClassPtr) continue;
                if (found != null) throw new InvalidOperationException("Duplicate perishable modules cannot be advanced.");
                found = module.Cast<PerishableItemInstanceModule>();
            }
            return found;
        }

        internal static void AdvancePerishable(PerishableItemInstanceModule module)
        {
            BeginPerishableWriter(module, true);
            try
            {
                module.Update();
                if (!_perishableUpdateEntered) throw new InvalidOperationException("Native decay did not consume its exact update permission.");
            }
            finally { _perishableWriter = IntPtr.Zero; _perishableUpdating = false; }
        }

        internal static void ProjectPerishable(PerishableItemInstanceModule module, in PlayerInventoryFrame frame)
        {
            if (!float.IsFinite(frame.PerishableRemaining)
                || !Enum.IsDefined(typeof(PerishableItemData.State), frame.PerishableState))
                throw new InvalidOperationException("Perishable projection contains an unknown state or nonfinite timer.");
            BeginPerishableWriter(module, false);
            try
            {
                // Timer-only updates must not repeat state/material events. SetState restarts
                // nonpositive timers, so the exact timer is assigned AFTER all native callbacks.
                if ((int)module._currentState != frame.PerishableState)
                {
                    module.SetState((PerishableItemData.State)frame.PerishableState, frame.PerishableRemaining, true);
                    if (!_perishableStateEntered) throw new InvalidOperationException("Perishable state writer was not admitted.");
                }
                module._remainingSecondsUntilDecay = frame.PerishableRemaining;
                module._PauseDecay_k__BackingField = frame.PauseDecay;
                if ((int)module._currentState != frame.PerishableState
                    || module._remainingSecondsUntilDecay != frame.PerishableRemaining
                    || module._PauseDecay_k__BackingField != frame.PauseDecay)
                    throw new InvalidOperationException("Perishable projection did not preserve exact native values.");
            }
            finally { _perishableWriter = IntPtr.Zero; }
        }

        private static void BeginPerishableWriter(PerishableItemInstanceModule module, bool update)
        {
            if (Environment.CurrentManagedThreadId != _hookThread || _perishableWriter != IntPtr.Zero
                || module == null || module.WasCollected || !module._isInstantiated || !ModulePaused(module)
                || (_guard?.RetainsModule(module) == true && _guard.InventoryFrozen))
                throw new InvalidOperationException("Perishable authority requires one initialized retained native module.");
            _perishableWriter = module.Pointer;
            _perishableDepth = _writerDepth;
            _perishableUpdating = update;
            _perishableUpdateEntered = _perishableStateEntered = false;
        }

        private static bool BeforeVolumeMutation(VolumeContainerItemInstanceModule __instance, out bool __state)
        { EnterWriter(out __state); return !ModulePaused(__instance); }

        private static bool BeforeVolumeEmpty(VolumeContainerItemInstanceModule __instance, float __0, bool __runOriginal, out bool __state)
        {
            EnterWriter(out __state);
            if (_guard?.SuppressPresentedVolumeDebit(__instance, __0, __runOriginal) == true) return false;
            if (!ModulePaused(__instance)) return true;
            var guard = _guard;
            if (!__runOriginal || !__state || guard == null || guard.IsDisposed || guard.IsFaulted
                || !guard._busy || !guard._intakePending || !guard._intakeVolumePermit || _writerDepth != 1
                || guard._intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeUnconfirmed
                || guard._intakeModule != __instance.Pointer || guard._intakeCommand.Debit.Portion != __0
                || guard.InventoryFrozen || !guard.Current()) return false;
            guard._intakeVolumePermit = false;
            return true;
        }
    }

    public sealed partial class PlayerInventoryAuthority
    {
        internal void RetainLedgerModules(PlayerInventoryEnforcement endpoint)
        {
            foreach (var item in _items.Values) endpoint.RetainPausedModules(item.Item);
        }
    }
}
