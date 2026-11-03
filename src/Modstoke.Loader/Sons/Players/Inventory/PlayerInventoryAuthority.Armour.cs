// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using ArmourCalculation = Modstoke.Sons.PlayerArmourAuthority.Calculation;
using ArmourState = Modstoke.Sons.PlayerArmourAuthority.State;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private ArmourReservation _armour, _lastSettledArmour;
        private PlayerArmourRoute _armourRoute;

        /// <summary>
        /// Include in PreparedReady, CanQueueNativeMutation and ServerExternalMutationPending.
        /// Remains true through canonical settlement until the exact source Completed ACK,
        /// and on uncertainty or retirement. A numeric preview never releases this boundary.
        /// </summary>
        public bool HasPendingArmourCustody => _armour != null || _armourRoute?.HasPendingCustody == true;
        public bool HasUnconfirmedArmourCustody => HasPendingArmourCustody
            && (_armour?.Unconfirmed == true || IsDisposed || IsFaulted || _armourRoute?.FailureReason != null);
        internal ArmourCalculation PendingArmourCalculation => _armour?.Calculation;

        internal bool MatchesArmourBridge(PlayerBridge bridge) => ReferenceEquals(bridge, _bridge);

        internal bool TryBindArmourRoute(PlayerArmourRoute route, PlayerBridge bridge)
        {
            if (route == null || _armourRoute != null || !ReferenceEquals(bridge, _bridge) || bridge.IsGameplayActive
                || Environment.CurrentManagedThreadId != _thread || !Current(out _)) return false;
            _armourRoute = route; return true;
        }

        internal void RetainArmourRouteFailure(PlayerArmourRoute route, string detail)
        {
            if (Environment.CurrentManagedThreadId == _thread && ReferenceEquals(route, _armourRoute))
                Fault("Armour route retains uncertain custody; no replay or refund. " + detail);
        }

        internal bool TryReadArmourTransaction(PlayerBridge bridge, out ulong transaction)
        {
            transaction = 0;
            if (!MatchesArmourBridge(bridge) || !Ready(out _) || HasPendingArmourCustody
                || !HasAcceptedBaseline || !ArmourOtherCustodyIdle(true)
                || (MutationEnforcement != null && !MutationEnforcement.CanQueue)) return false;
            transaction = _lastTransaction;
            return true;
        }

        // Received intents may wait behind an accepted armour reservation. Only admission
        // requires an empty queue; afterward validate actual custody, not queue bookkeeping.
        private bool ArmourOtherCustodyIdle(bool includeQueuedIntents)
            => !_suspended && _pending == null && _stow == null && _equip == null && !_equipNotification.HasValue
                && _removal == null && _grant == null && _passive == null && _worldTransfer == null
                && !HasNativeCraftReservation && !HasNativeConsumptionReservation && !HasNativeDeathTransfer
                && !HasPendingNativePowerSupply && !HasPendingNativePowerProjection
                && (MutationEnforcement == null || (!MutationEnforcement.IsDisposed && MutationEnforcement.IsArmed
                    && (includeQueuedIntents ? !MutationEnforcement.HasPendingCustody
                        && !MutationEnforcement.HasPendingHeldOnlyCustody && !MutationEnforcement.HasPendingNativeCraftPresentation
                        && !MutationEnforcement.BlocksNativeWorldMutation : !MutationEnforcement.HasExecutingServerCustody)));

        internal bool TryReserveArmour(ArmourCalculation calculation, out string detail)
        {
            detail = "Armour reservation requires an exact calculation and idle canonical inventory.";
            if (Environment.CurrentManagedThreadId != _thread || _busy || calculation == null) return false;
            if (_armour != null)
            {
                detail = "The existing armour reservation is retained; no source effects were dispatched by this call.";
                return ReferenceEquals(_armour.Calculation, calculation) && !HasUnconfirmedArmourCustody;
            }
            if (!Ready(out var target) || !HasAcceptedBaseline || !ArmourOtherCustodyIdle(true)
                || (MutationEnforcement != null && !MutationEnforcement.CanQueue)
                || !calculation.Owner.MatchesArmourCalculation(this, _bridge, calculation)
                || !Fresh(calculation.Transaction) || _lastTransaction != calculation.BeforeInventoryTransaction) return false;
            _busy = true;
            try
            {
                var state = target.GetState<IPlayerState>();
                var equipment = EquipmentState.Capture(state);
                if (equipment == null || !OwnedEquipmentMatches(equipment)
                    || !ArmourPublicationMatches(calculation.Before, equipment.Armour)) return false;
                var records = CaptureArmourRecords(calculation.Before);
                if (records == null || !ValidateArmourChanges(calculation, records)) return false;
                var reservation = new ArmourReservation(calculation, records, equipment, target, state,
                    state.PlayerArmourIds, ArmourFingerprint(calculation));
                if (!ArmourReservationCurrent(reservation, false)) return false;

                _armour = reservation;
                // No native effect or transaction has escaped. Refuse integration that has
                // not wired the canonical account-wide gates; do not hold _busy across frames.
                _busy = false;
                if (Ready(out _) || CanQueueNativeMutation)
                {
                    _armour = null;
                    detail = "Wire HasPendingArmourCustody into PreparedReady and CanQueueNativeMutation before reserving armour.";
                    return false;
                }
                _busy = true;
                _lastTransaction = calculation.Transaction;
                Detail = detail = "Exact armour item records reserved; source execution and its authenticated ACK are pending.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Armour reservation refused before dispatch: " + error.Message;
                if (_lastTransaction == calculation.BeforeInventoryTransaction) _armour = null;
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Call only after the source route is ready, immediately BEFORE its one dispatch.
        /// Caller must authenticate and execute this exact calculation, then acknowledge the
        /// fingerprint only after every native condition/break/cursor effect has been confirmed.
        /// A second begin is never permission to resend or replay native effects.
        /// </summary>
        internal bool TryBeginArmourSource(ArmourCalculation calculation, out string fingerprint, out string detail)
        {
            fingerprint = null;
            detail = "No undispatched exact armour reservation is ready.";
            var pending = _armour;
            if (Environment.CurrentManagedThreadId != _thread || _busy || pending == null
                || !ReferenceEquals(pending.Calculation, calculation) || pending.SourceDispatched
                || HasUnconfirmedArmourCustody) return false;
            _busy = true;
            try
            {
                if (!ArmourReservationCurrent(pending, true))
                    throw new InvalidOperationException("Reserved armour custody changed before source dispatch.");
                pending.SourceDispatched = true;
                fingerprint = pending.Fingerprint;
                Detail = detail = "Source armour dispatch marked once; no numeric settlement is authorized before its ACK.";
                return true;
            }
            catch (Exception error) { RetainUnconfirmedArmour(calculation, error.Message); detail = Detail; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Trusted authenticated source-effect receipt route, not an observation or client
        /// damage request. A matching digest correlates the command; the source executor must
        /// separately prove all its native effects before the route accepts its Applied frame.
        /// Acceptance holds the reservation until TrySettleArmour commits the canonical batch.
        /// </summary>
        internal bool TryAcknowledgeArmourSource(PlayerArmourRoute route,
            ControlPeer authenticatedPeer, ulong transaction, string fingerprint, out string detail)
        {
            detail = "No exact dispatched armour command matches this authenticated source receipt.";
            if (Environment.CurrentManagedThreadId != _thread || _busy
                || !ReferenceEquals(route, _armourRoute)
                || !ReferenceEquals(authenticatedPeer, _bridge.Peer) || authenticatedPeer?.IsCurrent != true
                || !_bridge.IsGameplayActive || !Current(out _)) return false;
            var pending = _armour;
            if (pending == null)
            {
                bool duplicate = _lastSettledArmour != null
                    && transaction == _lastSettledArmour.Calculation.Transaction
                    && string.Equals(fingerprint, _lastSettledArmour.Fingerprint, StringComparison.Ordinal)
                    && _lastSettledArmour.Calculation.Owner.IsCurrentArmourOwner(this, _bridge)
                    && route?.ConfirmsSourceEffects(this, _lastSettledArmour.Calculation, fingerprint) == true;
                if (duplicate) detail = "Already-settled source armour receipt observed; nothing was reapplied.";
                return duplicate;
            }
            if (transaction != pending.Calculation.Transaction || !pending.SourceDispatched
                || !string.Equals(fingerprint, pending.Fingerprint, StringComparison.Ordinal)
                || HasUnconfirmedArmourCustody) return false;
            _busy = true;
            try
            {
                if (route?.ConfirmsSourceEffects(this, pending.Calculation, fingerprint) != true
                    || !ArmourReservationCurrent(pending, true))
                    throw new InvalidOperationException("Source armour effects or reserved canonical custody are unconfirmed.");
                pending.SourceAcknowledged = true;
                Detail = detail = "Exact source armour ACK retained; canonical settlement is still pending.";
                return true;
            }
            catch (Exception error) { RetainUnconfirmedArmour(pending.Calculation, error.Message); detail = Detail; return false; }
            finally { _busy = false; }
        }

        internal void RetainUnconfirmedArmour(ArmourCalculation calculation, string detail)
        {
            if (Environment.CurrentManagedThreadId != _thread || _armour == null
                || !ReferenceEquals(_armour.Calculation, calculation)) return;
            _armour.Unconfirmed = true;
            Fault("Armour custody is unconfirmed; retain the accepted hit, do not replay or refund. " + detail);
        }

        /// <summary>
        /// After the source ACK, publish broken server slots once and commit the existing
        /// ItemRecords as a batch. This does not debit stored items by ID, construct native
        /// items, apply health/stamina, or complete ingress. Caller immediately commits the
        /// armour revision with no intervening native call or allocation on this game thread.
        /// </summary>
        internal bool TryCommitArmour(ArmourCalculation calculation, out string detail)
        {
            detail = "Canonical armour settlement is waiting for the exact source native-effect ACK.";
            var pending = _armour;
            if (Environment.CurrentManagedThreadId != _thread || _busy || pending == null
                || !ReferenceEquals(pending.Calculation, calculation) || !pending.SourceAcknowledged
                || pending.PublicationAttempted || HasUnconfirmedArmourCustody
                || !calculation.Owner.IsSettlingArmour(this, _bridge, calculation)) return false;
            _busy = true;
            try
            {
                if (!ArmourReservationCurrent(pending, true))
                    throw new InvalidOperationException("Armour custody changed after the source ACK.");
                pending.PublicationAttempted = true;
                for (int i = 0; i < calculation.ChangeCount; i++)
                {
                    var change = calculation.GetChange(i);
                    if (!change.Broken) continue;
                    RequireArmourPublicationCurrent(pending);
                    pending.Published[change.Before.Index] = 0;
                    pending.Equipment.Armour[change.Before.Index] = 0;
                    if (!pending.Equipment.Matches(EquipmentState.Capture(pending.NativeState)))
                        throw new InvalidOperationException("Broken-slot publication or unrelated equipment changed during readback.");
                }
                if (!pending.Equipment.Matches(EquipmentState.Capture(pending.NativeState))
                    || !ArmourPublicationMatches(calculation.After, pending.Equipment.Armour)
                    || !ArmourRecordsCurrent(calculation.Before, pending.Records))
                    throw new InvalidOperationException("Armour inventory or equipment no longer matches the reserved batch.");
                RequireArmourPublicationCurrent(pending);

                // All fallible/native work precedes the managed commit. Consumed item
                // records have zero points; the empty native slot retains its stale points
                // separately in Calculation.After, matching TryUnequipSlot's actual behavior.
                for (int i = 0; i < calculation.ChangeCount; i++)
                {
                    var change = calculation.GetChange(i);
                    var item = pending.Records[change.Before.Index];
                    if (change.Broken)
                    {
                        item.ArmourSlot = -1;
                        item.Consumed = true;
                        item.Quantity = 0;
                        item.ArmourPoints = 0;
                    }
                    else item.ArmourPoints = change.After.RemainingPoints;
                }
                _lastSettledArmour = pending;
                _armour = null;
                Detail = detail = "Source armour ACK, server slots and canonical armour custody settled; combat completion remains separate.";
                return true;
            }
            catch (Exception error) { RetainUnconfirmedArmour(calculation, error.Message); detail = Detail; return false; }
            finally { _busy = false; }
        }

        private bool ArmourReservationCurrent(ArmourReservation pending, bool reserved)
            => pending != null && !pending.Unconfirmed && ArmourOtherCustodyIdle(!reserved)
                && Current(out var current) && current.Pointer == pending.Target.Pointer
                && _lastTransaction == (reserved ? pending.Calculation.Transaction : pending.Calculation.BeforeInventoryTransaction)
                && pending.Calculation.Owner.MatchesArmourCalculation(this, _bridge, pending.Calculation)
                && pending.NativeState != null && !pending.NativeState.WasCollected
                && current.GetState<IPlayerState>()?.Pointer == pending.NativeState.Pointer
                && pending.Published != null && !pending.Published.WasCollected
                && pending.NativeState.PlayerArmourIds?.Pointer == pending.Published.Pointer
                && pending.Equipment.Matches(EquipmentState.Capture(pending.NativeState))
                && ArmourRecordsCurrent(pending.Calculation.Before, pending.Records);

        private void RequireArmourPublicationCurrent(ArmourReservation pending)
        {
            if (!ArmourOtherCustodyIdle(false) || !Current(out var current) || current.Pointer != pending.Target.Pointer
                || pending.NativeState.WasCollected || current.GetState<IPlayerState>()?.Pointer != pending.NativeState.Pointer
                || pending.Published.WasCollected || pending.NativeState.PlayerArmourIds?.Pointer != pending.Published.Pointer
                || _lastTransaction != pending.Calculation.Transaction
                || !ReferenceEquals(_armour, pending) || pending.Unconfirmed
                || !pending.Calculation.Owner.IsSettlingArmour(this, _bridge, pending.Calculation))
                throw new InvalidOperationException("The exact armour publication target or settlement owner retired.");
        }

        private static bool ValidateArmourChanges(ArmourCalculation calculation, ItemRecord[] records)
        {
            if (calculation.Before.SlotCount != calculation.After.SlotCount) return false;
            var changed = new bool[records.Length];
            for (int i = 0; i < calculation.ChangeCount; i++)
            {
                var change = calculation.GetChange(i);
                int slot = change.Before.Index;
                if ((uint)slot >= (uint)records.Length || changed[slot] || records[slot] == null
                    || records[slot].Key != change.Before.InventoryKey || records[slot].Id != change.Before.ItemId
                    || !ArmourConditionEqual(records[slot].ArmourPoints, change.Before.RemainingPoints)
                    || !ArmourSlotEqual(change.Before, calculation.Before.GetSlot(slot))
                    || !ArmourSlotEqual(change.After, calculation.After.GetSlot(slot))
                    || change.After.Index != slot || (change.Broken
                        ? change.After.InventoryKey != 0 || change.After.ItemId != 0
                        : change.After.InventoryKey != change.Before.InventoryKey || change.After.ItemId != change.Before.ItemId)) return false;
                changed[slot] = true;
            }
            for (int i = 0; i < changed.Length; i++)
                if (!changed[i] && !ArmourSlotEqual(calculation.Before.GetSlot(i), calculation.After.GetSlot(i))) return false;
            return true;
        }

        private ItemRecord[] CaptureArmourRecords(ArmourState before)
        {
            var records = new ItemRecord[before.SlotCount];
            for (int i = 0; i < records.Length; i++)
                if (before.GetSlot(i).IsEquipped)
                {
                    if (!_items.TryGetValue(before.GetSlot(i).InventoryKey, out var item)) return null;
                    records[i] = item;
                }
            return ArmourRecordsCurrent(before, records) ? records : null;
        }

        private bool ArmourRecordsCurrent(ArmourState before, ItemRecord[] records)
        {
            for (int i = 0; i < records.Length; i++)
            {
                var slot = before.GetSlot(i);
                if (!slot.IsEquipped) continue;
                var item = records[i];
                if (slot.Index != i || item == null || !_items.TryGetValue(slot.InventoryKey, out var owned)
                    || !ReferenceEquals(owned, item) || item.Key != slot.InventoryKey || item.Id != slot.ItemId
                    || item.Consumed || item.Quantity != 1 || item.ArmourSlot != i || !item.DefinitionOnly
                    || item.Item != null || item.HeldSlot >= 0 || item.Clothing || item.ClothingSlots != 0
                    || !ArmourConditionEqual(item.ArmourPoints, slot.RemainingPoints) || !item.IdentityCurrent()) return false;
            }
            return true;
        }

        private static bool ArmourPublicationMatches(ArmourState armour, int[] published)
        {
            for (int i = 0; i < published.Length; i++)
                if (published[i] != (i < armour.SlotCount ? armour.GetSlot(i).ItemId : 0)) return false;
            for (int i = published.Length; i < armour.SlotCount; i++)
                if (armour.GetSlot(i).IsEquipped) return false;
            return true;
        }

        private static bool ArmourConditionEqual(float left, float right)
            => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

        private static bool ArmourSlotEqual(PlayerArmourAuthority.Slot left,
            PlayerArmourAuthority.Slot right)
            => left.Index == right.Index && left.InventoryKey == right.InventoryKey && left.ItemId == right.ItemId
                && ArmourConditionEqual(left.RemainingPoints, right.RemainingPoints);

        private static string ArmourFingerprint(ArmourCalculation calculation)
            => PlayerArmourRoute.Command.Capture(calculation).Fingerprint;

        // References to the existing canonical records, not a shadow condition/quantity ledger.
        private sealed class ArmourReservation(ArmourCalculation calculation, ItemRecord[] records,
            EquipmentState equipment, BoltEntity target, IPlayerState nativeState,
            Bolt.NetworkArray_Integer published, string fingerprint)
        {
            internal readonly ArmourCalculation Calculation = calculation;
            internal readonly ItemRecord[] Records = records;
            internal readonly EquipmentState Equipment = equipment;
            internal readonly BoltEntity Target = target;
            internal readonly IPlayerState NativeState = nativeState;
            internal readonly Bolt.NetworkArray_Integer Published = published;
            internal readonly string Fingerprint = fingerprint;
            internal bool SourceDispatched, SourceAcknowledged, PublicationAttempted, Unconfirmed;
        }
    }
}
