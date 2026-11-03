// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private readonly RebreatherReceiptAssembly _pendingRebreatherReceipt = new();
        private readonly RebreatherReceiptAssembly _finalRebreatherReceipt = new();
        private StrictCombatOwnedRebreatherState _acceptedRebreather;
        private StrictCombatBinding _acceptedRebreatherBinding;
        private ulong _acceptedRebreatherStateRevision, _acceptedRebreatherLife;

        /// <summary>Reads server-owned tank resources correlated with the accepted combat state.</summary>
        public bool TryReadOwnedRebreather(out StrictCombatBinding binding, out StrictCombatSnapshot state,
            out StrictCombatOwnedRebreatherState rebreather)
        {
            rebreather = null;
            if (!TryRead(out binding, out state) || !_requiresRebreatherReceipts || _acceptedRebreather == null
                || !binding.SameBinding(in _acceptedRebreatherBinding)
                || state.Revision != _acceptedRebreatherStateRevision || state.LifeGeneration != _acceptedRebreatherLife)
                return false;
            rebreather = _acceptedRebreather;
            return true;
        }

        private bool TryReceiveRebreatherReceipt(ReadOnlySpan<byte> payload, out StrictCombatReceipt receipt,
            out StrictCombatOwnedArmourState armour, out StrictCombatOwnedRebreatherState rebreather,
            out bool ready)
        {
            receipt = default;
            armour = null;
            rebreather = null;
            ready = false;
            if (!_state.HasBinding || !_state.HasPendingRequest) return false;
            StrictCombatReceipt received;
            StrictCombatOwnedArmourState receivedArmour = null;
            StrictCombatOwnedRebreatherState receivedRebreather;
            bool isRebreather = _requiresRebreatherWearReceipts
                ? StrictCombatRebreatherProtocol.TryReadWearReceipt(payload, out received, out receivedRebreather)
                : StrictCombatRebreatherProtocol.TryReadReceipt(payload, out received, out receivedRebreather);
            if (!isRebreather)
            {
                if (!_requiresArmourReceipts
                    || !TryReceiveArmourPage(payload, out received, out receivedArmour, out bool armourReady))
                    return false;
                if (!armourReady) return true;
            }
            var pending = _state.PendingIntent;
            if (received.Epoch != _state.Binding.Epoch || received.Sequence != pending.Sequence
                || received.Action != pending.Action || received.RequestLifeGeneration != pending.LifeGeneration)
                return false;
            var assembly = received.Result == StrictCombatResult.ShotPending
                ? _pendingRebreatherReceipt : _finalRebreatherReceipt;
            if (!assembly.TryAdd(in received, receivedRebreather, receivedArmour, _requiresArmourReceipts,
                out rebreather, out armour, out ready))
            { Retire(); return false; }
            if (ready) receipt = received;
            return true;
        }

        private bool CanAcceptOwnedRebreather(in StrictCombatReceipt receipt, StrictCombatOwnedRebreatherState value)
        {
            if (value == null || (value.IsProvisioned && value.HasWearState != _requiresRebreatherWearReceipts)) return false;
            var state = receipt.State;
            if (_state.HasSnapshot && state.Revision < _state.Snapshot.Revision) return true;
            var previous = _acceptedRebreather;
            if (previous == null || _acceptedRebreatherLife != state.LifeGeneration || !previous.IsProvisioned)
                return true;
            if (!value.IsProvisioned || value.Revision < previous.Revision) return false;
            if (value.Revision == previous.Revision) return previous.SameContents(value);
            // Resource revisions advance independently of health. Funded refills and
            // server grants may add resources; neither is inferred from native values.
            return BitConverter.DoubleToInt64Bits(value.Capacity) == BitConverter.DoubleToInt64Bits(previous.Capacity)
                && value.LastRefillSequence >= previous.LastRefillSequence;
        }

        private void AcceptOwnedRebreather(in StrictCombatReceipt receipt, StrictCombatOwnedRebreatherState value)
        {
            if (!_state.HasSnapshot || _state.Snapshot.Revision != receipt.State.Revision
                || _state.Snapshot.LifeGeneration != receipt.State.LifeGeneration) return;
            _acceptedRebreather = value;
            _acceptedRebreatherBinding = _state.Binding;
            _acceptedRebreatherStateRevision = receipt.State.Revision;
            _acceptedRebreatherLife = receipt.State.LifeGeneration;
        }

        private void ResetRebreatherFragments()
        {
            _pendingRebreatherReceipt.Clear();
            _finalRebreatherReceipt.Clear();
        }

        private void ResetOwnedRebreather()
        {
            ResetRebreatherFragments();
            _acceptedRebreather = null;
            _acceptedRebreatherBinding = default;
            _acceptedRebreatherStateRevision = 0;
            _acceptedRebreatherLife = 0;
        }

        // Keep pending and final shot results separate. Armour and tank packets can
        // arrive in either order, but only the same complete receipt can authorize both.
        private sealed class RebreatherReceiptAssembly
        {
            private StrictCombatReceipt _receipt;
            private StrictCombatOwnedRebreatherState _rebreather;
            private StrictCombatOwnedArmourState _armour;
            private bool _hasReceipt, _complete;

            internal bool TryAdd(in StrictCombatReceipt receipt, StrictCombatOwnedRebreatherState rebreather,
                StrictCombatOwnedArmourState armour, bool requiresArmour,
                out StrictCombatOwnedRebreatherState result, out StrictCombatOwnedArmourState resultArmour,
                out bool ready)
            {
                result = null;
                resultArmour = null;
                ready = false;
                if ((_hasReceipt && !StrictCombatArmourProtocol.SameReceipt(in _receipt, in receipt))
                    || (_rebreather != null && rebreather != null && !_rebreather.SameContents(rebreather))
                    || (_armour != null && armour != null && !_armour.SameContents(armour))) return false;
                if (_complete) return true;
                _receipt = receipt;
                _hasReceipt = true;
                if (rebreather != null) _rebreather = rebreather;
                if (armour != null) _armour = armour;
                if (_rebreather == null || (requiresArmour && _armour == null)) return true;
                _complete = true;
                result = _rebreather;
                resultArmour = _armour;
                ready = true;
                return true;
            }

            internal void Clear()
            {
                _receipt = default;
                _rebreather = null;
                _armour = null;
                _hasReceipt = false;
                _complete = false;
            }
        }
    }
}
