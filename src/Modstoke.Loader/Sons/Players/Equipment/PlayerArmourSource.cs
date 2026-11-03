// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Sons.Combat;
using global::Sons.Wearable;
using global::Sons.Wearable.Armour;
using global::Sons.Wearable.Clothing;
using TheForest.Utils;
using UnityEngine;
using Armour = Modstoke.Sons.PlayerArmourAuthority;
using Command = Modstoke.Sons.PlayerArmourRoute.Command;
using Environment = System.Environment;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerArmourSource : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventoryEnforcement _inventory;
        private readonly Armour.Snapshot _baseline;
        private readonly StrictCombatArmour _original;
        private readonly PlayerStats _stats;
        private readonly PlayerClothingSystem _clothing;
        private readonly GameObject _root;
        private readonly bool[] _broken;
        private readonly Il2CppObjectBase[] _initialInstances;
        private readonly Il2CppSystem.Action<ArmourPiece, WearableSlots> _brokenObserver;
        private Armour.State _state;
        private Command _command;
        private bool _attempted, _effectsApplied, _effectsConfirmed, _executing, _observerAdded;
        private int _change = -1, _nativeCallbacks, _equipmentCallbacks;
        internal ulong Revision { get; private set; }
        internal ulong LastTransaction { get; private set; }
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }
        internal bool HasPendingCustody => _command != null;
        internal bool EffectsApplied => _command != null && _effectsConfirmed && FailureReason == null && !IsDisposed;
        private PlayerArmourSystem System => _original.System;

        private PlayerArmourSource(PlayerBridge bridge,
            PlayerInventoryEnforcement inventory, Armour.Snapshot baseline, PlayerStats stats, StrictCombatArmour original)
        {
            _bridge = bridge; _inventory = inventory; _baseline = baseline; _stats = stats; _original = original;
            _clothing = original.System._clothingSystem; _root = LocalPlayer.GameObject;
            _state = baseline.Armour; _broken = new bool[_state.SlotCount];
            _initialInstances = new Il2CppObjectBase[_state.SlotCount];
            for (int i = 0; i < _initialInstances.Length; i++) _initialInstances[i] = original.GetRetainedSlotData(i).ArmourInstance;
            _brokenObserver = (Action<ArmourPiece, WearableSlots>)ObserveNativeBreak;
        }

        internal static bool TryPrepare(PlayerBridge bridge, PlayerInventoryEnforcement inventory,
            Armour.Snapshot baseline, out PlayerArmourSource source, out string detail)
        {
            source = null; detail = "Armour source requires installed gates and the exact retained inactive source graph.";
            if (!_installed || Environment.CurrentManagedThreadId != _hookThread || bridge == null || bridge.IsServer
                || bridge.IsDisposed || bridge.IsGameplayActive || baseline?.Plan != bridge.Plan || inventory == null
                || !inventory.TryReadStatProjectionActor(bridge, out var stats)) return false;
            try
            {
                if (!baseline.TryRetainSourceGraph(stats, inventory.Baseline, out var original)) return false;
                foreach (var old in Owners) if (Same(old.System, original.System)) return false;
                var candidate = new PlayerArmourSource(bridge, inventory, baseline, stats, original);
                if (!candidate.NativeMatches(candidate._state)) return false;
                if (!inventory.TryBindArmourSource(candidate, bridge)) return false;
                // Retain before the delegate write. Failure never forgets a possibly installed observer/guard.
                Owners.Add(candidate); source = candidate;
                bridge.Retired += candidate.Dispose;
                candidate._observerAdded = true;
                SystemSubscribe(candidate);
                detail = "Original armour graph and source account prepared; no native damage or gameplay activation.";
                return true;
            }
            catch (Exception error) { source?.Fail(error.Message); detail = error.Message; return false; }
        }

        private static void SystemSubscribe(PlayerArmourSource owner)
        {
            var current = owner.System.OnArmourBroken;
            owner.System.OnArmourBroken = current == null ? owner._brokenObserver
                : Il2CppSystem.Delegate.Combine(current, owner._brokenObserver).Cast<Il2CppSystem.Action<ArmourPiece, WearableSlots>>();
        }

        internal bool Current()
            => Environment.CurrentManagedThreadId == _thread && !IsDisposed && FailureReason == null
                && _load == SaveLifecycle.State.BeforeLoadCount && _bridge.Peer.IsCurrent
                && !_bridge.IsDisposed && _bridge.TryReadCurrentTarget(out var target) && Same(target, LocalPlayer.Entity)
                && !target.isOwner && Same(_root, LocalPlayer.GameObject) && Live(_root) && Live(_stats)
                && Same(_stats, LocalPlayer.Stats) && Same(_stats._armourSystem, System) && Live(System)
                && Same(_clothing, LocalPlayer.ClothingSystem) && Same(System._clothingSystem, _clothing) && Live(_clothing)
                && _stats.transform.IsChildOf(_root.transform) && System.transform.IsChildOf(_root.transform)
                && _clothing.transform.IsChildOf(_root.transform)
                && _inventory.ArmourSourceCurrent(this);

        internal bool TryBegin(Command command, out string detail)
        {
            detail = FailureReason ?? "Armour awaits settled source inventory.";
            if (!Current() || !_bridge.IsGameplayActive || _executing || _command != null || Time.inFixedTimeStep) return false;
            try
            {
                if (command.Plan != _bridge.Plan || command.Actor != _bridge.NetworkId
                    || command.ArmourFingerprint != _baseline.Fingerprint || command.InventoryFingerprint != _baseline.InventoryFingerprint
                    || command.Numbers.BeforeRevision != Revision || command.Numbers.Transaction <= LastTransaction
                    || !Command.SameState(command.Numbers.Before, _state))
                    throw new InvalidOperationException("Source armour plan/actor/revision/baseline or exact before-state disagrees.");
                if (!NativeMatches(_state)) throw new InvalidOperationException("Retained source armour changed before execution.");
                if (!_inventory.TryReserveArmourSource(this, command)) return false;
                _command = command;
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                    throw new InvalidOperationException("Accepted armour could not yield the source body hold: " + detail);
                if (!Current() || !NativeMatches(_state)) throw new InvalidOperationException("Armour changed while yielding the input hold.");
                // Consume before the first native/direct-field effect. No later call can replay this attempt.
                _attempted = true; _executing = true;
                var slots = _state.CopySlots(); int cursor = _state.NextAvailableOrderIndex;
                for (int i = 0; i < command.Numbers.ChangeCount; i++)
                {
                    var change = command.Numbers.GetChange(i);
                    _change = i;
                    if (!Current() || !NativeMatches(new Armour.State(slots, _state.CopyOrder(), cursor,
                        _state.WearingGoldenClothing, _state.GoldenGeneralMultiplier, _state.GoldenDemonicMultiplier)))
                        throw new InvalidOperationException("Armour changed between ordered native effects.");
                    var data = _original.GetRetainedSlotData(change.Before.Index);
                    if (change.Broken)
                    {
                        _nativeCallbacks = _equipmentCallbacks = 0;
                        BeginNativeBreak();
                        bool result = System.TryUnequipSlot((WearableSlots)change.Before.Index, true);
                        if (!result || !_breakCompleted || _nativeCallbacks != 1 || _equipmentCallbacks != 1 || FailureReason != null)
                            throw new InvalidOperationException("Native armour break/callback/clothing/tech completion is unconfirmed.");
                        _broken[change.Before.Index] = true;
                        cursor = change.OrderIndex;
                        System._nextAvailableArmourSlotIndex = cursor;
                    }
                    else data.RemainingArmourpoints = change.After.RemainingPoints;
                    slots[change.Before.Index] = change.After;
                }
                _change = -1;
                if (!Current() || !NativeMatches(command.Numbers.After))
                    throw new InvalidOperationException("Native armour slot identities, conditions or cursor did not match the exact command.");
                _inventory.ApplyArmourSourceRecords(this, command);
                _state = command.Numbers.After;
                _effectsApplied = true;
                detail = "Native armour executed once; canonical server publication and source snapshot reconciliation remain pending.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
            finally { _executing = false; _change = -1; _breakPermit = false; }
        }

        internal bool TryConfirmEffects(Command command, out string detail)
        {
            detail = FailureReason ?? "Waiting for the native armour instance destruction to finish.";
            if (!Current() || !ReferenceEquals(command, _command) || !_attempted || !_effectsApplied || _executing) return false;
            try
            {
                if (!NativeMatches(_state)) throw new InvalidOperationException("Native armour changed after the one execution attempt.");
                for (int i = 0; i < command.Numbers.ChangeCount; i++)
                {
                    var change = command.Numbers.GetChange(i);
                    if (change.Broken && Live(_initialInstances[change.Before.Index] as UnityEngine.Object)) return false;
                }
                _effectsConfirmed = true;
                detail = "Native condition/break/cursor effects and deferred destruction confirmed; no native work was replayed.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal bool TryComplete(Command command, out string detail)
        {
            detail = FailureReason ?? "Waiting for exact server armour publication.";
            if (!Current() || !ReferenceEquals(command, _command) || !_attempted || !_effectsConfirmed || _executing) return false;
            try
            {
                if (!NativeMatches(_state)) throw new InvalidOperationException("Applied source armour changed while awaiting publication.");
                if (!_inventory.TryCompleteArmourSource(this, command)) return false;
                Revision = command.Numbers.BeforeRevision + 1; LastTransaction = command.Numbers.Transaction;
                _command = null; _effectsApplied = _effectsConfirmed = false;
                detail = "Exact source armour transaction settled without replay.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        // Parent calls this at Equipment.Enqueue, BEFORE it assigns another equipment
        // sequence. The exact native callback is an observation of this command, not a grant.
        internal static bool TryObserveEquipment(PlayerBridge bridge, in PlayerEquipmentChange change)
        {
            foreach (var owner in Owners)
            {
                if (!ReferenceEquals(owner._bridge, bridge) || !owner._executing || owner._change < 0) continue;
                var expected = owner._command.Numbers.GetChange(owner._change);
                if (change.Kind != PlayerEquipmentKind.ArmourBroken) return false;
                if (!owner.Current() || !expected.Broken || !owner._breakEntered || !change.IsValid
                    || change.Value != expected.Before.ItemId || change.ToSlot != expected.Before.Index
                    || ++owner._equipmentCallbacks != 1) owner.Fail("Armour equipment notification differs from the active native break.");
                return true;
            }
            return false;
        }

        private void ObserveNativeBreak(ArmourPiece piece, WearableSlots slot)
        {
            try
            {
                if (!_executing || _command == null || _change < 0 || !_breakEntered || !Current())
                    throw new InvalidOperationException("An unowned native armour break callback entered the retained source.");
                var expected = _command.Numbers.GetChange(_change);
                if (!expected.Broken || (int)slot != expected.Before.Index || !Live(piece)
                    || piece._itemId != expected.Before.ItemId || piece.Pointer != _breakPiece.Pointer || ++_nativeCallbacks != 1)
                    throw new InvalidOperationException("Native armour break callback identity changed.");
            }
            catch (Exception error) { Fail(error.Message); }
        }

        private bool NativeMatches(Armour.State expected)
        {
            if (!StrictCombatArmour.TryCapture(System, out var current, out _) || !_original.HasSameLayout(current)
                || current.IsEnabled != _original.IsEnabled || current.ActiveInHierarchy != _original.ActiveInHierarchy
                || current.NextAvailableOrderIndex != expected.NextAvailableOrderIndex
                || !Command.SameFloat(current.GoldenGeneralMultiplier, expected.GoldenGeneralMultiplier)
                || !Command.SameFloat(current.GoldenDemonicMultiplier, expected.GoldenDemonicMultiplier)
                || _clothing.IsWearingItem(572) != expected.WearingGoldenClothing) return false;
            for (int i = 0; i < expected.SlotCount; i++)
            {
                if (!current.TryGetSlot(i, out var actual) || !Same(_original.GetRetainedSlotData(i), current.GetRetainedSlotData(i))) return false;
                var slot = expected.GetSlot(i);
                if (slot.ItemId != actual.ItemId || slot.IsEquipped != actual.IsEquipped
                    || !Command.SameFloat(slot.RemainingPoints, actual.RemainingPoints)) return false;
                if (slot.IsEquipped ? !_original.HasSamePiece(current, i)
                    : _broken[i] && !_original.HasSameBrokenPiece(current, i)) return false;
                // An initially empty slot can retain a deferred-destroy Transform. It must
                // keep that same reference, whether Unity has finished destroying it or not.
                if (!slot.IsEquipped && !_broken[i])
                {
                    var original = _original.GetRetainedSlotData(i);
                    if (original.ArmourPiece != null || (_initialInstances[i] is null ? original.ArmourInstance is not null
                        : !Same(_initialInstances[i], original.ArmourInstance))) return false;
                }
            }
            return true;
        }

        internal void Fail(string detail)
        {
            FailureReason ??= detail;
            _inventory.FailArmourSource(this, FailureReason);
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Armour source belongs to its game thread.");
            if (IsDisposed) return;
            if (_command != null) Fail("Armour source retired with an accepted transaction still retained.");
            IsDisposed = true; _bridge.Retired -= Dispose;
            if (_observerAdded)
            {
                try
                {
                    var current = System.OnArmourBroken;
                    System.OnArmourBroken = current == null ? null
                        : Il2CppSystem.Delegate.Remove(current, _brokenObserver)?.Cast<Il2CppSystem.Action<ArmourPiece, WearableSlots>>();
                    _observerAdded = false;
                }
                catch (Exception error) { FailureReason ??= "Armour observer retirement is unconfirmed: " + error.Message; }
            }
            // Keep the exact native guard/tombstone. Disposal never thaws or reaccepts this actor.
        }

        private static bool Same(Il2CppObjectBase a, Il2CppObjectBase b)
            => a is not null && b is not null && !a.WasCollected && !b.WasCollected && a.Pointer != IntPtr.Zero && a.Pointer == b.Pointer;
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value;
    }
}
