// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private readonly Dictionary<ulong, DecayClock> _decay = new();
        private readonly HashSet<ulong> _decayInspected = new();
        private PendingPassive _passive;
        private int _decayFrame = -1;
        private ulong _decayCursor;
        // At most one timer-only absolute snapshot per account per realtime second.
        // State/PauseDecay changes bypass this interval, after any pending receipt settles.
        private long _nextTimerProjection = Stopwatch.GetTimestamp() + Stopwatch.Frequency;

        // Simulation copies are not inventory custody. Their native ModuleUpdater listeners
        // are removed on retention; copies and acknowledged ledger modules remain guarded.
        // Only this one call per server frame advances a copy, even while its receipt is in flight.
        internal void PumpPassiveModules()
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !_bridge.IsGameplayActive
                || MutationEnforcement == null || !MutationEnforcement.IsArmed || !Current(out _)) return;
            bool canProject = Ready(out _) && _pending == null && _stow == null && !HasPendingNativePowerSupply
                && MutationEnforcement.CanQueue;
            _busy = true;
            try
            {
                int frame = Time.frameCount;
                if (_decayFrame != frame)
                {
                    _decayFrame = frame;
                    foreach (var item in _items.Values)
                    {
                        if (item.Consumed || item.Item == null) continue;
                        if (!item.IdentityCurrent()) throw new InvalidOperationException("Passive inventory identity changed.");
                        if (_decayInspected.Add(item.Key))
                        {
                            var module = PlayerInventoryEnforcement.FindPerishable(item.Item);
                            if (module == null) continue;
                            var native = PlayerInventoryBaseline.RestoreInstance(new PlayerInventoryBaseline.Entry
                            {
                                ItemId = item.Id, Modules = PlayerInventoryBaseline.CaptureModules(item.Item),
                                ModuleState = PlayerInventoryBaseline.CaptureModuleState(item.Item)
                            });
                            MutationEnforcement.RetainPausedModules(native);
                            var simulation = new ItemRecord(item.Key, native);
                            var timer = PlayerInventoryEnforcement.FindPerishable(native);
                            if (!simulation.CaptureIdentity() || timer == null || !timer._isInstantiated
                                || timer._itemObject != null)
                                throw new InvalidOperationException("Native decay copy is not detached and initialized.");
                            _decay.Add(item.Key, new DecayClock(simulation, timer));
                        }
                        if (!_decay.TryGetValue(item.Key, out var clock)) continue;
                        if (!clock.Item.IdentityCurrent() || clock.Module._itemObject != null)
                            throw new InvalidOperationException("Detached decay copy changed identity or acquired presentation.");
                        PlayerInventoryEnforcement.AdvancePerishable(clock.Module);
                        if (!clock.Item.IdentityCurrent() || !float.IsFinite(clock.Module._remainingSecondsUntilDecay))
                            throw new InvalidOperationException("Native decay produced an unavailable identity or nonfinite timer.");
                    }
                }
                if (!canProject || _passive != null) return;
                DecayClock selected = null;
                ItemRecord selectedItem = null;
                bool selectedUrgent = false;
                long now = Stopwatch.GetTimestamp();
                // Round-robin acknowledgement prevents a frequently changing item from starving
                // the other exact instances. No elapsed game time is dropped during transport.
                foreach (var pair in _decay)
                {
                    if (!_items.TryGetValue(pair.Key, out var item) || item.Consumed) continue;
                    var ledger = PlayerInventoryEnforcement.FindPerishable(item.Item);
                    if (ledger == null) throw new InvalidOperationException("Acknowledged perishable module disappeared.");
                    if (SameDecay(ledger, pair.Value.Module)) continue;
                    bool urgent = ledger._currentState != pair.Value.Module._currentState
                        || ledger._PauseDecay_k__BackingField != pair.Value.Module._PauseDecay_k__BackingField;
                    if (!urgent && (selectedUrgent || now < _nextTimerProjection)) continue;
                    if (selectedItem == null || (urgent && !selectedUrgent) || (item.Key > _decayCursor
                        ? selectedItem.Key <= _decayCursor || item.Key < selectedItem.Key
                        : selectedItem.Key <= _decayCursor && item.Key < selectedItem.Key))
                    { selected = pair.Value; selectedItem = item; selectedUrgent = urgent; }
                }
                if (selected == null) return;
                if (_lastTransaction == ulong.MaxValue) throw new InvalidOperationException("Inventory transaction sequence exhausted.");
                string before = PlayerInventoryBaseline.CaptureModules(selectedItem.Item);
                string after = PlayerInventoryBaseline.CaptureModules(selected.Item.Item);
                int moduleId = selected.Module.GetSaveData().ModuleId, moduleIndex = -1;
                var saved = selected.Item.Item.GetModuleSaveData();
                for (int i = 0; i < saved.Count; i++) if (saved[i].ModuleId == moduleId) moduleIndex = i;
                if (!PlayerInventoryBaseline.OnlyModuleChanged(before, after, moduleIndex))
                    throw new InvalidOperationException("Passive simulation changed more than its perishable save.");
                if (PlayerInventoryBaseline.CaptureModuleState(selectedItem.Item)
                    != PlayerInventoryBaseline.CaptureModuleState(selected.Item.Item))
                    throw new InvalidOperationException("Passive simulation changed supplemental module state.");
                var timerState = selected.Module;
                var command = new PlayerInventoryFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                    AcceptedBaselineFingerprint, _lastTransaction + 1, selectedItem.Key, selectedItem.Id, 1, 1,
                    PlayerInventoryMessage.Perishable, PlayerInventoryWire.HashModules(after), PlayerInventoryWire.HashModules(before),
                    (int)timerState._currentState, timerState._remainingSecondsUntilDecay, timerState._PauseDecay_k__BackingField);
                if (!_bridge.TryReadCurrentTarget(out var target)) throw new InvalidOperationException("Passive player target is unavailable.");
                var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
                if (equipment == null || !OwnedEquipmentMatches(equipment) || !Current(out _))
                    throw new InvalidOperationException("Player custody changed before passive projection.");
                _passive = new PendingPassive(command, selectedItem, before, after, equipment);
                _lastTransaction = command.Transaction;
                _decayCursor = selectedItem.Key;
                _nextTimerProjection = now + Stopwatch.Frequency;
                MutationEnforcement.QueueMutation(command);
            }
            catch (Exception error)
            {
                MutationEnforcement.FailPassive("Server perishable progression is unconfirmed: " + error.Message);
            }
            finally { _busy = false; }
        }

        internal bool CompletePassive(in PlayerInventoryFrame receipt, out string detail)
        {
            detail = "No exact pending passive projection matches this receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || !Current(out var target)
                || _passive == null || receipt.Kind != PlayerInventoryMessage.PerishableApplied
                || receipt != (_passive.Frame with { Kind = PlayerInventoryMessage.PerishableApplied })) return false;
            _busy = true;
            try
            {
                var pending = _passive;
                var item = pending.Item;
                if (item.Consumed || item.Quantity != 1 || !item.IdentityCurrent()
                    || pending.ModuleState != PlayerInventoryBaseline.CaptureModuleState(item.Item)
                    || !pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))
                    || !PlayerInventoryBaseline.JsonEqual(pending.Before,
                        PlayerInventoryBaseline.CaptureModules(item.Item))) return false;
                var module = PlayerInventoryEnforcement.FindPerishable(item.Item);
                PlayerInventoryEnforcement.ProjectPerishable(module, receipt);
                if (!item.IdentityCurrent() || !Current(out _)
                    || pending.ModuleState != PlayerInventoryBaseline.CaptureModuleState(item.Item)
                    || !PlayerInventoryBaseline.JsonEqual(pending.After,
                        PlayerInventoryBaseline.CaptureModules(item.Item))) return false;
                _passive = null;
                Detail = detail = "Server perishable state and exact retained native module readback agree.";
                return true;
            }
            catch (Exception error) { detail = "Passive projection reconciliation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        private static bool SameDecay(PerishableItemInstanceModule left, PerishableItemInstanceModule right)
            => left._currentState == right._currentState && left._remainingSecondsUntilDecay == right._remainingSecondsUntilDecay
                && left._PauseDecay_k__BackingField == right._PauseDecay_k__BackingField;

        private void ReleaseConsumedModules(ItemRecord item)
        {
            if (!item.Consumed) return;
            if (_decay.TryGetValue(item.Key, out var clock))
            {
                MutationEnforcement.ReleaseDetachedModules(clock.Item.Item);
                _decay.Remove(item.Key);
            }
            MutationEnforcement.ReleaseDetachedModules(item.Item);
            _decayInspected.Remove(item.Key);
            // Keep the bounded public ledger tombstone and transaction high-water mark.
            // Consumed clocks and their suppression entries no longer remain rooted here.
        }

        private sealed class DecayClock(ItemRecord item, PerishableItemInstanceModule module)
        {
            internal readonly ItemRecord Item = item;
            internal readonly PerishableItemInstanceModule Module = module;
        }

        private sealed class PendingPassive(PlayerInventoryFrame frame, ItemRecord item, string before, string after, EquipmentState equipment)
        {
            internal readonly PlayerInventoryFrame Frame = frame;
            internal readonly ItemRecord Item = item;
            internal readonly string Before = before, After = after;
            internal readonly PlayerInventoryModuleState ModuleState = PlayerInventoryBaseline.CaptureModuleState(item.Item);
            internal readonly EquipmentState Equipment = equipment;
        }
    }
}
