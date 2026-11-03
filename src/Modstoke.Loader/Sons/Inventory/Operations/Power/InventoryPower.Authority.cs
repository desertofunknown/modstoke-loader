// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private readonly Dictionary<ulong, FlashlightPower> _flashlightPower = new();

        /// <summary>Complete this supply before admitting another inventory transaction or migration.</summary>
        public bool HasPendingNativePowerSupply => _powerSupply != null;

        // Called only once the owning inventory authority has retired.
        internal void ClearPowerState()
        {
            if (Environment.CurrentManagedThreadId != _thread || !IsDisposed)
                throw new InvalidOperationException("Clear native power state only after retiring its inventory authority.");
            _powerSupply = null;
            _powerProjection = null;
            _flashlightPower.Clear();
        }

        /// <summary>
        /// Explicit server policy acceptance of a source observation correlated with this baseline/key.
        /// Must precede all powered activity. The V2 baseline retains exact fractional charge;
        /// controller rates still need explicit approval. This does not install consumer interception.
        /// </summary>
        public bool TryAcceptUnverifiedFlashlightPower(ulong instanceKey,
            in FlashlightPowerCapture capture, out string detail)
        {
            detail = "An idle enforced account, accepted baseline and exact approved flashlight observation are required.";
            if (!Ready(out _) || _pending != null || _stow != null || _equip != null || _powerSupply != null
                || HasPendingNativePowerProjection || !HasAcceptedBaseline || MutationEnforcement == null || !MutationEnforcement.CanQueue
                || !capture.IsValid || _flashlightPower.ContainsKey(instanceKey)
                || !_items.TryGetValue(instanceKey, out var item) || item.Consumed || item.Quantity != 1
                || item.Id != capture.ItemId || !item.FromAcceptedBaseline || !item.IdentityCurrent()) return false;
            _busy = true;
            bool attempted = false;
            try
            {
                var module = InventoryPower.FindPowerModule(item.Item);
                if (module == null || !InventoryPower.IsDetached(item.Item)
                    || !MutationEnforcement.RetainsPowerModule(module)
                    || !InventoryPower.Same(module._MaxVolume_k__BackingField, capture.Capacity)) return false;
                float current = module._CurrentVolume_k__BackingField;
                if (!InventoryPower.Same(current, capture.Charge)) return false;
                double now = Time.timeAsDouble;
                if (!double.IsFinite(now) || now < 0) return false;
                var power = new FlashlightPower(item, module, capture, current, now);
                attempted = true;
                WriteFlashlightCharge(power, capture.Charge);
                power.AcknowledgedModules = PlayerInventoryBaseline.CaptureModules(item.Item);
                power.AcknowledgedModuleState = PlayerInventoryBaseline.CaptureModuleState(item.Item);
                _flashlightPower.Add(instanceKey, power);
                detail = "Exact approved flashlight charge retained; inactive until a separate server activity decision.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Flashlight acceptance failed: " + error.Message;
                if (attempted) Fault(detail + " Native readback is uncertain; do not retry.");
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Apply a trusted server decision at the activity boundary, before changing held equipment.
        /// Advances the PREVIOUS admitted interval first. Never forward client activity without validation.
        /// No controller Update, input, IK, camera or LocalPlayer globals are touched.
        /// </summary>
        public bool TrySetFlashlightPowerActive(ulong instanceKey, bool active,
            out FlashlightPowerState state, out string detail)
            => AdvanceFlashlightPower(instanceKey, active, out state, out detail);

        /// <summary>
        /// Pump on the game thread. Drain uses the server's scaled Unity clock and retained native rate.
        /// Repeated calls at the same time do not drain twice. Changed state reserves an exact
        /// projection; the inventory endpoint must pump and receive the power protocol.
        /// </summary>
        public bool TryAdvanceFlashlightPower(ulong instanceKey,
            out FlashlightPowerState state, out string detail)
            => AdvanceFlashlightPower(instanceKey, null, out state, out detail);

        public bool TryReadFlashlightPower(ulong instanceKey, out FlashlightPowerState state)
        {
            state = default;
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out _)) return false;
            try
            {
                if (!_flashlightPower.TryGetValue(instanceKey, out var power) || !FlashlightCurrent(power)) return false;
                state = PowerState(power);
                return true;
            }
            catch (Exception) { return false; }
        }

        private bool AdvanceFlashlightPower(ulong key, bool? active,
            out FlashlightPowerState state, out string detail)
        {
            state = default;
            detail = "A current retained flashlight and stable server activity interval are required.";
            if (!Ready(out _) || HasPendingNativePowerProjection || HasPendingNativePowerSupply
                || MutationEnforcement == null || !MutationEnforcement.CanQueue
                || _pending != null || _stow != null || _equip != null
                || !_flashlightPower.TryGetValue(key, out var power)) return false;
            _busy = true;
            try
            {
                RequireFlashlightCurrent(power);
                if (active.HasValue && (!Current(out var target)
                    || !OwnedEquipmentMatches(EquipmentState.Capture(target.GetState<IPlayerState>())))) return false;
                if (active == true && power.Item.HeldSlot < 0) return false;
                power.AcknowledgedModules = PlayerInventoryBaseline.CaptureModules(power.Item.Item);
                power.AcknowledgedModuleState = PlayerInventoryBaseline.CaptureModuleState(power.Item.Item);
                AdvanceFlashlightClock(power);
                if (active.HasValue && active.Value != power.Active)
                {
                    RequirePowerRevision(power);
                    power.Active = active.Value;
                    power.Revision++;
                }
                state = PowerState(power);
                StagePowerProjection(power);
                detail = "Server flashlight charge advanced; changed state awaits exact source projection acknowledgement.";
                return true;
            }
            catch (Exception error)
            {
                Fault("Flashlight progression is unconfirmed: " + error.Message);
                detail = Detail;
                return false;
            }
            finally { _busy = false; }
        }

        private void AdvanceFlashlightClock(FlashlightPower power)
        {
            double now = Time.timeAsDouble;
            double elapsed = now - power.Time;
            if (!double.IsFinite(now) || !double.IsFinite(elapsed) || elapsed < 0)
                throw new InvalidOperationException("Server power clock changed epoch.");
            if (power.Active && power.Item.HeldSlot < 0)
                throw new InvalidOperationException("Held custody ended without closing its powered interval.");
            if (power.Active && elapsed > 0 && power.Charge > 0)
            {
                double amount = power.Capture.DrainPerSecond * elapsed;
                float charge = amount >= power.Charge ? 0 : Math.Max(0, power.Charge - (float)amount);
                WriteFlashlightCharge(power, charge);
            }
            power.Time = now;
        }

        private void WriteFlashlightCharge(FlashlightPower power, float charge)
        {
            RequireFlashlightCurrent(power);
            if (!float.IsFinite(charge) || charge < 0 || charge > power.Capture.Capacity)
                throw new InvalidOperationException("Invalid authorized flashlight charge.");
            if (InventoryPower.Same(charge, power.Charge)) return;
            RequirePowerRevision(power);
            var changed = power.Module.OnVolumeChanged;
            if (changed != null && changed.WasCollected)
                throw new InvalidOperationException("Power callback became unavailable.");
            nint callback = changed?.Pointer ?? 0;
            // Like accepted native rebreather projection, bypass SetContents' 0.001 tolerance.
            // Recipe-free power preserves the Empty/Refill value and callback ordering without
            // temporarily opening the retained module's native mutation guard to callback writers.
            power.Module._CurrentVolume_k__BackingField = charge;
            power.Charge = charge;
            RequireFlashlightCurrent(power);
            changed?.Invoke(charge);
            RequireFlashlightCurrent(power);
            if ((power.Module.OnVolumeChanged?.Pointer ?? 0) != callback)
                throw new InvalidOperationException("Power callback identity changed during mutation.");
            power.Revision++;
        }

        private bool FlashlightCurrent(FlashlightPower power)
            => Current(out _) && MutationEnforcement != null && MutationEnforcement.IsArmed
                && !power.Item.Consumed && power.Item.Quantity == 1 && power.Item.IdentityCurrent()
                && _items.TryGetValue(power.Item.Key, out var item) && ReferenceEquals(item, power.Item)
                && InventoryPower.IsDetached(item.Item) && MutationEnforcement.RetainsPowerModule(power.Module)
                && InventoryPower.FindPowerModule(item.Item)?.Pointer == power.Module.Pointer
                && (power.Module._data?.Pointer ?? 0) == power.Data
                && (power.Module.OnVolumeChanged?.Pointer ?? 0) == power.Callback
                && InventoryPower.Same(power.Module._MaxVolume_k__BackingField, power.Capture.Capacity)
                && InventoryPower.Same(power.Module._CurrentVolume_k__BackingField, power.Charge);

        private void RequireFlashlightCurrent(FlashlightPower power)
        {
            if (!FlashlightCurrent(power)) throw new InvalidOperationException("Exact actor/item/module power custody changed.");
        }

        private static void RequirePowerRevision(FlashlightPower power)
        {
            if (power.Revision == ulong.MaxValue) throw new InvalidOperationException("Power revision exhausted.");
        }

        private static FlashlightPowerState PowerState(FlashlightPower power)
            => new(power.Item.Key, power.Revision, power.Item.Id, power.Capture.Capacity, power.Charge,
                power.Active, power.Active && power.Charge / power.Capture.Capacity <= power.Capture.AutoRefillThreshold);

        private sealed class FlashlightPower(ItemRecord item, VolumeContainerItemInstanceModule module,
            FlashlightPowerCapture capture, float charge, double time)
        {
            internal readonly ItemRecord Item = item;
            internal readonly VolumeContainerItemInstanceModule Module = module;
            internal readonly FlashlightPowerCapture Capture = capture;
            internal readonly nint Data = module._data?.Pointer ?? 0;
            internal readonly nint Callback = module.OnVolumeChanged?.Pointer ?? 0;
            internal float Charge = charge;
            internal double Time = time;
            internal bool Active;
            internal ulong Revision = 1;
            internal ulong AcknowledgedRevision;
            internal float AcknowledgedCharge = capture.Charge;
            internal string AcknowledgedModules;
            internal PlayerInventoryModuleState AcknowledgedModuleState;
        }
    }

    public sealed partial class PlayerInventoryEnforcement
    {
        internal bool RetainsPowerModule(VolumeContainerItemInstanceModule module)
            => OnThread && !IsDisposed && !IsFaulted && _authority != null && RetainsModule(module);
    }
}
