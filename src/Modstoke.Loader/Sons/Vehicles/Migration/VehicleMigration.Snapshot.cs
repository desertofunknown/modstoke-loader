// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bolt;
using global::Sons.Ai.Vail;
using UnityEngine;

namespace Modstoke.Sons
{
    public enum CartSeatKind { Empty, DriverGhost, Player, VailActor }

    public sealed record CartSeatIdentity(CartSeatKind Kind,
        ulong NetworkId, int PrefabId, int ActorUniqueId, ulong DriverNetworkId, int DriverPrefabId);

    public sealed record CartRadioValues(bool Present, bool IsOn, int TrackHash, float Volume,
        bool ContinuousPlayback, bool ScreenActive, bool ControlsActive, bool UiActive);

    /// <summary>
    /// Independent bounded payload of known cart values and exact retained occupant identities.
    /// Not a complete migration contract: save-registry custody, timers, subscriptions and local
    /// actor handles are separate lifecycles. No pointers, old frames or native audio handles.
    /// </summary>
    public sealed class GolfCartMigrationSnapshot
    {
        private readonly byte[] _payload;
        public ulong OriginalNetworkId { get; }
        public int PrefabId { get; }
        public IReadOnlyList<CartSeatIdentity> Seats { get; }
        internal GolfCartMigrationSnapshot(byte[] payload, ulong id, int prefab, CartSeatIdentity[] seats)
        {
            _payload = (byte[])payload.Clone(); OriginalNetworkId = id; PrefabId = prefab;
            Seats = Array.AsReadOnly((CartSeatIdentity[])seats.Clone());
        }
        public byte[] Encode() => (byte[])_payload.Clone();
    }

    public static partial class VehiclePreservation
    {
        private const int CartSnapshotLimit = 128 * 1024;
        private static readonly JsonSerializerOptions CartSnapshotJson = new()
        {
            IncludeFields = true, MaxDepth = 32, PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };

        private sealed record CartSnapshotData(int Version, ulong OriginalNetworkId, int PrefabId, int StateTypeId,
            VehicleBodyValues Body, VehicleSaveIdentity Save,
            System.Numerics.Vector3 Velocity, float Steering, float Power, float[] WheelRpms, float[] AudioParameters,
            GolfCartDriveValues Drive, CartSeatIdentity[] Seats, CartNetworkSeed[] Transforms,
            CartRadioValues Radio);

        /// <summary>Requires a held source. Two canonical reads detect changes, not writer coverage.</summary>
        public static bool TryCaptureGolfCartSnapshot(BoltEntity source,
            out GolfCartMigrationSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "GolfCart snapshot capture is unavailable or reentrant.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                var binding = new Binding(source);
                Require(binding.Cart != null && source.isFrozen && !source.gameObject.activeSelf,
                    "A held physical GolfCart is required.");
                var first = CaptureCartSnapshotData(binding);
                byte[] bytes = EncodeCartSnapshot(first);
                Require(binding.Matches() && bytes.AsSpan().SequenceEqual(EncodeCartSnapshot(CaptureCartSnapshotData(binding)))
                    && !_reentered, "Cart state or occupant identities changed during snapshot capture.");
                snapshot = new(bytes, binding.Id, binding.Prefab, first.Seats);
                detail = "Known cart values, fresh transform seeds and stable seat identities captured; no old network/physics history included.";
                return true;
            }
            catch (Exception error) { detail = Failure("GolfCart snapshot capture rejected", error); return false; }
            finally { _busy = false; }
        }

        public static bool TryDecodeGolfCartSnapshot(ReadOnlySpan<byte> payload, ulong expectedOriginalNetworkId,
            out GolfCartMigrationSnapshot snapshot, out string detail)
        {
            snapshot = null;
            try
            {
                var data = DecodeCartSnapshot(payload, expectedOriginalNetworkId);
                snapshot = new(payload.ToArray(), data.OriginalNetworkId, data.PrefabId, data.Seats);
                detail = "Bounded known-state payload validated; live native and lifecycle preflight is still required.";
                return true;
            }
            catch (Exception error) { detail = Failure("GolfCart snapshot rejected", error); return false; }
        }

        /// <summary>
        /// Seeds known values into a fresh owned cart with exactly one storage frame. Does not
        /// rewrite save identities or certify occupant/reference repair. Rejected writes nothing;
        /// Unconfirmed can be partial and must not be replayed. Caller keeps the new cart held.
        /// </summary>
        public static BoltMigrationMutationResult TryRestoreGolfCartSnapshot(BoltEntity replacement,
            GolfCartMigrationSnapshot snapshot, out string detail)
        {
            detail = "GolfCart snapshot restoration is unavailable or reentrant.";
            if (!Enter()) return BoltMigrationMutationResult.Rejected;
            bool attempted = false;
            try
            {
                RequireContext();
                Require(snapshot != null, "A validated cart snapshot is required.");
                var data = DecodeCartSnapshot(snapshot.Encode(), snapshot.OriginalNetworkId);
                var target = new Binding(replacement);
                Require(target.Cart != null && target.Entity.isOwner && target.Id != data.OriginalNetworkId
                    && target.Prefab == data.PrefabId && target.Entity.isFrozen && !target.Entity.gameObject.activeSelf,
                    "Restore requires the held new actual owner of the captured physical-cart prefab.");
                var current = Capture(target);
                Require(target.CartState.AudioParams.Length == data.AudioParameters.Length,
                    "Replacement audio parameter schema differs from the captured cart.");
                Require((current.Blockers & (VehiclePreservationBlockers.LocalConnectionUnavailable
                    | VehiclePreservationBlockers.OpaqueSaveData | VehiclePreservationBlockers.UnresolvedReference)) == 0,
                    "Replacement contains unknown or unavailable native state.");
                _ = ResolveCartSeats(data.Seats);
                for (int i = 0; i < 4; i++)
                {
                    ulong raw = target.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                    Require(raw == data.Seats[i].NetworkId, "Bind the exact retained occupants before restoring known cart values.");
                }
                var prepared = PrepareCartNetworkSeeds(target, data.Transforms);
                var cart = new GolfCartValues(data.Velocity, data.Steering, data.Power,
                    data.WheelRpms, data.AudioParameters, data.Drive);
                Require(target.Matches() && !_reentered, "Replacement changed before known-state writes.");
                attempted = true;
                RestoreCart(target.Cart, target.CartState, cart);
                RestoreBody(target.Body, data.Body);
                RestoreCartNetworkSeeds(target, data.Transforms, prepared);
                Require(target.Matches() && !_reentered, "Replacement changed during known-state writes.");
                var read = Capture(target);
                Require(read.Body == data.Body && read.GolfCart.Drive == data.Drive
                    && read.GolfCart.Velocity == data.Velocity && read.GolfCart.SteeringWheelRotation == data.Steering
                    && read.GolfCart.Power == data.Power && Equal(read.GolfCart.WheelRpms, data.WheelRpms)
                    && Equal(read.GolfCart.AudioParameters, data.AudioParameters), "Cart known-state readback differs.");
                for (int i = 0; i < 4; i++)
                    Require(target.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue == data.Seats[i].NetworkId,
                        "Restored raw seat identity differs.");
                detail = "Known cart/body/seat values restored with fresh authoritative transform storage; save and local occupant lifecycles remain separate.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = Failure(attempted ? "Cart snapshot restoration unconfirmed; do not replay" : "Cart snapshot restoration rejected", error);
                return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private static CartSnapshotData CaptureCartSnapshotData(Binding binding)
        {
            var values = Capture(binding);
            Require((values.Blockers & (VehiclePreservationBlockers.LocalConnectionUnavailable
                | VehiclePreservationBlockers.OpaqueSaveData | VehiclePreservationBlockers.UnresolvedReference)) == 0,
                "Unresolved occupants or opaque cart state cannot be serialized.");
            var seats = new CartSeatIdentity[4];
            for (int i = 0; i < 4; i++)
            {
                ulong id = binding.CartState.ConnectedSlots.GetValueNetworkId(i).PackedValue;
                if (id == 0) { seats[i] = new(CartSeatKind.Empty, 0, 0, 0, 0, 0); continue; }
                var entity = RequireCartEndpoint(id);
                if (i == 0)
                {
                    Require(entity.TryFindState<IGolfCartState>(out var ghost) && ghost.ConnectedSlots.Length == 4,
                        "Driver slot must name a retained GolfCart ghost.");
                    ulong driverId = ghost.ConnectedSlots.GetValueNetworkId(0).PackedValue;
                    var driver = RequireCartEndpoint(driverId);
                    Require(driver.TryFindState<IPlayerState>(out _), "Ghost driver is not a player.");
                    for (int j = 1; j < 4; j++) Require(ghost.ConnectedSlots.GetValueNetworkId(j).PackedValue == 0, "Ghost passenger slot is occupied.");
                    seats[i] = new(CartSeatKind.DriverGhost, id, entity.prefabId.Value, 0, driverId, driver.prefabId.Value);
                }
                else if (entity.TryFindState<IPlayerState>(out _))
                    seats[i] = new(CartSeatKind.Player, id, entity.prefabId.Value, 0, 0, 0);
                else
                {
                    var actor = binding.Cart._localConnectedActors[i] ?? binding.Cart._externalVailActors[i];
                    Require(Live(actor) && actor._actorNetworked?._entity?.Pointer == entity.Pointer && actor.UniqueId > 0,
                        "Actor slot lacks its exact stable Vail identity.");
                    seats[i] = new(CartSeatKind.VailActor, id, entity.prefabId.Value, actor.UniqueId, 0, 0);
                }
            }
            var cart = values.GolfCart;
            return new(1, binding.Id, binding.Prefab, binding.StateType, values.Body, values.WorldItemSave,
                cart.Velocity, cart.SteeringWheelRotation, cart.Power, cart.WheelRpms.ToArray(), cart.AudioParameters.ToArray(),
                cart.Drive, seats, CaptureCartNetworkSeeds(binding), OccupantsFor(binding.Cart)?.RadioValues
                    ?? CartOccupants.CaptureRadio(binding));
        }

        private static BoltEntity RequireCartEndpoint(ulong id)
        {
            var entity = id == 0 ? null : BoltNetwork.FindEntity(new NetworkId(id));
            Require(Live(entity) && entity.isAttached && entity.networkId.PackedValue == id,
                "Exact occupant network identity is unavailable.");
            return entity;
        }

        private static BoltEntity[] ResolveCartSeats(CartSeatIdentity[] seats)
        {
            var result = new BoltEntity[4];
            for (int i = 0; i < 4; i++)
            {
                var seat = seats[i];
                if (seat.Kind == CartSeatKind.Empty) continue;
                var entity = result[i] = RequireCartEndpoint(seat.NetworkId);
                Require(entity.prefabId.Value == seat.PrefabId, "Occupant prefab changed.");
                if (seat.Kind == CartSeatKind.DriverGhost)
                {
                    var driver = RequireCartEndpoint(seat.DriverNetworkId);
                    Require(entity.TryFindState<IGolfCartState>(out var ghost) && ghost.ConnectedSlots.Length == 4
                        && ghost.ConnectedSlots.GetValueNetworkId(0).PackedValue == seat.DriverNetworkId
                        && driver.prefabId.Value == seat.DriverPrefabId && driver.TryFindState<IPlayerState>(out _),
                        "Ghost/driver relationship changed.");
                    for (int j = 1; j < 4; j++)
                        Require(ghost.ConnectedSlots.GetValueNetworkId(j).PackedValue == 0,
                            "Retained driver ghost acquired an unexpected passenger.");
                }
                else if (seat.Kind == CartSeatKind.Player)
                    Require(entity.TryFindState<IPlayerState>(out _), "Player occupant changed schema.");
                else
                {
                    var actor = entity.GetComponentInChildren<VailActor>(true);
                    Require(Live(actor) && actor.UniqueId == seat.ActorUniqueId && actor._actorNetworked?._entity?.Pointer == entity.Pointer,
                        "The stable actor ID does not match the exact retained native actor.");
                }
            }
            return result;
        }

        private static byte[] EncodeCartSnapshot(CartSnapshotData data)
        {
            ValidateCartSnapshot(data);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, CartSnapshotJson);
            Require(bytes.Length <= CartSnapshotLimit, "Cart snapshot exceeds its byte bound.");
            return bytes;
        }

        private static CartSnapshotData DecodeCartSnapshot(ReadOnlySpan<byte> bytes, ulong expectedId)
        {
            Require(expectedId != 0 && bytes.Length > 0 && bytes.Length <= CartSnapshotLimit, "Invalid cart payload size or identity.");
            var data = JsonSerializer.Deserialize<CartSnapshotData>(bytes, CartSnapshotJson);
            ValidateCartSnapshot(data);
            Require(data.OriginalNetworkId == expectedId && bytes.SequenceEqual(EncodeCartSnapshot(data)),
                "Cart payload identity or canonical schema differs (duplicate/omitted/unknown fields are rejected).");
            return data;
        }

        private static void ValidateCartSnapshot(CartSnapshotData data)
        {
            Require(data != null && data.Version == 1 && data.OriginalNetworkId != 0 && data.StateTypeId == 233
                && data.PrefabId > 0 && data.Body != null && data.Drive != null && data.Save != null && !data.Save.HasOpaqueData
                && data.Save.ObjectNameId != null && data.Save.ObjectNameId.Length <= 256 && data.Save.ItemId > 0
                && data.WheelRpms?.Length == 4 && data.AudioParameters != null && data.AudioParameters.Length <= 64 && data.Seats?.Length == 4
                && data.Transforms?.Length == 5 && data.Radio != null && data.Radio.Volume >= 0,
                "Cart snapshot schema is incomplete or exceeds its bound.");
            Require(data.Body.Mass > 0 && data.Body.Drag >= 0 && data.Body.AngularDrag >= 0
                && ValidRotation(data.Body.Rotation) && ValidRotation(data.Body.InertiaTensorRotation)
                && data.Body.InertiaTensor.X > 0 && data.Body.InertiaTensor.Y > 0 && data.Body.InertiaTensor.Z > 0
                && data.Body.MaxDepenetrationVelocity >= 0 && data.Body.SleepThreshold >= 0
                && data.Body.MaxAngularVelocity >= 0 && data.Body.MaxLinearVelocity >= 0
                && data.Body.SolverIterations > 0 && data.Body.SolverVelocityIterations > 0
                && Enum.IsDefined(typeof(CollisionDetectionMode), data.Body.CollisionDetectionMode)
                && Enum.IsDefined(typeof(RigidbodyInterpolation), data.Body.Interpolation)
                && (data.Body.Constraints & ~(int)RigidbodyConstraints.FreezeAll) == 0,
                "Cart Rigidbody policy is invalid.");
            Require(data.Radio.Present || (!data.Radio.IsOn && data.Radio.TrackHash == 0 && data.Radio.Volume == 0
                && !data.Radio.ContinuousPlayback && !data.Radio.ScreenActive && !data.Radio.ControlsActive && !data.Radio.UiActive),
                "An absent radio contains live playback state.");
            var ids = new HashSet<ulong>();
            var actors = new HashSet<int>();
            for (int i = 0; i < 4; i++)
            {
                var seat = data.Seats[i];
                Require(seat != null && Enum.IsDefined(seat.Kind), "Unknown cart seat kind.");
                if (seat.Kind == CartSeatKind.Empty)
                    Require(seat.NetworkId == 0 && seat.PrefabId == 0 && seat.ActorUniqueId == 0
                        && seat.DriverNetworkId == 0 && seat.DriverPrefabId == 0, "Empty seat has nonempty identity data.");
                else
                {
                    Require(seat.NetworkId != 0 && seat.NetworkId != data.OriginalNetworkId && ids.Add(seat.NetworkId)
                        && seat.PrefabId > 0 && (i == 0) == (seat.Kind == CartSeatKind.DriverGhost),
                        "Seat identity, uniqueness or driver role is invalid.");
                    if (seat.Kind == CartSeatKind.DriverGhost)
                        Require(seat.ActorUniqueId == 0 && seat.DriverNetworkId != 0 && seat.DriverNetworkId != data.OriginalNetworkId
                            && ids.Add(seat.DriverNetworkId) && seat.DriverPrefabId > 0, "Ghost driver identity is invalid.");
                    else Require(seat.DriverNetworkId == 0 && seat.DriverPrefabId == 0
                        && (seat.Kind == CartSeatKind.VailActor ? seat.ActorUniqueId > 0 && actors.Add(seat.ActorUniqueId)
                            : seat.ActorUniqueId == 0), "Actor/player role data is invalid.");
                }
            }
            ValidateCartNetworkSeeds(data.Transforms);
            // Strict JSON rejects nonfinite floats in every nested known value, including vectors,
            // quaternions and wheel data. Do not enable named floating-point literals in this codec.

            static bool ValidRotation(System.Numerics.Quaternion value)
                => value.LengthSquared() > 0.99f && value.LengthSquared() < 1.01f;
        }
    }
}
