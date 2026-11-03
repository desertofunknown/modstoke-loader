// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Animation;
using global::Sons.Characters;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Explicit live endpoint pair. Include unchanged endpoints as identity pairs.</summary>
    public sealed record VehicleOccupantReplacement(BoltEntity Original, BoltEntity Replacement);

    /// <summary>Exact retained local handles for the independent occupant-writer lifecycle.</summary>
    public sealed record VehicleRetainedOccupant(int Slot, ulong SeatNetworkId, ulong PlayerNetworkId,
        PlayerGolfCartAction LocalPlayerAction, VailActor LocalActor, BoltEntity DriverGhost);

    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Prepares an occupied cart's local save handoff using an ALREADY CREATED replacement.
        /// Both carts must be actual local owners of the same prefab. The replacement must be
        /// fresh, empty and independently registered. Occupants and their original actions/IK
        /// are moved before source retirement; never destroy the source before this succeeds.
        /// Supply exactly one explicit pair for every seat endpoint and ghost driver, including
        /// retained identities. Player/actor rigs and ghosts are retained, not recreated here.
        /// Caller must suspend external player, AI, IK and ghost writers for the entire handoff;
        /// this API suspends the two carts only. Matching readbacks do not prove external quiescence.
        /// Does not move FPC/camera, replay enter/exit, grant authority, or certify full migration.
        /// Dry carts with quiet engine/wheel audio only. Radio station/play state is restarted
        /// through native lifecycle calls; pending IK transitions, actor replacement and opaque
        /// driver subscribers refuse before native mutation.
        /// After success: retire source, bind THIS replacement, restore values, resume saving,
        /// then resume writers. Before retirement TryCancel restores the original occupants.
        /// A nonnull failed handoff must remain quarantined; never retry a native mutation.
        /// </summary>
        public static bool TryPrepareOccupiedCartSaveHandoff(BoltEntity source, BoltEntity replacement,
            IReadOnlyList<VehicleOccupantReplacement> replacements,
            out CartSaveHandoff handoff, out string detail)
        {
            handoff = null;
            detail = "Occupied cart preparation requires the selected game thread without reentry.";
            if (!Enter()) return false;
            CartOccupants occupants = null;
            try
            {
                RequireContext();
                var from = new Binding(source);
                var to = new Binding(replacement);
                Require(from.Entity.isOwner && to.Entity.isOwner && from.Id != to.Id
                    && from.Prefab == to.Prefab && from.Cart != null && to.Cart != null,
                    "An occupied cart and a distinct new owned cart of the exact prefab are required.");
                CartWriterHooks.RequireInitialized(from.Cart);
                CartWriterHooks.RequireInitialized(to.Cart);
                RequireEmptySaveHandoffCart(to);
                Require(global::Sons.Inventory.WorldItemManager.TryGetInstance(out var manager) && Live(manager),
                    "The local world-item registry is unavailable.");
                occupants = new CartOccupants(from, to, replacements);
                occupants.Register();
                var pending = new CartSaveHandoff(from, manager);
                handoff = pending;
                pending.Prepare();
                detail = "Occupied bindings moved to the held replacement; source save reserved. Retire only the source cart, then bind the exact prepared replacement.";
                return true;
            }
            catch (Exception error)
            {
                if (handoff == null) occupants?.Unregister();
                else handoff.QuarantineNativeWriters();
                detail = Failure(handoff == null ? "Occupied cart preparation rejected"
                    : "Occupied cart preparation unconfirmed; keep both carts held", error);
                return false;
            }
            finally { _busy = false; }
        }

        // Only explicit leases relax the empty-cart guard. An occupied flag is never permission.
        private static readonly Dictionary<IntPtr, CartOccupants> OccupiedCartHandoffs = new();

        private static CartOccupants OccupantsFor(GolfCartController cart)
            => cart is not null && OccupiedCartHandoffs.TryGetValue(cart.Pointer, out var value) ? value : null;

        /// <summary>
        /// Captures a participant with an explicit retained-rig player identity map. Both player
        /// attachments must still exist at capture. Other occupant writers remain caller-owned.
        /// Unchanged occupants and the retained ghost must be included as identity pairs.
        /// </summary>
        public static bool TryCaptureOccupiedCartParticipant(BoltEntity original,
            IReadOnlyList<VehicleOccupantReplacement> replacements,
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
                occupants = new CartOccupants(binding, null, replacements);
                occupants.Register();
                participant = new CartOccupiedParticipant(binding);
                detail = "Exact occupant map and retained local references captured without native mutation.";
                return true;
            }
            catch (Exception error)
            {
                occupants?.Unregister();
                detail = Failure("Mapped cart participant capture rejected", error);
                return false;
            }
            finally { _busy = false; }
        }

        private sealed partial class CartOccupants
        {
            internal readonly Binding Source;
            internal Binding Target;
            private readonly Seat[] _seats = new Seat[4];
            private readonly Dictionary<ulong, Endpoint> _endpoints = new();
            private readonly HashSet<ulong> _used = new();
            private readonly CartConnectionsSnapshot _connections;
            private CartConnectionsSnapshot _empty;
            private readonly List<CartIkBinding> _ik = new();
            private readonly CartRadioBinding _sourceRadio;
            private CartRadioBinding _targetRadio;
            private readonly PlayerGolfCartAction _localPlayer;
            private readonly Il2CppObjectBase _playerState;
            private readonly GameObject _inputHolder;
            private readonly bool _inputsSet;
            private readonly Component _originalFpc;
            private readonly Transform _originalCamera;
            private readonly BoltEntity _localEntity;
            private readonly Endpoint _localEndpoint;
            private readonly Transform _localRoot;
            private bool _moved;
            private bool _parked;
            private bool _uncertain;
            private bool _writingSeats, _wroteSeats;
            private bool _targetSaveActive;
            private readonly UnityEngine.Vector3 _sourcePosition, _sourceScale;
            private readonly UnityEngine.Quaternion _sourceRotation;

            internal CartOccupants(Binding source, Binding target,
                IReadOnlyList<VehicleOccupantReplacement> replacements)
            {
                Source = source; Target = target;
                _sourcePosition = source.Entity.transform.position; _sourceRotation = source.Entity.transform.rotation;
                _sourceScale = source.Entity.transform.localScale;
                if (target != null)
                {
                    _targetSaveActive = target.Saver._saveActive;
                    Require(_sourcePosition == target.Entity.transform.position && _sourceRotation == target.Entity.transform.rotation
                        && _sourceScale == target.Entity.transform.localScale,
                        "The precreated replacement must already have the source cart's exact world pose and scale.");
                }
                Require(replacements != null && replacements.Count > 0 && replacements.Count <= 5,
                    "The exact bounded seat/ghost-driver replacement map is required.");
                var destinations = new HashSet<ulong>();
                foreach (var pair in replacements)
                {
                    Require(pair != null, "A replacement-map entry is null.");
                    var endpoint = new Endpoint(pair.Original, pair.Replacement);
                    Require(_endpoints.TryAdd(endpoint.OldId, endpoint) && destinations.Add(endpoint.NewId),
                        "Duplicate source or destination occupant identity.");
                    Require(endpoint.OldId != source.Id && endpoint.OldId != target?.Id
                        && endpoint.NewId != source.Id && endpoint.NewId != target?.Id,
                        "Cart identities cannot be seat occupants.");
                }
                _connections = new CartConnectionsSnapshot(source.Cart);
                if (target != null) _empty = new CartConnectionsSnapshot(target.Cart);
                Require(CartConnections(source.Cart) != VehiclePreservationBlockers.LocalConnectionUnavailable,
                    "Occupied cart connection arrays are unavailable.");
                Require(source.Cart._pendingDriverIkTargetsController == null
                    && source.Cart._driverConnectIkCoroutine == null, "A driver IK transition is pending.");
                int localPlayers = 0;
                var occupiedIds = new HashSet<ulong>();
                for (int i = 0; i < 4; i++)
                {
                    ulong id = source.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                    Require(id == 0 || occupiedIds.Add(id), "An occupant identity is assigned to multiple physical seats.");
                    var endpoint = id == 0 ? null : Use(id);
                    var seat = _seats[i] = new Seat(source, target, i, endpoint);
                    if (seat.Player is not null)
                    {
                        Require(++localPlayers == 1, "Multiple local player actions are ambiguous.");
                        _localPlayer = seat.Player;
                        Require(seat.Player._state != null && seat.Player._state.IsConnected
                            && seat.Player._state.ConnectedSlotIndex == i
                            && seat.Player._state.ConnectedGolfCartController?.Pointer == source.Cart.Pointer,
                            "The original local player's connected state does not match its seat.");
                        Require(seat.Player._state.Definition?.Pointer == source.Cart._definition?.Pointer
                            && (target == null || source.Cart._definition?.Pointer == target.Cart._definition?.Pointer),
                            "Local action and replacement definitions differ.");
                        if (i == 0)
                        {
                            var driver = seat.Player.TryCast<PlayerGolfCartDriverAction>();
                            Require(driver != null && driver._driverConnectIkCoroutine == null,
                                "The local driver is not settled or has a pending IK coroutine.");
                        }
                    }
                    if (seat.Actor is not null)
                    {
                        Require(i > 0 && seat.Actor._actorNetworked != null
                            && seat.Actor._actorNetworked._entity?.Pointer == endpoint?.Original.Pointer
                            && endpoint.Original.Pointer == endpoint.Replacement.Pointer,
                            "Kelvin/actor seats require the exact retained native actor and network lifetime.");
                    }
                }
                var ghost = _connections.OwnedGhostEntity ?? _connections.ExternalGhostEntity;
                if (ghost is not null)
                {
                    Require(!ghost.transform.IsChildOf(source.Entity.transform),
                        "A retained ghost must not be destroyed as a child of the source cart.");
                    var endpoint = Use(ghost.networkId.PackedValue);
                    Require(endpoint.Original.Pointer == ghost.Pointer && endpoint.Replacement.Pointer == ghost.Pointer,
                        "Ghost replacement/ownership has an independent lifecycle; retain its exact live identity.");
                    Require(_seats[0].Endpoint != null && _seats[0].Endpoint.Original.Pointer == ghost.Pointer,
                        "The physical driver seat must name this exact cached ghost, not its player or another ghost.");
                    Require(ghost.TryFindState<IGolfCartState>(out var state) && state.ConnectedSlots.Length == 4,
                        "Driver ghost state is unavailable.");
                    ulong driver = state.ConnectedSlots.GetValueNetworkId(0).PackedValue;
                    Require(driver != 0 && occupiedIds.Add(driver), "The ghost driver is missing or also occupies a physical passenger seat.");
                    var player = Use(driver);
                    Require(player.Original.TryFindState<IPlayerState>(out _) && player.Replacement.TryFindState<IPlayerState>(out _),
                        "Ghost driver endpoints must both be player states.");
                    Require(player.OldId == player.NewId || ghost.isOwner || target == null,
                        "Only the ghost's real owner can rewrite its driver identity.");
                    for (int i = 1; i < 4; i++)
                        Require(state.ConnectedSlots.GetValueNetworkId(i).PackedValue == 0,
                            "Unexpected occupied ghost passenger slot.");
                    Ghost = ghost; GhostState = state; GhostDriver = player;
                }
                Require(_used.Count == _endpoints.Count, "The supplied map contains unrelated occupant identities.");
                Require(_seats[0].Endpoint == null || Ghost != null,
                    "A raw driver slot must resolve through its exact ghost, not be guessed to be a player.");
                Require(Ptr(source.Cart._localPlayerDriverAction) == (_seats[0].Player is null
                    ? IntPtr.Zero : Ptr(source.Cart._localConnectedActions[0])), "Local driver cache disagrees with the exact seat action.");
                RequireActorReservations();
                _sourceRadio = new CartRadioBinding(source.Cart);
                if (target != null)
                {
                    _targetRadio = new CartRadioBinding(target.Cart);
                    _sourceRadio.RequireCompatible(_targetRadio);
                }
                if (_connections.CurrentIk is not null) _ik.Add(new CartIkBinding(_connections.CurrentIk, source, target));
                if (_connections.ExternalIk is not null && _connections.ExternalIk.Pointer != _connections.CurrentIk?.Pointer)
                    _ik.Add(new CartIkBinding(_connections.ExternalIk, source, target));
                if (_localPlayer is not null)
                {
                    Endpoint localEndpoint = null;
                    for (int i = 0; i < 4; i++)
                        if (_seats[i].Player?.Pointer == _localPlayer.Pointer)
                            localEndpoint = i == 0 ? GhostDriver : _seats[i].Endpoint;
                    _localEntity = TheForest.Utils.LocalPlayer.Entity;
                    _localEndpoint = localEndpoint;
                    _localRoot = TheForest.Utils.LocalPlayer.Transform;
                    // A server's remote driver is NOT its host LocalPlayer. Only the action on
                    // this exact retained local rig may use the local controls/camera sentinel.
                    Require(localEndpoint != null && Live(_localEntity) && Live(_localRoot)
                        && (SameEntity(_localEntity, localEndpoint.Original) || SameEntity(_localEntity, localEndpoint.Replacement))
                        && _localPlayer.transform.IsChildOf(_localRoot)
                        && _localEntity.gameObject.Pointer == TheForest.Utils.LocalPlayer.GameObject?.Pointer,
                        "The local seat action does not belong to the exact local occupant; host input cannot drive a remote player.");
                    _playerState = _localPlayer._state;
                    _inputHolder = _localPlayer._inputStateHolder;
                    _inputsSet = _localPlayer._hasSetupInputs;
                    _originalFpc = TheForest.Utils.LocalPlayer.FpCharacter;
                    _originalCamera = TheForest.Utils.LocalPlayer.MainCamTr;
                    Require(Live(_originalFpc) && Live(_originalCamera), "Original controls/camera are unavailable.");
                }
                RequireUnmoved();
            }

            private readonly BoltEntity Ghost;
            private readonly IGolfCartState GhostState;
            private readonly Endpoint GhostDriver;

            private Endpoint Use(ulong id)
            {
                Require(_endpoints.TryGetValue(id, out var endpoint), "The exact raw seat/driver identity is missing from the replacement map.");
                _used.Add(id);
                return endpoint;
            }

            internal void Register()
            {
                Require(!OccupiedCartHandoffs.ContainsKey(Source.Cart.Pointer)
                    && (Target == null || !OccupiedCartHandoffs.ContainsKey(Target.Cart.Pointer)), "A cart already has an occupant lease.");
                OccupiedCartHandoffs.Add(Source.Cart.Pointer, this);
                if (Target != null) OccupiedCartHandoffs.Add(Target.Cart.Pointer, this);
            }

            internal void Unregister()
            {
                if (ReferenceEquals(OccupantsFor(Source.Cart), this)) OccupiedCartHandoffs.Remove(Source.Cart.Pointer);
                if (Target != null && ReferenceEquals(OccupantsFor(Target.Cart), this)) OccupiedCartHandoffs.Remove(Target.Cart.Pointer);
            }

            internal void RequireCallbacks(GolfCartController cart)
            {
                Require(!_uncertain, "An occupant mutation is unconfirmed; keep both carts quarantined.");
                (cart.Pointer == Source.Cart.Pointer ? _sourceRadio : _targetRadio).RequireCallbacks(cart);
            }

            internal void RequireCart(Binding binding)
            {
                Require(!_uncertain && (binding.Cart.Pointer == Source.Cart.Pointer || binding.Cart.Pointer == Target?.Cart.Pointer),
                    "The occupied cart lease is not current.");
                if (_moved) RequireMoved();
                else if (_parked) RequireParked();
                else RequireUnmoved();
            }

            private void RequireParticipants()
            {
                foreach (var endpoint in _endpoints.Values) endpoint.RequireCurrent(_moved || _parked);
                if (Ghost is not null)
                    Require(Live(Ghost) && Ghost.isAttached && GhostState != null
                        && (GhostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == (_moved ? GhostDriver.NewId : GhostDriver.OldId)
                            || (_parked && !Ghost.isOwner && GhostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == GhostDriver.NewId)),
                        "Ghost driver identity changed during the occupant handoff.");
                if (_localPlayer is not null)
                    Require(Live(_localPlayer) && _localPlayer._state?.Pointer == _playerState.Pointer
                        && _localPlayer._inputStateHolder?.Pointer == _inputHolder?.Pointer
                        && _localPlayer._hasSetupInputs == _inputsSet
                        && (SameEntity(TheForest.Utils.LocalPlayer.Entity, _localEntity)
                            || ((_moved || _parked) && SameEntity(TheForest.Utils.LocalPlayer.Entity, _localEndpoint.Replacement)))
                        && TheForest.Utils.LocalPlayer.Transform?.Pointer == _localRoot.Pointer
                        && _localPlayer.transform.IsChildOf(_localRoot)
                        && TheForest.Utils.LocalPlayer.FpCharacter?.Pointer == _originalFpc.Pointer
                        && TheForest.Utils.LocalPlayer.MainCamTr?.Pointer == _originalCamera.Pointer,
                        "Original player action/input holder, controls or camera changed.");
            }

            private void RequireActorReservations()
            {
                var before = Source.Cart._robbyStimuli;
                var after = Target?.Cart._robbyStimuli;
                if (Target == null)
                {
                    if (before is null) return;
                    Require(Live(before) && before._seats != null && before._seats.Length <= 4,
                        "AI seat reservations are unavailable.");
                    foreach (var reservation in before._seats)
                    {
                        Require(reservation != null, "AI seat reservation is unavailable.");
                        if (reservation._actor is null) continue;
                        bool captured = false;
                        foreach (var seat in _seats) captured |= seat.Actor?.Pointer == reservation._actor.Pointer;
                        Require(captured, "An unmodeled AI reservation references the source cart.");
                    }
                    return;
                }
                Require((before is null) == (after is null), "Replacement AI stimulus topology differs.");
                if (before is null) return;
                Require(Live(before) && Live(after) && before._seats != null && after._seats != null
                    && before._seats.Length == after._seats.Length && before._seats.Length <= 4,
                    "AI seat reservations are unavailable.");
                for (int i = 0; i < before._seats.Length; i++)
                {
                    Require(before._seats[i] != null && after._seats[i] != null && after._seats[i]._actor is null,
                        "The replacement has an unmodeled AI reservation.");
                    var actor = before._seats[i]._actor;
                    if (actor is null) continue;
                    bool retained = false;
                    foreach (var seat in _seats) retained |= seat.Actor?.Pointer == actor.Pointer;
                    Require(retained, "An approaching or external actor holds an unmodeled source-cart reservation.");
                }
            }

            private void RequireUnmoved()
            {
                Require(Source.Matches() && (Target == null || Target.Matches()), "A cart lifetime changed before occupant transfer.");
                _connections.RequireCurrent(Source.Cart);
                if (Target != null) _empty.RequireCurrent(Target.Cart);
                RequireActorReservations();
                // Constructor validates participants before the local input sentinel is assigned.
                foreach (var endpoint in _endpoints.Values) endpoint.RequireCurrent();
                for (int i = 0; i < 4; i++) _seats[i].RequireCurrent(Source.Cart, false);
                foreach (var ik in _ik) ik.RequireCurrent(false);
            }

            internal void ParkBeforeRetirement()
            {
                Require(Target == null && !_parked && !_moved, "The source-only occupant lease is not fresh.");
                RequireUnmoved(); RequireParticipants();
                Require(Source.Entity.isFrozen && !Source.Entity.gameObject.activeSelf,
                    "Suspend the original cart before parking retained occupants.");
                try
                {
                    foreach (var seat in _seats) seat.ParkPlayerReferences();
                    foreach (var ik in _ik) ik.Park();
                    _parked = true;
                    RequireParked();
                }
                catch { _uncertain = true; throw; }
            }

            internal void RequireParked()
            {
                Require(_parked && !_moved && !_uncertain, "The retained occupant set is not confirmed parked.");
                RequireParticipants();
                foreach (var seat in _seats) seat.RequireRetained(Source.Cart);
                foreach (var ik in _ik) ik.RequireCurrent(false);
                if (Target != null) Require(Target.Matches(), "The staged replacement attachment changed.");
                if (Live(Source.Entity))
                {
                    Require(Source.Matches(), "The original cart attachment changed before retirement.");
                    _connections.RequireCurrent(Source.Cart);
                    for (int i = 0; i < 4; i++)
                        Require(Source.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue == (_seats[i].Endpoint?.OldId ?? 0),
                            "Original replicated seat identities changed while parked.");
                }
            }

            internal void BindAfterRetirement(Binding target)
            {
                RequireParked();
                Require((Target == null || Target.Entity.Pointer == target.Entity.Pointer) && !Live(Source.Entity)
                    && target.Id != Source.Id && target.Prefab == Source.Prefab,
                    "Bind the exact new cart only after ordinary source retirement.");
                Require(target.Entity.isFrozen && !target.Entity.gameObject.activeSelf,
                    "The replacement must be held before binding retained occupants.");
                var occupants = OccupantsFor(target.Cart);
                Require(occupants == null || ReferenceEquals(occupants, this),
                    "Replacement already has an occupant lease.");
                // All target paths are resolved from cached source schema, not the destroyed source.
                foreach (var seat in _seats) seat.BindTarget(target);
                foreach (var ik in _ik) ik.BindTarget(target);
                _targetRadio = new CartRadioBinding(target.Cart);
                _sourceRadio.RequireCompatible(_targetRadio);
                _empty ??= new CartConnectionsSnapshot(target.Cart, true);
                _empty.RequireCurrent(target.Cart);
                Require(Ghost is null || GhostDriver.OldId == GhostDriver.NewId || Ghost.isOwner
                    || GhostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == GhostDriver.NewId,
                    "Wait for the retained ghost owner's exact driver remap before proxy repair.");
                for (int i = 0; i < 4; i++)
                {
                    ulong id = target.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                    ulong expected = _seats[i].Endpoint?.NewId ?? 0;
                    Require(target.Entity.isOwner ? id == 0 || id == expected : id == expected,
                        "Replacement seat IDs are not the exact captured IDs; wait for authoritative state before repair.");
                    Require(!target.Cart._isLocalConnectedSlots[i] && target.Cart._localConnectedActions[i] == null
                        && target.Cart._localConnectedActors[i] is null && target.Cart._previousProcessedConnectedSlots[i] is null,
                        "Replacement already processed occupant connection callbacks; do not replay them.");
                }
                Target = target;
                OccupiedCartHandoffs[target.Cart.Pointer] = this;
                try
                {
                    foreach (var ik in _ik) ik.Move(true);
                    _connections.Apply(target.Cart, Remap);
                    _writingSeats = true; _wroteSeats = true;
                    for (int i = 0; i < 4; i++)
                    {
                        _seats[i].Rebind(target.Cart);
                        if (target.Entity.isOwner) target.CartState.ConnectedSlots[i] = _seats[i].Endpoint?.Replacement;
                    }
                    if (Ghost is not null && GhostDriver.OldId != GhostDriver.NewId && Ghost.isOwner)
                    {
                        GhostState.ConnectedSlots[0] = GhostDriver.Replacement;
                    }
                    _parked = false; _moved = true;
                    RequireMoved();
                }
                catch { _uncertain = true; throw; }
                finally { _writingSeats = false; }
            }

            internal void StageReplacement(Binding target)
            {
                RequireParked();
                Require(Target == null && !Live(Source.Entity) && target.Id != Source.Id && target.Prefab == Source.Prefab
                    && OccupantsFor(target.Cart) == null, "Replacement staging identity is invalid.");
                _targetRadio = new CartRadioBinding(target.Cart);
                _sourceRadio.RequireCompatible(_targetRadio);
                _empty = new CartConnectionsSnapshot(target.Cart, true);
                Target = target;
                OccupiedCartHandoffs.Add(target.Cart.Pointer, this);
            }

            internal void QuarantineStagedReplacement()
            {
                _uncertain = true;
                if (Target == null) return;
                try { if (Live(Target.Entity) && Target.Entity.isAttached) Target.Entity.Freeze(true); } catch (Exception) { }
                try { if (Live(Target.Entity)) Target.Entity.gameObject.SetActive(false); } catch (Exception) { }
                try { if (Live(Target.Saver)) Target.Saver.SetSaveActive(false); } catch (Exception) { }
            }

            internal bool IsParked => _parked;
            internal CartRadioValues RadioValues => _sourceRadio.Values;

            internal IReadOnlyList<VehicleRetainedOccupant> RetainedOccupants()
            {
                var values = new List<VehicleRetainedOccupant>();
                for (int i = 0; i < 4; i++)
                {
                    var seat = _seats[i];
                    if (seat.Endpoint == null) continue;
                    values.Add(new(i, seat.Endpoint.OldId, i == 0 ? GhostDriver?.OldId ?? 0
                        : seat.PlayerNetworkId,
                        seat.Player, seat.Actor, i == 0 ? Ghost : null));
                }
                return values.AsReadOnly();
            }

            internal static CartRadioValues CaptureRadio(Binding binding) => new CartRadioBinding(binding.Cart).Values;

            internal void PauseSourceRadio()
            {
                try { _sourceRadio.Pause(); }
                catch { _uncertain = true; throw; }
            }

            internal void ResumeRadio(bool replacement)
            {
                try { (replacement ? _targetRadio : _sourceRadio).RestoreFrom(_sourceRadio,
                    replacement ? Remap : value => value); }
                catch { _uncertain = true; throw; }
            }

            internal void CancelParked()
            {
                RequireParked();
                Require(Source.Matches() && Target == null, "A retired source cannot be cancelled.");
                foreach (var endpoint in _endpoints.Values) endpoint.RequireCurrent();
                Require(Ghost is null || GhostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == GhostDriver.OldId,
                    "Cancel the ghost-driver remap before cancelling parked occupants.");
                try
                {
                    foreach (var ik in _ik) ik.Move(false);
                    foreach (var seat in _seats) seat.RestorePlayerReferences();
                    _parked = false;
                    RequireUnmoved(); RequireParticipants();
                }
                catch { _uncertain = true; throw; }
            }

            internal void Move()
            {
                RequireUnmoved(); RequireParticipants();
                Require(Source.Entity.isFrozen && Target.Entity.isFrozen
                    && !Source.Entity.gameObject.activeSelf && !Target.Entity.gameObject.activeSelf,
                    "Both cart writers must be suspended before occupant mutation.");
                try
                {
                    // All mutations are main-thread, synchronous and read back before returning.
                    // No Initialize/Disconnect/SetupIkChains call: those reset player input/IK state.
                    foreach (var ik in _ik) ik.Move(true);
                    _connections.Apply(Target.Cart, Remap);
                    _empty.Apply(Source.Cart, value => value);
                    _writingSeats = true; _wroteSeats = true;
                    for (int i = 0; i < 4; i++)
                    {
                        _seats[i].Rebind(Target.Cart);
                        Target.CartState.ConnectedSlots[i] = _seats[i].Endpoint?.Replacement;
                        Source.CartState.ConnectedSlots[i] = null;
                    }
                    if (Ghost is not null && GhostDriver.OldId != GhostDriver.NewId)
                        GhostState.ConnectedSlots[0] = GhostDriver.Replacement;
                    _moved = true;
                    RequireMoved();
                }
                catch
                {
                    // Native setters can throw after applying a write. Do not guess which half
                    // committed, replay enter/exit, or expose either cart as ready for retirement.
                    _uncertain = true;
                    throw;
                }
                finally { _writingSeats = false; }
            }

            private BoltEntity Remap(BoltEntity value)
            {
                if (value is null) return null;
                foreach (var endpoint in _endpoints.Values)
                    if (value.Pointer == endpoint.Original.Pointer) return endpoint.Replacement;
                throw new InvalidOperationException("A cached occupant is outside the exact map.");
            }

            internal void RequireMoved()
            {
                Require(_moved && !_uncertain && Target.Matches(), "Occupied replacement is not confirmed.");
                RequireParticipants();
                _empty.RequireStorage(Target.Cart);
                _connections.RequireMapped(Target.Cart, Remap);
                for (int i = 0; i < 4; i++) _seats[i].RequireCurrent(Target.Cart, true);
                foreach (var ik in _ik) ik.RequireCurrent(true);
                if (Live(Source.Entity))
                {
                    Require(Source.Matches(), "Source attachment changed before retirement.");
                    _connections.RequireStorage(Source.Cart);
                    _empty.RequireCurrent(Source.Cart);
                    for (int i = 0; i < 4; i++)
                        Require(Source.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue == 0,
                            "The source acquired a new seat occupant before retirement.");
                }
            }

            internal void RollBack()
            {
                RequireMoved();
                Require(Source.Matches() && Source.Entity.isFrozen && !Source.Entity.gameObject.activeSelf,
                    "Occupants cannot roll back after source retirement or resumption.");
                foreach (var endpoint in _endpoints.Values) endpoint.RequireCurrent();
                try
                {
                    _writingSeats = true;
                    foreach (var ik in _ik) ik.Move(false);
                    _connections.Apply(Source.Cart, value => value);
                    _empty.Apply(Target.Cart, value => value);
                    for (int i = 0; i < 4; i++)
                    {
                        _seats[i].Rebind(Source.Cart);
                        Source.CartState.ConnectedSlots[i] = _seats[i].Endpoint?.Original;
                        Target.CartState.ConnectedSlots[i] = null;
                    }
                    if (Ghost is not null && GhostDriver.OldId != GhostDriver.NewId)
                        GhostState.ConnectedSlots[0] = GhostDriver.Original;
                    _moved = false;
                    RequireUnmoved(); RequireParticipants();
                }
                catch { _uncertain = true; throw; }
                finally { _writingSeats = false; }
            }

            internal void RebindTarget(Binding rebound)
            {
                Require(rebound.Entity.Pointer == Target.Entity.Pointer && rebound.Id == Target.Id,
                    "The occupied replacement attachment differs.");
                Target = rebound;
            }

            internal void RestoreCancelledTargetSaving() => Target.Saver.SetSaveActive(_targetSaveActive);

            internal bool OwnSeatNotification(Binding binding)
            {
                if (_uncertain || !_wroteSeats) return false;
                if (binding.Cart.Pointer != Source.Cart.Pointer && binding.Cart.Pointer != Target?.Cart.Pointer) return false;
                if (_writingSeats) return true;
                bool target = binding.Entity.Pointer == Target?.Entity.Pointer;
                for (int i = 0; i < 4; i++)
                {
                    ulong expected = target == _moved && _seats[i].Endpoint != null
                        ? (_moved ? _seats[i].Endpoint.NewId : _seats[i].Endpoint.OldId) : 0;
                    if (binding.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue != expected) return false;
                }
                return true;
            }

            internal bool IsRetainedIkNode(Transform node)
            {
                foreach (var ik in _ik) if (ik.Contains(node)) return true;
                return false;
            }

            internal bool MatchesRestoredReferences(Binding binding)
            {
                if (_moved && binding.Cart.Pointer == Target.Cart.Pointer) { RequireMoved(); return true; }
                if (_parked && binding.Cart.Pointer == Source.Cart.Pointer) { RequireParked(); return true; }
                if (!_moved && binding.Cart.Pointer == Source.Cart.Pointer) { RequireUnmoved(); return true; }
                return false;
            }

            private sealed class Endpoint
            {
                internal readonly BoltEntity Original, Replacement;
                internal readonly ulong OldId, NewId;
                private readonly IntPtr _oldBacking, _newBacking, _oldState, _newState;
                private readonly Entity _oldAttachment;
                private readonly GameObject _oldRoot;
                private readonly Transform _retainedPlayerTransform;
                private readonly int _prefab;
                internal Endpoint(BoltEntity original, BoltEntity replacement)
                {
                    Require(Live(original) && Live(replacement) && original.isAttached && replacement.isAttached,
                        "Every occupant-map endpoint must be an exact live attachment.");
                    Original = original; Replacement = replacement;
                    OldId = original.networkId.PackedValue; NewId = replacement.networkId.PackedValue;
                    _prefab = original.prefabId.Value;
                    Require(OldId != 0 && NewId != 0 && replacement.prefabId.Value == _prefab,
                        "Occupant replacement prefab/identity differs.");
                    _oldBacking = original._entity.Pointer; _newBacking = replacement._entity.Pointer;
                    _oldAttachment = original._entity; _oldRoot = original.gameObject;
                    _oldState = original._entity.Serializer.Pointer; _newState = replacement._entity.Serializer.Pointer;
                    if (OldId != NewId)
                    {
                        Require(original.TryFindState<IPlayerState>(out var before)
                            && replacement.TryFindState<IPlayerState>(out var after)
                            && Live(before.Transform?.Simulate) && Live(after.Transform?.Simulate)
                            && before.Transform.Simulate.Pointer == after.Transform.Simulate.Pointer,
                            "A changed occupant ID requires an explicitly retained player rig. Actor/ghost recreation is not covered.");
                        _retainedPlayerTransform = before.Transform.Simulate;
                    }
                    RequireCurrent();
                }
                internal void RequireCurrent(bool moved = false)
                {
                    Require(Live(Replacement) && Replacement.isAttached && Replacement.networkId.PackedValue == NewId
                        && Replacement.prefabId.Value == _prefab && Replacement._entity?.Pointer == _newBacking
                        && Replacement._entity.Serializer?.Pointer == _newState
                        && SameEntity(BoltNetwork.FindEntity(new NetworkId(NewId)), Replacement),
                        "An occupant endpoint changed; do not resolve by prefab, name, or nearest player.");
                    if (!moved || OldId == NewId || (Live(Original) && Original.isAttached))
                        Require(Live(Original) && Original.isAttached && Original.networkId.PackedValue == OldId
                            && Original.prefabId.Value == _prefab && Original._entity?.Pointer == _oldBacking
                            && Original._entity.Serializer?.Pointer == _oldState
                            && SameEntity(BoltNetwork.FindEntity(new NetworkId(OldId)), Original),
                            "An original occupant attachment changed.");
                    else
                    {
                        // PlayerNetwork retirement detaches the old Bolt component while its
                        // original Unity rig survives. A live Unity object is not an attachment.
                        Require(_oldAttachment != null && !_oldAttachment.WasCollected
                            && _oldAttachment.Pointer == _oldBacking && !_oldAttachment.IsAttached
                            && BoltNetwork.FindEntity(new NetworkId(OldId)) == null
                            && BoltCore.FindEntity(new NetworkId(OldId)) == null
                            && (!Live(Original) || (Original._entity == null && !Original.isAttached
                                && Live(_oldRoot) && Original.gameObject.Pointer == _oldRoot.Pointer))
                            && Live(_retainedPlayerTransform)
                            && Replacement.TryFindState<IPlayerState>(out var state)
                            && state.Transform?.Simulate?.Pointer == _retainedPlayerTransform.Pointer,
                            "The exact retained player attachment has not retired or its original rig changed.");
                    }
                }
            }

            private sealed class Seat
            {
                internal readonly Endpoint Endpoint;
                internal readonly PlayerGolfCartAction Player;
                internal readonly VailActor Actor;
                internal readonly ulong PlayerNetworkId;
                private readonly VailActorGolfCartAction _actorAction;
                private readonly int _index;
                private readonly bool _local;
                private readonly IGolfCartAction _action;
                private readonly CartActorBinding _actorBindings;
                private readonly CartPlayerBinding _playerBindings;
                internal Seat(Binding source, Binding target, int index, Endpoint endpoint)
                {
                    var cart = source.Cart;
                    _index = index; Endpoint = endpoint;
                    if (index > 0 && endpoint != null && endpoint.Original.TryFindState<IPlayerState>(out _))
                        PlayerNetworkId = endpoint.OldId;
                    _local = cart._isLocalConnectedSlots[index];
                    _action = cart._localConnectedActions[index]; Actor = cart._localConnectedActors[index];
                    Player = _action?.TryCast<PlayerGolfCartAction>();
                    _actorAction = _action?.TryCast<VailActorGolfCartAction>();
                    Require(endpoint != null || (!_local && _action == null && Actor is null),
                        "Local occupancy has no settled replicated identity.");
                    Require(!_local || (Player != null ^ _actorAction != null), "Unknown or missing local seat action.");
                    Require(_local || (_action == null && Actor is null), "Unflagged local occupant state.");
                    Require((_actorAction is not null) == (Actor is not null), "Actor action and Kelvin reference disagree.");
                    if (Player is not null) _playerBindings = new CartPlayerBinding(Player, source, target);
                    if (_actorAction is not null)
                    {
                        Require(_actorAction._slotIndex == index && _actorAction._golfCartController?.Pointer == cart.Pointer
                            && Live(Actor) && Actor.GetComponentInChildren<VailActorGolfCartAction>(true)?.Pointer == _actorAction.Pointer,
                            "Actor action does not belong to the exact seated actor.");
                        _actorBindings = new CartActorBinding(Actor, source, target);
                    }
                    RequireCurrent(cart, false);
                }
                internal void Rebind(GolfCartController cart)
                {
                    if (Player is not null)
                    {
                        Player._state.ConnectedGolfCartController = cart;
                        _playerBindings.Move(cart);
                    }
                    if (_actorAction is not null)
                    {
                        _actorAction._golfCartController = cart;
                        _actorBindings.Move(cart);
                    }
                }
                internal void BindTarget(Binding target)
                {
                    _actorBindings?.BindTarget(target);
                    _playerBindings?.BindTarget(target);
                    if (Player is not null)
                        Require(Player._state.Definition?.Pointer == target.Cart._definition?.Pointer,
                            "Replacement cart definition differs from the retained local action.");
                }
                internal void ParkPlayerReferences() => _playerBindings?.Park();
                internal void RestorePlayerReferences() => _playerBindings?.Move(_playerBindings.SourceCart);
                internal void RequireRetained(GolfCartController original)
                {
                    if (Player is not null)
                        Require(Live(Player) && Player._state.IsConnected && Player._state.ConnectedSlotIndex == _index
                            && Player._state.ConnectedGolfCartController?.Pointer == original.Pointer,
                            "Retained player action changed while awaiting replacement.");
                    if (_actorAction is not null)
                        Require(Live(_actorAction) && Live(Actor) && _actorAction._slotIndex == _index
                            && _actorAction._golfCartController?.Pointer == original.Pointer,
                            "Retained actor action changed while awaiting replacement.");
                    _actorBindings?.RequireCurrent(original, true);
                    _playerBindings?.RequireCurrent(original, true);
                }
                internal void RequireCurrent(GolfCartController cart, bool mapped)
                {
                    Require(cart.BoltEntityState.ConnectedSlots.GetValueNetworkId(_index).PackedValue
                        == (Endpoint == null ? 0 : mapped ? Endpoint.NewId : Endpoint.OldId)
                        && cart._isLocalConnectedSlots[_index] == _local
                        && Ptr(cart._localConnectedActions[_index]) == Ptr(_action)
                        && Ptr(cart._localConnectedActors[_index]) == Ptr(Actor), "Seat identity/action binding changed.");
                    if (Player is not null)
                        Require(Live(Player) && Player._state.IsConnected && Player._state.ConnectedSlotIndex == _index
                            && Player._state.ConnectedGolfCartController?.Pointer == cart.Pointer,
                            "Original player action was disconnected or rebound elsewhere.");
                    if (_actorAction is not null)
                        Require(Live(_actorAction) && Live(Actor) && _actorAction._slotIndex == _index
                            && _actorAction._golfCartController?.Pointer == cart.Pointer,
                            "Retained Kelvin action/stimulus was disconnected or rebound elsewhere.");
                    _actorBindings?.RequireCurrent(cart);
                    _playerBindings?.RequireCurrent(cart);
                }
            }

            private static IntPtr Ptr(Il2CppObjectBase value) => value?.Pointer ?? IntPtr.Zero;
        }
    }
}
