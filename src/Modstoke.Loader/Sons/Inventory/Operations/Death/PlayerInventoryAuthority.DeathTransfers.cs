// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public enum InventoryDeathState { Reserved, Removing, Restoring, Applied, Unconfirmed }
    public readonly record struct InventoryDeathReceipt(ulong Transaction, ulong Life,
        ulong BagNetworkId, bool Restoring, InventoryDeathState State,
        int Completed, int Total, ulong LastTransaction, string Detail);
    public readonly record struct InventoryDeathRestoredItem(ulong OriginalKey, ulong RestoredKey, int ItemId);

    public sealed partial class PlayerInventoryAuthority
    {
        private NativeDeathTransfer _nativeDeath;
        private InventoryDeathBag _nativeDeathBag;
        private readonly List<NativeDeathItem> _nativeDeathContents = new();
        private readonly List<InventoryDeathRestoredItem> _nativeDeathRestored = new();
        private ulong _nativeDeathLife;
        private InventoryDeathReceipt? _nativeDeathReceipt;
        public bool HasNativeDeathTransfer => _nativeDeath != null;
        public bool HasNativeDeathBag => _nativeDeathBag != null;

        /// <summary>
        /// Bind an empty, independently created server-owned world bag, never the host's
        /// LocalPlayer inventory/action. The integrator must fence vanilla collectors and
        /// writes using DeathBag.IsHeld for the entire binding, not just while a transfer runs.
        /// </summary>
        public bool TryBindDeathBag(BoltEntity entity, ItemInstanceManager manager,
            out InventoryDeathBag bag, out string detail)
        {
            bag = null;
            detail = "Death custody requires an idle accepted actor and an empty distinct server-owned bag.";
            if (!DeathReady(out var target) || _nativeDeathBag != null
                || !InventoryDeath.Live(entity) || !InventoryDeath.Live(manager)
                || entity.Pointer == target.Pointer || entity.networkId.PackedValue == _bridge.NetworkId) return false;
            _busy = true;
            try
            {
                DeathRequire(InventoryDeath.Live(manager.transform)
                    && InventoryDeath.Live(target.transform)
                    && !manager.transform.IsChildOf(target.transform), "Player storage cannot be rebound as death-bag storage.");
                bag = _nativeDeathBag = new InventoryDeathBag(this, entity, manager);
                detail = "Exact empty server bag bound. Creation, client bag presentation and save ownership are not implied.";
                return true;
            }
            catch (Exception error) { detail = "Death bag binding rejected: " + error.Message; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Trusted actor-life decision, not a client death claim. Selects every owned DropsOnDeath
        /// record, refusing transient/equipped custody rather than silently skipping it. Stow and
        /// life presentation must settle first. No native whole-death routine is run.
        /// A null bag means explicit permanent removal; it is never inferred from a missing bag.
        /// </summary>
        public bool TryBeginDeathRemoval(ulong transaction, ulong life,
            InventoryDeathBag bag, out string detail)
        {
            detail = "Death removal requires a fresh authoritative life, settled equipment and exclusive inventory ingress.";
            if (!DeathReady(out var target) || !Fresh(transaction) || life == 0 || life <= _nativeDeathLife
                || (bag != null && !ReferenceEquals(bag, _nativeDeathBag))
                || (bag == null && _nativeDeathBag != null)) return false;
            try
            {
                DeathRequire(bag == null || bag.Unchanged(this), "Native death bag differs from retained custody.");
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                DeathRequire(equipment != null && OwnedEquipmentMatches(equipment), "Death equipment publication differs from custody.");
                var selected = new List<NativeDeathItem>();
                var amounts = new Dictionary<int, int>();
                foreach (var record in _items.Values)
                {
                    if (record.Consumed) continue;
                    DeathRequire(record.IdentityCurrent(), "Death selection contains an expired inventory identity.");
                    var data = record.Item == null ? ItemDatabaseManager.ItemById(record.Id) : record.Item.Data;
                    if (!data.DropsOnDeath) continue;
                    DeathRequire(!record.DefinitionOnly && record.HeldSlot < 0 && !record.Clothing && record.ArmourSlot < 0
                        && record.Quantity > 0 && (data._eachInstanceIsUnique
                            ? record.Item != null && record.Quantity == 1 : record.Item == null),
                        "A drop-on-death item is not stowed. Exact held/clothing/armour custody must settle before death removal.");
                    selected.Add(new NativeDeathItem(record));
                    amounts.TryGetValue(record.Id, out int amount);
                    amounts[record.Id] = checked(amount + record.Quantity);
                }
                DeathRequire(selected.Count + _nativeDeathContents.Count <= MaximumIssuedItems, "Death bag custody exceeds its entry bound.");
                bag?.CheckCapacity(amounts);
                var transfer = new NativeDeathTransfer(transaction, life, bag, selected.ToArray(), false, equipment,
                    checked(transaction + (ulong)selected.Count));
                if (!ReserveDeath(transfer, out detail)) return false;
                _nativeDeathLife = life;
                return true;
            }
            catch (Exception error) { detail = "Death removal reservation refused: " + error.Message; return false; }
        }

        /// <summary>
        /// Restores only this account's exact retained bag custody after independent server
        /// interaction/reach authorization. Full capacity is checked before any removal.
        /// Each restored unit gets a new source key; the original key mapping is retained.
        /// Debit precedes grant preparation. Rejection retains the removed item in unresolved
        /// custody; it never refunds a possibly delivered item or sends another grant.
        /// </summary>
        public bool TryBeginDeathRestore(ulong transaction, InventoryDeathBag bag, out string detail)
        {
            detail = "Restoration requires this actor's unchanged bag and idle exact native inventory endpoint.";
            if (!DeathReady(out var target) || !Fresh(transaction) || bag == null
                || !ReferenceEquals(bag, _nativeDeathBag) || _nativeDeathContents.Count == 0) return false;
            try
            {
                DeathRequire(bag.Unchanged(this), "Native death bag differs from retained custody.");
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                DeathRequire(equipment != null && OwnedEquipmentMatches(equipment), "Restore equipment publication differs from custody.");
                var units = new List<NativeDeathItem>();
                var totals = new Dictionary<int, long>();
                foreach (var record in _items.Values)
                    if (!record.Consumed)
                    { totals.TryGetValue(record.Id, out long count); totals[record.Id] = count + record.Quantity; }
                foreach (var item in _nativeDeathContents)
                {
                    DeathRequire(item.Current(), "Retained death item identity or modules changed.");
                    var data = ItemDatabaseManager.ItemById(item.Id);
                    totals.TryGetValue(item.Id, out long count);
                    long total = count + item.Remaining;
                    totals[item.Id] = total;
                    DeathRequire(!data._canNotBeStored && total <= data._maxAmount,
                        "The complete bag no longer fits in player inventory; no bag debit was attempted.");
                    DeathRequire((long)units.Count + item.Remaining + _items.Count <= MaximumIssuedItems,
                        "Restoration would exceed the exact source-key bound.");
                    for (int i = 0; i < item.Remaining; i++) units.Add(item);
                }
                var transfer = new NativeDeathTransfer(transaction, _nativeDeathLife, bag, units.ToArray(), true, equipment,
                    checked(transaction + (ulong)units.Count));
                if (!ReserveDeath(transfer, out detail)) return false;
                _nativeDeathRestored.Clear();
                return true;
            }
            catch (Exception error) { detail = "Death restore reservation refused: " + error.Message; return false; }
        }

        private bool DeathReady(out BoltEntity target)
            => Ready(out target) && !HasNativeDeathTransfer && HasAcceptedBaseline && !HasInventoryLifecycleHold
                && !HasPendingNativePowerSupply && _pending == null && _stow == null && _equip == null
                && MutationEnforcement != null && MutationEnforcement.CanQueue && !MutationEnforcement.HasPendingCustody;

        private bool ReserveDeath(NativeDeathTransfer transfer, out string detail)
        {
            detail = "Death custody changed during preflight; no command was queued.";
            if (!DeathReady(out _)) return false;
            _nativeDeath = transfer;
            // A partial integration must not open other inventory writers while this owns custody.
            if (Ready(out _))
            {
                _nativeDeath = null;
                detail = "Add HasNativeDeathTransfer to authority PreparedReady before binding death custody.";
                return false;
            }
            _lastTransaction = transfer.Last;
            SetDeathReceipt(transfer, "Exact death custody reserved; no native removal or restoration is yet confirmed.");
            detail = Detail;
            return true;
        }

        /// <summary>Call on the inventory game-thread pump before passive writers; never from a native death callback.</summary>
        internal void PumpDeathInventory()
        {
            var transfer = _nativeDeath;
            if (Environment.CurrentManagedThreadId != _thread || _busy || transfer == null
                || transfer.State == InventoryDeathState.Unconfirmed) return;
            if (IsDisposed || IsFaulted || _suspended || !_bridge.IsGameplayActive || !Current(out var target)
                || MutationEnforcement == null || MutationEnforcement.IsDisposed || MutationEnforcement.IsFaulted)
            { FailDeath(transfer, "Death custody lifetime ended; partial native changes cannot be retried or refunded."); return; }
            _busy = true;
            try
            {
                DeathRequire(_lastTransaction == transfer.Last && transfer.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>())),
                    "Death transaction floor or actor equipment changed.");
                DeathRequire(transfer.Bag == null || transfer.Bag.Current(this), "Bound death bag retired.");
                if (transfer.Awaiting)
                {
                    DeathRequire(MutationEnforcement.TryReadDeathMutationReceipt(transfer.Command,
                        out bool complete, out bool applied, out string receipt), receipt);
                    if (!complete) return;
                    DeathRequire(transfer.Bag == null || transfer.Bag.Unchanged(this), "Native bag custody changed while a receipt was pending.");
                    var item = transfer.Items[transfer.Completed];
                    DeathRequire(item.Current(), "Death item identity changed before custody settlement.");
                    if (transfer.Restoring)
                    {
                        if (!applied) MutationEnforcement.RetainPausedModules(item.Native);
                        DeathRequire(applied, "Restore was rejected after bag debit; the removed item remains in unresolved custody, without refund.");
                        DeathRequire(transfer.BagTaken && transfer.BagDebitAttempted && transfer.Restored != null
                            && _items.TryGetValue(transfer.Command.Key, out var restored) && ReferenceEquals(restored, transfer.Restored)
                            && !restored.Consumed && restored.Quantity == 1 && restored.IdentityCurrent(),
                            "Restore receipt has no matching bag debit and committed server instance.");
                        _nativeDeathRestored.Add(new(item.OriginalKey, transfer.Restored.Key, item.Id));
                        transfer.BagTaken = transfer.BagDebitAttempted = false;
                        transfer.Restored = null;
                        transfer.PlainGrant = null;
                    }
                    else
                    {
                        DeathRequire(applied && item.Original.Consumed && item.Original.Quantity == 0,
                            "Death removal has no exact committed player debit.");
                        if (transfer.Bag != null)
                        {
                            transfer.Bag.Add(item.Id, item.Quantity, item.Native);
                            DeathRequire(item.Current() && Current(out var afterDeposit) && afterDeposit.Pointer == target.Pointer,
                                "Bag callbacks changed the deposited item or actor lifetime.");
                            MutationEnforcement.RetainPausedModules(item.Native);
                            _nativeDeathContents.Add(item);
                        }
                    }
                    transfer.Completed++;
                    transfer.Awaiting = false;
                }
                if (transfer.Completed == transfer.Items.Length)
                {
                    _nativeDeathContents.RemoveAll(item => item.Remaining == 0);
                    transfer.State = InventoryDeathState.Applied;
                    SetDeathReceipt(transfer, transfer.Restoring
                        ? "Every bag debit and exact native restore grant was acknowledged; controls and life presentation were not changed."
                        : "Every selected death item has a confirmed native removal and, when requested, exact bag custody.");
                    _nativeDeath = null;
                    return;
                }
                DeathRequire(_pending == null && _stow == null && _equip == null && _grant == null
                    && _removal == null && _passive == null && _worldTransfer == null,
                    "Another transaction entered reserved death custody.");
                if (!MutationEnforcement.CanQueue) return;
                DeathRequire(transfer.Bag == null || transfer.Bag.Unchanged(this), "Bag changed before the next exact custody command.");
                var selected = transfer.Items[transfer.Completed];
                DeathRequire(selected.Current(), "Selected death instance or native modules are unavailable.");
                ulong commandId = transfer.Transaction + (ulong)transfer.Completed + 1;
                if (!transfer.Restoring)
                {
                    DeathRequire(!selected.Original.Consumed && selected.Original.Quantity == selected.Quantity
                        && selected.Original.HeldSlot < 0 && !selected.Original.Clothing && selected.Original.ArmourSlot < 0,
                        "Death removal no longer has the selected exact stowed custody.");
                    transfer.Command = DeathFrame(commandId, selected.OriginalKey, selected.Id, selected.Quantity,
                        selected.Quantity, PlayerInventoryMessage.Remove);
                    _removal = new PendingRemoval(transfer.Command, selected.Original, selected.Modules, transfer.Equipment);
                    transfer.State = InventoryDeathState.Removing;
                }
                else
                {
                    DeathRequire(selected.Remaining > 0 && !_items.ContainsKey(commandId), "Restored key or bag quantity is unavailable.");
                    var native = selected.Native;
                    if (native == null)
                    {
                        native = new ItemInstance(selected.Id);
                        transfer.PlainGrant = native;
                        PlayerInventoryEnforcement.InitializeGrantModules(native);
                        MutationEnforcement.RetainPausedModules(native);
                        DeathRequire(native._modules.Count == 0, "Nonunique restoration cannot discard native modules.");
                    }
                    string modules = PlayerInventoryBaseline.CaptureModules(native);
                    int stored = 0;
                    foreach (var existing in _items.Values)
                        if (!existing.Consumed && existing.Id == selected.Id && existing.HeldSlot < 0 && !existing.Clothing && existing.ArmourSlot < 0)
                            stored = checked(stored + existing.Quantity);
                    transfer.Restored = selected.Native != null ? new ItemRecord(commandId, native) : new ItemRecord(commandId, selected.Id, 1);
                    DeathRequire(transfer.Restored.CaptureIdentity(), "Restored native identity could not be retained.");
                    transfer.Command = DeathFrame(commandId, commandId, selected.Id, 1, stored,
                        PlayerInventoryMessage.GrantPrepare, PlayerInventoryWire.HashModules(modules));
                    // Own the item outside the bag before exposing any grant command. The normal
                    // receiver can now reject or send once without any second custody writer hook.
                    transfer.BagDebitAttempted = true;
                    transfer.Bag.Take(selected.Id, selected.Native);
                    DeathRequire(selected.Current() && Current(out _), "Bag debit changed item identity or actor lifetime.");
                    selected.Remaining--;
                    transfer.BagTaken = true;
                    _grant = new PendingGrant(transfer.Command, transfer.Restored, native, modules, transfer.Equipment);
                    transfer.State = InventoryDeathState.Restoring;
                }
                transfer.Awaiting = true;
                MutationEnforcement.QueueMutation(transfer.Command);
                SetDeathReceipt(transfer, "One exact death custody command queued; only its authenticated native receipt advances this transfer.");
            }
            catch (Exception error) { FailDeath(transfer, "Death custody is unconfirmed: " + error.Message); }
            finally { _busy = false; }
        }

        public bool TryReadDeathReceipt(ulong transaction, out InventoryDeathReceipt receipt)
        {
            receipt = default;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            if (_nativeDeath != null && (IsDisposed || IsFaulted)
                && _nativeDeath.State != InventoryDeathState.Unconfirmed)
                FailDeath(_nativeDeath, "Actor retired or faulted while death custody was unresolved.");
            if (_nativeDeathReceipt is not { } current || current.Transaction != transaction) return false;
            receipt = current;
            return true;
        }

        public InventoryDeathRestoredItem[] GetDeathRestoredItems(ulong transaction)
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Read death custody on its owning thread.");
            if (_nativeDeathReceipt is not { } receipt || !receipt.Restoring || receipt.Transaction != transaction)
                return Array.Empty<InventoryDeathRestoredItem>();
            return _nativeDeathRestored.ToArray();
        }

        /// <summary>Release only empty settled custody. The lifecycle owner may then retire the exact world bag.</summary>
        public bool TryReleaseDeathBag(InventoryDeathBag bag, out string detail)
        {
            detail = "A current empty bag with no unresolved transfer is required.";
            if (!DeathReady(out _) || bag == null || !ReferenceEquals(bag, _nativeDeathBag) || _nativeDeathContents.Count != 0) return false;
            try { bag.Release(this); _nativeDeathBag = null; detail = "Empty bag world fence released; no native player lifecycle was invoked."; return true; }
            catch (Exception error) { detail = "Bag release refused: " + error.Message; return false; }
        }

        // Call before inventory Dispose clears ledger records. Keep the bag fence and retained
        // instances: disconnect/load is not permission to mint a replacement bag or replay grants.
        internal void RetireDeathInventory()
        {
            if (_nativeDeath != null) FailDeath(_nativeDeath, "Inventory retired with unresolved death custody; no replay or refund.");
        }

        private PlayerInventoryFrame DeathFrame(ulong transaction, ulong key, int id, int quantity, int before,
            PlayerInventoryMessage kind, string modules = null)
            => new(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId, AcceptedBaselineFingerprint,
                transaction, key, id, quantity, before, kind, modules);

        private void SetDeathReceipt(NativeDeathTransfer transfer, string detail)
        {
            Detail = detail;
            _nativeDeathReceipt = new(transfer.Transaction, transfer.Life, transfer.Bag?.NetworkId ?? 0,
                transfer.Restoring, transfer.State, transfer.Completed, transfer.Items.Length, transfer.Last, detail);
        }
        private void FailDeath(NativeDeathTransfer transfer, string detail)
        {
            transfer.State = InventoryDeathState.Unconfirmed;
            SetDeathReceipt(transfer, detail);
            Fault(detail);
            MutationEnforcement?.FailDeathMutation(detail);
        }
        private static void DeathRequire(bool condition, string detail)
        { if (!condition) throw new InvalidOperationException(detail); }

        private sealed class NativeDeathItem(ItemRecord record)
        {
            internal readonly ItemRecord Original = record;
            internal readonly ulong OriginalKey = record.Key;
            internal readonly int Id = record.Id, Quantity = record.Quantity;
            internal int Remaining = record.Quantity;
            internal readonly ItemInstance Native = record.Item;
            internal readonly string Modules = record.Item == null ? null : PlayerInventoryBaseline.CaptureModules(record.Item);
            internal bool Current() => Original.IdentityCurrent() && (Native == null ||
                PlayerInventoryBaseline.JsonEqual(Modules, PlayerInventoryBaseline.CaptureModules(Native)));
        }
        private sealed class NativeDeathTransfer(ulong transaction, ulong life, InventoryDeathBag bag,
            NativeDeathItem[] items, bool restoring, EquipmentState equipment, ulong last)
        {
            internal readonly ulong Transaction = transaction, Life = life, Last = last;
            internal readonly InventoryDeathBag Bag = bag;
            internal readonly NativeDeathItem[] Items = items;
            internal readonly bool Restoring = restoring;
            internal readonly EquipmentState Equipment = equipment;
            internal InventoryDeathState State = InventoryDeathState.Reserved;
            internal int Completed;
            internal bool Awaiting, BagTaken, BagDebitAttempted;
            internal ItemRecord Restored;
            internal ItemInstance PlainGrant;
            internal PlayerInventoryFrame Command;
        }
    }
}
