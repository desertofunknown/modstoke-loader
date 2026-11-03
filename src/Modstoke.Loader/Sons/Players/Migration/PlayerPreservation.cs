// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bolt;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// A concrete replicated-state component, NOT a complete player migration adapter.
    /// The caller owns quiescence, lifecycle, subscriptions, external references and single-use restore.
    /// Capture success and payload validation do not authorize retirement or gameplay migration.
    /// </summary>
    public static partial class PlayerPreservation
    {
        public const uint PayloadVersion = 2;
        public const int MaximumPayloadBytes = 65536;
        public const bool SupportsCompletePlayerState = false;
        public const string RemainingCoverage = "Meaningful-action and knockBackTrigger drain; callback reconstruction; "
            + "unregistered protocol token types, transform roles and custom clampers. "
            + "Settled Animator preservation must run before scalar restoration. Rig, inventory, save and lifecycle preservation are separate.";

        private const uint Magic = 0x31535041;
        private const int MaximumElements = 1024;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        [ThreadStatic] private static bool _busy;
        [ThreadStatic] private static bool _reentered;

        private sealed class StateData
        {
            internal ulong SourceId, RaftId;
            internal int StateType;
            internal int[] Values;
            internal string Name;
            internal Color Color;
            internal Vector3 LoopingPosition;
            internal int[][] Integers;
            internal float[][] Floats;
            internal ItemData[] Items;
            internal WeaponData[] Weapons;
            internal int PerishableState, DismemberType, DismemberSeed;
            internal float PerishableTime;
            internal bool DismemberBloody;
            internal int StoredId, StoredCount, TokenCount;
            internal bool StoredUnique;
            internal ItemTokenData LoadedAmmo;
            internal ItemTokenData[] StoredTokens;
            internal int TransferId, TransferFrom, TransferTo;
            internal TransformData[] Transforms;
        }
        private sealed record ItemData(int Id, int Bonus, float BonusValue, int IntVal1, float FloatVal1);
        private sealed record WeaponData(int ItemId, int[] Mods);

        /// <summary>
        /// Captures independent deterministic data on the enrolled game thread while the owner is quiescent.
        /// Item-instance tokens preserve native module data; other token types, callbacks and
        /// transforms outside the supported hierarchy reject with their field name.
        /// Getter-only scalars capture the replicated sample, not the animator's state-machine history.
        /// </summary>
        public static bool TryCaptureSupportedState(ControlPeer peer, BoltEntity source,
            out byte[] payload, out string detail)
            => TryCaptureSupportedState(peer, source, null, out payload, out detail);

        public static bool TryCaptureSupportedState(ControlPeer peer, BoltEntity source,
            IReadOnlyDictionary<string, Transform> retainedRigRoots, out byte[] payload, out string detail)
        {
            payload = null;
            detail = "Player state capture is unavailable or reentrant.";
            if (!Enter()) return false;
            try
            {
                RequireContext(peer);
                ValidateRigRoots(retainedRigRoots);
                var state = RequireState(source);
                ulong id = source.networkId.PackedValue;
                var data = Capture(source, state, retainedRigRoots);
                byte[] encoded = Encode(data);
                RequireCurrent(source, state, id);
                payload = encoded;
                detail = "Captured supported player state only. " + RemainingCoverage;
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
            finally { _busy = false; }
        }

        /// <summary>Pure schema/content validation only, not a complete migration preflight.</summary>
        public static bool TryValidatePayload(ReadOnlySpan<byte> payload, ulong expectedSourceNetworkId,
            out string detail)
        {
            try
            {
                Decode(payload, expectedSourceNetworkId);
                detail = "Supported-state payload is valid; complete migration coverage is not established.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
        }

        internal static bool TryValidateMigrationState(BoltMigrationPlan plan,
            ReadOnlySpan<byte> statePayload, ReadOnlySpan<byte> animationPayload, out string detail)
        {
            try
            {
                Require(plan.IsValid, "The player migration identity is invalid.");
                var state = Decode(statePayload, plan.OldNetworkId);
                var animation = DecodeAnimation(animationPayload, plan.OldNetworkId);
                Require(state.StateType == plan.StateTypeId && animation.StateType == plan.StateTypeId,
                    "The player and animation observations must use the migration's exact state schema.");
                detail = "Correlated player and animation schemas are valid.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
        }

        /// <summary>
        /// Preflights every array, animator scalar binding, reference and transform before writing an owned replacement.
        /// Self references always remap to replacement; other ID remaps are supplied by the orchestrator.
        /// Rejected means no writes; Unconfirmed means potentially partial writes and MUST NOT be replayed.
        /// Applied confirms only this component's immediate readback, not trigger/history or lifecycle coverage.
        /// </summary>
        public static BoltMigrationMutationResult TryRestoreWritableState(
            ControlPeer peer, BoltEntity replacement, ulong expectedSourceNetworkId,
            ReadOnlySpan<byte> payload, IReadOnlyDictionary<ulong, ulong> referenceRemaps, out string detail)
            => TryRestoreWritableState(peer, replacement, expectedSourceNetworkId, payload,
                referenceRemaps, null, out detail);

        public static BoltMigrationMutationResult TryRestoreWritableState(
            ControlPeer peer, BoltEntity replacement, ulong expectedSourceNetworkId,
            ReadOnlySpan<byte> payload, IReadOnlyDictionary<ulong, ulong> referenceRemaps,
            IReadOnlyDictionary<string, Transform> retainedRigRoots, out string detail)
        {
            detail = "Player state restoration is unavailable or reentrant.";
            if (!Enter()) return BoltMigrationMutationResult.Rejected;
            bool attempted = false;
            try
            {
                RequireContext(peer);
                ValidateRigRoots(retainedRigRoots);
                var data = Decode(payload, expectedSourceNetworkId);
                var state = RequireState(replacement);
                ulong replacementId = replacement.networkId.PackedValue;
                Require(replacementId != data.SourceId, "Restore requires a distinct replacement identity.");
                Require(state.Meta.TypeId.Value == data.StateType, "PlayerState TypeId changed.");
                var current = Capture(replacement, state, retainedRigRoots);
                RequireShape(data, current);
                var animator = PrepareAnimatorScalars(replacement, state, data.Values);
                ulong raftId = data.RaftId == data.SourceId ? replacementId : data.RaftId;
                if (data.RaftId != 0 && data.RaftId != data.SourceId && referenceRemaps != null
                    && referenceRemaps.TryGetValue(data.RaftId, out ulong mapped))
                {
                    Require(mapped != 0, "RaftEntity remap cannot discard a non-null reference.");
                    raftId = mapped;
                }
                BoltEntity raft = ResolveEntity(raftId);
                // The native entity setter may skip live-to-live assignment. Never clear a different live reference.
                Require(current.RaftId == 0 || current.RaftId == raftId, "RaftEntity already refers to a different live entity.");
                var bindings = PrepareTransforms(replacement, data.Transforms, referenceRemaps, retainedRigRoots);
                var loadedAmmo = RestoreItemToken(data.LoadedAmmo);
                var storedTokens = new Bolt.IProtocolToken[data.StoredTokens.Length];
                for (int i = 0; i < storedTokens.Length; i++) storedTokens[i] = RestoreItemToken(data.StoredTokens[i]);
                RequireCurrent(replacement, state, replacementId);
                attempted = true;
                RestoreAnimatorScalars(replacement, state, replacementId, animator, data.Values);
                for (int i = 0; i < Scalars.Length; i++) Scalars[i].Write?.Invoke(state, data.Values[i]);
                state.name = data.Name;
                state.PlayerColor = data.Color;
                state.LoopingEventPosition = data.LoopingPosition;
                for (int a = 0; a < IntegerArrays.Length; a++)
                {
                    var target = IntegerArrays[a].Read(state);
                    for (int i = 0; i < data.Integers[a].Length; i++) target[i] = data.Integers[a][i];
                }
                for (int a = 0; a < FloatArrays.Length; a++)
                {
                    var target = FloatArrays[a].Read(state);
                    for (int i = 0; i < data.Floats[a].Length; i++) target[i] = data.Floats[a][i];
                }
                var items = state.SharableStorage;
                for (int i = 0; i < data.Items.Length; i++)
                {
                    var target = items[i]; var item = data.Items[i];
                    target.Id = item.Id; target.Bonus = item.Bonus; target.BonusValue = item.BonusValue;
                    target.IntVal1 = item.IntVal1; target.FloatVal1 = item.FloatVal1;
                }
                var weapons = state.HeldItemsWeaponMods;
                for (int i = 0; i < data.Weapons.Length; i++)
                {
                    var target = weapons[i]; var weapon = data.Weapons[i];
                    target.ItemId = weapon.ItemId;
                    var mods = target.WeaponModIds;
                    for (int m = 0; m < weapon.Mods.Length; m++) mods[m] = weapon.Mods[m];
                }
                var perishable = state.HeldRightHandItemPerishableState;
                perishable.CurrentState = data.PerishableState; perishable.TimeRemainingInState = data.PerishableTime;
                var dismember = state.HeldRightHandItemDismemberState;
                dismember.ActorTypeId = data.DismemberType; dismember.ActorSeed = data.DismemberSeed;
                dismember.IsBloody = data.DismemberBloody;
                var stored = state.StoringItems;
                stored.ItemId = data.StoredId; stored.Count = data.StoredCount; stored.AreUnique = data.StoredUnique;
                state.LoadedAmmoVisualInstance = loadedAmmo;
                for (int i = 0; i < storedTokens.Length; i++) stored.ItemInstances[i] = storedTokens[i];
                var transfer = state.HeldItemEquipSlotTransfer;
                transfer.ItemId = data.TransferId; transfer.FromSlot = data.TransferFrom; transfer.ToSlot = data.TransferTo;
                if (current.RaftId != raftId) state.RaftEntity = raft;
                RestoreTransforms(state, data.Transforms, bindings, referenceRemaps, retainedRigRoots);
                RequireCurrent(replacement, state, replacementId);
                VerifyAnimatorScalars(replacement, state, replacementId, animator, data.Values);
                data.SourceId = replacementId;
                data.RaftId = raftId;
                data.Transforms = RemapTransformReadback(data.Transforms, referenceRemaps);
                Require(Encode(Capture(replacement, state, retainedRigRoots)).AsSpan().SequenceEqual(Encode(data)),
                    "Player state readback differs after restoration; do not retry partial writes.");
                RequireCurrent(replacement, state, replacementId);
                detail = "Supported fields restored and read back. " + RemainingCoverage;
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = Describe(error);
                return attempted ? BoltMigrationMutationResult.Unconfirmed
                    : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private static bool Enter()
        {
            if (_busy) { _reentered = true; return false; }
            _busy = true; _reentered = false; return true;
        }

        private static void RequireContext(ControlPeer peer)
        {
            Require(peer != null
                && ClientEnrollment.TryRead(peer, out var enrollment) == EnrollmentReadStatus.Success
                && enrollment != null && ReferenceEquals(enrollment.Peer, peer)
                && enrollment.State == EnrollmentState.Enrolled
                && (enrollment.RequiredCapabilities & BoltMigrationProtocol.Capability) != 0
                && (enrollment.OfferedCapabilities & BoltMigrationProtocol.Capability) != 0,
                "The current enrolled game thread and peer with migration capability 15 are required.");
            Require(GameBuild.IsRecognized
                && HarmonySupport.CreateState()?.SafeForHarmonyPatches == true,
                "The current native game build is not qualified.");
            Require(SchemaError.Value == null, SchemaError.Value);
        }

        private static global::PlayerState RequireState(BoltEntity entity)
        {
            Require(entity != null && !entity.WasCollected && entity && entity.isAttached && entity.isOwner
                && entity.prefabId == BoltPrefabs.PlayerNetwork, "An attached owned PlayerNetwork is required.");
            var state = entity._entity?.Serializer?.TryCast<global::PlayerState>();
            Require(state != null && !state.WasCollected
                && IL2CPP.il2cpp_object_get_class(state.Pointer) == Il2CppClassPointerStore<global::PlayerState>.NativeClassPtr,
                "The exact PlayerState serializer is required.");
            RequireCurrent(entity, state, entity.networkId.PackedValue);
            return state;
        }

        private static void RequireCurrent(BoltEntity entity, global::PlayerState state, ulong id)
        {
            Require(!_reentered && id != 0 && entity && entity.isAttached && entity.isOwner
                && !entity.WasCollected && !state.WasCollected && entity.networkId.PackedValue == id
                && entity._entity?.Serializer?.Pointer == state.Pointer
                && state.Entity?.Pointer == entity._entity?.Pointer
                && BoltNetwork.FindEntity(new NetworkId(id))?.Pointer == entity.Pointer,
                "The player attachment changed or preservation was reentered.");
        }

        private static BoltEntity ResolveEntity(ulong id)
        {
            if (id == 0) return null;
            var entity = BoltNetwork.FindEntity(new NetworkId(id));
            Require(entity != null && !entity.WasCollected && entity && entity.isAttached
                && entity.networkId.PackedValue == id, "RaftEntity does not resolve to a live attached identity.");
            return entity;
        }

        private static StateData Capture(BoltEntity entity, global::PlayerState state,
            IReadOnlyDictionary<string, Transform> retainedRigRoots = null)
        {
            Require(state.OnknockBackTrigger == null, "OnknockBackTrigger callback is not serializable.");
            var data = new StateData { SourceId = entity.networkId.PackedValue, StateType = state.Meta.TypeId.Value,
                Values = new int[Scalars.Length], Name = state.name, Color = state.PlayerColor,
                LoopingPosition = state.LoopingEventPosition, Integers = new int[IntegerArrays.Length][],
                Floats = new float[FloatArrays.Length][], Transforms = new TransformData[Transforms.Length],
                LoadedAmmo = CaptureItemToken(state.LoadedAmmoVisualInstance, "LoadedAmmoVisualInstance") };
            for (int i = 0; i < Scalars.Length; i++) data.Values[i] = Scalars[i].Read(state);
            for (int a = 0; a < IntegerArrays.Length; a++) data.Integers[a] = Copy(IntegerArrays[a].Read(state), IntegerArrays[a].Name);
            for (int a = 0; a < FloatArrays.Length; a++)
            {
                var source = FloatArrays[a].Read(state);
                Require(source != null, FloatArrays[a].Name + " is missing.");
                data.Floats[a] = new float[Count(source.Length)];
                for (int i = 0; i < source.Length; i++) data.Floats[a][i] = source[i];
            }
            data.RaftId = state.GetRaftEntityNetworkIdValue().PackedValue;
            var raft = ResolveEntity(data.RaftId);
            Require((state.RaftEntity?.Pointer ?? IntPtr.Zero) == (raft?.Pointer ?? IntPtr.Zero),
                "RaftEntity raw identity and resolved getter disagree.");
            var items = state.SharableStorage;
            Require(items != null, "SharableStorage is missing.");
            data.Items = new ItemData[Count(items.Length)];
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i]; Require(item != null, "SharableStorage item is missing.");
                data.Items[i] = new(item.Id, item.Bonus, item.BonusValue, item.IntVal1, item.FloatVal1);
            }
            var weapons = state.HeldItemsWeaponMods;
            Require(weapons != null, "HeldItemsWeaponMods is missing.");
            data.Weapons = new WeaponData[Count(weapons.Length)];
            for (int i = 0; i < weapons.Length; i++)
            {
                var weapon = weapons[i]; Require(weapon != null, "HeldItemsWeaponMods item is missing.");
                data.Weapons[i] = new(weapon.ItemId, Copy(weapon.WeaponModIds, "HeldItemsWeaponMods.WeaponModIds"));
            }
            var perishable = state.HeldRightHandItemPerishableState;
            Require(perishable != null, "HeldRightHandItemPerishableState is missing.");
            data.PerishableState = perishable.CurrentState; data.PerishableTime = perishable.TimeRemainingInState;
            var dismember = state.HeldRightHandItemDismemberState;
            Require(dismember != null, "HeldRightHandItemDismemberState is missing.");
            data.DismemberType = dismember.ActorTypeId; data.DismemberSeed = dismember.ActorSeed; data.DismemberBloody = dismember.IsBloody;
            var stored = state.StoringItems;
            Require(stored != null && stored.ItemInstances != null, "StoringItems is missing.");
            data.StoredId = stored.ItemId; data.StoredCount = stored.Count; data.StoredUnique = stored.AreUnique;
            var tokens = stored.ItemInstances;
            data.TokenCount = Count(tokens.Length);
            data.StoredTokens = new ItemTokenData[data.TokenCount];
            for (int i = 0; i < tokens.Length; i++)
                data.StoredTokens[i] = CaptureItemToken(tokens[i], "StoringItems.ItemInstances[" + i + "]");
            var transfer = state.HeldItemEquipSlotTransfer;
            Require(transfer != null, "HeldItemEquipSlotTransfer is missing.");
            data.TransferId = transfer.ItemId; data.TransferFrom = transfer.FromSlot; data.TransferTo = transfer.ToSlot;
            for (int i = 0; i < Transforms.Length; i++)
                data.Transforms[i] = CaptureTransform(entity.transform, Transforms[i].Read(state), Transforms[i].Name, retainedRigRoots);
            return data;
        }

        private static int[] Copy(NetworkArray_Integer source, string name)
        {
            Require(source != null, name + " is missing.");
            var result = new int[Count(source.Length)];
            for (int i = 0; i < result.Length; i++) result[i] = source[i];
            return result;
        }

        private static void RequireShape(StateData data, StateData target)
        {
            for (int i = 0; i < IntegerArrays.Length; i++) Require(data.Integers[i].Length == target.Integers[i].Length, IntegerArrays[i].Name + " length changed.");
            for (int i = 0; i < FloatArrays.Length; i++) Require(data.Floats[i].Length == target.Floats[i].Length, FloatArrays[i].Name + " length changed.");
            Require(data.Items.Length == target.Items.Length, "SharableStorage length changed.");
            Require(data.Weapons.Length == target.Weapons.Length, "HeldItemsWeaponMods length changed.");
            for (int i = 0; i < data.Weapons.Length; i++) Require(data.Weapons[i].Mods.Length == target.Weapons[i].Mods.Length, "HeldItemsWeaponMods.WeaponModIds length changed.");
            Require(data.TokenCount == target.TokenCount, "StoringItems.ItemInstances length changed.");
            for (int i = 0; i < data.Transforms.Length; i++)
            {
                RequireBindingCompatible(data.Transforms[i].Simulate, target.Transforms[i].Simulate, Transforms[i].Name + ".Simulate");
                RequireBindingCompatible(data.Transforms[i].Render, target.Transforms[i].Render, Transforms[i].Name + ".Render");
            }
        }

        private static byte[] Encode(StateData data)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(Magic); writer.Write(PayloadVersion); writer.Write(data.SourceId); writer.Write(data.StateType);
            for (int i = 0; i < Scalars.Length; i++) { ValidateScalar(Scalars[i], data.Values[i]); writer.Write(data.Values[i]); }
            WriteString(writer, data.Name);
            WriteFloat(writer, data.Color.r); WriteFloat(writer, data.Color.g); WriteFloat(writer, data.Color.b); WriteFloat(writer, data.Color.a);
            WriteVector(writer, data.LoopingPosition); writer.Write(data.RaftId);
            foreach (var array in data.Integers) WriteIntegers(writer, array);
            foreach (var array in data.Floats) { writer.Write(Count(array.Length)); foreach (float v in array) WriteFloat(writer, v); }
            writer.Write(Count(data.Items.Length));
            foreach (var item in data.Items) { writer.Write(item.Id); writer.Write(item.Bonus); WriteFloat(writer, item.BonusValue); writer.Write(item.IntVal1); WriteFloat(writer, item.FloatVal1); }
            writer.Write(Count(data.Weapons.Length));
            foreach (var weapon in data.Weapons) { writer.Write(weapon.ItemId); WriteIntegers(writer, weapon.Mods); }
            writer.Write(data.PerishableState); WriteFloat(writer, data.PerishableTime);
            writer.Write(data.DismemberType); writer.Write(data.DismemberSeed); writer.Write(data.DismemberBloody);
            writer.Write(data.StoredId); writer.Write(data.StoredCount); writer.Write(data.StoredUnique); writer.Write(Count(data.TokenCount));
            WriteItemToken(writer, data.LoadedAmmo);
            foreach (var token in data.StoredTokens) WriteItemToken(writer, token);
            writer.Write(data.TransferId); writer.Write(data.TransferFrom); writer.Write(data.TransferTo);
            foreach (var transform in data.Transforms) WriteTransform(writer, transform);
            Require(stream.Length <= MaximumPayloadBytes, "Player state payload exceeds its bound.");
            return stream.ToArray();
        }

        private static StateData Decode(ReadOnlySpan<byte> payload, ulong expectedId)
        {
            Require(expectedId != 0 && payload.Length >= 20 && payload.Length <= MaximumPayloadBytes, "Player state payload size or expected identity is invalid.");
            Require(SchemaError.Value == null, SchemaError.Value);
            using var stream = new MemoryStream(payload.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            Require(reader.ReadUInt32() == Magic && reader.ReadUInt32() == PayloadVersion, "Player state payload version is unsupported.");
            var data = new StateData { SourceId = reader.ReadUInt64(), StateType = reader.ReadInt32(),
                Values = new int[Scalars.Length], Integers = new int[IntegerArrays.Length][],
                Floats = new float[FloatArrays.Length][], Transforms = new TransformData[Transforms.Length] };
            Require(data.SourceId == expectedId, "Player state source identity differs from the migration plan.");
            for (int i = 0; i < Scalars.Length; i++) { data.Values[i] = reader.ReadInt32(); ValidateScalar(Scalars[i], data.Values[i]); }
            data.Name = ReadString(reader);
            data.Color = new(ReadFloat(reader), ReadFloat(reader), ReadFloat(reader), ReadFloat(reader));
            data.LoopingPosition = ReadVector(reader); data.RaftId = reader.ReadUInt64();
            for (int i = 0; i < IntegerArrays.Length; i++) data.Integers[i] = ReadIntegers(reader);
            for (int a = 0; a < FloatArrays.Length; a++)
            {
                data.Floats[a] = new float[ReadCount(reader, 4)];
                for (int i = 0; i < data.Floats[a].Length; i++) data.Floats[a][i] = ReadFloat(reader);
            }
            data.Items = new ItemData[ReadCount(reader, 20)];
            for (int i = 0; i < data.Items.Length; i++) data.Items[i] = new(reader.ReadInt32(), reader.ReadInt32(), ReadFloat(reader), reader.ReadInt32(), ReadFloat(reader));
            data.Weapons = new WeaponData[ReadCount(reader, 8)];
            for (int i = 0; i < data.Weapons.Length; i++) data.Weapons[i] = new(reader.ReadInt32(), ReadIntegers(reader));
            data.PerishableState = reader.ReadInt32(); data.PerishableTime = ReadFloat(reader);
            data.DismemberType = reader.ReadInt32(); data.DismemberSeed = reader.ReadInt32(); data.DismemberBloody = ReadBoolean(reader);
            data.StoredId = reader.ReadInt32(); data.StoredCount = reader.ReadInt32(); data.StoredUnique = ReadBoolean(reader); data.TokenCount = Count(reader.ReadInt32());
            data.LoadedAmmo = ReadItemToken(reader);
            Require(data.TokenCount <= stream.Length - stream.Position, "Stored item tokens are truncated.");
            data.StoredTokens = new ItemTokenData[data.TokenCount];
            for (int i = 0; i < data.StoredTokens.Length; i++) data.StoredTokens[i] = ReadItemToken(reader);
            data.TransferId = reader.ReadInt32(); data.TransferFrom = reader.ReadInt32(); data.TransferTo = reader.ReadInt32();
            for (int i = 0; i < data.Transforms.Length; i++) data.Transforms[i] = ReadTransform(reader);
            Require(stream.Position == stream.Length, "Player state payload contains trailing data.");
            return data;
        }

        private static void ValidateScalar(Scalar field, int value)
        {
            if (field.Kind == ScalarKind.Float) Require(float.IsFinite(BitConverter.Int32BitsToSingle(value)), field.Name + " is not finite.");
            if (field.Kind == ScalarKind.Boolean) Require(value == 0 || value == 1, field.Name + " is not a canonical boolean.");
        }
        private static int Count(int count) { Require(count >= 0 && count <= MaximumElements, "Player state collection length is unsupported."); return count; }
        private static int ReadCount(BinaryReader r, int minimumElementBytes)
        {
            int count = Count(r.ReadInt32());
            Require((long)count * minimumElementBytes <= r.BaseStream.Length - r.BaseStream.Position,
                "Player state collection is truncated.");
            return count;
        }
        private static void WriteIntegers(BinaryWriter w, int[] values) { w.Write(Count(values.Length)); foreach (int v in values) w.Write(v); }
        private static int[] ReadIntegers(BinaryReader r) { var values = new int[ReadCount(r, 4)]; for (int i = 0; i < values.Length; i++) values[i] = r.ReadInt32(); return values; }
        private static void WriteFloat(BinaryWriter w, float v) { Require(float.IsFinite(v), "Player state contains a non-finite value."); w.Write(v); }
        private static float ReadFloat(BinaryReader r) { float v = r.ReadSingle(); Require(float.IsFinite(v), "Player state contains a non-finite value."); return v; }
        private static bool ReadBoolean(BinaryReader r) { byte v = r.ReadByte(); Require(v <= 1, "Player state contains a non-canonical boolean."); return v != 0; }
        private static void WriteVector(BinaryWriter w, Vector3 v) { WriteFloat(w, v.x); WriteFloat(w, v.y); WriteFloat(w, v.z); }
        private static Vector3 ReadVector(BinaryReader r) => new(ReadFloat(r), ReadFloat(r), ReadFloat(r));
        private static void WriteRotation(BinaryWriter w, Quaternion v) { WriteFloat(w, v.x); WriteFloat(w, v.y); WriteFloat(w, v.z); WriteFloat(w, v.w); }
        private static Quaternion ReadRotation(BinaryReader r) => new(ReadFloat(r), ReadFloat(r), ReadFloat(r), ReadFloat(r));
        private static void WriteString(BinaryWriter w, string value)
        {
            if (value == null) { w.Write(-1); return; }
            Require(Utf8.GetByteCount(value) <= 4096, "Player state string exceeds its bound.");
            byte[] bytes = Utf8.GetBytes(value);
            w.Write(bytes.Length); w.Write(bytes);
        }
        private static string ReadString(BinaryReader r)
        {
            int length = r.ReadInt32();
            if (length == -1) return null;
            Require(length >= 0 && length <= 4096, "Player state string length is unsupported.");
            Require(length <= r.BaseStream.Length - r.BaseStream.Position, "Player state string is truncated.");
            byte[] bytes = r.ReadBytes(length);
            Require(bytes.Length == length, "Player state string is truncated.");
            return Utf8.GetString(bytes);
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static string Describe(Exception error) => error is InvalidOperationException
            ? error.Message : "Player state preservation failed (" + error.GetType().Name + ").";
    }
}
