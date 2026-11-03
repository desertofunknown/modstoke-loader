// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Crafting.Structures;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Modstoke.Sons
{
    public sealed record VehiclePreservationResult(bool MutationAttempted,
        bool KnownValuesRestored, VehiclePreservationBlockers RemainingBlockers, string Detail);

    /// <summary>
    /// Typed value preservation, not an <see cref="IBoltMigrationAdapter"/>. No hooks, automatic registration,
    /// retirement or ownership changes. The explicit cart save-handoff API handles local save registration
    /// only; value capture/restoration does not transfer save records or repair inbound references.
    /// </summary>
    public static partial class VehiclePreservation
    {
        private const int MaximumArrayLength = 1024;
        private const VehiclePreservationBlockers CommonBlockers =
            VehiclePreservationBlockers.WriterQuiescence
            | VehiclePreservationBlockers.NetworkTransformHistory
            | VehiclePreservationBlockers.SaveRegistryHandoff
            | VehiclePreservationBlockers.InboundReferences
            | VehiclePreservationBlockers.OccupantLifecycle
            | VehiclePreservationBlockers.LocalControllerLifecycle
            | VehiclePreservationBlockers.NativePhysicsHistory;
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        /// <summary>
        /// Copies a physical GolfCart or linked mobile LogSled on the selected game thread, without
        /// invoking save serializers or connection callbacks. The separate restore point is local,
        /// single-use and bound to this exact live attachment; Values contains no native references.
        /// Two matching reads detect observed changes, but do not establish writer quiescence.
        /// </summary>
        public static bool TryCapture(BoltEntity entity, out VehicleValues values,
            out InPlaceRestorePoint restorePoint, out string detail)
        {
            values = null; restorePoint = null;
            detail = "Vehicle capture requires the selected game thread and recognized game build.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                var binding = new Binding(entity);
                var first = Capture(binding);
                var second = Capture(binding);
                Require(binding.Matches() && !_reentered && Same(first, second),
                    "Vehicle values or bindings changed during capture.");
                values = second;
                restorePoint = new InPlaceRestorePoint(binding, values);
                detail = "Known typed vehicle values captured. Full migration is unsupported: " + values.Blockers + ".";
                return true;
            }
            catch (Exception error) { detail = Failure("Vehicle capture rejected", error); return false; }
            finally { _busy = false; }
        }

        public sealed class InPlaceRestorePoint
        {
            private readonly Binding _binding;
            private readonly VehicleValues _values;
            private bool _consumed;
            private VehiclePreservationResult _result;

            internal InPlaceRestorePoint(Binding binding, VehicleValues values)
            { _binding = binding; _values = values; }

            /// <summary>
            /// Restores only named network/drive fields and exposed Rigidbody values on the captured
            /// attachment. Caller must first suspend ALL writers, including managed worker callbacks.
            /// Frozen Bolt state and inactive Unity hierarchy are required, but do not prove that contract.
            /// Save identities, references, network transform buffers, storage instances, callbacks,
            /// timers, wheel contacts and controller lifecycles are NEVER restored by this operation.
            /// The hierarchy remains inactive and Bolt remains frozen. No blind replay after mutation.
            /// </summary>
            public VehiclePreservationResult TryRestoreKnownValues()
            {
                if (!Enter()) return Rejected("Restore requires the selected game thread without reentry.");
                bool attempted = false;
                try
                {
                    RequireContext();
                    // A consumed restore point does not touch a possibly destroyed native object.
                    if (_consumed) return _result;
                    Require(_binding.Matches(), "The captured attachment is no longer current; replacement restoration is unsupported.");
                    Require(_binding.Entity.isOwner, "Only the actual owner may restore network values.");
                    Require(_binding.Entity.isFrozen && !_binding.Entity.gameObject.activeInHierarchy,
                        "Restore requires frozen Bolt state and an inactive vehicle hierarchy; caller must also suspend non-Unity writers.");
                    var current = Capture(_binding);
                    const VehiclePreservationBlockers unsafeState =
                        VehiclePreservationBlockers.LocalConnectionObserved
                        | VehiclePreservationBlockers.LocalConnectionUnavailable
                        | VehiclePreservationBlockers.OpaqueStorageToken
                        | VehiclePreservationBlockers.OpaqueSaveData;
                    Require(((_values.Blockers | current.Blockers) & unsafeState) == 0,
                        "Occupied, unqualified connection, opaque storage-token or opaque save-data state cannot be restored.");
                    Require(_values.WorldItemSave == current.WorldItemSave && _values.StructureSaveId == current.StructureSaveId,
                        "Save identity or save data changed. Registry transfer and save replay are not supported.");
                    Require(Equal(_values.References, current.References),
                        "A vehicle reference changed; this operation never clears, resolves away or remaps references.");
                    if (_values.LogSled != null)
                        Require(Equal(_values.LogSled.StorageSlots, current.LogSled.StorageSlots)
                            && Equal(_values.LogSled.SlotAvailability, current.LogSled.SlotAvailability),
                            "Sled storage changed. Rewinding counts without restoring item instances and storage callbacks is unsupported.");
                    // Supporting structures are inbound/outbound construction ownership, not empty seats.
                    foreach (var reference in current.References)
                        Require(reference.NetworkId == 0, "Linked occupants, ghosts, translation controllers or supporting structures require lifecycle repair.");
                    ValidateRestoreShape(_binding, _values);
                    Require(_binding.Matches() && !_reentered, "Binding changed before restoration.");
                    _consumed = true;
                    attempted = true;
                    if (_binding.Cart != null) RestoreCart(_binding.Cart, _binding.CartState, _values.GolfCart);
                    else RestoreSled(_binding.SledState, _values.LogSled);
                    Require(_binding.Matches() && !_reentered, "Binding changed after network/controller writes.");
                    RestoreBody(_binding.Body, _values.Body);
                    Require(_binding.Matches() && !_reentered && _binding.Entity.isFrozen
                        && !_binding.Entity.gameObject.activeInHierarchy, "Vehicle resumed or changed during restoration.");
                    var after = Capture(_binding);
                    // NetworkTransform history/poses are deliberately excluded from this writable-field readback.
                    Require(SameWritable(_values, after) && _values.WorldItemSave == after.WorldItemSave
                        && _values.StructureSaveId == after.StructureSaveId
                        && Equal(_values.References, after.References)
                        && (after.Blockers & unsafeState) == 0,
                        "Known-value readback or untouched save/reference invariants did not match; no rollback or replay attempted.");
                    return _result = new(true, true, _values.Blockers,
                        "Known network/controller/body values restored in place. Vehicle remains inactive/frozen; full migration and lifecycle restoration remain unsupported.");
                }
                catch (Exception error)
                {
                    var result = new VehiclePreservationResult(attempted, false, _values.Blockers,
                        Failure(attempted ? "Vehicle restoration unconfirmed; do not replay" : "Vehicle restoration rejected", error));
                    if (attempted) _result = result;
                    return result;
                }
                finally { _busy = false; }
            }

            private VehiclePreservationResult Rejected(string detail) => new(false, false, _values.Blockers, detail);
        }

        internal sealed class Binding
        {
            internal readonly BoltEntity Entity;
            internal readonly GolfCartController Cart;
            internal readonly LogSledController Sled;
            internal readonly ScrewStructureWithStorageMobile Mobile;
            internal readonly GolfCartState CartState;
            internal readonly ScrewStructureWithStorageMobileState SledState;
            internal readonly Rigidbody Body;
            internal readonly WorldItemSaver Saver;
            internal readonly int Prefab;
            internal readonly int StateType;
            internal readonly ulong Id;
            private readonly Entity _backing;
            private readonly NetworkState _state;
            private readonly Il2CppObjectBase _socket;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly bool _server;
            private readonly bool _owner;
            private readonly IntPtr _source;
            private readonly IntPtr _controller;
            private readonly WorldItemState _saveState;
            private readonly Il2CppObjectBase _saveData;

            internal Binding(BoltEntity entity)
            {
                Require(Live(entity) && entity.isAttached, "A live attached vehicle is required.");
                Entity = entity; Id = entity.networkId.PackedValue; Prefab = entity.prefabId.Value;
                _backing = entity._entity;
                Require(_backing != null && !_backing.WasCollected, "Entity backing is unavailable.");
                _state = _backing.Serializer?.TryCast<NetworkState>();
                Require(_state != null && !_state.WasCollected && _state.Meta != null, "Vehicle state is unavailable.");
                StateType = _state.Meta.TypeId.Value;
                _socket = BoltCore._udpSocket; _server = BoltNetwork.isServer; _owner = entity.isOwner;
                _source = entity.source?.Pointer ?? IntPtr.Zero; _controller = entity.controller?.Pointer ?? IntPtr.Zero;
                if (StateType == 233)
                {
                    RuntimeHelpers.RunClassConstructor(typeof(GolfCartState).TypeHandle);
                    Require(IL2CPP.il2cpp_object_get_class(_state.Pointer) == Il2CppClassPointerStore<GolfCartState>.NativeClassPtr,
                        "Only the exact GolfCartState schema is supported.");
                    CartState = _state.Cast<GolfCartState>();
                    foreach (var candidate in entity.GetComponentsInChildren<GolfCartController>(true))
                    {
                        Require(Cart == null && Live(candidate) && SameEntity(candidate.entity, entity),
                            "Ambiguous or differently owned cart controller.");
                        Cart = candidate;
                    }
                    Require(Cart != null && entity.GetComponentsInChildren<GolfCartGhostController>(true).Length == 0,
                        "Ghost carts and absent physical controllers are not supported.");
                    Body = Cart._rigidbody;
                }
                else if (StateType == 214)
                {
                    RuntimeHelpers.RunClassConstructor(typeof(ScrewStructureWithStorageMobileState).TypeHandle);
                    Require(IL2CPP.il2cpp_object_get_class(_state.Pointer) == Il2CppClassPointerStore<ScrewStructureWithStorageMobileState>.NativeClassPtr,
                        "Only the exact mobile storage schema is supported.");
                    SledState = _state.Cast<ScrewStructureWithStorageMobileState>();
                    Mobile = entity.GetComponent<ScrewStructureWithStorageMobile>();
                    Require(Live(Mobile) && SameEntity(Mobile.entity, entity), "Missing linked mobile storage component.");
                    foreach (var candidate in entity.GetComponentsInChildren<LogSledController>(true))
                    {
                        Require(Sled == null && Live(candidate) && candidate._screwStructure?.Pointer == Mobile.Pointer
                            && SameEntity(candidate.GetBoltEntity(), entity), "Ambiguous or differently owned sled controller.");
                        Sled = candidate;
                    }
                    Require(Sled != null, "The mobile structure is not a qualified LogSled.");
                    Body = Sled._rigidbody;
                }
                else throw new InvalidOperationException("Only physical GolfCart state 233 and linked LogSled mobile state 214 are supported.");
                var bodies = entity.GetComponentsInChildren<Rigidbody>(true);
                Require(Live(Body) && bodies.Length == 1 && bodies[0].Pointer == Body.Pointer,
                    "Exactly one linked Rigidbody is required; compound/multi-body vehicles are unsupported.");
                Require(entity.GetComponentsInChildren<BoltEntity>(true).Length == 1,
                    "Nested network entities require independent preservation.");
                var savers = entity.GetComponentsInChildren<WorldItemSaver>(true);
                Require(savers.Length <= 1, "Multiple world-item save records are unsupported.");
                Saver = savers.Length == 1 ? savers[0] : null;
                _saveState = Saver?._itemState;
                _saveData = _saveState?.ItemStateData;
                Require(Saver == null || (_saveState != null && !_saveState.WasCollected), "World item save binding is unavailable.");
                Require(Matches(), "Vehicle attachment binding is unstable.");
            }

            internal bool Matches()
            {
                if (Environment.CurrentManagedThreadId != _thread || !ContextAvailable() || !Live(Entity)
                    || !Entity.isAttached || !Live(Body) || _backing.WasCollected || _state.WasCollected
                    || _socket == null || _socket.WasCollected || BoltCore._udpSocket?.Pointer != _socket.Pointer
                    || BoltNetwork.isServer != _server || Entity.networkId.PackedValue != Id || Id == 0
                    || Entity.prefabId.Value != Prefab || Entity.isOwner != _owner
                    || Entity._entity?.Pointer != _backing.Pointer || _backing.Serializer?.Pointer != _state.Pointer
                    || _state.Meta.TypeId.Value != StateType || _state.Entity?.Pointer != _backing.Pointer
                    || _backing.UnityObject?.Pointer != Entity.Pointer || _backing.NetworkId.PackedValue != Id
                    || !SameEntity(BoltNetwork.FindEntity(new NetworkId(Id)), Entity)
                    || (Entity.source?.Pointer ?? IntPtr.Zero) != _source
                    || (Entity.controller?.Pointer ?? IntPtr.Zero) != _controller) return false;
                if (Cart != null && (!Live(Cart) || !SameEntity(Cart.entity, Entity) || Cart._rigidbody?.Pointer != Body.Pointer)) return false;
                if (Sled != null && (!Live(Sled) || !Live(Mobile) || !SameEntity(Mobile.entity, Entity)
                    || !SameEntity(Sled.GetBoltEntity(), Entity) || Sled._screwStructure?.Pointer != Mobile.Pointer
                    || Sled._rigidbody?.Pointer != Body.Pointer)) return false;
                if (Cart != null)
                {
                    var carts = Entity.GetComponentsInChildren<GolfCartController>(true);
                    if (carts.Length != 1 || carts[0].Pointer != Cart.Pointer
                        || Entity.GetComponentsInChildren<GolfCartGhostController>(true).Length != 0) return false;
                }
                else
                {
                    var sleds = Entity.GetComponentsInChildren<LogSledController>(true);
                    var mobiles = Entity.GetComponentsInChildren<ScrewStructureWithStorageMobile>(true);
                    if (sleds.Length != 1 || sleds[0].Pointer != Sled.Pointer
                        || mobiles.Length != 1 || mobiles[0].Pointer != Mobile.Pointer) return false;
                }
                var bodies = Entity.GetComponentsInChildren<Rigidbody>(true);
                var savers = Entity.GetComponentsInChildren<WorldItemSaver>(true);
                return bodies.Length == 1 && bodies[0].Pointer == Body.Pointer
                    && Entity.GetComponentsInChildren<BoltEntity>(true).Length == 1
                    && (Saver == null ? savers.Length == 0 : Live(Saver) && savers.Length == 1 && savers[0].Pointer == Saver.Pointer
                        && _saveState != null && !_saveState.WasCollected && Saver._itemState?.Pointer == _saveState.Pointer
                        && (_saveState.ItemStateData?.Pointer ?? IntPtr.Zero) == (_saveData?.Pointer ?? IntPtr.Zero)
                        && (_saveData == null || !_saveData.WasCollected));
            }
        }

        private static VehicleValues Capture(Binding binding)
        {
            Require(binding.Matches(), "Vehicle attachment changed.");
            var blockers = CommonBlockers;
            var save = CaptureSave(binding.Saver);
            if (save?.HasOpaqueData == true) blockers |= VehiclePreservationBlockers.OpaqueSaveData;
            VehicleNetworkPose[] poses;
            VehicleReference[] references;
            GolfCartValues cart = null;
            LogSledValues sled = null;
            int? structureSaveId = null;
            if (binding.Cart != null)
            {
                var state = binding.CartState;
                Require(state.ConnectedSlots.Length == 4, "GolfCart requires exactly four raw connected slots.");
                references = ReadReferences(state.ConnectedSlots);
                poses = new[] { Pose(state.Transform), Pose(state.FrontLeftWheel), Pose(state.FrontRightWheel),
                    Pose(state.BackLeftWheel), Pose(state.BackRightWheel) };
                var audio = new float[Count(state.AudioParams.Length)];
                for (int i = 0; i < audio.Length; i++) audio[i] = Finite(state.AudioParams[i]);
                cart = new(V(state.Velocity), Finite(state.SteeringWheelRotation), Finite(state.Power),
                    new[] { Finite(state.FrontLeftWheelRpm), Finite(state.FrontRightWheelRpm),
                        Finite(state.BackLeftWheelRpm), Finite(state.BackRightRpm) }, audio, CaptureDrive(binding.Cart));
                blockers |= CartConnections(binding.Cart);
            }
            else
            {
                var state = binding.SledState;
                var support = ReadReferences(state.SupportingScrewStructures);
                references = new VehicleReference[support.Length + 1];
                references[0] = Reference(state.GetTranslationControllerNetworkIdValue().PackedValue);
                Array.Copy(support, 0, references, 1, support.Length);
                poses = new[] { Pose(state.Transform) };
                var vectors = new Vector3[Count(state.VectorBuffer.Length)];
                for (int i = 0; i < vectors.Length; i++) vectors[i] = V(state.VectorBuffer[i]);
                var slots = new VehicleStorageSlot[Count(state.StorageSlots.Length)];
                for (int i = 0; i < slots.Length; i++)
                {
                    var slot = state.StorageSlots[i];
                    Require(slot != null && !slot.WasCollected && slot.ItemInstances != null, "Storage slot/token array unavailable.");
                    int tokens = Count(slot.ItemInstances.Length);
                    bool opaque = false;
                    for (int j = 0; j < tokens; j++) opaque |= slot.ItemInstances[j] != null;
                    slots[i] = new(slot.ItemId, slot.Count, slot.AreUnique, tokens, opaque);
                    if (opaque || slot.AreUnique) blockers |= VehiclePreservationBlockers.OpaqueStorageToken;
                }
                var availability = new int[Count(state.SlotAvailability.Length)];
                for (int i = 0; i < availability.Length; i++) availability[i] = state.SlotAvailability[i];
                sled = new(state.GoldPlated, state.ElectricityConnectorNum, state.IsActiveState, vectors, slots, availability);
                structureSaveId = binding.Mobile._Sons_Crafting_Structures_IScrewStructureSaveID_SaveId_k__BackingField;
                blockers |= VehiclePreservationBlockers.StorageInstancesAndCallbacks;
                if (binding.Sled._isLocalPlayerConnected || binding.Sled._isDisconnectingPlayer
                    || Present(binding.Sled._localPlayerController) || Present(binding.Sled._disconnectedCallback)
                    || Present(binding.Sled._currentIkTargetsController) || Present(binding.Sled._currentBoltPlayerSetup)
                    || Present(binding.Sled._playerCollidersInstance) || Present(binding.Mobile._translationControllerTransform))
                    blockers |= VehiclePreservationBlockers.LocalConnectionObserved;
            }
            foreach (var reference in references)
                if (reference.NetworkId != 0 && !reference.IsResolved) blockers |= VehiclePreservationBlockers.UnresolvedReference;
            Require(binding.Matches(), "Vehicle attachment changed during capture.");
            return new(binding.Id, binding.Prefab, binding.StateType, CaptureBody(binding.Body), save, structureSaveId,
                poses, references, blockers, cart, sled);
        }

        private static VehiclePreservationBlockers CartConnections(GolfCartController cart)
        {
            var flags = cart._isLocalConnectedSlots;
            var actors = cart._localConnectedActors;
            var actions = cart._localConnectedActions;
            var previous = cart._previousProcessedConnectedSlots;
            var transforms = cart._externalEntityTransforms;
            var animators = cart._externalPlayerAnimators;
            var externalActors = cart._externalVailActors;
            if (flags == null || actors == null || actions == null || previous == null || transforms == null
                || animators == null || externalActors == null || flags.Length != 4 || actors.Length != 4
                || actions.Length != 4 || previous.Length != 4 || transforms.Length != 4
                || animators.Length != 4 || externalActors.Length != 4)
                return VehiclePreservationBlockers.LocalConnectionUnavailable;
            bool occupied = Present(cart._localPlayerDriverAction) || Present(cart._pendingDriverIkTargetsController)
                || Present(cart._currentDriverIkTargetsController) || Present(cart._driverConnectIkCoroutine)
                || Present(cart._ownedGolfCartGhost) || Present(cart._ownedGolfCartGhostController)
                || Present(cart._ownedGolfCartGhostEntity) || Present(cart._externalGolfCartGhostState)
                || Present(cart._externalGolfCartGhostController) || Present(cart._externalGolfCartGhostEntity)
                || Present(cart._externalGolfCartGhostTransform) || cart._externalDriverPlayerInitialized
                || Present(cart._externalDriverPlayerState) || Present(cart._externalDriverIkTargetsController)
                || Present(cart._externalDriverPlayerSmoothScript);
            for (int i = 0; i < 4; i++)
                occupied |= flags[i] || Present(actors[i]) || Present(actions[i]) || Present(previous[i])
                    || Present(transforms[i]) || Present(animators[i]) || Present(externalActors[i]);
            return occupied ? VehiclePreservationBlockers.LocalConnectionObserved : 0;
        }

        private static VehicleSaveIdentity CaptureSave(WorldItemSaver saver)
        {
            if (saver == null) return null;
            Require(Live(saver) && !saver._selfDestructFromState, "Save component is unavailable or pending destruction.");
            var state = saver._itemState;
            Require(state != null && !state.WasCollected, "World item save state is unavailable.");
            Require(state.State != 1, "World item save state is marked destroyed.");
            float? power = null;
            bool opaque = false;
            var data = state.ItemStateData;
            if (data != null)
            {
                RuntimeHelpers.RunClassConstructor(typeof(GolfCartWorldItemStateData).TypeHandle);
                Count(data.Length);
                foreach (var entry in data)
                {
                    if (entry == null || entry.WasCollected) { opaque = true; continue; }
                    if (IL2CPP.il2cpp_object_get_class(entry.Pointer) == Il2CppClassPointerStore<GolfCartWorldItemStateData>.NativeClassPtr
                        && !power.HasValue) power = Finite(entry.Cast<GolfCartWorldItemStateData>().Power);
                    else opaque = true;
                }
            }
            return new(state.ObjectNameId, state.ItemId, state.State, state.RuntimeCreated, saver._saveActive,
                saver._ignorePosition, saver._ignoreRotation,
                new(Finite(state.Position.x), Finite(state.Position.y), Finite(state.Position.z)),
                new(Finite(state.Rotation.x), Finite(state.Rotation.y), Finite(state.Rotation.z)), power, opaque);
        }

        private static VehicleNetworkPose Pose(NetworkTransform transform)
        {
            Require(transform != null && !transform.WasCollected, "Network transform unavailable.");
            return new(V(transform.Position), Q(transform.Rotation));
        }

        private static VehicleReference[] ReadReferences(NetworkArray_Entity array)
        {
            Require(array != null && !array.WasCollected, "Reference array unavailable.");
            var result = new VehicleReference[Count(array.Length)];
            for (int i = 0; i < result.Length; i++) result[i] = Reference(array.GetValueNetworkId(i).PackedValue);
            return result;
        }

        private static VehicleReference Reference(ulong id)
        {
            if (id == 0) return new(0, false);
            var entity = BoltNetwork.FindEntity(new NetworkId(id));
            return new(id, Live(entity) && entity.isAttached && entity.networkId.PackedValue == id);
        }

        private static bool Enter()
        {
            if (_busy) { _reentered = true; return false; }
            if (!OnSelectedThread()) return false;
            _busy = true; _reentered = false; return true;
        }
        private static bool OnSelectedThread() => ClientEnrollment.TryRead(
            (ControlPeer)null, out _) == EnrollmentReadStatus.PeerUnavailable;
        private static bool ContextAvailable() => OnSelectedThread() && GameBuild.IsRecognized
            && HarmonySupport.CreateState()?.SafeForHarmonyPatches == true && BoltNetwork.isRunning
            && (BoltNetwork.isServer ^ BoltNetwork.isClient);
        private static void RequireContext() => Require(ContextAvailable(), "Unrecognized build or unavailable selected Bolt game thread.");
        private static bool Live(UnityEngine.Object obj) => obj is not null && !obj.WasCollected && obj;
        private static bool Present(Il2CppObjectBase obj) => obj is not null;
        private static bool SameEntity(BoltEntity a, BoltEntity b) => Live(a) && Live(b) && a.isAttached
            && b.isAttached && a.Pointer == b.Pointer && a.networkId.PackedValue == b.networkId.PackedValue;
        private static int Count(int count) { Require(count >= 0 && count <= MaximumArrayLength, "Array length outside the supported bound; no truncation permitted."); return count; }
        private static void Require(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
        private static string Failure(string prefix, Exception error) => prefix + ": "
            + (error is InvalidOperationException ? error.Message : error.GetType().Name + ".");
        private static float Finite(float value) { Require(float.IsFinite(value), "Nonfinite vehicle value."); return value; }
        private static Vector3 V(UnityEngine.Vector3 value) => new(Finite(value.x), Finite(value.y), Finite(value.z));
        private static Quaternion Q(UnityEngine.Quaternion value) => new(Finite(value.x), Finite(value.y), Finite(value.z), Finite(value.w));
        private static UnityEngine.Vector3 U(Vector3 value) => new(value.X, value.Y, value.Z);
        private static UnityEngine.Quaternion U(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
        private static bool Equal<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
            return true;
        }
        private static bool Same(VehicleValues a, VehicleValues b) => SameWritable(a, b)
            && a.NetworkId == b.NetworkId && a.PrefabId == b.PrefabId && a.StateTypeId == b.StateTypeId
            && a.WorldItemSave == b.WorldItemSave && a.StructureSaveId == b.StructureSaveId
            && a.Blockers == b.Blockers && Equal(a.NetworkPoses, b.NetworkPoses) && Equal(a.References, b.References);
        private static bool SameWritable(VehicleValues a, VehicleValues b)
        {
            if (a.Body != b.Body) return false;
            if (a.GolfCart != null && b.GolfCart != null)
            {
                var x = a.GolfCart; var y = b.GolfCart;
                return x.Velocity == y.Velocity && x.Power == y.Power && x.SteeringWheelRotation == y.SteeringWheelRotation
                    && x.Drive == y.Drive && Equal(x.WheelRpms, y.WheelRpms) && Equal(x.AudioParameters, y.AudioParameters);
            }
            if (a.LogSled == null || b.LogSled == null) return false;
            var p = a.LogSled; var q = b.LogSled;
            return p.GoldPlated == q.GoldPlated && p.ElectricityConnectorNum == q.ElectricityConnectorNum
                && p.IsActiveState == q.IsActiveState && Equal(p.VectorBuffer, q.VectorBuffer)
                && Equal(p.StorageSlots, q.StorageSlots) && Equal(p.SlotAvailability, q.SlotAvailability);
        }
    }
}
