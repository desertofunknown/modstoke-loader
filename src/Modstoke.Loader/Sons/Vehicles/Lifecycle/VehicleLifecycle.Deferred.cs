// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Source-owner save reservation and occupied-cart hold without a precreated replacement.
        /// Retained player/AI/ghost writers must already be suspended by their owning lifecycle.
        /// Generated IK targets are parked outside the retiring cart. After ordinary destruction,
        /// use the existing handoff Bind/Restore/Saving/Writers sequence on the new actual owner.
        /// This local API does not export a save reservation to another peer or certify readiness.
        /// </summary>
        public static bool TryPrepareOccupiedCartSourceSaveHandoff(BoltEntity source,
            out CartSaveHandoff handoff, out string detail)
        {
            handoff = null;
            detail = "Source-only cart preparation requires the selected game thread without reentry.";
            if (!Enter()) return false;
            CartOccupants occupants = null;
            try
            {
                RequireContext();
                var binding = new Binding(source);
                Require(binding.Cart != null && source.isOwner, "The exact physical cart owner is required.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                Require(global::Sons.Inventory.WorldItemManager.TryGetInstance(out var manager) && Live(manager),
                    "The local save registry is unavailable.");
                occupants = new CartOccupants(binding, null, CaptureRetainedOccupantPairs(binding));
                occupants.Register();
                handoff = new CartSaveHandoff(binding, manager);
                handoff.Prepare();
                detail = "Source cart/save held and IK parked independently; replacement is not required until binding. External occupant quiescence remains caller-owned.";
                return true;
            }
            catch (Exception error)
            {
                if (handoff == null) occupants?.Unregister(); else handoff.QuarantineNativeWriters();
                detail = Failure(handoff == null ? "Source-only cart preparation rejected" : "Source preparation unconfirmed; keep held", error);
                return false;
            }
            finally { _busy = false; }
        }

        /// <summary>
        /// Read-only native capture for a source, destination or observer. Call TryQuiesceCart
        /// before retirement and TryBindReplacement only after that peer's old cart was destroyed.
        /// This retains local occupant handles, not a borrowed source attachment. It does not
        /// migrate save registration or suspend player/AI/ghost schedulers; those are explicit
        /// integration obligations, not inferred from a successful cart-writer hold.
        /// </summary>
        public static bool TryCaptureOccupiedCartParticipant(BoltEntity original,
            out CartOccupiedParticipant participant, out string detail)
        {
            participant = null;
            detail = "Cart participant capture requires the selected game thread without reentry.";
            if (!Enter()) return false;
            CartOccupants occupants = null;
            try
            {
                RequireContext();
                var binding = new Binding(original);
                Require(binding.Cart != null, "A physical GolfCart is required.");
                CartWriterHooks.RequireInitialized(binding.Cart);
                occupants = new CartOccupants(binding, null, CaptureRetainedOccupantPairs(binding));
                occupants.Register();
                participant = new CartOccupiedParticipant(binding);
                detail = "Exact local occupant handles and cart schema captured without native mutation.";
                return true;
            }
            catch (Exception error)
            {
                occupants?.Unregister();
                detail = Failure("Cart participant capture rejected", error);
                return false;
            }
            finally { _busy = false; }
        }

        private static VehicleOccupantReplacement[] CaptureRetainedOccupantPairs(Binding binding)
        {
            var pairs = new List<VehicleOccupantReplacement>();
            var ids = new HashSet<ulong>();
            for (int i = 0; i < 4; i++)
            {
                ulong id = binding.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                if (id == 0) continue;
                var entity = Add(id);
                if (i == 0)
                {
                    Require(entity.TryFindState<IGolfCartState>(out var ghost) && ghost.ConnectedSlots.Length == 4,
                        "The physical driver slot does not name the exact driver ghost.");
                    Add(ghost.ConnectedSlots.GetValueNetworkId(0).PackedValue);
                }
            }
            Require(pairs.Count > 0 && pairs.Count <= 5, "At least one exact settled seat occupant is required.");
            return pairs.ToArray();

            BoltEntity Add(ulong id)
            {
                Require(id != 0 && ids.Add(id), "An occupant ID is zero, duplicated or unresolved.");
                var entity = BoltNetwork.FindEntity(new NetworkId(id));
                Require(Live(entity) && entity.isAttached && entity.networkId.PackedValue == id,
                    "An exact occupant attachment is unavailable.");
                pairs.Add(new(entity, entity));
                return entity;
            }
        }

        public sealed class CartOccupiedParticipant
        {
            private readonly CartOccupants _occupants;
            private readonly CartWriterHold _source;
            private readonly CartSnapshotData _captured;
            private CartWriterHold _replacement;
            private bool _held, _bound, _finished, _uncertain;

            internal CartOccupiedParticipant(Binding binding)
            {
                _occupants = OccupantsFor(binding.Cart);
                _source = new CartWriterHold(binding, true);
                _captured = CaptureCartSnapshotData(binding);
            }

            public ulong OriginalNetworkId => _occupants.Source.Id;
            public IReadOnlyList<VehicleRetainedOccupant> RetainedOccupants => _occupants.RetainedOccupants();

            /// <summary>Validates peer-independent seat IDs against this peer's retained actors.</summary>
            public bool TryValidateSnapshot(GolfCartMigrationSnapshot snapshot, out string detail)
            {
                detail = "Cart snapshot comparison is unavailable or reentrant.";
                if (!Enter()) return false;
                try
                {
                    RequireContext();
                    Require(snapshot != null && !_uncertain && !_finished, "The cart participant is unavailable.");
                    var data = DecodeCartSnapshot(snapshot.Encode(), OriginalNetworkId);
                    Require(data.PrefabId == _captured.PrefabId && Equal(data.Seats, _captured.Seats)
                        && data.Radio.Present == _captured.Radio.Present && data.Radio.IsOn == _captured.Radio.IsOn
                        && (!data.Radio.IsOn || data.Radio.TrackHash == _captured.Radio.TrackHash),
                        "This peer's exact retained seat/actor/radio identities differ from the authoritative snapshot.");
                    if (_bound) _occupants.RequireMoved(); else if (_held) _occupants.RequireParked();
                    detail = "Peer-independent identities match the retained local handles; no old cart attachment was resolved.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart snapshot comparison rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>Holds cart writers only. Never use this alone as an aggregate Ready receipt.</summary>
            public bool TryQuiesceCart(out string detail)
            {
                detail = "Cart quiescence is unavailable or reentrant.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext();
                    Require(!_uncertain && !_finished && !_bound, "The participant is no longer preparable.");
                    if (_held)
                    {
                        _source.RequireSuspended(); _occupants.RequireParked();
                        detail = "Cart hold is still current; independent occupant holds are not certified here.";
                        return true;
                    }
                    attempted = true;
                    _source.Suspend();
                    _occupants.PauseSourceRadio();
                    _occupants.ParkBeforeRetirement();
                    _held = true;
                    detail = "Cart writers held; retained local occupants parked. Their independent writer lifecycles must also be held before Ready.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) Quarantine();
                    detail = Failure(attempted ? "Cart quiescence unconfirmed" : "Cart quiescence rejected", error);
                    return false;
                }
                finally { _busy = false; }
            }

            /// <summary>
            /// Call on every peer after native recreate and authoritative seat replication, while
            /// independent occupant writers are still held. Never invokes enter/exit on occupants.
            /// Target caches must be pristine; already-processed automatic connection is refused.
            /// </summary>
            public bool TryBindReplacement(BoltEntity replacement, out string detail)
            {
                detail = "Cart replacement binding is unavailable or reentrant.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext();
                    Require(_held && !_bound && !_finished && !_uncertain, "The participant is not awaiting replacement.");
                    var binding = new Binding(replacement);
                    _occupants.StageReplacement(binding);
                    _replacement = new CartWriterHold(binding, true);
                    attempted = true;
                    _replacement.Suspend();
                    _occupants.BindAfterRetirement(binding);
                    _source.ReleaseRetired();
                    _bound = true;
                    detail = "Retained local occupants rebound to the exact new cart. Cart writers remain held; independent occupant holds remain caller-owned.";
                    return true;
                }
                catch (Exception error)
                {
                    // Staging installs an exact lease. Keep it quarantined even when later
                    // preflight fails; never silently replace it with another new identity.
                    if (attempted || _occupants.Target != null) Quarantine();
                    detail = Failure(_uncertain ? "Replacement binding unconfirmed; keep held" : "Replacement binding rejected", error);
                    return false;
                }
                finally { _busy = false; }
            }

            public bool TryValidateRetainedBindings(out string detail)
            {
                detail = "Retained binding validation is unavailable or reentrant.";
                if (!Enter()) return false;
                try
                {
                    RequireContext();
                    Require(_held && !_finished && !_uncertain, "The participant is not held.");
                    if (_bound) { _replacement.RequireSuspended(); _occupants.RequireMoved(); }
                    else { _occupants.RequireParked(); if (Live(_occupants.Source.Entity)) _source.RequireSuspended(); }
                    detail = "Captured bindings still match. This does not establish independent player/AI/ghost writer quiescence.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Retained binding validation rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>Only after aggregate Repaired; independent occupant holds resume separately.</summary>
            public bool TryResumeCart(out string detail) => Finish(false, out detail);

            /// <summary>Only before source retirement; restores parked targets and original cart policy.</summary>
            public bool TryCancel(out string detail) => Finish(true, out detail);

            private bool Finish(bool cancel, out string detail)
            {
                detail = "Cart participant completion is unavailable or reentrant.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext();
                    Require(!_finished && !_uncertain, "The participant is terminal.");
                    if (!_held && cancel) { _occupants.Unregister(); _finished = true; return true; }
                    Require(_held && (cancel ? !_bound && _occupants.Source.Matches() : _bound),
                        "Cancel requires the intact source; resume requires the exact repaired replacement.");
                    attempted = true;
                    if (cancel) { _occupants.CancelParked(); _occupants.ResumeRadio(false); _source.Resume(_source); }
                    else { _occupants.RequireMoved(); _occupants.ResumeRadio(true); _replacement.Resume(_replacement); }
                    _occupants.Unregister(); _finished = true;
                    detail = cancel ? "Original cart policy restored; independent occupant holds may cancel."
                        : "Repaired cart resumed under its new native owner/proxy policy; independent occupant holds may resume.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) Quarantine();
                    detail = Failure(attempted ? "Cart participant completion unconfirmed" : "Cart participant completion rejected", error);
                    return false;
                }
                finally { _busy = false; }
            }

            private void Quarantine()
            {
                _uncertain = true;
                _source.QuarantineIfHeld(); _replacement?.QuarantineIfHeld();
                _occupants.QuarantineStagedReplacement();
            }
        }
    }
}
