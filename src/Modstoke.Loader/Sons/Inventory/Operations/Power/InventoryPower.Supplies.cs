// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Sons.Combat;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private PendingPowerSupply _powerSupply;

        /// <summary>
        /// Queues removal of one exact battery only after admitted flashlight drain reaches its
        /// native threshold. No credit occurs here. Poll completion before another inventory transaction.
        /// </summary>
        public bool TryQueueFlashlightBattery(ulong transaction, ulong flashlightKey, ulong batteryKey, out string detail)
        {
            detail = "An active admitted flashlight at its native refill threshold is required.";
            if (HasPendingNativePowerProjection || !TryReadFlashlightPower(flashlightKey, out var power)) return false;
            if (!power.NeedsBattery) { detail = "The flashlight has no admitted native-threshold battery demand."; return false; }
            return QueuePowerSupply(transaction, flashlightKey, batteryKey,
                InventoryPower.BatteryItemId, null, out detail);
        }

        /// <summary>
        /// Reserves item 469 for the already authorized StrictCombat refill decision. That owner
        /// remains responsible for water/mouthpiece/view admission, timed progress, cancellation and
        /// native rebreather projection. Commit its canister count once from the resulting receipt;
        /// do not issue a second native inventory removal for that same refill.
        /// </summary>
        public bool TryQueueRebreatherCanister(ulong transaction, ulong rebreatherKey, ulong canisterKey,
            StrictCombatOwnedRebreatherState acceptedAir, out string detail)
        {
            detail = "A provisioned idle StrictCombat rebreather account with available tank capacity is required.";
            if (acceptedAir == null || !acceptedAir.IsProvisioned || acceptedAir.Air >= acceptedAir.Capacity
                || acceptedAir.RefillMode != StrictCombatRebreatherRefillMode.None) return false;
            return QueuePowerSupply(transaction, rebreatherKey, canisterKey,
                InventoryPower.AirCanisterItemId, acceptedAir, out detail);
        }

        private bool QueuePowerSupply(ulong transaction, ulong poweredKey, ulong supplyKey, int supplyId,
            StrictCombatOwnedRebreatherState air, out string detail)
        {
            detail = "Fresh exact enforced supply removal and unchanged powered-item custody are required.";
            if (!Ready(out _) || !Fresh(transaction) || _pending != null || _stow != null || _equip != null
                || _powerSupply != null || HasPendingNativePowerProjection
                || MutationEnforcement == null || !MutationEnforcement.CanQueue || poweredKey == supplyKey
                || !_items.TryGetValue(poweredKey, out var powered) || powered.Consumed || powered.Quantity != 1
                || !powered.IdentityCurrent() || !_items.TryGetValue(supplyKey, out var supply)
                || supply.Consumed || supply.Quantity < 1 || supply.Id != supplyId || !supply.IdentityCurrent()
                || supply.HeldSlot >= 0 || supply.Clothing || supply.ArmourSlot >= 0) return false;
            try
            {
                var module = InventoryPower.FindPowerModule(powered.Item);
                if (module == null || !InventoryPower.IsDetached(powered.Item)
                    || !MutationEnforcement.RetainsPowerModule(module)) return false;
                if (supplyId == InventoryPower.BatteryItemId)
                {
                    if (!_flashlightPower.TryGetValue(poweredKey, out var light) || !FlashlightCurrent(light)
                        || !PowerState(light).NeedsBattery) return false;
                }
                else if (powered.Id != InventoryPower.RebreatherItemId || air == null
                    || air.Capacity != module._MaxVolume_k__BackingField) return false;
                var pending = new PendingPowerSupply(transaction, powered, supply, module, supply.Quantity, air);
                _powerSupply = pending;
                if (!TryConsumeItems(transaction, supplyKey, 1, out detail))
                {
                    if (_removal == null) _powerSupply = null;
                    return false;
                }
                detail = "Exact supply removal queued; no battery charge or rebreather refill has been credited.";
                return true;
            }
            catch (Exception error)
            {
                Fault("Power supply reservation is uncertain: " + error.Message);
                detail = Detail;
                return false;
            }
        }

        /// <summary>
        /// Call immediately after the exact applied removal receipt, before another inventory transaction.
        /// Battery: advances admitted drain, then fills the exact module once. Canister: returns one debit
        /// and the original accepted StrictCombat state; the existing owner must verify that revision is
        /// still current and start its funded refill. No new native rebreather engine is installed here.
        /// Failure after a debit never refunds/retries it automatically.
        /// </summary>
        public bool TryCompletePowerSupply(out PowerDebit debit,
            out StrictCombatOwnedRebreatherState rebreatherDecision,
            out FlashlightPowerState flashlight, out string detail)
        {
            debit = default;
            rebreatherDecision = null;
            flashlight = default;
            detail = "The exact applied native removal receipt and committed server ledger debit are pending.";
            if (!Ready(out _) || _powerSupply == null || MutationEnforcement?.LastReceipt is not { } receipt) return false;
            var pending = _powerSupply;
            if (receipt.Transaction < pending.Transaction) return false;
            _busy = true;
            try
            {
                if (receipt.Transaction != pending.Transaction || _lastTransaction != pending.Transaction
                    || MutationEnforcement.LastCompletedTransaction != pending.Transaction
                    || !receipt.Applied || receipt.IsGrant || receipt.IsEquipmentStow || receipt.IsEquipmentEquip
                    || receipt.IsPassiveProgression || receipt.IsConsumption
                    || receipt.InstanceKey != pending.Supply.Key || receipt.ItemId != pending.Supply.Id || receipt.Quantity != 1
                    || !_items.TryGetValue(pending.Supply.Key, out var supply) || !ReferenceEquals(supply, pending.Supply)
                    || supply.Quantity != pending.BeforeQuantity - 1 || supply.Consumed != (supply.Quantity == 0)
                    || !_items.TryGetValue(pending.Powered.Key, out var powered) || !ReferenceEquals(powered, pending.Powered)
                    || powered.Consumed || powered.Quantity != 1 || !powered.IdentityCurrent()
                    || InventoryPower.FindPowerModule(powered.Item)?.Pointer != pending.Module.Pointer
                    || !MutationEnforcement.RetainsPowerModule(pending.Module)
                    || !InventoryPower.IsDetached(powered.Item)
                    || (pending.Module._data?.Pointer ?? 0) != pending.Data
                    || !InventoryPower.Same(pending.Module._MaxVolume_k__BackingField, pending.Capacity))
                    throw new InvalidOperationException("Supply receipt or powered-item custody no longer matches its reservation.");
                if (supply.Id == InventoryPower.BatteryItemId)
                {
                    var power = _flashlightPower[powered.Key];
                    RequireFlashlightCurrent(power);
                    power.AcknowledgedModules = PlayerInventoryBaseline.CaptureModules(power.Item.Item);
                    power.AcknowledgedModuleState = PlayerInventoryBaseline.CaptureModuleState(power.Item.Item);
                    AdvanceFlashlightClock(power);
                    WriteFlashlightCharge(power, power.Capture.Capacity);
                    flashlight = PowerState(power);
                    StagePowerProjection(power);
                }
                if (!Current(out _)) throw new InvalidOperationException("Player lifetime changed during supply completion.");
                debit = new(pending.Transaction, powered.Key, supply.Key, supply.Id);
                rebreatherDecision = pending.Air;
                _powerSupply = null;
                detail = rebreatherDecision == null
                    ? "Verified battery debit funded one full charge; exact source projection is reserved."
                    : "Verified canister debit returned once to the existing rebreather owner; no tank credit applied here.";
                return true;
            }
            catch (Exception error)
            {
                Fault("Power supply completion is unconfirmed: " + error.Message);
                detail = Detail;
                return false;
            }
            finally { _busy = false; }
        }

        private sealed class PendingPowerSupply(ulong transaction, ItemRecord powered, ItemRecord supply,
            VolumeContainerItemInstanceModule module, int beforeQuantity, StrictCombatOwnedRebreatherState air)
        {
            internal readonly ulong Transaction = transaction;
            internal readonly ItemRecord Powered = powered, Supply = supply;
            internal readonly VolumeContainerItemInstanceModule Module = module;
            internal readonly nint Data = module._data?.Pointer ?? 0;
            internal readonly float Capacity = module._MaxVolume_k__BackingField;
            internal readonly int BeforeQuantity = beforeQuantity;
            internal readonly StrictCombatOwnedRebreatherState Air = air;
        }
    }
}
