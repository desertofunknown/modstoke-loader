// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json;
using Bolt;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed record CartTransferData(int Version, CartSnapshotData Cart,
            CartSaveTransfer Save, CartLifecycleData Lifecycle);

        /// <summary>
        /// One transaction per instance. Qualified, settled occupied GolfCarts only.
        /// Player seats additionally require a transaction-bound player lifecycle provider.
        /// Requires the coordinator's deferred-replacement and aggregate repair lifecycle.
        /// Unknown inbound Bolt references are rejected rather than guessed to be entity IDs.
        /// </summary>
        public sealed class GolfCartMigrationAdapter : IBoltMigrationAdapter,
            IBoltMigrationPlanParticipant, IBoltMigrationReplicaLifecycle,
            IBoltMigrationReplacementAdmission, IBoltMigrationReplacementIdentity,
            IBoltMigrationAdmission
        {
            public Guid ContractId => new("415de540-9d78-4449-a3e5-4e0158f97ca5");
            public uint ContractVersion => 1;
            private Binding _candidate;
            private CartOccupants _occupants;
            private CartWriterHold _source, _replacement;
            private CartOccupantWriterHold _occupantWriters;
            private readonly ICartPlayerMigrationLifecycle _playerLifecycle;
            private CartPlayerMigration _players;
            private CartMigrationSaveCustody _save;
            private CartReplacementGate _birth;
            private CartTransferData _wire;
            private BoltMigrationPlan _plan;
            private BoltMigrationRole _role;
            private bool _held, _restored, _resumed, _cancelled, _uncertain;
            private bool _holdAttempted, _radioPauseAttempted, _radioPaused, _parkAttempted, _parked;
            private bool _sourceReleaseAttempted, _replacementReleaseAttempted;

            public GolfCartMigrationAdapter() { }

            public GolfCartMigrationAdapter(ICartPlayerMigrationLifecycle playerLifecycle)
                => _playerLifecycle = playerLifecycle ?? throw new ArgumentNullException(nameof(playerLifecycle));

            /// <summary>
            /// Source/observer: call from the authenticated Recreated receipt before admission.
            /// Repeating the exact ID after local retirement may hold the authorized replacement.
            /// Already processed connections remain a rejection, never a disconnect/reconnect.
            /// </summary>
            public bool TryAuthorizeReplacementIdentity(ulong networkId, out string detail)
            {
                detail = "Replacement identity authorization requires a held source/observer.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireUsable();
                    Require(_held && _role != BoltMigrationRole.Destination && !_restored,
                        "Replacement identity authorization has the wrong role or phase.");
                    _players?.RequireHeld();
                    _birth.Authorize(networkId);
                    detail = "Exact replacement identity authorized; acquisition waits for local retirement and never selects by pose."; return true;
                }
                catch (Exception error) { Quarantine(); detail = Failure("Replacement identity authorization rejected", error); return false; }
                finally { _busy = false; }
            }

            public BoltMigrationAdmission ReadAdmission(BoltEntity original,
                BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail)
            {
                if (CanMigrate(original, out detail) && (snapshot == null || TryValidateSnapshot(snapshot, out detail)))
                    return BoltMigrationAdmission.Ready;
                return _uncertain || _cancelled || _resumed ? BoltMigrationAdmission.Rejected
                    : BoltMigrationAdmission.Pending;
            }

            public bool CanMigrate(BoltEntity entity, out string detail)
            {
                detail = "Cart preflight requires the selected game thread.";
                if (!Enter()) return false;
                CartOccupants temporary = null;
                try
                {
                    RequireContext(); RequireUsable();
                    if (_held)
                    {
                        Require(Live(entity) && entity.isAttached, "Held cart admission requires an exact live attachment.");
                        if (entity.networkId.PackedValue == _candidate.Id)
                        {
                            Require(SameEntity(entity, _candidate.Entity), "Original held cart identity differs.");
                            RequireHeldOriginal();
                            detail = "Exact cart and occupant preservation holds remain acquired.";
                            return true;
                        }
                        _occupantWriters.RequireHeld(); _save.RequireHeld(); _players?.RequireHeld();
                        if (_restored)
                        {
                            Require(SameEntity(entity, _replacement.Binding.Entity), "Restored cart identity differs.");
                            _replacement.RequireSuspended(); _occupants.RequireMoved();
                            detail = "Exact restored cart and occupant preservation holds remain acquired.";
                            return true;
                        }
                        _occupants.RequireParked();
                        Require(_birth.Read(entity, out detail) == BoltMigrationAdmission.Ready, detail);
                        Require(entity.prefabId.Value == _candidate.Prefab, "Replacement prefab differs.");
                        return true;
                    }
                    var binding = new Binding(entity);
                    Require(binding.Cart != null && entity.gameObject.activeInHierarchy && !entity.isFrozen,
                        "Only an active, settled physical GolfCart is admitted.");
                    Require(!CartDriverAuthorityHooks.IsAcquired(binding.Cart)
                        || (_playerLifecycle is CartPlayerMigrationRuntime runtime && runtime.CanYieldActiveAuthority(entity)),
                        "Existing authority must be the exact drained driver lease retained by this provider; other active or quarantined policies cannot migrate.");
                    RequireSettledBody(binding);
                    CartWriterHooks.RequireInitialized(binding.Cart);
                    CartOccupantWriterHooks.RequireReady();
                    temporary = new CartOccupants(binding, null, CaptureRetainedOccupantPairs(binding));
                    _ = CartPlayerMigration.Capture(temporary, _playerLifecycle);
                    temporary.Register();
                    _ = new CartWriterHold(binding, true);
                    _ = new CartOccupantWriterHold(temporary);
                    _ = new CartMigrationSaveCustody(binding);
                    _ = CaptureCartSnapshotData(binding);
                    Require(binding.Matches() && !_reentered, "Cart changed during admission.");
                    _candidate = binding;
                    detail = "Settled occupied cart, retained occupants, native writers and save custody preflighted.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Occupied GolfCart admission rejected", error); return false; }
                finally { temporary?.Unregister(); _busy = false; }
            }

            public bool TryBeginPreparation(BoltMigrationPlan plan, BoltMigrationRole role,
                ControlPeer peer, out string detail)
            {
                detail = "A current exact occupied-cart plan is required.";
                if (_candidate == null || _held || _uncertain || _plan.IsValid || !plan.IsValid
                    || plan.ContractId != ContractId || plan.ContractVersion != ContractVersion
                    || plan.OldNetworkId != _candidate.Id || plan.PrefabId != _candidate.Prefab || plan.StateTypeId != 233) return false;
                _plan = plan; _role = role;
                detail = "Exact cart transaction recorded without native mutation.";
                return true;
            }

            public bool TryPrepareReferences(ulong oldNetworkId, out IBoltMigrationReferenceRepair references,
                out string detail)
            {
                references = null;
                detail = "Read-only cart admission must precede reference capture.";
                if (_candidate == null || _candidate.Id != oldNetworkId || _held || _uncertain) return false;
                // The native cart caches are covered by CartOccupants. No other vanilla
                // inbound Bolt property has been qualified as a physical-cart identity.
                return BoltMigrationReferences.TryCaptureLoadedReferences(oldNetworkId,
                    (_, _, _) => false, out references, out detail);
            }

            public bool TryQuiesce(BoltEntity entity, out string detail)
                => HoldOriginal(entity, BoltMigrationRole.Source, out detail);

            public bool TryQuiesceReplica(BoltEntity original, BoltMigrationRole role, out string detail)
                => HoldOriginal(original, role, out detail);

            private bool HoldOriginal(BoltEntity entity, BoltMigrationRole role, out string detail)
            {
                detail = "Cart quiescence requires the exact prepared role and game thread.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext(); RequireUsable();
                    Require(_plan.IsValid && _role == role && !_held && _candidate.Matches()
                        && SameEntity(entity, _candidate.Entity), "Original cart preparation differs.");
                    RequireSettledBody(_candidate);
                    _occupants = new CartOccupants(_candidate, null, CaptureRetainedOccupantPairs(_candidate));
                    _players = CartPlayerMigration.Capture(_occupants, _playerLifecycle);
                    _occupants.Register();
                    _source = new CartWriterHold(_candidate, true);
                    _save = new CartMigrationSaveCustody(_candidate);
                    _occupantWriters = new CartOccupantWriterHold(_occupants);
                    _birth = new CartReplacementGate(_candidate);
                    attempted = true; _holdAttempted = true;
                    _occupantWriters.Acquire();
                    _players?.Acquire(_plan, role, _candidate);
                    _source.Suspend();
                    _radioPauseAttempted = true;
                    _occupants.PauseSourceRadio();
                    _radioPaused = true; _parkAttempted = true;
                    _occupants.ParkBeforeRetirement();
                    _parked = true;
                    _save.Reserve();
                    _birth.Arm();
                    _held = true;
                    RequireHeldOriginal();
                    detail = "Cart, retained occupants and original save custody held until cancellation or aggregate repair.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) Quarantine(); else _occupants?.Unregister();
                    detail = Failure(attempted ? "Cart pause unconfirmed; keep held" : "Cart pause rejected", error); return false;
                }
                finally { _busy = false; }
            }

            public bool TryCapture(BoltEntity entity, out byte[] payload, out string detail)
            {
                payload = null; detail = "Cart capture requires the held original owner.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireUsable(); RequireHeldOriginal();
                    Require(_role == BoltMigrationRole.Source && SameEntity(entity, _candidate.Entity)
                        && entity.isOwner, "Only the exact original owner may capture transfer values.");
                    var data = new CartTransferData(1, CaptureCartSnapshotData(_candidate), _save.Captured, _source.State.Export());
                    var bytes = EncodeTransfer(data);
                    RequireHeldOriginal();
                    Require(bytes.AsSpan().SequenceEqual(EncodeTransfer(new(1, CaptureCartSnapshotData(_candidate),
                        _save.Captured, _source.State.Export()))) && !_reentered, "Held cart changed during capture.");
                    if (_wire != null) Require(bytes.AsSpan().SequenceEqual(EncodeTransfer(_wire)), "Captured transfer changed.");
                    _wire = data; payload = bytes;
                    detail = "Independent occupied-cart, timer and save-custody payload captured.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart transfer capture rejected", error); return false; }
                finally { _busy = false; }
            }

            public bool TryValidateSnapshot(BoltMigrationSnapshot snapshot, out string detail)
            {
                detail = "Cart snapshot validation requires the selected game thread.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireUsable();
                    var data = Decode(snapshot);
                    Require(_candidate != null && data.Cart.OriginalNetworkId == _candidate.Id
                        && data.Cart.PrefabId == _candidate.Prefab, "Snapshot does not name the preflighted cart.");
                    if (_wire != null) Require(EncodeTransfer(data).AsSpan().SequenceEqual(EncodeTransfer(_wire)), "Transaction payload changed.");
                    if (!_held)
                    {
                        var local = CaptureCartSnapshotData(_candidate);
                        Require(Equal(local.Seats, data.Cart.Seats) && local.Radio.Present == data.Cart.Radio.Present
                            && local.Radio.IsOn == data.Cart.Radio.IsOn
                            && (!local.Radio.IsOn || local.Radio.TrackHash == data.Cart.Radio.TrackHash),
                            "Local exact occupants or radio intent differ from the source.");
                        new CartMigrationSaveCustody(_candidate).ValidateImport(data.Save);
                    }
                    else
                    {
                        _occupantWriters.RequireHeld(); _save.RequireHeld(); _players?.RequireHeld();
                        if (!_restored) _occupants.RequireParked(); else _occupants.RequireMoved();
                        if (Live(_candidate.Entity)) RequireHeldOriginal();
                    }
                    _wire = data;
                    detail = "Exact payload, retained local identities and save custody validated without resolving a retired source.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart transfer validation rejected", error); return false; }
                finally { _busy = false; }
            }

            public bool TryQuiesceReplacement(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
            {
                detail = "Replacement birth hold requires the destination game thread.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireUsable(); RequirePayload(snapshot);
                    Require(_held && !_restored && _role == BoltMigrationRole.Destination,
                        "Only the prepared destination may hold its new owner.");
                    _players?.RequireHeld();
                    Require(Live(replacement) && replacement.isAttached && replacement.isOwner,
                        "The exact new native owner is required before startup hold acquisition.");
                    // The session passes the verified return value from its single Instantiate.
                    // Do not choose an object inside an Attached callback: nested spawns are
                    // not identified by a thread-local creation scope alone.
                    _birth.Authorize(replacement.networkId.PackedValue);
                    _birth.Claim(replacement);
                    _birth.HoldPhysics();
                    detail = "Fresh replacement physics and writer callbacks held; ordinary native Start remains enabled.";
                    return true;
                }
                catch (Exception error) { Quarantine(); detail = Failure("Replacement hold unconfirmed", error); return false; }
                finally { _busy = false; }
            }

            public BoltMigrationAdmission ReadReplacementAdmission(BoltEntity replacement,
                BoltMigrationRole role, BoltMigrationSnapshot snapshot, out string detail)
            {
                detail = "Replacement admission requires the selected game thread.";
                if (!Enter()) return BoltMigrationAdmission.Rejected;
                try
                {
                    RequireContext(); RequireUsable(); RequirePayload(snapshot);
                    Require(_held && role == _role && !_restored, "Replacement phase or role differs.");
                    _occupantWriters.RequireHeld(); _save.RequireHeld(); _occupants.RequireParked(); _players?.RequireHeld();
                    var admission = _birth.Read(replacement, out detail);
                    if (admission != BoltMigrationAdmission.Ready) return admission;
                    if (role != BoltMigrationRole.Destination)
                    {
                        Require(replacement.TryFindState<IGolfCartState>(out var state) && state.ConnectedSlots.Length == 4,
                            "Replacement state schema differs.");
                        for (int i = 0; i < 4; i++)
                            if (state.ConnectedSlots.GetValueNetworkId(i).PackedValue != _wire.Cart.Seats[i].NetworkId)
                            { detail = "Waiting for exact authoritative seat replication."; return BoltMigrationAdmission.Pending; }
                    }
                    return BoltMigrationAdmission.Ready;
                }
                catch (Exception error) { detail = Failure("Replacement admission rejected", error); return BoltMigrationAdmission.Rejected; }
                finally { _busy = false; }
            }

            public bool TryRestore(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
                => Restore(replacement, snapshot, true, out detail);

            public bool TryRebindReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
            {
                if (_role != BoltMigrationRole.Destination) return Restore(replacement, snapshot, false, out detail);
                detail = "Destination rebind validation requires the selected game thread.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireUsable();
                    Require(_restored && SameEntity(_replacement.Binding.Entity, replacement), "Destination cart is not restored.");
                    RequirePayload(snapshot); _occupantWriters.RequireHeld(); _occupants.RequireMoved(); _players?.RequireHeld();
                    detail = "Destination already restored; retained occupants remain held.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Destination rebind validation rejected", error); return false; }
                finally { _busy = false; }
            }

            private bool Restore(BoltEntity replacement, BoltMigrationSnapshot snapshot, bool destination, out string detail)
            {
                detail = "Cart restoration requires the exact initialized replacement.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext(); RequireUsable(); RequirePayload(snapshot);
                    Require(_held && !_restored && destination == (_role == BoltMigrationRole.Destination)
                        && replacement.isOwner == destination, "Replacement restoration is single-use and role-bound.");
                    _occupantWriters.RequireHeld(); _save.RequireHeld(); _players?.RequireHeld();
                    Require(_birth.Read(replacement, out detail) == BoltMigrationAdmission.Ready, detail);
                    attempted = true;
                    _birth.RestorePhysicsBeforeHold();
                    var binding = new Binding(replacement);
                    _occupants.StageReplacement(binding);
                    _replacement = new CartWriterHold(binding, true);
                    _replacement.Suspend();
                    _birth.Release();
                    _occupants.BindAfterRetirement(binding);
                    if (destination)
                    {
                        var prepared = PrepareCartNetworkSeeds(binding, _wire.Cart.Transforms);
                        RestoreCart(binding.Cart, binding.CartState, new(_wire.Cart.Velocity, _wire.Cart.Steering,
                            _wire.Cart.Power, _wire.Cart.WheelRpms, _wire.Cart.AudioParameters, _wire.Cart.Drive));
                        RestoreBody(binding.Body, _wire.Cart.Body);
                        RestoreCartNetworkSeeds(binding, _wire.Cart.Transforms, prepared);
                        _replacement.ImportLocalState(_wire.Lifecycle);
                        var read = Capture(binding);
                        Require(read.Body == _wire.Cart.Body && read.GolfCart.Drive == _wire.Cart.Drive
                            && read.GolfCart.Velocity == _wire.Cart.Velocity && read.GolfCart.SteeringWheelRotation == _wire.Cart.Steering
                            && read.GolfCart.Power == _wire.Cart.Power && Equal(read.GolfCart.WheelRpms, _wire.Cart.WheelRpms)
                            && Equal(read.GolfCart.AudioParameters, _wire.Cart.AudioParameters), "Restored cart/body value readback differs.");
                    }
                    else _replacement.ImportLocalState(_source.State.Export() with
                        { Kinematic = _replacement.State.Export().Kinematic });
                    var rebound = _save.Bind(binding, _wire.Save, destination);
                    _replacement.Rebind(rebound); _occupants.RebindTarget(rebound);
                    _players?.Rebind(rebound);
                    _source.ReleaseRetired();
                    _replacement.RequireSuspended(); _occupants.RequireMoved(); _occupantWriters.RequireHeld();
                    _restored = true;
                    detail = "New cart restored, exact occupants rebound and destination-only save custody imported; all writers remain held.";
                    return true;
                }
                catch (Exception error)
                {
                    if (attempted) Quarantine();
                    detail = Failure(attempted ? "Cart restoration unconfirmed; do not replay" : "Cart restoration rejected", error); return false;
                }
                finally { _busy = false; }
            }

            public bool TryResume(BoltEntity entity, out string detail)
            {
                if (_candidate != null && SameEntity(entity, _candidate.Entity)) return Cancel(entity, out detail);
                return Resume(entity, out detail);
            }

            public bool TryResumeReplica(BoltEntity replacement, BoltMigrationSnapshot snapshot, out string detail)
            {
                try { RequirePayload(snapshot); }
                catch (Exception error) { detail = Failure("Cart resume payload rejected", error); return false; }
                return Resume(replacement, out detail);
            }

            private bool Resume(BoltEntity entity, out string detail)
            {
                detail = "Cart resumption requires aggregate repair authorization and the exact new cart.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext();
                    Require(!_uncertain && !_cancelled && _restored && SameEntity(entity, _replacement.Binding.Entity),
                        "Replacement cart is not restored.");
                    if (_resumed) { detail = "This exact replacement was already resumed."; return true; }
                    _occupantWriters.RequireHeld(); _occupants.RequireMoved(); _replacement.RequireSuspended(); _save.RequireHeld();
                    _players?.PrepareResume(_replacement.Binding);
                    attempted = true;
                    if (_players == null) { _save.ResumeSaving(); _replacement.SerializeAllowed = entity.isOwner; }
                    // New native instances were initialized by their own lifetime. Radio connects
                    // only now, after ordinary Start, and restarts through fresh FMOD instances.
                    _occupants.ResumeRadio(true);
                    _occupantWriters.Release();
                    _replacementReleaseAttempted = true;
                    _replacement.Resume(_replacement);
                    _occupants.Unregister();
                    _players?.Resume();
                    if (_players != null) { _save.ResumeSaving(); _replacement.SerializeAllowed = entity.isOwner; }
                    _resumed = true;
                    detail = "Exact new cart and original occupant actions resumed; only destination saving is enabled.";
                    return true;
                }
                catch (Exception error) { if (attempted) Quarantine(); detail = Failure("Cart resumption unconfirmed", error); return false; }
                finally { _busy = false; }
            }

            public bool TryCancelReplica(BoltEntity original, out string detail) => Cancel(original, out detail);

            private bool Cancel(BoltEntity original, out string detail)
            {
                detail = "Cart cancellation requires the intact original lifetime.";
                if (!Enter()) return false;
                bool attempted = false;
                try
                {
                    RequireContext();
                    Require(_candidate != null && !_restored && !_resumed && SameEntity(original, _candidate.Entity)
                        && _candidate.Matches(), "Original cart cannot be resumed after retirement.");
                    if (_cancelled) { detail = "This exact original was already restored."; return true; }
                    Require(_holdAttempted && _source != null && _occupants != null && _save != null,
                        "No original hold was acquired by this adapter.");
                    Require(!_radioPauseAttempted || _radioPaused, "Native radio pause was partial; cancellation cannot certify its audio state.");
                    Require(!_parkAttempted || _parked, "IK parking was partial; cancellation cannot certify original target bindings.");
                    // Ready rejects suppressed occupant input; Cancel deliberately does not.
                    // A partial cart Suspend still must prove its complete held policy before
                    // native activation is safe. Otherwise retain it and report unconfirmed.
                    if (_source.IsHeld) _source.RequireCancellationState();
                    Require(_occupants.MatchesRestoredReferences(_candidate), "Original occupant bindings changed.");
                    _players?.PrepareCancel();
                    attempted = true;
                    if (_players != null) _sourceReleaseAttempted = true;
                    _save.Cancel(); _source.SerializeAllowed = _save.Captured.Active;
                    if (_parked) { _occupants.CancelParked(); _parked = false; _parkAttempted = false; }
                    if (_radioPaused) { _occupants.ResumeRadio(false); _radioPaused = false; _radioPauseAttempted = false; }
                    _occupantWriters.Cancel();
                    if (_source.IsHeld) { _sourceReleaseAttempted = true; _source.Resume(_source); }
                    _birth.Release(); _occupants.Unregister();
                    _players?.Cancel();
                    _cancelled = true;
                    detail = "Original save claim, parked targets and occupant writers restored without dismounting."; return true;
                }
                catch (Exception error) { if (attempted) Quarantine(); detail = Failure("Cart cancellation rejected or unconfirmed", error); return false; }
                finally { _busy = false; }
            }

            private void RequireHeldOriginal()
            {
                Require(_held && !_restored && _candidate.Matches(), "Original held cart changed.");
                _source.RequireSuspended(); _source.State.RequireUnchanged(_candidate, _source.Components);
                _occupantWriters.RequireHeld(); _occupants.RequireParked(); _save.RequireHeld();
                _players?.RequireHeld();
            }

            private static void RequireSettledBody(Binding binding)
            {
                var body = CaptureBody(binding.Body);
                Require(body.IsKinematic || (body.IsSleeping && body.Velocity == System.Numerics.Vector3.Zero
                    && body.AngularVelocity == System.Numerics.Vector3.Zero),
                    "Moving or awake dynamic carts require unmapped native physics history and are not admitted.");
            }

            private void RequireUsable() => Require(!_uncertain && !_cancelled && !_resumed, "Cart adapter is terminal.");
            private void Quarantine()
            {
                _uncertain = true;
                // A provider may have released some writers before reporting failure. It must
                // retain the exact lease and quarantine those writers even after partial resume.
                try { _players?.Quarantine(); }
                catch (Exception) { /* The adapter remains terminal and never replays release. */ }
                if (_players != null)
                {
                    try { _occupantWriters?.ReholdForPlayerRuntime(); }
                    catch (Exception) { /* Retain the original lease and every gate acquired before failure. */ }
                }
                if (_players != null && _sourceReleaseAttempted) _source?.KeepSuspended();
                else _source?.QuarantineIfHeld();
                if (_players != null && _replacementReleaseAttempted) _replacement?.KeepSuspended();
                else _replacement?.QuarantineIfHeld();
                _occupants?.QuarantineStagedReplacement();
            }

            private CartTransferData Decode(BoltMigrationSnapshot snapshot)
            {
                Require(snapshot != null && snapshot.Plan.ContractId == ContractId && snapshot.Plan.ContractVersion == ContractVersion
                    && snapshot.Plan.StateTypeId == 233 && (!_plan.IsValid || snapshot.Plan == _plan)
                    && snapshot.PayloadSpan.Length is > 0 and <= CartSnapshotLimit, "Wrong or oversized cart transfer contract.");
                var data = JsonSerializer.Deserialize<CartTransferData>(snapshot.PayloadSpan, CartSnapshotJson);
                Require(snapshot.PayloadSpan.SequenceEqual(EncodeTransfer(data))
                    && data.Cart.OriginalNetworkId == snapshot.Plan.OldNetworkId && data.Cart.PrefabId == snapshot.Plan.PrefabId
                    && data.Cart.Body.Position == V(snapshot.Position) && data.Cart.Body.Rotation == Q(snapshot.Rotation),
                    "Cart snapshot identity, pose or canonical encoding differs.");
                return data;
            }

            private void RequirePayload(BoltMigrationSnapshot snapshot)
                => Require(_wire != null && EncodeTransfer(Decode(snapshot)).AsSpan().SequenceEqual(EncodeTransfer(_wire)),
                    "The cart transaction payload changed.");

            private static byte[] EncodeTransfer(CartTransferData data)
            {
                Require(data != null && data.Version == 1, "Unknown cart transfer version.");
                ValidateCartSnapshot(data.Cart); CartMigrationSaveCustody.Validate(data.Save); CartLocalState.Validate(data.Lifecycle);
                var record = data.Save.Record;
                var save = data.Cart.Save;
                Require(record.Name == save.ObjectNameId && record.ItemId == save.ItemId && record.State == save.State
                    && record.RuntimeCreated == save.RuntimeCreated && record.Position == save.SavedPosition
                    && record.Rotation == save.SavedRotation && !save.HasOpaqueData
                    && save.SavedCartPower == (record.DataLength == 1 ? record.Power : (float?)null)
                    && data.Save.IgnorePosition == save.IgnorePosition && data.Save.IgnoreRotation == save.IgnoreRotation,
                    "Cart value and save-custody identities or contents differ.");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(data, CartSnapshotJson);
                Require(bytes.Length <= CartSnapshotLimit, "Cart transfer exceeds its payload bound."); return bytes;
            }
        }
    }
}
