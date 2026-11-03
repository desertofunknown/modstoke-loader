// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;
using Enforcement = Modstoke.Sons.PlayerInventoryEnforcement;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerHeldOnlyRoute
    {
        internal sealed record ForcedHandRequest(int Slot, ulong Key, int ItemId, string Modules);
        private sealed record ForcedRequest(Held.MutationKind Kind, ulong Transition);
        private ForcedRequest _forcedRequest;
        private bool _forcedSettled, _forcedArmed, _forcedHolding;
        private ulong _forcedIntentFloor;
        private ulong _lastKnockDown, _lastSwimming;
        private readonly PlayerInventoryFrame?[] _forcedEquipRequests = new PlayerInventoryFrame?[2];
        private readonly PlayerInventoryFrame?[] _forcedEquipCommands = new PlayerInventoryFrame?[2];
        private readonly ulong[] _forcedEquipSettled = new ulong[2];
        private ulong[] _forcedSourceTransactions;

        internal bool HoldsForcedTransition => _forcedHolding;
        internal bool HasPendingForcedDrop => _forcedHolding && _forcedArmed && !_forcedSettled;
        internal bool HoldForcedIdlePump => _forcedHolding && _forcedArmed
            && !HasPendingCustody && !_inventory.HasPendingCustody;
        internal string ForcedFailure => _failure ?? _source?.FailureReason ?? _held?.FailureReason;

        private bool MatchesForcedTransition(Held.MutationKind kind, ulong transition)
            => _forcedRequest != null && _forcedRequest.Kind == kind && _forcedRequest.Transition == transition;

        internal bool TryReadForcedDropAdmission(out string detail)
        {
            detail = ForcedFailure ?? "Forced drop awaits the real world route and settled accepted custody.";
            if (!Current() || HasPendingCustody || _inventory.HasPendingCustody) return false;
            if (_source != null)
            {
                if (_inventory.HasPendingNativeCraftRoute
                    || _source.Inventory._currentView != TheForest.Items.Inventory.PlayerInventory.PlayerViews.World)
                { Detail = detail = "Forced transition awaits accepted craft/stow completion and the owned native inventory-close path; selected mat custody is not empty."; return false; }
                return _inventory.HeldOnlySourceReady && _source.Lifetime(true) && !_source.HasPendingCustody
                    && !_source.HasUnownedForcedCallback
                    && !Enforcement.HasObservedHeldOnlyWork(_source.Inventory, false)
                    && Held.Snapshot.TryCaptureRetained(_bridge, _source.Inventory, _source.Controller, out var snapshot, out detail)
                    && _source.MatchesSettledCustody(snapshot);
            }
            return _inventory.HeldOnlyServerReady(_bridge) && !_held.HasPendingCustody
                && _held.TryRead(_bridge, out _, out detail);
        }

        internal bool TryRequestForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = "An exact ordered server life/water transition and current held route are required.";
            if (!Current() || !Held.IsForcedDrop(kind) || transition == 0) return false;
            if (MatchesForcedTransition(kind, transition))
            { detail = _forcedSettled ? "Exact forced drop already settled." : "Exact forced drop remains queued; do not replay."; return true; }
            if (_forcedHolding || transition <= (kind == Held.MutationKind.KnockDownDrop ? _lastKnockDown : _lastSwimming)) return false;
            _forcedRequest = new(kind, transition); _forcedSettled = _forcedArmed = false; _forcedHolding = true;
            _forcedIntentFloor = Enforcement.HeldOnlyIntentSequence;
            if (kind == Held.MutationKind.KnockDownDrop) _lastKnockDown = transition; else _lastSwimming = transition;
            detail = "Forced drop queued behind accepted custody. Keep native life/water transition held until exact settlement.";
            return true;
        }

        internal bool TryReadForcedDropPrepared(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = ForcedFailure ?? "Earlier accepted work must drain before the forced transition is armed.";
            if (!_forcedHolding || !MatchesForcedTransition(kind, transition)) return false;
            if (_source != null && Enforcement.PeekHeldOnlyIntent(_source, _forcedIntentFloor) != null) return false;
            return TryReadForcedDropAdmission(out detail);
        }

        internal bool TryArmForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = ForcedFailure ?? "Both endpoints must prepare the same forced transition before dispatch.";
            if (!Current() || !_forcedHolding || !MatchesForcedTransition(kind, transition)) return false;
            if (_forcedArmed) return true;
            if (!TryReadForcedDropPrepared(kind, transition, out detail)) return false;
            _forcedArmed = true;
            return true;
        }

        internal bool TryReleaseForcedDrop(Held.MutationKind kind, ulong transition, out string detail)
        {
            if (!TryReadForcedDropSettlement(kind, transition, out detail)) return false;
            _forcedHolding = false;
            detail = "Exact settled forced transition released by its owning life/water boundary.";
            return true;
        }

        internal bool TryReadForcedDropSettlement(Held.MutationKind kind, ulong transition, out string detail)
        {
            detail = _failure ?? "Forced drop awaits accepted actions, native callbacks, world objects, equipment and final source ACK.";
            if (!Current() || !_forcedArmed || !MatchesForcedTransition(kind, transition) || HasPendingCustody) return false;
            if (_source != null)
            {
                if (!_source.Lifetime(true) || _source.HasPendingCustody || _source.HasUnownedForcedCallback
                    || _source.Controller._heldCount != 0 || _inventory.HasPendingCustody) return false;
                if (!Held.Snapshot.TryCaptureRetained(_bridge, _source.Inventory, _source.Controller, out var empty, out detail)
                    || !_source.MatchesSettledCustody(empty)) return false;
                _forcedSettled = true; // Settled native empty custody has no outgoing effects.
            }
            else
            {
                if (!_held.TryRead(_bridge, out var state, out detail) || state.Count != 0 || _inventory.HasPendingCustody) return false;
                _forcedSettled = true;
            }
            detail = "Exact held route is settled and native/server custody is empty; life/water owner may continue its own transition.";
            return true;
        }

        private bool TryStartForcedSource()
        {
            if (!_forcedHolding) return false;
            if (!_forcedArmed)
            {
                // Drain only the intent already retained when this transition arrived.
                // Unsupported accepted work stays retained for its actual owner.
                var intent = Enforcement.PeekHeldOnlyIntent(_source, _forcedIntentFloor);
                if (intent == null || !_inventory.HeldOnlySourceReady) return true;
                if (intent.Kind is not (Held.MutationKind.PutDown or Held.MutationKind.DropAll)
                    || !intent.Drop || intent.Fake || intent.RequiresLifeOwner || intent.PreSpawned != null || intent.Delay != -1f
                    || intent.Kind == Held.MutationKind.PutDown && intent.ItemId != -1)
                { Detail = "Earlier held intent still needs its concrete owner; forced transition cannot overtake it."; return true; }
                bool restore = intent.EquipPrevious && _source.Controller._heldCount == 1;
                if (!_source.TryPrepareCompoundRestore(restore, out var pendingDetail))
                { Detail = pendingDetail; return true; }
                Require(Enforcement.TryTakeHeldOnlyIntent(_source, out var accepted) && ReferenceEquals(accepted, intent),
                    "The earlier held intent changed before its exact drain.");
                ResetExchange(intent.Sequence);
                _request = new(_source.SettledFingerprint, intent.Kind, restore, _source.CompoundKey,
                    _source.CompoundItemId, _source.CompoundModules);
                SendJson(1, _request);
                return true;
            }
            if (_forcedSettled) return true;
            if (TryReadForcedDropSettlement(_forcedRequest.Kind, _forcedRequest.Transition, out _)) return true;
            if (!_inventory.HeldOnlySourceReady)
            { Detail = "Forced drop waits for already accepted ordinary inventory custody to drain."; return true; }
            if (!_source.TryPrepareForcedRestore(out var hands, out var detail))
            { Detail = detail; return true; }
            Require(EnforcementSequence(out var request), "Held intent sequence exhausted before forced dispatch.");
            ResetExchange(request);
            _request = new(_source.SettledFingerprint, _forcedRequest.Kind, false, 0, 0, null,
                _forcedRequest.Transition, hands);
            SendJson(1, _request);
            Detail = detail;
            return true;
        }

        private static bool EnforcementSequence(out ulong sequence)
            => PlayerInventoryEnforcement.TryAllocateHeldOnlySequence(out sequence);

        private bool ForcedRequestMatches(Request request)
        {
            if (!Held.IsForcedDrop(request.Kind)) return request.ForcedTransition == 0 && request.ForcedHands == null;
            if (!_forcedHolding || !_forcedArmed || !MatchesForcedTransition(request.Kind, request.ForcedTransition) || _forcedSettled
                || request.Restore || request.EquipKey != 0 || request.EquipItem != 0 || request.EquipModules != null
                || request.ForcedHands is not { Length: 2 }) return false;
            for (int i = 0; i < 2; i++)
            {
                var hand = request.ForcedHands[i];
                if (hand == null || hand.Slot != (int)(i == 0 ? EquipmentSlot.LeftHand : EquipmentSlot.RightHand)
                    || (hand.Key == 0 ? hand.ItemId != 0 || hand.Modules != null : hand.ItemId <= 0 || hand.Modules?.Length != 64)) return false;
            }
            return request.ForcedHands[0].Key == 0 || request.ForcedHands[0].Key != request.ForcedHands[1].Key;
        }

        private void CaptureForcedEquipmentReceipts()
        {
            if (_held == null || _request?.ForcedHands == null) return;
            for (int i = 0; i < 2; i++)
            {
                var hand = _request.ForcedHands[i];
                if (hand.Key == 0 || _forcedEquipCommands[i] is not { } command || _forcedEquipSettled[i] != 0) continue;
                if (_inventory.TryReadCompoundServerEquip(hand.Key, hand.ItemId, out var receipt)
                    && receipt.Transaction == command.Transaction) _forcedEquipSettled[i] = receipt.Transaction;
            }
        }

        private bool MatchesForcedRestoreRequest(in PlayerInventoryFrame frame)
        {
            if (_worldCommit == null || frame.Kind != PlayerInventoryMessage.EquipRequest
                || frame.Quantity != 1 || frame.EquipmentOptions != 0) return false;
            CaptureForcedEquipmentReceipts();
            for (int i = 0; i < 2; i++)
            {
                var hand = _request.ForcedHands[i];
                if (hand.Key == 0 || hand.Key != frame.Key || hand.ItemId != frame.ItemId
                    || hand.Slot != frame.BeforeQuantity || hand.Modules != frame.ModuleFingerprint) continue;
                if (i == 1 && _request.ForcedHands[0].Key != 0 && _forcedEquipSettled[0] == 0) return false;
                if (_forcedEquipRequests[i] is { } request) return frame == request;
                _forcedEquipRequests[i] = frame;
                return true;
            }
            return false;
        }

        private bool MatchesForcedRestoreCommand(in PlayerInventoryFrame frame)
        {
            if (frame.Kind is not (PlayerInventoryMessage.EquipApplied or PlayerInventoryMessage.EquipSettled
                or PlayerInventoryMessage.EquipRejected or PlayerInventoryMessage.EquipUnconfirmed)) return false;
            for (int i = 0; i < 2; i++)
            {
                if (_forcedEquipRequests[i] is not { } request || frame.Key != request.Key || frame.ItemId != request.ItemId
                    || frame.Quantity != 1 || frame.BeforeQuantity != request.BeforeQuantity || frame.EquipmentOptions != 0
                    || frame.ModuleFingerprint != request.ModuleFingerprint) continue;
                if (_forcedEquipCommands[i] is { } command) return frame == (command with { Kind = frame.Kind });
                _forcedEquipCommands[i] = frame;
                return true;
            }
            return false;
        }

        private bool ForcedServerEquipmentComplete()
        {
            CaptureForcedEquipmentReceipts();
            if (_forcedSourceTransactions is not { Length: 2 }) return false;
            for (int i = 0; i < 2; i++)
                if (_request.ForcedHands[i].Key == 0 ? _forcedSourceTransactions[i] != 0
                    : _forcedEquipSettled[i] == 0 || _forcedEquipSettled[i] != _forcedSourceTransactions[i]) return false;
            return true;
        }
    }
}
