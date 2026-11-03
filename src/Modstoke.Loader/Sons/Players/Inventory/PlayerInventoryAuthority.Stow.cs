// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private PendingStow _stow;
        private PendingHeldEquip _equip;
        private PlayerEquipmentChange? _equipNotification;

        internal bool TryConsumeEnforcedEquipNotification(in PlayerEquipmentChange change)
        {
            if (Environment.CurrentManagedThreadId != _thread || _suspended || !Current(out _)
                || MutationEnforcement == null || !_equipNotification.HasValue || _equipNotification.Value != change) return false;
            // The separate authenticated native callback is only an observation. It cannot
            // publish equipment or settle custody, even if its datagram arrives after settlement.
            _equipNotification = null;
            return true;
        }

        /// <summary>
        /// Reserves an exact stored unique instance for an empty native hand. Source completion,
        /// server equipment publication and replicated readback are separate receipts. This does
        /// not authorize replacing an occupied hand, displacing its opposite hand, world pickup or equipped stat effects.
        /// </summary>
        public bool TryEquipHeldItem(ulong transaction, ulong instanceKey, int slot, out string detail,
            bool memorizePrevious = false, bool forceFirstLook = false)
        {
            detail = "Native equip requires an enforced account and an exact stored unique instance.";
            if (MutationEnforcement == null || !Ready(out _) || !_items.TryGetValue(instanceKey, out var item)) return false;
            var change = new PlayerEquipmentChange(PlayerEquipmentKind.HeldInitialized, item.Id);
            return TryQueueEnforcedHeldEquip(transaction, instanceKey, in change, slot,
                (byte)((memorizePrevious ? 1 : 0) | (forceFirstLook ? 2 : 0)), out detail);
        }

        /// <summary>
        /// Reserve an unlit unique hand instance's native return to storage, without equipped
        /// stat effects or overflow dropping. Requires a backpack and idle source. Completion is
        /// LastReceipt.Applied with IsEquipmentStow after both peers confirm server publication.
        /// This does not enable general equip/unequip or override another participant's native guard.
        /// </summary>
        public bool TryStowHeldItem(ulong transaction, ulong instanceKey, out string detail)
        {
            detail = "Native stowing requires an enforced current account and a live owned held instance.";
            if (MutationEnforcement == null || !Ready(out _) || !_items.TryGetValue(instanceKey, out var item)) return false;
            var change = new PlayerEquipmentChange(PlayerEquipmentKind.HeldRemoved, item.Id);
            return TryQueueEnforcedEquipment(transaction, instanceKey, in change, -1, out detail);
        }

        private bool TryQueueEnforcedEquipment(ulong transaction, ulong key,
            in PlayerEquipmentChange change, int heldSlot, out string detail)
        {
            if (change.Kind == PlayerEquipmentKind.HeldInitialized)
                return TryQueueEnforcedHeldEquip(transaction, key, in change, heldSlot, 0, out detail);
            detail = "Enforced equipment accepts exact unlit, unique hand stowing; other transitions need their participant transaction.";
            if (!Ready(out var target) || !MutationEnforcement.CanQueue || !Fresh(transaction)
                || _pending != null || _stow != null || heldSlot != -1 || !change.IsValid
                || change.Kind != PlayerEquipmentKind.HeldRemoved || change.Flag
                || !_items.TryGetValue(key, out var item) || item.Consumed || item.Item == null || item.Quantity != 1
                || !HandSlot(item.HeldSlot) || item.Clothing || item.ArmourSlot >= 0
                || item.Id != change.Value || !item.IdentityCurrent()) return false;
            _busy = true;
            try
            {
                var data = item.Item.Data;
                if (!PlayerInventoryEnforcement.SupportsHeldStow(data)) return false;
                var before = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (!ValidateTransition(item, in change, -1, before, out detail)
                    || !PlayerEquipment.Preflight(target.GetState<IPlayerState>(), in change, item.Item, out detail)) return false;
                string modules = PlayerInventoryBaseline.CaptureModules(item.Item);
                if (!Current(out var current) || current.Pointer != target.Pointer) return false;
                var frame = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, key, item.Id, 1, item.HeldSlot,
                    PlayerInventoryMessage.StowHeld, PlayerInventoryWire.HashModules(modules));
                _pending = new PendingEquipment(item, change, -1, before);
                _stow = new PendingStow(frame, modules, PlayerInventoryBaseline.CaptureModuleState(item.Item));
                _lastTransaction = transaction;
                MutationEnforcement.QueueMutation(frame);
                Detail = detail = "Held stow reserved; exact native custody receipt must precede equipment publication.";
                return true;
            }
            catch (Exception error) { detail = "Held stow reservation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool CompleteEnforcedStow(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No exact pending held-stow transaction matches this receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out _)
                || _stow == null || _pending == null || _stow.SourceConfirmed
                || receipt.Kind != PlayerInventoryMessage.StowApplied
                || receipt != (_stow.Frame with { Kind = PlayerInventoryMessage.StowApplied })) return false;
            try
            {
                if (_pending.Item == null || !_pending.Item.IdentityCurrent()
                    || _stow.ModuleState != PlayerInventoryBaseline.CaptureModuleState(_pending.Item.Item)
                    || !PlayerInventoryBaseline.JsonEqual(_stow.Modules,
                        PlayerInventoryBaseline.CaptureModules(_pending.Item.Item))) return false;
                _stow.SourceConfirmed = true;
                // The existing publisher checks its native module/state contract and calls
                // CompletePublication to move this exact record from held to stowed.
                var result = Equipment.PublishInventoryCommand(_pending.Change, out detail);
                if (result != PlayerEquipmentReceipt.Applied) return false;
                if (!Current(out var current)) return false;
                _stow.Published = EquipmentState.Capture(current.GetState<IPlayerState>());
                if (_stow.Published == null) return false;
                Detail = detail = "Exact native held instance stowed; existing equipment publisher confirmed the server custody transition.";
                return true;
            }
            catch (Exception error) { detail = "Held stow publication is unconfirmed: " + error.Message; return false; }
        }

        internal bool ConfirmEnforcedStow(in PlayerInventoryFrame receipt)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out var target)
                || _stow?.SourceConfirmed != true || _stow.Published == null || _pending != null
                || receipt != (_stow.Frame with { Kind = PlayerInventoryMessage.StowSettled })
                || !_items.TryGetValue(receipt.Key, out var item) || item.Consumed || item.Quantity != 1
                || item.HeldSlot != -1 || item.Clothing || item.ArmourSlot != -1 || !item.IdentityCurrent()
                || !_stow.Published.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))
                || _stow.ModuleState != PlayerInventoryBaseline.CaptureModuleState(item.Item)
                || !PlayerInventoryBaseline.JsonEqual(_stow.Modules,
                    PlayerInventoryBaseline.CaptureModules(item.Item))) return false;
            _stow = null;
            return true;
        }

        private sealed class PendingStow(PlayerInventoryFrame frame, string modules, PlayerInventoryModuleState moduleState)
        {
            internal readonly PlayerInventoryFrame Frame = frame;
            internal readonly string Modules = modules;
            internal readonly PlayerInventoryModuleState ModuleState = moduleState;
            internal bool SourceConfirmed;
            internal EquipmentState Published;
        }

        private bool TryQueueEnforcedHeldEquip(ulong transaction, ulong key,
            in PlayerEquipmentChange change, int slot, byte options, out string detail)
        {
            detail = "Only an exact unique, effect-free stored instance and vacant native hand can be reserved.";
            if (!Ready(out var target) || !MutationEnforcement.CanQueue || !Fresh(transaction)
                || _pending != null || _stow != null || _equip != null || _equipNotification.HasValue
                || !HandSlot(slot) || options != 0
                || !change.IsValid || change.Kind != PlayerEquipmentKind.HeldInitialized
                || !_items.TryGetValue(key, out var item) || item.Consumed || item.Item == null
                || item.Quantity != 1 || item.HeldSlot >= 0 || item.Clothing || item.ArmourSlot >= 0
                || item.Id != change.Value || !item.IdentityCurrent()) return false;
            _busy = true;
            try
            {
                if (!PlayerInventoryEnforcement.SupportsHeldStow(item.Item.Data)
                    || (int)item.Item.Data._equipmentSlot != slot) return false;
                foreach (var other in _items.Values)
                {
                    if (other.Consumed || other.HeldSlot < 0) continue;
                    if (other.HeldSlot == slot || !HandSlot(other.HeldSlot) || other.Item == null || !other.IdentityCurrent()
                        || item.Item.Data._requiresBothHands || item.Item.Data._firstLookRequiresBothHands
                        || other.Item.Data._requiresBothHands) return false;
                }
                var state = target.GetState<IPlayerState>();
                var before = EquipmentState.Capture(state);
                if (before == null
                    || !ValidateTransition(item, in change, slot, before, out detail)
                    || !PlayerEquipment.Preflight(state, in change, item.Item, out detail)) return false;
                string modules = PlayerInventoryBaseline.CaptureModules(item.Item);
                if (!Current(out var current) || current.Pointer != target.Pointer) return false;
                var frame = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, transaction, key, item.Id, 1, slot,
                    PlayerInventoryMessage.EquipHeld, PlayerInventoryWire.HashModules(modules), EquipmentOptions: options);
                _pending = new PendingEquipment(item, change, slot, before);
                _equip = new PendingHeldEquip(frame, modules, PlayerInventoryBaseline.CaptureModuleState(item.Item));
                _equipNotification = change;
                _lastTransaction = transaction;
                MutationEnforcement.QueueMutation(frame);
                Detail = detail = "Exact native equip reserved; coroutine completion and full custody readback are pending.";
                return true;
            }
            catch (Exception error) { detail = "Native equip reservation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        internal bool CompleteEnforcedHeldEquip(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No exact native equip reservation matches this completion receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out _)
                || _equip == null || _equip.SourceConfirmed || _pending == null
                || receipt != (_equip.Frame with { Kind = PlayerInventoryMessage.EquipApplied })) return false;
            try
            {
                if (_pending.Item == null || !_pending.Item.IdentityCurrent()
                    || _equip.ModuleState != PlayerInventoryBaseline.CaptureModuleState(_pending.Item.Item)
                    || !PlayerInventoryBaseline.JsonEqual(_equip.Modules,
                        PlayerInventoryBaseline.CaptureModules(_pending.Item.Item))) return false;
                _equip.SourceConfirmed = true;
                if (Equipment.PublishInventoryCommand(_pending.Change, out detail) != PlayerEquipmentReceipt.Applied
                    || !Current(out var target)) return false;
                _equip.Published = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (_equip.Published == null) return false;
                Detail = detail = "Native equip custody confirmed and server equipment publication applied; source readback is pending.";
                return true;
            }
            catch (Exception error) { detail = "Native equip publication is unconfirmed: " + error.Message; return false; }
        }

        internal bool ConfirmEnforcedHeldEquip(in PlayerInventoryFrame receipt)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out var target)
                || _equip?.SourceConfirmed != true || _equip.Published == null || _pending != null
                || receipt != (_equip.Frame with { Kind = PlayerInventoryMessage.EquipSettled })
                || !_items.TryGetValue(receipt.Key, out var item) || item.Consumed || item.Quantity != 1
                || item.HeldSlot != receipt.BeforeQuantity || item.Clothing || item.ArmourSlot >= 0
                || !item.IdentityCurrent() || !_equip.Published.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))
                || _equip.ModuleState != PlayerInventoryBaseline.CaptureModuleState(item.Item)
                || !PlayerInventoryBaseline.JsonEqual(_equip.Modules,
                    PlayerInventoryBaseline.CaptureModules(item.Item))) return false;
            _equip = null;
            return true;
        }

        internal bool RejectEnforcedHeldEquip(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No wholly unwritten equip reservation matches this rejection.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out var target)
                || _equip == null || _equip.SourceConfirmed || _pending == null || _pending.Attempted
                || receipt != (_equip.Frame with { Kind = PlayerInventoryMessage.EquipRejected })
                || !_pending.Before.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))
                || !_pending.Item.IdentityCurrent() || _equip.ModuleState != PlayerInventoryBaseline.CaptureModuleState(_pending.Item.Item)
                || !PlayerInventoryBaseline.JsonEqual(_equip.Modules,
                    PlayerInventoryBaseline.CaptureModules(_pending.Item.Item))) return false;
            _pending = null;
            _equip = null;
            _equipNotification = null;
            Detail = detail = "Native equip declined before any mutation; server custody is unchanged.";
            return true;
        }

        private sealed class PendingHeldEquip(PlayerInventoryFrame frame, string modules, PlayerInventoryModuleState moduleState)
        {
            internal readonly PlayerInventoryFrame Frame = frame;
            internal readonly string Modules = modules;
            internal readonly PlayerInventoryModuleState ModuleState = moduleState;
            internal bool SourceConfirmed;
            internal EquipmentState Published;
        }
    }

    public sealed partial class PlayerEquipment
    {
        internal PlayerEquipmentReceipt PublishInventoryCommand(in PlayerEquipmentChange change, out string detail)
        {
            detail = "Equipment publication needs its idle, current server endpoint.";
            if (!OnThread || _busy || !_server || IsFaulted || !Current(out var target)) return PlayerEquipmentReceipt.Unconfirmed;
            _busy = true;
            try { return Publish(target, in change, out detail, inventoryCommand: true); }
            finally { _busy = false; }
        }
    }
}
