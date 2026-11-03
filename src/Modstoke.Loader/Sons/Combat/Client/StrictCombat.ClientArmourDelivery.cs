// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public sealed partial class StrictCombatClient
    {
        private readonly ArmourReceiptAssembly _pendingArmourReceipt = new ArmourReceiptAssembly();
        private readonly ArmourReceiptAssembly _finalArmourReceipt = new ArmourReceiptAssembly();
        private StrictCombatOwnedArmourState _acceptedArmour;
        private StrictCombatBinding _acceptedArmourBinding;
        private ulong _acceptedArmourStateRevision;
        private ulong _acceptedArmourLife;

        /// <summary>
        /// Reads armour delivered with the current accepted combat state. This does not
        /// bind resources to native pieces, equip items or apply break effects.
        /// </summary>
        public bool TryReadOwnedArmour(out StrictCombatBinding binding, out StrictCombatSnapshot state,
            out StrictCombatOwnedArmourState armour)
        {
            armour = null;
            if (!TryRead(out binding, out state) || !_requiresArmourReceipts || _acceptedArmour == null
                || !binding.SameBinding(in _acceptedArmourBinding)
                || state.Revision != _acceptedArmourStateRevision || state.LifeGeneration != _acceptedArmourLife)
                return false;
            armour = _acceptedArmour;
            return true;
        }

        private bool TryReceiveArmourPage(ReadOnlySpan<byte> payload, out StrictCombatReceipt receipt,
            out StrictCombatOwnedArmourState armour, out bool ready)
        {
            receipt = default;
            armour = null;
            ready = false;
            if (!_state.HasBinding || !_state.HasPendingRequest
                || !StrictCombatArmourProtocol.TryReadReceiptPage(payload, out var page)) return false;
            var pending = _state.PendingIntent;
            var received = page.Receipt;
            if (received.Epoch != _state.Binding.Epoch || received.Sequence != pending.Sequence
                || received.Action != pending.Action || received.RequestLifeGeneration != pending.LifeGeneration)
                return false;

            // A pending launch and its final result can cross in transit. They have
            // separate bounded assemblies, and neither can complete the other's pages.
            var assembly = received.Result == StrictCombatResult.ShotPending
                ? _pendingArmourReceipt : _finalArmourReceipt;
            if (!assembly.TryAdd(page, out armour, out ready))
            { Retire(); return false; }
            if (ready) receipt = received;
            return true;
        }

        private bool CanAcceptOwnedArmour(in StrictCombatReceipt receipt, StrictCombatOwnedArmourState armour)
        {
            if (armour == null) return false;
            var state = receipt.State;
            // A retained older receipt may finish its request without replacing a
            // newer state already accepted through the independent respawn transaction.
            if (_state.HasSnapshot && state.Revision < _state.Snapshot.Revision) return true;
            var previous = _acceptedArmour;
            if (previous == null || _acceptedArmourLife != state.LifeGeneration) return true;
            if (state.Revision == _acceptedArmourStateRevision) return previous.SameContents(armour);
            if (state.Revision < _acceptedArmourStateRevision) return true;
            if (!previous.IsProvisioned) return true;
            if (!armour.IsProvisioned || armour.Revision < previous.Revision) return false;
            if (armour.Revision == previous.Revision) return previous.SameContents(armour);
            if (armour.Count != previous.Count || armour.Golden != previous.Golden
                || !SameArmourFloat(armour.GeneralMultiplier, previous.GeneralMultiplier)
                || !SameArmourFloat(armour.DemonicMultiplier, previous.DemonicMultiplier)) return false;
            for (int i = 0; i < previous.Count; i++)
            {
                if (!previous.TryGetPiece(i, out var oldPiece) || !armour.TryGetPiece(i, out var piece)
                    || piece.Slot != oldPiece.Slot || piece.ResourceId != oldPiece.ResourceId
                    || piece.ItemId != oldPiece.ItemId || (oldPiece.IsBroken && !piece.IsBroken)
                    || piece.RemainingPoints > oldPiece.RemainingPoints) return false;
            }
            return true;
        }

        private void AcceptOwnedArmour(in StrictCombatReceipt receipt, StrictCombatOwnedArmourState armour)
        {
            if (!_state.HasSnapshot || _state.Snapshot.Revision != receipt.State.Revision
                || _state.Snapshot.LifeGeneration != receipt.State.LifeGeneration) return;
            _acceptedArmour = armour;
            _acceptedArmourBinding = _state.Binding;
            _acceptedArmourStateRevision = receipt.State.Revision;
            _acceptedArmourLife = receipt.State.LifeGeneration;
        }

        private void ResetArmourFragments()
        {
            _pendingArmourReceipt.Clear();
            _finalArmourReceipt.Clear();
        }

        private void ResetOwnedArmour()
        {
            ResetArmourFragments();
            _acceptedArmour = null;
            _acceptedArmourBinding = default;
            _acceptedArmourStateRevision = 0;
            _acceptedArmourLife = 0;
        }

        private static bool SameArmourFloat(float first, float second)
            => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);

        private sealed class ArmourReceiptAssembly
        {
            private StrictCombatArmourReceiptPage _first;
            private StrictCombatArmourReceiptPage _second;
            private bool _complete;

            internal bool TryAdd(StrictCombatArmourReceiptPage page, out StrictCombatOwnedArmourState armour,
                out bool ready)
            {
                armour = null;
                ready = false;
                var existing = _first ?? _second;
                if (existing != null && !existing.SameEnvelope(page)) return false;
                var duplicate = page.PageIndex == 0 ? _first : _second;
                if (duplicate != null && !SamePage(duplicate, page)) return false;
                if (_complete) return true;
                if (page.PageIndex == 0) _first = page; else _second = page;
                if (_first == null || (page.TotalCount > StrictCombatArmourProtocol.PiecesPerPage && _second == null))
                    return true;
                if (!page.IsProvisioned) armour = StrictCombatOwnedArmourState.Unprovisioned;
                else
                {
                    Span<StrictCombatArmourResource> pieces = stackalloc StrictCombatArmourResource[StrictCombatArmourProtocol.MaximumPieces];
                    for (int i = 0; i < page.TotalCount; i++)
                    {
                        var source = i < StrictCombatArmourProtocol.PiecesPerPage ? _first : _second;
                        if (!source.TryGetPiece(i % StrictCombatArmourProtocol.PiecesPerPage, out pieces[i])) return false;
                    }
                    if (!StrictCombatOwnedArmourState.TryCreate(page.Revision, page.Golden,
                        page.GeneralMultiplier, page.DemonicMultiplier, pieces.Slice(0, page.TotalCount), out armour))
                        return false;
                }
                _complete = true;
                ready = true;
                return true;
            }

            internal void Clear()
            {
                _first = null;
                _second = null;
                _complete = false;
            }

            private static bool SamePage(StrictCombatArmourReceiptPage first, StrictCombatArmourReceiptPage second)
            {
                if (!first.SameEnvelope(second) || first.PageIndex != second.PageIndex
                    || first.PieceCount != second.PieceCount) return false;
                for (int i = 0; i < first.PieceCount; i++)
                {
                    if (!first.TryGetPiece(i, out var a) || !second.TryGetPiece(i, out var b)
                        || a.Slot != b.Slot || a.ResourceId != b.ResourceId || a.ItemId != b.ItemId
                        || a.IsBroken != b.IsBroken || !SameArmourFloat(a.RemainingPoints, b.RemainingPoints)) return false;
                }
                return true;
            }
        }
    }
}
