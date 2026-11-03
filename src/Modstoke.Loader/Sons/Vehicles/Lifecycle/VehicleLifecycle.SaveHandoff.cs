// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Save;

namespace Modstoke.Sons
{
    public enum VehicleSaveHandoffStage { Prepared, Bound, SavingResumed, Cancelled, Unconfirmed, KnownValuesRestored, WritersResumed }

    public sealed record VehicleSaveHandoffResult(bool MutationAttempted,
        bool Confirmed, VehicleSaveHandoffStage Stage, string Detail);

    public static partial class VehiclePreservation
    {
        /// <summary>
        /// Reserves this empty physical cart's LOCAL save record across destruction/recreation.
        /// Stops the supported native cart writers before reserving the save record. Requires
        /// installed cart lifecycle hooks, an empty dry cart and no active audio or pending coroutine.
        /// The first eligible call arms writer observation and must be retried on a later frame.
        /// External/mod writers and the cross-peer reference barrier remain the caller's responsibility.
        /// Call outside save/load callbacks and serialization; this helper installs no save barrier.
        /// Captures named vehicle/body values and internal transform bindings for replacement restore.
        /// No retirement, transport, callback replay or complete migration authorization.
        /// On failure a nonnull handoff may be Unconfirmed: keep the vehicle suspended, do not retry.
        /// Reacquire preservation bindings after cancellation or replacement binding.
        /// </summary>
        public static bool TryPrepareCartSaveHandoff(BoltEntity source, out CartSaveHandoff handoff,
            out string detail)
        {
            handoff = null;
            detail = "Cart save handoff requires the selected game thread without reentry.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                var binding = new Binding(source);
                RequireEmptySaveHandoffCart(binding);
                Require(WorldItemManager.TryGetInstance(out var manager) && Live(manager),
                    "The local world-item registry is unavailable.");
                handoff = new CartSaveHandoff(binding, manager);
                handoff.Prepare();
                detail = "Native cart writers suspended and local save record reserved. Cross-peer migration is not authorized.";
                return true;
            }
            catch (Exception error)
            {
                handoff?.QuarantineNativeWriters();
                detail = Failure(handoff == null ? "Cart save handoff rejected" : "Cart save handoff unconfirmed; do not retry", error);
                return false;
            }
            finally { _busy = false; }
        }

        public sealed partial class CartSaveHandoff
        {
            private readonly Binding _source;
            private readonly WorldItemManager _manager;
            private readonly WorldItemManager.WorldItemSaveData _saveData;
            private readonly Il2CppSystem.Collections.Generic.List<WorldItemSaver> _savers;
            private readonly Il2CppSystem.Collections.Generic.List<WorldItemState> _records;
            private readonly WorldItemState _originalRecord;
            private readonly WorldItemState _originalLocalState;
            private readonly CartSaveRecord _snapshot;
            private readonly WorldItemState _reservedRecord;
            private readonly Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase _socket;
            private readonly int _thread;
            private readonly bool _server;
            private readonly bool _saveActive;
            private readonly bool _ignorePosition;
            private readonly bool _ignoreRotation;
            private readonly int _loadCount;
            private readonly System.Numerics.Vector3 _sourceSavePosition;
            private readonly System.Numerics.Vector3 _sourceSaveRotation;
            private readonly float _sourcePower;
            private CartReplacementValues _replacementValues;
            private readonly CartWriterHold _sourceWriters;
            private CartWriterHold _replacementWriters;
            private Binding _replacement;
            private readonly CartOccupants _occupants;
            private VehicleSaveHandoffResult _terminalResult;

            /// <summary>
            /// Still-required coverage, not cleared by restoring named values. In particular this
            /// local handoff does not establish all-writer quiescence or repair inbound references.
            /// </summary>
            public VehiclePreservationBlockers RemainingBlockers => _replacementValues?.Blockers ?? CommonBlockers;

            public VehicleSaveHandoffStage Stage { get; private set; }
                = VehicleSaveHandoffStage.Unconfirmed;

            internal CartSaveHandoff(Binding source, WorldItemManager manager)
            {
                _source = source; _manager = manager;
                _occupants = OccupantsFor(source.Cart);
                _saveData = manager._saveData;
                _savers = manager._worldItems;
                _records = _saveData?.WorldItemStates;
                _socket = BoltCore._udpSocket;
                _thread = Environment.CurrentManagedThreadId;
                _server = BoltNetwork.isServer;
                _originalLocalState = source.Saver._itemState;
                _snapshot = ReadCartSaveRecord(_originalLocalState);
                _saveActive = source.Saver._saveActive;
                _ignorePosition = source.Saver._ignorePosition;
                _ignoreRotation = source.Saver._ignoreRotation;
                _loadCount = SaveLifecycle.State.BeforeLoadCount;
                _sourceSavePosition = V(source.Saver.transform.position);
                _sourceSaveRotation = V(source.Saver.transform.eulerAngles);
                _sourcePower = Finite(source.Cart._currentPower);
                _sourceWriters = new CartWriterHold(source);
                if (_occupants?.Target != null)
                {
                    _replacementWriters = new CartWriterHold(_occupants.Target);
                    _sourceWriters.RequireCompatible(_replacementWriters);
                    // Prove freshness before moving any occupants. Binding after retirement
                    // performs the same exact registration checks again before discarding it.
                    var temporary = ReadCartSaveRecord(_occupants.Target.Saver._itemState);
                    Require(temporary.Name != _snapshot.Name && temporary.ItemId == _snapshot.ItemId
                        && temporary.DataLength == -1, "Occupied replacement requires a distinct fresh save identity.");
                }
                RequireRegistry();
                int index = FindSlot(_snapshot, source.Saver);
                _originalRecord = _records[index];
                Require(ReadCartSaveRecord(_originalRecord) == _snapshot,
                    "Live and registered save records differ; complete the ordinary save update before preparing a handoff.");
                // A loaded saver can alias the registry record. A separate deep copy is essential:
                // native OnDestroy marks the saver's own state destroyed even after unregistering.
                _reservedRecord = CopyCartSaveRecord(_originalLocalState);
                Require(ReadCartSaveRecord(_reservedRecord) == _snapshot && source.Matches() && !_reentered,
                    "Save record changed while preparing the reservation.");
                if (_occupants?.Target != null) RequireFreshOccupiedReplacement(_occupants.Target);
            }

            private void RequireFreshOccupiedReplacement(Binding target)
            {
                var temporary = ReadCartSaveRecord(target.Saver._itemState);
                Require(temporary.Name != _snapshot.Name && temporary.ItemId == _snapshot.ItemId
                    && temporary.DataLength == -1, "Occupied replacement does not have a fresh distinct save identity.");
                int index = FindSlot(temporary, target.Saver);
                var record = _records[index];
                var registered = ReadCartSaveRecord(record);
                Require(record.Pointer != target.Saver._itemState.Pointer
                    && registered == (temporary with { Position = registered.Position, Rotation = registered.Rotation })
                    && registered.Position == V(target.Saver.transform.position)
                    && registered.Rotation == V(target.Saver.transform.eulerAngles),
                    "Occupied replacement save record is not a fresh independent registration.");
            }

            internal void Prepare()
            {
                RequireRegistry();
                RequireSource(_saveActive);
                _sourceWriters.Suspend();
                _occupants?.PauseSourceRadio();
                if (_occupants != null)
                {
                    if (_occupants.Target == null) _occupants.ParkBeforeRetirement();
                    else
                    {
                        _replacementWriters.Suspend();
                        _occupants.Target.Saver.SetSaveActive(false);
                        _occupants.Move();
                    }
                }
                _replacementValues = CartReplacementValues.CaptureFrom(_source);
                int index = FindSlot(_snapshot, _source.Saver);
                Require(_records[index].Pointer == _originalRecord.Pointer && _source.Matches() && !_reentered,
                    "Source save registration changed before reservation.");
                // WorldItemManager.UnregisterItemInternal(false) retains the parallel state slot.
                // OnDestroy can no longer update it by saver index; the deep copy also prevents alias writes.
                _records[index] = _reservedRecord;
                _manager.UnregisterItemInternal(_source.Saver, false);
                _source.Saver.SetSaveActive(false);
                RequireReservedSlot();
                RequireSource(false);
                _replacementValues.RequireSourceUnchanged(_source);
                Stage = VehicleSaveHandoffStage.Prepared;
            }

            /// <summary>
            /// Read-only recheck of the reserved source and captured named values. This is not
            /// authorization to retire: writer, reference and unsupported-state coverage is still required.
            /// </summary>
            public bool TryValidatePreparedSource(out string detail)
            {
                detail = "Source validation requires the selected game thread without reentry.";
                if (!Enter()) return false;
                try
                {
                    Require(Stage == VehicleSaveHandoffStage.Prepared && _terminalResult == null,
                        "Only an intact prepared source can be validated.");
                    RequireReservedSlot();
                    RequireSource(false);
                    _sourceWriters.RequireSuspended();
                    _sourceWriters.State.RequireUnchanged(_source, _sourceWriters.Components);
                    _replacementValues.RequireSourceUnchanged(_source);
                    if (_occupants?.IsParked == true) _occupants.RequireParked();
                    else _occupants?.RequireMoved();
                    detail = "Reserved source and captured values are unchanged; complete migration remains unsupported.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart source validation rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>
            /// Restores the exact original local registration before source retirement. Does not
            /// activate the hierarchy, unfreeze Bolt, replay callbacks or resume any vehicle writer.
            /// </summary>
            public VehicleSaveHandoffResult TryCancel()
            {
                if (!Enter()) return Rejected("Cancellation requires the selected game thread without reentry.");
                bool attempted = false;
                try
                {
                    if (_terminalResult != null)
                        return Stage == VehicleSaveHandoffStage.Cancelled ? _terminalResult
                            : Rejected("A completed or unconfirmed handoff cannot be cancelled.");
                    Require(Stage == VehicleSaveHandoffStage.Prepared, "The save handoff is not prepared.");
                    RequireRegistry();
                    RequireSource(false);
                    _sourceWriters.RequireSuspended();
                    _sourceWriters.State.RequireUnchanged(_source, _sourceWriters.Components);
                    RequireSaveHandoffCart(_source);
                    int index = RequireReservedSlot();
                    Require(ReadCartSaveRecord(_originalRecord) == _snapshot && !_reentered,
                        "Original save record changed; cancellation cannot overwrite it.");
                    attempted = true;
                    Stage = VehicleSaveHandoffStage.Unconfirmed;
                    _records[index] = _originalRecord;
                    _savers[index] = _source.Saver;
                    _sourceWriters.SerializeAllowed = true;
                    _source.Saver.SetSaveActive(_saveActive);
                    RequireSource(_saveActive);
                    Require(FindSlot(_snapshot, _source.Saver) == index
                        && _records[index].Pointer == _originalRecord.Pointer && !_reentered,
                        "Original save registration readback failed.");
                    if (_occupants != null)
                    {
                        if (_occupants.IsParked) _occupants.CancelParked();
                        else
                        {
                            _occupants.RollBack();
                            _occupants.RestoreCancelledTargetSaving();
                            _replacementWriters.SerializeAllowed = true;
                            _replacementWriters.Resume(_replacementWriters);
                        }
                    }
                    Stage = VehicleSaveHandoffStage.Cancelled;
                    return _terminalResult = new(true, true, Stage,
                        "Original cart save registration restored. Vehicle remains inactive/frozen; writers were not resumed.");
                }
                catch (Exception error) { return Failed(attempted, "Cart save cancellation", error); }
                finally { _busy = false; }
            }

            /// <summary>
            /// Binds the reserved LOCAL save record to an empty replacement of the same prefab,
            /// after both source entity and saver have actually been destroyed. The replacement
            /// must be the actual owner with observed native Start; its writers are suspended here.
            /// Only a fresh, independently registered
            /// temporary save slot may be removed. An already natively rebound reservation is accepted.
            /// The caller supplies its newly created replacement, never an existing world vehicle.
            /// This restores save registration only, NOT vehicle state or deserialize side effects.
            /// Target callbacks are left intact and never invoked or copied from the old lifetime.
            /// Saving remains disabled until TryResumeReplacementSaving succeeds, even if the
            /// original saver was active: inactive Unity objects are still serialized by the manager.
            /// </summary>
            public VehicleSaveHandoffResult TryBindReplacement(BoltEntity replacement)
            {
                if (!Enter()) return Rejected("Replacement binding requires the selected game thread without reentry.");
                bool attempted = false;
                try
                {
                    if (_terminalResult != null) return Rejected("A completed or unconfirmed handoff cannot bind another replacement.");
                    Require(Stage == VehicleSaveHandoffStage.Prepared, "The save handoff is not prepared.");
                    RequireRegistry();
                    Require(!Live(_source.Entity) && !Live(_source.Saver),
                        "Source entity and saver must be destroyed, not merely detached or disabled.");
                    var target = new Binding(replacement);
                    bool deferred = _occupants?.IsParked == true;
                    if (deferred)
                    {
                        _occupants.StageReplacement(target);
                        // The exact replacement lease cannot be abandoned for a second identity,
                        // even when a later preflight fails before its native writer hold exists.
                        attempted = true;
                        Stage = VehicleSaveHandoffStage.Unconfirmed;
                    }
                    RequireLifecycleCart(target);
                    if (_occupants != null)
                        Require(target.Entity.Pointer == _occupants.Target.Entity.Pointer && target.Id == _occupants.Target.Id,
                            "Only the exact precreated occupied replacement can receive this save reservation.");
                    Require(target.Entity.isOwner && target.Id != _source.Id && target.Prefab == _source.Prefab,
                        "Replacement must be a new owned attachment of the exact source prefab.");
                    Require(target.Saver._ignorePosition == _ignorePosition && target.Saver._ignoreRotation == _ignoreRotation,
                        "Replacement save transform policy differs from the source.");
                    Require(ReadCartSaveRecord(_reservedRecord) == _snapshot, "Reserved save record was changed.");
                    // Install the replacement barrier before touching its temporary save identity.
                    if (_occupants == null || deferred)
                    {
                        _replacementWriters = new CartWriterHold(target);
                        _sourceWriters.RequireCompatible(_replacementWriters);
                    }
                    else _replacementWriters.RequireSuspended();
                    attempted = true;
                    Stage = VehicleSaveHandoffStage.Unconfirmed;
                    if (_occupants == null || deferred) _replacementWriters.Suspend();
                    if (deferred) _occupants.BindAfterRetirement(target);
                    _replacementValues.ValidateReplacement(target);

                    // A named prefab may already have claimed the vacant slot in native Awake.
                    if (target.Saver._itemState.Pointer == _reservedRecord.Pointer)
                    {
                        int bound = FindSlot(_snapshot, target.Saver);
                        Require(_records[bound].Pointer == _reservedRecord.Pointer && target.Saver._saveActive == _saveActive
                            && target.Matches() && !_reentered, "Native replacement registration does not match the reservation.");
                        target.Saver.SetSaveActive(false);
                    }
                    else
                    {
                        RequireReservedSlot();
                        var temporary = ReadCartSaveRecord(target.Saver._itemState);
                        Require(temporary.Name != _snapshot.Name && temporary.ItemId == _snapshot.ItemId,
                            "Replacement does not have a distinct temporary save identity of the same item type.");
                        int temporaryIndex = FindSlot(temporary, target.Saver);
                        var temporaryRecord = _records[temporaryIndex];
                        var registered = ReadCartSaveRecord(temporaryRecord);
                        var transform = target.Saver.transform;
                        Require(temporaryRecord.Pointer != target.Saver._itemState.Pointer
                            && temporary.DataLength == -1
                            && registered == (temporary with { Position = registered.Position, Rotation = registered.Rotation })
                            && registered.Position == V(transform.position) && registered.Rotation == V(transform.eulerAngles),
                            "Replacement save slot is not a fresh, unsaved registration; it cannot be discarded.");
                        Require(target.Matches() && !_reentered, "Replacement changed before save binding.");
                        // Native true removes BOTH aligned slots; re-find the reservation after the shift.
                        target.Saver.SetSaveActive(false);
                        _manager.UnregisterItemInternal(target.Saver, true);
                        int index = RequireReservedSlot();
                        target.Saver._itemState = _reservedRecord;
                        _savers[index] = target.Saver;
                    }

                    var rebound = new Binding(replacement);
                    _occupants?.RebindTarget(rebound);
                    RequireSaveHandoffCart(rebound);
                    int finalIndex = FindSlot(_snapshot, rebound.Saver);
                    Require(_records[finalIndex].Pointer == _reservedRecord.Pointer
                        && rebound.Saver._itemState.Pointer == _reservedRecord.Pointer
                        && !rebound.Saver._saveActive && ReadCartSaveRecord(_reservedRecord) == _snapshot
                        && !_reentered, "Replacement save registration readback failed.");
                    _replacement = rebound;
                    _replacementWriters.Rebind(rebound);
                    _sourceWriters.ReleaseRetired();
                    Stage = VehicleSaveHandoffStage.Bound;
                    return new(attempted, true, Stage,
                        "Local cart save registration bound; saving remains paused. Restore captured values before resuming saving. Migration is unsupported.");
                }
                catch (Exception error) { return Failed(attempted, "Cart replacement save binding", error); }
                finally { _busy = false; }
            }

            /// <summary>
            /// Restores captured named GolfCart state, drive fields, Rigidbody values, internal
            /// hierarchy poses and five Bolt transform bindings/render buffers to the bound replacement.
            /// Also restores paused timers and supported dry water/buoyancy caches under the native gate.
            /// Caller must separately cover external writers, native solver/replication history and
            /// inbound references. This does not authorize complete migration or resume writers.
            /// A failed mutation is terminal; no rollback or retry is attempted.
            /// </summary>
            public VehicleSaveHandoffResult TryRestoreReplacementKnownValues()
            {
                if (!Enter()) return Rejected("Value restoration requires the selected game thread without reentry.");
                bool attempted = false;
                try
                {
                    Require(_terminalResult == null, "A completed or unconfirmed handoff cannot restore values.");
                    Require(Stage == VehicleSaveHandoffStage.Bound && _replacement != null,
                        "A freshly bound replacement is required; value restoration is single-use.");
                    RequireBoundReplacement();
                    var nodes = _replacementValues.ValidateReplacement(_replacement);
                    Require(!_reentered, "Reentry occurred before value restoration.");
                    attempted = true;
                    Stage = VehicleSaveHandoffStage.Unconfirmed;
                    _replacementValues.Restore(_replacement, nodes);
                    _sourceWriters.RestoreLocalState(_replacementWriters);
                    RequireBoundReplacement();
                    RequireSaveInputs(_replacement);
                    Stage = VehicleSaveHandoffStage.KnownValuesRestored;
                    return new(true, true, Stage,
                        "Captured cart/body/transform values restored on the new attachment. Saving and vehicle remain paused; complete migration remains unsupported.");
                }
                catch (Exception error) { return Failed(attempted, "Cart replacement value restoration", error); }
                finally { _busy = false; }
            }

            /// <summary>
            /// Re-enables only the replacement's original save-active policy, after the caller has
            /// restored the captured values via TryRestoreReplacementKnownValues. Does not resume the vehicle.
            /// Full controller/callback/physics preservation remains the caller's separate obligation.
            /// </summary>
            public VehicleSaveHandoffResult TryResumeReplacementSaving()
            {
                if (!Enter()) return Rejected("Save resumption requires the selected game thread without reentry.");
                bool attempted = false;
                try
                {
                    if (_terminalResult != null)
                        return Stage == VehicleSaveHandoffStage.SavingResumed ? _terminalResult
                            : Rejected("A cancelled or unconfirmed handoff cannot resume saving.");
                    Require(Stage == VehicleSaveHandoffStage.KnownValuesRestored && _replacement != null,
                        "The bound replacement's captured values have not been restored.");
                    RequireBoundReplacement();
                    _replacementValues.RequireRestored(_replacement);
                    RequireSaveInputs(_replacement);
                    Require(!_reentered, "Reentry occurred before save resumption.");
                    attempted = true;
                    Stage = VehicleSaveHandoffStage.Unconfirmed;
                    _replacementWriters.SerializeAllowed = true;
                    _replacement.Saver.SetSaveActive(_saveActive);
                    Require(_replacement.Matches() && _replacement.Saver._saveActive == _saveActive && !_reentered,
                        "Replacement save-active readback failed.");
                    Stage = VehicleSaveHandoffStage.SavingResumed;
                    return _terminalResult = new(true, true, Stage,
                        "Original save-active policy restored. Vehicle remains inactive/frozen; explicitly resume supported writers after the caller's remaining barriers are satisfied.");
                }
                catch (Exception error) { return Failed(attempted, "Cart saving resumption", error); }
                finally { _busy = false; }
            }

            private void RequireBoundReplacement()
            {
                RequireRegistry();
                Require(_replacement != null && _replacement.Matches(), "Replacement attachment changed.");
                _replacementWriters.RequireSuspended();
                RequireSaveHandoffCart(_replacement);
                int index = FindSlot(_snapshot, _replacement.Saver);
                Require(_records[index].Pointer == _reservedRecord.Pointer
                    && _replacement.Saver._itemState.Pointer == _reservedRecord.Pointer
                    && ReadCartSaveRecord(_reservedRecord) == _snapshot
                    && !_replacement.Saver._saveActive && !_reentered,
                    "Replacement save registration changed or saving resumed before completion.");
            }

            private void RequireSaveInputs(Binding binding)
            {
                Require(binding.Saver._ignorePosition == _ignorePosition && binding.Saver._ignoreRotation == _ignoreRotation
                    && (_ignorePosition || V(binding.Saver.transform.position) == _sourceSavePosition)
                    && (_ignoreRotation || V(binding.Saver.transform.eulerAngles) == _sourceSaveRotation)
                    && Finite(binding.Cart._currentPower) == _sourcePower,
                    "Restore the original serializer pose, power and save policy before enabling replacement saving.");
            }

            private void RequireSource(bool saveActive)
            {
                Require(_source.Matches() && _source.Saver._itemState.Pointer == _originalLocalState.Pointer
                    && ReadCartSaveRecord(_originalLocalState) == _snapshot
                    && _source.Saver._saveActive == saveActive && _source.Saver._ignorePosition == _ignorePosition
                    && _source.Saver._ignoreRotation == _ignoreRotation && !_reentered,
                    "Original source save state or attachment changed.");
                RequireSaveInputs(_source);
            }

            private void RequireRegistry()
            {
                RequireContext();
                var lifecycle = SaveLifecycle.State;
                Require(lifecycle.HarmonyPatchesInstalled && lifecycle.BeforeSaveCount == lifecycle.AfterSaveCount
                    && lifecycle.BeforeLoadCount == lifecycle.AfterLoadCount && lifecycle.BeforeLoadCount == _loadCount
                    && SaveGameManager.TryGetInstance(out var saveManager) && Live(saveManager) && !saveManager._loadingInProgress,
                    "Save/load is active, changed, or cannot be observed; save registration cannot be modified.");
                Require(Environment.CurrentManagedThreadId == _thread && BoltNetwork.isServer == _server
                    && _socket != null && !_socket.WasCollected && BoltCore._udpSocket?.Pointer == _socket.Pointer
                    && Live(_manager) && WorldItemManager.TryGetInstance(out var current) && Live(current)
                    && current.Pointer == _manager.Pointer && _saveData != null && !_saveData.WasCollected
                    && _manager._saveData?.Pointer == _saveData.Pointer && _savers != null && !_savers.WasCollected
                    && _records != null && !_records.WasCollected && _manager._worldItems?.Pointer == _savers.Pointer
                    && _saveData.WorldItemStates?.Pointer == _records.Pointer && _savers.Count == _records.Count
                    && _records.Count <= 65536, "Local save registry/session changed or is outside the supported bound.");
            }

            private int RequireReservedSlot()
            {
                RequireRegistry();
                int index = FindSlot(_snapshot, null);
                Require(_records[index].Pointer == _reservedRecord.Pointer
                    && ReadCartSaveRecord(_reservedRecord) == _snapshot, "Reserved save record changed or was claimed.");
                return index;
            }

            private int FindSlot(CartSaveRecord wanted, WorldItemSaver owner)
            {
                RequireRegistry();
                int found = -1;
                int ownerCount = 0;
                for (int i = 0; i < _records.Count; i++)
                {
                    var record = _records[i];
                    var saver = _savers[i];
                    Require(record != null && !record.WasCollected, "World-item registry contains an unavailable record.");
                    if (owner != null && saver?.Pointer == owner.Pointer) ownerCount++;
                    if (record.ItemId != wanted.ItemId || !string.Equals(record.ObjectNameId, wanted.Name, StringComparison.Ordinal)) continue;
                    Require(found < 0 && (owner == null ? saver is null : saver?.Pointer == owner.Pointer),
                        "Duplicate or differently owned save identity; no registration will be replaced.");
                    found = i;
                }
                Require(found >= 0 && (owner == null || ownerCount == 1), "Save registration is missing or ambiguous.");
                return found;
            }

            private VehicleSaveHandoffResult Rejected(string detail) => new(false, false, Stage, detail);

            internal void QuarantineNativeWriters()
            {
                _sourceWriters?.QuarantineIfHeld();
                _replacementWriters?.QuarantineIfHeld();
                if (_occupants?.IsParked == true && _occupants.Target != null) _occupants.QuarantineStagedReplacement();
            }

            private VehicleSaveHandoffResult Failed(bool attempted, string operation, Exception error)
            {
                if (attempted) QuarantineNativeWriters();
                var result = new VehicleSaveHandoffResult(attempted, false, Stage,
                    Failure(operation + (attempted ? " unconfirmed; keep suspended and do not retry" : " rejected"), error));
                if (attempted) _terminalResult = result;
                return result;
            }
        }

        private static void RequireSaveHandoffCart(Binding binding)
        {
            RequireLifecycleCart(binding);
            Require(binding.Entity.isFrozen && !binding.Entity.gameObject.activeInHierarchy,
                "Save handoff requires inactive/frozen vehicles with suspended native writers.");
        }

        private static void RequireLifecycleCart(Binding binding)
        {
            var occupants = OccupantsFor(binding.Cart);
            if (occupants == null) { RequireEmptySaveHandoffCart(binding); return; }
            Require(binding.Cart != null && Live(binding.Saver) && binding.Cart._saver?.Pointer == binding.Saver.Pointer,
                "Occupied handoff lost its exact physical cart/saver.");
            RequireExactSaveType<GolfCartController>(binding.Cart);
            RequireExactSaveType<WorldItemSaver>(binding.Saver);
            Require((Capture(binding).Blockers & (VehiclePreservationBlockers.LocalConnectionUnavailable
                | VehiclePreservationBlockers.UnresolvedReference
                | VehiclePreservationBlockers.OpaqueSaveData)) == 0,
                "Occupied handoff has unresolved, opaque or unavailable native state.");
            occupants.RequireCart(binding);
        }

        private static void RequireEmptySaveHandoffCart(Binding binding)
        {
            Require(binding.Cart != null && Live(binding.Saver) && binding.Cart._saver?.Pointer == binding.Saver.Pointer,
                "Save handoff supports only a physical GolfCart with its exact linked WorldItemSaver; LogSled construction saves are unsupported.");
            RequireExactSaveType<GolfCartController>(binding.Cart);
            RequireExactSaveType<WorldItemSaver>(binding.Saver);
            var values = Capture(binding);
            const VehiclePreservationBlockers unsafeState =
                VehiclePreservationBlockers.LocalConnectionObserved
                | VehiclePreservationBlockers.LocalConnectionUnavailable
                | VehiclePreservationBlockers.OpaqueSaveData;
            Require((values.Blockers & unsafeState) == 0, "Occupied, unqualified or opaque carts cannot enter a save handoff.");
            foreach (var reference in values.References)
                Require(reference.NetworkId == 0, "Connected occupants or ghosts require their own lifecycle preservation.");
        }
    }
}
