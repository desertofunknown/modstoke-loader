// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private readonly Dictionary<ulong, SourceFlashlightPower> _sourcePower = new();

        /// <summary>
        /// Capture on the quiesced source before resuming its held controller. Forward only this
        /// exact migration/baseline/key observation for explicit server approval. Does not approve
        /// activity. Rebind after native equipment initialization, never from a prefab or item ID.
        /// </summary>
        public bool TryCaptureSourceFlashlightPower(ulong instanceKey, FlashlightController controller,
            out FlashlightPowerCapture capture, out string detail)
        {
            capture = default;
            detail = "Installed power guards and the retained source's exact initialized flashlight are required.";
            if (!OnThread || _busy || _writerDepth != 0 || IsDisposed || IsFaulted || _authority != null
                || !_nativePowerHooksInstalled || !_hooksInstalled || !IsNativeGuardHeld) return false;
            try
            {
                if (!SourceLifetime() || !Unchanged()) return false;
                var source = FindPowerSource(instanceKey);
                if (source == null || source.Quantity != 1 || source.Instance == null
                    || !PlayerInventoryBaseline.MatchesInstance(source.Entry, source.Instance)
                    || !InventoryPower.TryCaptureFlashlight(source.Instance, controller, out capture, out detail)) return false;
                var held = source.Instance._ItemObject_k__BackingField;
                var module = InventoryPower.FindPowerModule(source.Instance);
                if (held == null || held.WasCollected || !held || !controller.transform.IsChildOf(held.transform)
                    || !RetainsModule(module)) return false;
                if (_sourcePower.TryGetValue(instanceKey, out var retained))
                {
                    if (!SourcePowerCurrent(retained, source) || capture.ItemId != retained.Capture.ItemId
                        || capture.Capacity != retained.Capture.Capacity || capture.Charge != retained.Charge
                        || capture.DrainPerSecond != retained.Capture.DrainPerSecond
                        || capture.AutoRefillThreshold != retained.Capture.AutoRefillThreshold
                        || (retained.Controller != null && !retained.Controller.WasCollected && retained.Controller
                            && retained.Controller.Pointer != controller.Pointer)) return false;
                    retained.Controller = controller;
                }
                else _sourcePower.Add(instanceKey, new(source.Instance, module, capture, controller));
                detail = "Exact source power retained under native consumer exclusion; server baseline and activity approval remain required.";
                return true;
            }
            catch (Exception error) { detail = "Source flashlight capture failed: " + error.Message; return false; }
        }

        /// <summary>
        /// Read the actual retained native activity for the authenticated input adapter. This is
        /// an observation, not server authorization. Never send charge, rate or client delta as activity.
        /// </summary>
        public bool TryReadSourceFlashlightActivity(ulong instanceKey, out bool active, out string detail)
        {
            active = false;
            detail = "The exact retained flashlight controller and active source bridge are required.";
            if (!OnThread || _busy || _writerDepth != 0 || IsDisposed || IsFaulted || !IsArmed
                || _authority != null || _bridge?.IsGameplayActive != true) return false;
            try
            {
                if (!Current() || !_sourcePower.TryGetValue(instanceKey, out var power)) return false;
                var source = FindPowerSource(instanceKey);
                if (!SourcePowerCurrent(power, source)) return false;
                var controller = power.Controller;
                bool live = controller != null && !controller.WasCollected && controller;
                if (source.Entry.EquipmentSlot < 0)
                {
                    if (live && controller.isActiveAndEnabled) return false;
                    detail = "The exact flashlight is stowed and inactive.";
                    return true;
                }
                if (!live || !InventoryPower.TryCaptureFlashlight(power.Item, controller,
                    out var capture, out _) || capture.Capacity != power.Capture.Capacity
                    || capture.DrainPerSecond != power.Capture.DrainPerSecond
                    || capture.AutoRefillThreshold != power.Capture.AutoRefillThreshold) return false;
                var held = power.Item._ItemObject_k__BackingField;
                if (held == null || held.WasCollected || !held || !controller.transform.IsChildOf(held.transform)) return false;
                active = controller.isActiveAndEnabled && controller.IsLocalPlayer();
                detail = "Actual native controller activity observed; validate actor input and held custody on the server.";
                return true;
            }
            catch (Exception error) { detail = "Flashlight activity observation failed: " + error.Message; return false; }
        }

        private SourceItem FindPowerSource(ulong key)
        {
            foreach (var source in _sourceItems) if (source.Entry.Key == key) return source;
            return null;
        }

        private bool SourcePowerCurrent(SourceFlashlightPower power, SourceItem source)
            => SourceLifetime() && IsNativeGuardHeld && source != null && source.Quantity == 1
                && source.Instance != null && !source.Instance.WasCollected && !power.Item.WasCollected
                && source.Instance.Pointer == power.Item.Pointer && source.Entry.ItemId == power.Capture.ItemId
                && power.Item._itemID == power.Capture.ItemId && RetainsModule(power.Module)
                && InventoryPower.FindPowerModule(power.Item)?.Pointer == power.Module.Pointer
                && (power.Module._data?.Pointer ?? 0) == power.Data
                && (power.Module.OnVolumeChanged?.Pointer ?? 0) == power.Callback
                && InventoryPower.Same(power.Module._MaxVolume_k__BackingField, power.Capture.Capacity)
                && InventoryPower.Same(power.Module._CurrentVolume_k__BackingField, power.Charge);

        private bool ApplyPower(in PowerFrame frame)
        {
            if (InventoryFrozen || !Unchanged() || !_nativePowerHooksInstalled
                || !_sourcePower.TryGetValue(frame.State.InstanceKey, out var power)) return false;
            var source = FindPowerSource(frame.State.InstanceKey);
            if (!SourcePowerCurrent(power, source) || frame.State.Revision <= power.Revision
                || !PlayerInventoryBaseline.MatchesInstance(source.Entry, power.Item)
                || frame.State.ItemId != power.Capture.ItemId
                || !InventoryPower.Same(frame.State.Capacity, power.Capture.Capacity)
                || !InventoryPower.Same(frame.BeforeCharge, power.Charge)
                || frame.State.NeedsBattery != (frame.State.Active
                    && frame.State.Charge / frame.State.Capacity <= power.Capture.AutoRefillThreshold)
                || (frame.State.Active && source.Entry.EquipmentSlot < 0)) return false;
            int index = FindCurrentEntry(source);
            if (index < 0 || PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(power.Item))
                != frame.BeforeModules) return false;
            var expectedModuleState = source.Entry.ModuleState with { ContainerVolume = frame.State.Charge };
            if (!InventoryPower.Same(power.Charge, frame.State.Charge))
            {
                var changed = power.Module.OnVolumeChanged;
                if (changed != null && changed.WasCollected) return false;
                // Native SetContents has a 0.001 deadband. Keep exact fractional server
                // charge and notify once while all competing volume writers remain guarded.
                power.Module._CurrentVolume_k__BackingField = frame.State.Charge;
                power.Charge = frame.State.Charge;
                changed?.Invoke(power.Charge);
            }
            if (!Current() || !SourcePowerCurrent(power, source)
                || PlayerInventoryBaseline.CaptureModuleState(power.Item) != expectedModuleState) return false;
            string modules = PlayerInventoryBaseline.CaptureModules(power.Item);
            if (PlayerInventoryWire.HashModules(modules) != frame.AfterModules) return false;
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            if (!_currentSnapshot.ConfirmsModuleChange(after, index, modules, expectedModuleState)) return false;
            var entry = source.Entry;
            _sourceItems[_sourceItems.IndexOf(source)] = new SourceItem(new PlayerInventoryBaseline.Entry
            {
                Key = entry.Key, ItemId = entry.ItemId, Quantity = source.Quantity, Modules = modules, ModuleState = expectedModuleState,
                EquipmentSlot = entry.EquipmentSlot, ClothingSlots = entry.ClothingSlots,
                ArmourSlot = entry.ArmourSlot, ArmourPoints = entry.ArmourPoints, DefinitionOnly = entry.DefinitionOnly
            }, source.Instance);
            _currentSnapshot = after;
            power.Revision = frame.State.Revision;
            return true;
        }

        private sealed class SourceFlashlightPower(ItemInstance item, VolumeContainerItemInstanceModule module,
            FlashlightPowerCapture capture, FlashlightController controller)
        {
            internal readonly ItemInstance Item = item;
            internal readonly VolumeContainerItemInstanceModule Module = module;
            internal readonly FlashlightPowerCapture Capture = capture;
            internal readonly nint Data = module._data?.Pointer ?? 0, Callback = module.OnVolumeChanged?.Pointer ?? 0;
            internal float Charge = capture.Charge;
            internal ulong Revision;
            internal FlashlightController Controller = controller;
        }
    }
}
