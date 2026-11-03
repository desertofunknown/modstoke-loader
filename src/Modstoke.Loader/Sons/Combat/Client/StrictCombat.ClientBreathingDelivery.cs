// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private readonly BreathingReceiptAssembly _pendingBreathingReceipt = new();
        private readonly BreathingReceiptAssembly _finalBreathingReceipt = new();
        private StrictCombatOwnedBreathingState _acceptedBreathing;
        private StrictCombatBinding _acceptedBreathingBinding;
        private ulong _acceptedBreathingStateRevision, _acceptedBreathingLife;

        /// <summary>Reads server-owned lung air correlated with the accepted combat state.</summary>
        public bool TryReadOwnedBreathing(out StrictCombatBinding binding, out StrictCombatSnapshot state,
            out StrictCombatOwnedBreathingState breathing)
        {
            breathing = null;
            if (!TryRead(out binding, out state) || !_requiresBreathingReceipts || _acceptedBreathing == null
                || !binding.SameBinding(in _acceptedBreathingBinding)
                || state.Revision != _acceptedBreathingStateRevision || state.LifeGeneration != _acceptedBreathingLife)
                return false;
            breathing = _acceptedBreathing;
            return true;
        }

        private bool TryReceiveBreathingReceipt(ReadOnlySpan<byte> payload, out StrictCombatReceipt receipt,
            out StrictCombatOwnedArmourState armour, out StrictCombatOwnedRebreatherState rebreather,
            out StrictCombatOwnedBreathingState breathing, out bool ready)
        {
            receipt = default;
            armour = null;
            rebreather = null;
            breathing = null;
            ready = false;
            if (!_state.HasBinding || !_state.HasPendingRequest) return false;
            StrictCombatOwnedArmourState receivedArmour = null;
            StrictCombatOwnedRebreatherState receivedRebreather = null;
            if (!StrictCombatBreathingProtocol.TryReadReceipt(payload, out var received, out var receivedBreathing))
            {
                if (_requiresRebreatherReceipts)
                {
                    if (!TryReceiveRebreatherReceipt(payload, out received, out receivedArmour,
                        out receivedRebreather, out bool resourcesReady)) return false;
                    if (!resourcesReady) return true;
                }
                else if (_requiresArmourReceipts)
                {
                    if (!TryReceiveArmourPage(payload, out received, out receivedArmour, out bool armourReady))
                        return false;
                    if (!armourReady) return true;
                }
                else return false;
            }
            var pending = _state.PendingIntent;
            if (received.Epoch != _state.Binding.Epoch || received.Sequence != pending.Sequence
                || received.Action != pending.Action || received.RequestLifeGeneration != pending.LifeGeneration)
                return false;
            var assembly = received.Result == StrictCombatResult.ShotPending
                ? _pendingBreathingReceipt : _finalBreathingReceipt;
            if (!assembly.TryAdd(in received, receivedBreathing, receivedRebreather, receivedArmour,
                _requiresRebreatherReceipts, _requiresArmourReceipts, out breathing, out rebreather,
                out armour, out ready))
            { Retire(); return false; }
            if (ready) receipt = received;
            return true;
        }

        private bool CanAcceptOwnedBreathing(in StrictCombatReceipt receipt, StrictCombatOwnedBreathingState value)
        {
            if (value == null) return false;
            var state = receipt.State;
            // A retained receipt can complete its request without replacing newer health or air.
            if (_state.HasSnapshot && state.Revision < _state.Snapshot.Revision) return true;
            var previous = _acceptedBreathing;
            if (previous == null || _acceptedBreathingLife != state.LifeGeneration || !previous.IsProvisioned)
                return true;
            if (!value.IsProvisioned || value.Revision < previous.Revision) return false;
            if (value.Revision == previous.Revision) return previous.SameContents(value);
            return BitConverter.DoubleToInt64Bits(value.Capacity) == BitConverter.DoubleToInt64Bits(previous.Capacity);
        }

        private void AcceptOwnedBreathing(in StrictCombatReceipt receipt, StrictCombatOwnedBreathingState value)
        {
            if (!_state.HasSnapshot || _state.Snapshot.Revision != receipt.State.Revision
                || _state.Snapshot.LifeGeneration != receipt.State.LifeGeneration) return;
            _acceptedBreathing = value;
            _acceptedBreathingBinding = _state.Binding;
            _acceptedBreathingStateRevision = receipt.State.Revision;
            _acceptedBreathingLife = receipt.State.LifeGeneration;
        }

        private void ResetBreathingFragments()
        {
            _pendingBreathingReceipt.Clear();
            _finalBreathingReceipt.Clear();
        }

        private void ResetOwnedBreathing()
        {
            ResetBreathingFragments();
            _acceptedBreathing = null;
            _acceptedBreathingBinding = default;
            _acceptedBreathingStateRevision = 0;
            _acceptedBreathingLife = 0;
        }

        // The inner assembly retains armour/tank fragments; this outer assembly waits for
        // lung air before any negotiated resource or its health receipt is accepted.
        private sealed class BreathingReceiptAssembly
        {
            private StrictCombatReceipt _receipt;
            private StrictCombatOwnedBreathingState _breathing;
            private StrictCombatOwnedRebreatherState _rebreather;
            private StrictCombatOwnedArmourState _armour;
            private bool _hasReceipt, _complete;

            internal bool TryAdd(in StrictCombatReceipt receipt, StrictCombatOwnedBreathingState breathing,
                StrictCombatOwnedRebreatherState rebreather, StrictCombatOwnedArmourState armour,
                bool requiresRebreather, bool requiresArmour, out StrictCombatOwnedBreathingState result,
                out StrictCombatOwnedRebreatherState resultRebreather, out StrictCombatOwnedArmourState resultArmour,
                out bool ready)
            {
                result = null;
                resultRebreather = null;
                resultArmour = null;
                ready = false;
                if ((_hasReceipt && !StrictCombatArmourProtocol.SameReceipt(in _receipt, in receipt))
                    || (_breathing != null && breathing != null && !_breathing.SameContents(breathing))
                    || (_rebreather != null && rebreather != null && !_rebreather.SameContents(rebreather))
                    || (_armour != null && armour != null && !_armour.SameContents(armour))) return false;
                if (_complete) return true;
                _receipt = receipt;
                _hasReceipt = true;
                if (breathing != null) _breathing = breathing;
                if (rebreather != null) _rebreather = rebreather;
                if (armour != null) _armour = armour;
                if (_breathing == null || (requiresRebreather && _rebreather == null)
                    || (requiresArmour && _armour == null)) return true;
                _complete = true;
                result = _breathing;
                resultRebreather = _rebreather;
                resultArmour = _armour;
                ready = true;
                return true;
            }

            internal void Clear()
            {
                _receipt = default;
                _breathing = null;
                _rebreather = null;
                _armour = null;
                _hasReceipt = false;
                _complete = false;
            }
        }
    }
}
