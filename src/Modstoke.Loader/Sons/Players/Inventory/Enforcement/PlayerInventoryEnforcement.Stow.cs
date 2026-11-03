// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Player;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private bool _sourceStowExecuting;
        private PlayerInventoryFrame _sourceStowFrame;
        private bool SourceStowPending => _sourceStowExecuting || _sourceStowAwaitingCommit;
        private bool _sourceStowAwaitingCommit, _sourceStowCommitted;
        private EquipmentTransfer _nativeEquip;
        private PlayerInventoryFrame _sourceEquipFrame;
        private bool _sourceEquipExecuting, _sourceEquipAwaitingCommit, _sourceEquipCommitted;
        private int _sourceEquipStoredIndex = -1;
        private bool SourceEquipPending => _sourceEquipExecuting || _sourceEquipAwaitingCommit;
        private InventoryProps _equipProps;
        private Il2CppSystem.Action<GameObject, InventoryProps.PropDefinition, ItemInstance> _equipInitialized;
        private bool _equipListenerAttempted;

        internal static bool SupportsHeldStow(ItemData data) => data != null && !data.WasCollected && data
            && data._eachInstanceIsUnique && !data._canNotBeStored && !data._alwaysDropOnUnequip
            && data._maxAmount > 0 && data._equippedStatEffect != null && data._equippedStatEffect.Length == 0;

        private bool ReceiveServerStow(in PlayerInventoryFrame frame)
        {
            if (frame.Kind is not (PlayerInventoryMessage.StowApplied or PlayerInventoryMessage.StowUnconfirmed
                or PlayerInventoryMessage.StowSettled)) return false;
            if (IsFaulted || !IsArmed || !_hasPending
                || _pending.Kind is not (PlayerInventoryMessage.StowHeld or PlayerInventoryMessage.StowCommitted)
                || frame != (_pending with { Kind = frame.Kind })) return true;
            if (frame.Kind == PlayerInventoryMessage.StowUnconfirmed)
            { Fail("Native held stow is unconfirmed; no retry or ledger relocation is authorized."); return true; }
            if (frame.Kind == PlayerInventoryMessage.StowApplied)
            {
                if (_pending.Kind == PlayerInventoryMessage.StowHeld)
                {
                    if (!_authority.CompleteEnforcedStow(frame, out var detail)) { Fail(detail); return true; }
                    _pending = frame with { Kind = PlayerInventoryMessage.StowCommitted };
                    Detail = detail;
                }
                Send(_pending);
                return true;
            }
            if (_pending.Kind != PlayerInventoryMessage.StowCommitted) return true;
            if (!_authority.ConfirmEnforcedStow(frame))
            { Fail("Server custody changed before the source confirmed stow publication."); return true; }
            LastCompletedTransaction = frame.Transaction;
            LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, true) { IsEquipmentStow = true };
            _hasPending = false;
            Detail = "Native held stow and server equipment publication agree on both peers.";
            return true;
        }

        private bool ReceiveSourceStow(in PlayerInventoryFrame frame)
        {
            if (frame.Kind == PlayerInventoryMessage.StowCommitted)
            {
                if (_lastReply.Kind == PlayerInventoryMessage.StowSettled
                    && frame == (_lastReply with { Kind = PlayerInventoryMessage.StowCommitted })) Send(_lastReply);
                else if (!IsFaulted && IsArmed && _sourceStowAwaitingCommit
                    && frame == (_lastReply with { Kind = PlayerInventoryMessage.StowCommitted }))
                { _sourceStowCommitted = true; PumpStow(); }
                return true;
            }
            if (frame.Kind != PlayerInventoryMessage.StowHeld) return false;
            if (_lastReply.Kind is PlayerInventoryMessage.StowApplied or PlayerInventoryMessage.StowUnconfirmed
                && frame == (_lastReply with { Kind = PlayerInventoryMessage.StowHeld }))
            { Send(_lastReply); return true; }
            if (SourceStowPending && frame == _sourceStowFrame)
            {
                if (_sourceStowAwaitingCommit) Send(_lastReply);
                return true;
            }
            if (IsFaulted || !IsArmed || _intakePending || _hasIncomingGrant || SourceStowPending || SourceEquipPending || frame.Transaction <= _lastReceived) return true;
            if (InventoryFrozen) return true;
            if (_requestingStow && frame.Key == _nativeStowRequest.Key && frame.ItemId == _nativeStowRequest.ItemId
                && frame.BeforeQuantity == _nativeStowRequest.BeforeQuantity && frame.ModuleFingerprint == _nativeStowRequest.ModuleFingerprint)
            { _requestingStow = false; _nativeStowReserved = true; }
            _lastReceived = frame.Transaction;
            _sourceStowFrame = frame;
            _lastReply = frame with { Kind = PlayerInventoryMessage.StowUnconfirmed };
            _sourceStowExecuting = true;
            _sourceStowCommitted = false;
            _started = Stopwatch.GetTimestamp();
            if (ApplyStow(frame))
            {
                PumpStow();
                return true;
            }
            else Fail("Native held stow could not be reconciled with exact custody; writers remain guarded.");
            Send(_lastReply);
            return true;
        }

        private void PumpStow()
        {
            if (!SourceStowPending || _nativeEquip == null) return;
            if (_sourceStowExecuting)
            {
                var stage = _nativeEquip.Readback(out var detail);
                if (stage == EquipmentTransfer.Stage.Unconfirmed)
                { Fail(detail); Send(_lastReply); return; }
                if (stage != EquipmentTransfer.Stage.ReadBack) return;
                ulong key = _sourceStowFrame.Key;
                int index = _sourceItems.FindIndex(item => item.Entry.Key == key);
                if (index < 0) { Fail("Reserved stow instance is no longer tracked."); Send(_lastReply); return; }
                var selected = _sourceItems[index];
                var after = _currentSnapshot.CaptureAfterHeldStow(_sourceStowFrame.BeforeQuantity);
                int storedIndex = FindStoredEntry(after, selected.Entry.ItemId, selected.Instance);
                IntPtr bucket = _stowCreatedBucket ? _manager._itemsMap[selected.Entry.ItemId].Pointer : IntPtr.Zero;
                if (!PlayerInventoryBaseline.MatchesInstance(selected.Entry, selected.Instance)
                    || !_currentSnapshot.ConfirmsHeldStow(after, _sourceStowFrame.BeforeQuantity, storedIndex, selected.Instance, bucket))
                { Fail("Native stow readback changed exact inventory/module custody."); Send(_lastReply); return; }
                _sourceItems[index] = new SourceItem(new PlayerInventoryBaseline.Entry
                {
                    Key = selected.Entry.Key, ItemId = selected.Entry.ItemId, Quantity = 1,
                    Modules = selected.Entry.Modules, ModuleState = selected.Entry.ModuleState
                }, selected.Instance);
                _currentSnapshot = after;
                _sourceStowAwaitingCommit = true;
                _sourceStowExecuting = false;
                _started = Stopwatch.GetTimestamp();
                _lastReply = _sourceStowFrame with { Kind = PlayerInventoryMessage.StowApplied };
                Send(_lastReply);
            }
            if (!_sourceStowAwaitingCommit || !_sourceStowCommitted || !_currentSnapshot.ReplicatedEquipmentMatches()) return;
            if (!Unchanged()) { Fail("Native custody changed while stow publication was pending."); return; }
            var committed = _sourceStowFrame with { Kind = PlayerInventoryMessage.StowCommitted };
            if (!_nativeEquip.TryApplyAuthoritativeResult(in committed, out var publication))
            {
                if (_nativeEquip.State == EquipmentTransfer.Stage.ReadBack) return;
                Fail(publication); Send(_lastReply); return;
            }
            _sourceStowAwaitingCommit = false;
            _nativeStowReserved = false;
            _nativeEquip = null;
            _lastReply = _lastReply with { Kind = PlayerInventoryMessage.StowSettled };
            LastCompletedTransaction = _lastReply.Transaction;
            LastReceipt = new(_lastReply.Transaction, _lastReply.Key, _lastReply.ItemId, 1, false, true) { IsEquipmentStow = true };
            RefreshEquipmentIntent();
            Detail = "Native stow and authenticated server equipment publication agree.";
            Send(_lastReply);
        }

        private bool _stowCreatedBucket;

        private bool ApplyStow(in PlayerInventoryFrame frame)
        {
            if (InventoryFrozen || !Unchanged() || !NativeHandSlot(frame.BeforeQuantity)) return false;
            ulong key = frame.Key;
            int sourceIndex = _sourceItems.FindIndex(item => item.Entry.Key == key);
            if (sourceIndex < 0) return false;
            var selected = _sourceItems[sourceIndex];
            var native = selected.Instance;
            if (selected.Quantity != 1 || selected.Entry.ItemId != frame.ItemId
                || selected.Entry.EquipmentSlot != frame.BeforeQuantity || native == null || native.WasCollected
                || _inventory._equipmentSlots[frame.BeforeQuantity]?.Pointer != native.Pointer
                || !SupportsHeldStow(native.Data) || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, native)
                || PlayerInventoryWire.HashModules(selected.Entry.Modules) != frame.ModuleFingerprint) return false;
            _stowCreatedBucket = !_manager._itemsMap.ContainsKey(frame.ItemId);
            return EquipmentTransfer.TryCapture(_bridge, _inventory, native, frame.Key,
                    selected.Entry.Modules, out _nativeEquip, out _)
                && _nativeEquip.TryAdmit(EquipmentTransfer.Operation.StowHeld, out _)
                && _nativeEquip.TryReserve(in frame, _baseline.Fingerprint, NativeEquipReservationCurrent, out _)
                && _nativeEquip.TryApply(out _);
        }

        private static bool NativeHandSlot(int slot)
            => slot == (int)EquipmentSlot.RightHand || slot == (int)EquipmentSlot.LeftHand;

        private bool TryReadNativeEquipSlot(ItemInstance item, int requestedSlot, out int slot)
        {
            slot = -1;
            if (item == null || item.WasCollected || !SupportsHeldStow(item.Data)) return false;
            slot = (int)item.Data._equipmentSlot;
            if (!NativeHandSlot(slot) || (requestedSlot >= 0 && requestedSlot != slot)
                || _inventory._equipmentSlots[slot] != null) return false;
            var other = _inventory._equipmentSlots[(int)(slot == (int)EquipmentSlot.RightHand ? EquipmentSlot.LeftHand : EquipmentSlot.RightHand)];
            if (other != null && (other.WasCollected || other.Data == null || other.Data.WasCollected
                || item.Data._requiresBothHands || item.Data._firstLookRequiresBothHands || other.Data._requiresBothHands)) return false;
            return true;
        }

        private bool ReceiveServerEquip(in PlayerInventoryFrame frame)
        {
            if (frame.Kind is not (PlayerInventoryMessage.EquipApplied or PlayerInventoryMessage.EquipUnconfirmed
                or PlayerInventoryMessage.EquipRejected or PlayerInventoryMessage.EquipSettled)) return false;
            if (IsFaulted || !IsArmed || !_hasPending
                || _pending.Kind is not (PlayerInventoryMessage.EquipHeld or PlayerInventoryMessage.EquipCommitted)
                || frame != (_pending with { Kind = frame.Kind })) return true;
            if (frame.Kind == PlayerInventoryMessage.EquipUnconfirmed)
            { Fail("Native equip custody is uncertain; no replay, replacement or ledger commit is authorized."); return true; }
            if (frame.Kind == PlayerInventoryMessage.EquipRejected)
            {
                if (_pending.Kind != PlayerInventoryMessage.EquipHeld || !_authority.RejectEnforcedHeldEquip(frame, out var detail))
                { Fail("Native equip rejection no longer matches a wholly unwritten server reservation."); return true; }
                _hasPending = false;
                LastCompletedTransaction = frame.Transaction;
                LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, false) { IsEquipmentEquip = true };
                Detail = detail;
                return true;
            }
            if (frame.Kind == PlayerInventoryMessage.EquipApplied)
            {
                if (_pending.Kind == PlayerInventoryMessage.EquipHeld)
                {
                    if (!_authority.CompleteEnforcedHeldEquip(frame, out var detail)) { Fail(detail); return true; }
                    _pending = frame with { Kind = PlayerInventoryMessage.EquipCommitted };
                    Detail = detail;
                }
                Send(_pending);
                return true;
            }
            if (_pending.Kind != PlayerInventoryMessage.EquipCommitted) return true;
            if (!_authority.ConfirmEnforcedHeldEquip(frame))
            { Fail("Server equip custody changed before source publication readback."); return true; }
            LastCompletedTransaction = frame.Transaction;
            LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, true) { IsEquipmentEquip = true };
            _hasPending = false;
            Detail = "Exact native equip, server custody and replicated equipment publication agree.";
            return true;
        }

        private bool ReceiveSourceEquip(in PlayerInventoryFrame frame)
        {
            if (frame.Kind == PlayerInventoryMessage.EquipCommitted)
            {
                if (_lastReply.Kind == PlayerInventoryMessage.EquipSettled
                    && frame == (_lastReply with { Kind = PlayerInventoryMessage.EquipCommitted })) Send(_lastReply);
                else if (!IsFaulted && IsArmed && _sourceEquipAwaitingCommit
                    && frame == (_sourceEquipFrame with { Kind = PlayerInventoryMessage.EquipCommitted }))
                { _sourceEquipCommitted = true; PumpEquip(); }
                return true;
            }
            if (frame.Kind != PlayerInventoryMessage.EquipHeld) return false;
            if (SourceEquipPending && frame == _sourceEquipFrame)
            {
                if (_sourceEquipAwaitingCommit) Send(_lastReply);
                return true;
            }
            if (_lastReply.Kind is PlayerInventoryMessage.EquipApplied or PlayerInventoryMessage.EquipRejected
                or PlayerInventoryMessage.EquipUnconfirmed or PlayerInventoryMessage.EquipSettled
                && frame == (_lastReply with { Kind = PlayerInventoryMessage.EquipHeld }))
            { Send(_lastReply); return true; }
            if (IsFaulted || !IsArmed || _intakePending || _hasIncomingGrant || SourceStowPending || SourceEquipPending
                || frame.Transaction <= _lastReceived || InventoryFrozen) return true;
            if (_requestingEquip && frame.Key == _nativeEquipRequest.Key && frame.ItemId == _nativeEquipRequest.ItemId
                && frame.BeforeQuantity == _nativeEquipRequest.BeforeQuantity
                && frame.ModuleFingerprint == _nativeEquipRequest.ModuleFingerprint
                && frame.EquipmentOptions == _nativeEquipRequest.EquipmentOptions) _requestingEquip = false;
            _lastReceived = frame.Transaction;
            _sourceEquipFrame = frame;
            _lastReply = frame with { Kind = PlayerInventoryMessage.EquipUnconfirmed };
            _nativeEquipReserved = true;
            _sourceEquipExecuting = true;
            _started = Stopwatch.GetTimestamp();
            if (StartEquip(frame, out bool untouched, out var detail)) return true;
            if (untouched)
            {
                _nativeEquip = null;
                _sourceEquipExecuting = _nativeEquipReserved = false;
                _lastReply = frame with { Kind = PlayerInventoryMessage.EquipRejected };
                Detail = detail;
                LastCompletedTransaction = frame.Transaction;
                LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, 1, false, false) { IsEquipmentEquip = true };
                RefreshEquipmentIntent();
            }
            else Fail(detail);
            Send(_lastReply);
            return true;
        }

        private bool NativeEquipReservationCurrent()
            => OnThread && !IsDisposed && !IsFaulted && IsArmed && (SourceEquipPending || SourceStowPending) && !InventoryFrozen
                && _bridge?.IsGameplayActive == true && ReferenceEquals(_guard, this) && _nativeEquip != null
                && _nativeEquip.Key == (SourceStowPending ? _sourceStowFrame.Key : _sourceEquipFrame.Key)
                && _nativeEquip.Transaction == (SourceStowPending ? _sourceStowFrame.Transaction : _sourceEquipFrame.Transaction)
                && Current();

        private void RetireNativeEquipment()
        {
            if (_nativeEquip == null || _writerDepth != 0) return;
            _nativeEquip.TryCancelBeforeApply();
            _nativeEquip.Readback(out _);
            if (_nativeEquip.TryDetachCallbacks(out _)) _nativeEquip = null;
        }

        private bool StartEquip(in PlayerInventoryFrame frame, out bool untouched, out string detail)
        {
            untouched = false;
            detail = "Native equip preflight did not match the retained source.";
            try
            {
                if (!Unchanged()) return false;
                SourceItem selected = null;
                foreach (var item in _sourceItems) if (item.Entry.Key == frame.Key) { selected = item; break; }
                if (selected == null || selected.Quantity != 1 || !Stowed(selected.Entry)
                    || selected.Entry.ItemId != frame.ItemId || selected.Instance == null || selected.Instance.WasCollected
                    || !TryReadNativeEquipSlot(selected.Instance, frame.BeforeQuantity, out _)
                    || frame.EquipmentOptions != 0 || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, selected.Instance)
                    || PlayerInventoryWire.HashModules(selected.Entry.Modules) != frame.ModuleFingerprint)
                { untouched = true; return false; }
                _sourceEquipStoredIndex = FindStoredEntry(_currentSnapshot, selected.Entry.ItemId, selected.Instance);
                if (_sourceEquipStoredIndex < 0) { untouched = true; return false; }
                if (!EquipmentTransfer.TryCapture(_bridge, _inventory, selected.Instance,
                    frame.Key, selected.Entry.Modules, out _nativeEquip, out detail)
                    || !_nativeEquip.TryAdmit(EquipmentTransfer.Operation.EquipStored, out detail)
                    || !RetainEquipListener()
                    || !_nativeEquip.TryReserve(in frame, _baseline.Fingerprint, NativeEquipReservationCurrent, out detail))
                {
                    untouched = (_nativeEquip == null || _nativeEquip.TryCancelBeforeApply()) && Unchanged();
                    return false;
                }
                if (_nativeEquip.TryApply(out detail)) return true;
                untouched = _nativeEquip.TryCancelBeforeApply() && Unchanged();
                return false;
            }
            catch (Exception error) { detail = "Native equip dispatch is unconfirmed: " + error.Message; return false; }
        }

        private void PumpEquip()
        {
            if (!SourceEquipPending || _nativeEquip == null) return;
            if (_sourceEquipExecuting)
            {
                var stage = _nativeEquip.Readback(out var detail);
                if (stage == EquipmentTransfer.Stage.Unconfirmed)
                { Fail(detail); Send(_lastReply); return; }
                if (stage != EquipmentTransfer.Stage.ReadBack) return;
                ulong key = _sourceEquipFrame.Key;
                int sourceIndex = _sourceItems.FindIndex(item => item.Entry.Key == key);
                if (sourceIndex < 0) { Fail("Reserved equip instance is no longer tracked."); Send(_lastReply); return; }
                var selected = _sourceItems[sourceIndex];
                var after = _currentSnapshot.CaptureAfterHeldEquip(selected.Entry.ItemId);
                if (_sourceEquipStoredIndex < 0 || !_currentSnapshot.ConfirmsHeldEquip(after,
                    _sourceEquipFrame.BeforeQuantity, _sourceEquipStoredIndex, selected.Instance))
                { Fail("Full native inventory readback disagrees with the exact equip relocation."); Send(_lastReply); return; }
                _sourceItems[sourceIndex] = new SourceItem(new PlayerInventoryBaseline.Entry
                {
                    Key = selected.Entry.Key, ItemId = selected.Entry.ItemId, Quantity = 1,
                    Modules = selected.Entry.Modules, ModuleState = selected.Entry.ModuleState, EquipmentSlot = _sourceEquipFrame.BeforeQuantity
                }, selected.Instance);
                _currentSnapshot = after;
                _sourceEquipExecuting = false;
                _sourceEquipAwaitingCommit = true;
                _sourceEquipCommitted = false;
                _started = Stopwatch.GetTimestamp();
                _lastReply = _sourceEquipFrame with { Kind = PlayerInventoryMessage.EquipApplied };
                Send(_lastReply);
            }
            if (!_sourceEquipCommitted || !_currentSnapshot.ReplicatedEquipmentMatches()) return;
            if (!Unchanged())
            { Fail("Native equip changed before server publication could be confirmed."); Send(_lastReply); return; }
            var committed = _sourceEquipFrame with { Kind = PlayerInventoryMessage.EquipCommitted };
            if (!_nativeEquip.TryApplyAuthoritativeResult(in committed, out var publication))
            {
                if (_nativeEquip.State == EquipmentTransfer.Stage.ReadBack) return;
                Fail(publication); Send(_lastReply); return;
            }
            _sourceEquipAwaitingCommit = _sourceEquipCommitted = _nativeEquipReserved = false;
            _nativeEquip = null;
            _lastReply = _sourceEquipFrame with { Kind = PlayerInventoryMessage.EquipSettled };
            LastCompletedTransaction = _sourceEquipFrame.Transaction;
            LastReceipt = new(_sourceEquipFrame.Transaction, _sourceEquipFrame.Key, _sourceEquipFrame.ItemId, 1, false, true)
                { IsEquipmentEquip = true };
            RefreshEquipmentIntent();
            Detail = "Native equip completed after exact coroutine termination, custody readback and server publication.";
            Send(_lastReply);
        }

        private bool RetainEquipListener()
        {
            if (_equipListenerAttempted)
                return _equipProps != null && !_equipProps.WasCollected && _equipProps
                    && _inventory._InventoryProps_k__BackingField?.Pointer == _equipProps.Pointer;
            _equipProps = _inventory._InventoryProps_k__BackingField;
            if (_equipProps == null || _equipProps.WasCollected || !_equipProps) return false;
            _equipInitialized = (Action<GameObject, InventoryProps.PropDefinition, ItemInstance>)ObserveEquipInitialized;
            _equipListenerAttempted = true;
            _equipProps.add_OnHeldItemInitialized(_equipInitialized);
            return true;
        }

        private void ObserveEquipInitialized(GameObject held, InventoryProps.PropDefinition definition, ItemInstance instance)
        {
            if (!OnThread || IsDisposed || IsFaulted || !SourceEquipPending || _nativeEquip == null) return;
            _nativeEquip.ObserveHeldInitialized(_equipProps, held, definition, instance);
        }

        private void ReleaseEquipListener()
        {
            if (!_equipListenerAttempted) return;
            try
            {
                if (_equipProps == null || _equipProps.WasCollected || !_equipProps)
                    throw new InvalidOperationException("Retained inventory props are unavailable during listener release.");
                _equipProps.remove_OnHeldItemInitialized(_equipInitialized);
                _equipListenerAttempted = false;
                _equipInitialized = null;
                _equipProps = null;
            }
            catch (Exception error) { Fail("Equipment listener release is unconfirmed: " + error.Message); }
        }
    }
}
