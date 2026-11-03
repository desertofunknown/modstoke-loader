// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Bolt;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private const uint TransformStateMagic = 0x32545041;
        private const uint SettledStateVersion = 2;

        /// <summary>
        /// Captures six independent pose/binding records after the caller has drained meaningful actions
        /// and quiesced writers. Retained roots are exact caller-owned roles, never scene-search hints.
        /// Replication frames and old render samples are deliberately not part of this new-identity payload.
        /// </summary>
        public static bool TryCaptureTransformState(ControlPeer peer, BoltEntity source,
            IReadOnlyDictionary<string, Transform> retainedRigRoots, out byte[] payload, out string detail)
        {
            payload = null;
            detail = "Player transform capture is unavailable or reentrant.";
            if (!Enter()) return false;
            try
            {
                RequireContext(peer);
                var state = RequireState(source);
                ulong id = source.networkId.PackedValue;
                var data = CaptureTransformState(source, state, retainedRigRoots);
                byte[] encoded = EncodeTransformState(id, state.Meta.TypeId.Value, data);
                RequireCurrent(source, state, id);
                Require(encoded.AsSpan().SequenceEqual(EncodeTransformState(id, state.Meta.TypeId.Value,
                    CaptureTransformState(source, state, retainedRigRoots))), "Player transform bindings changed during capture.");
                RequireCurrent(source, state, id);
                payload = encoded;
                detail = "Captured settled transform bindings and poses; meaningful-action drain remains caller-owned.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
            finally { _busy = false; }
        }

        public static bool TryValidateTransformState(ReadOnlySpan<byte> payload, ulong expectedSourceNetworkId,
            out string detail)
        {
            try
            {
                DecodeTransformState(payload, expectedSourceNetworkId, out _);
                detail = "Transform payload is structurally valid; live bindings require restore preflight.";
                return true;
            }
            catch (Exception error) { detail = Describe(error); return false; }
        }

        /// <summary>
        /// Single-use restore on a quiescent, newly created owned identity before publication.
        /// Every external entity requires an explicit old-to-current mapping, including identity mappings.
        /// Rejected performs no writes; Unconfirmed may have written and must not be replayed.
        /// </summary>
        public static BoltMigrationMutationResult TryRestoreTransformState(
            ControlPeer peer, BoltEntity replacement, ulong expectedSourceNetworkId,
            ReadOnlySpan<byte> payload, IReadOnlyDictionary<ulong, ulong> referenceRemaps,
            IReadOnlyDictionary<string, Transform> retainedRigRoots, out string detail)
        {
            detail = "Player transform restoration is unavailable or reentrant.";
            if (!Enter()) return BoltMigrationMutationResult.Rejected;
            bool attempted = false;
            try
            {
                RequireContext(peer);
                var data = DecodeTransformState(payload, expectedSourceNetworkId, out int stateType);
                var state = RequireState(replacement);
                ulong id = replacement.networkId.PackedValue;
                Require(id != expectedSourceNetworkId && state.Meta.TypeId.Value == stateType,
                    "Transform restore requires a distinct identity with the same PlayerState schema.");
                var bindings = PrepareTransforms(replacement, data, referenceRemaps, retainedRigRoots);
                RequireCurrent(replacement, state, id);
                attempted = true;
                RestoreTransforms(state, data, bindings, referenceRemaps, retainedRigRoots);
                RequireCurrent(replacement, state, id);
                for (int i = 0; i < data.Length; i++)
                {
                    RequireTransformBinding(state, bindings[i], data[i], Transforms[i].Name);
                    VerifyRestoredPose(bindings[i].Simulate, data[i].Simulate);
                    VerifyRestoredPose(bindings[i].Render, data[i].Render);
                }
                detail = "Transform bindings, render seeds and first serialized pose/velocity were restored and read back.";
                return BoltMigrationMutationResult.Applied;
            }
            catch (Exception error)
            {
                detail = Describe(error);
                return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
            }
            finally { _busy = false; }
        }

        private static TransformData[] CaptureTransformState(BoltEntity entity, global::PlayerState state,
            IReadOnlyDictionary<string, Transform> retainedRigRoots)
        {
            ValidateRigRoots(retainedRigRoots);
            var result = new TransformData[Transforms.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = CaptureTransform(entity.transform, Transforms[i].Read(state), Transforms[i].Name, retainedRigRoots);
            return result;
        }

        private static byte[] EncodeTransformState(ulong id, int stateType, TransformData[] data)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(TransformStateMagic); writer.Write(SettledStateVersion); writer.Write(id); writer.Write(stateType);
            foreach (var transform in data) WriteTransform(writer, transform);
            Require(stream.Length <= MaximumPayloadBytes, "Transform payload exceeds its bound.");
            return stream.ToArray();
        }

        private static TransformData[] DecodeTransformState(ReadOnlySpan<byte> payload, ulong id, out int stateType)
        {
            Require(id != 0 && payload.Length >= 20 && payload.Length <= MaximumPayloadBytes, "Invalid transform payload size or identity.");
            using var stream = new MemoryStream(payload.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            Require(reader.ReadUInt32() == TransformStateMagic && reader.ReadUInt32() == SettledStateVersion
                && reader.ReadUInt64() == id, "Transform payload version or source identity differs.");
            stateType = reader.ReadInt32();
            var result = new TransformData[Transforms.Length];
            for (int i = 0; i < result.Length; i++) result[i] = ReadTransform(reader);
            Require(stream.Position == stream.Length, "Transform payload has trailing data.");
            return result;
        }

        private static void ValidateRigRoots(IReadOnlyDictionary<string, Transform> roots)
        {
            if (roots == null) return;
            Require(roots.Count <= 64, "Too many retained rig roles.");
            var pointers = new HashSet<IntPtr>();
            foreach (var pair in roots)
                Require(!string.IsNullOrEmpty(pair.Key) && Utf8.GetByteCount(pair.Key) <= 4096
                    && pair.Value != null && !pair.Value.WasCollected && pair.Value && pointers.Add(pair.Value.Pointer),
                    "Retained rig roles must name distinct live exact roots.");
        }

        private static PoseData CaptureAnchoredPose(Transform playerRoot, Transform target, string name,
            IReadOnlyDictionary<string, Transform> roots)
        {
            if (target == null) return null;
            ValidateRigRoots(roots);
            var path = new List<PathStep>();
            var cursor = target;
            while (cursor != null && !cursor.WasCollected && cursor)
            {
                // Explicit retained roles take precedence even when a retained rig is the old entity root.
                if (roots != null)
                    foreach (var pair in roots)
                        if (Same(cursor, pair.Value)) return Finish(PoseAnchor.RetainedRig, 0, 0, pair.Key);
                if (Same(cursor, playerRoot)) return Finish(PoseAnchor.Player, 0, 0, null);
                var entity = cursor.GetComponent<BoltEntity>();
                if (entity != null)
                {
                    Require(!entity.WasCollected && entity && entity.isAttached && entity.networkId.PackedValue != 0
                        && BoltNetwork.FindEntity(entity.networkId)?.Pointer == entity.Pointer,
                        name + " external entity is not an exact live identity.");
                    return Finish(PoseAnchor.Entity, entity.networkId.PackedValue, entity.prefabId.Value, null);
                }
                Require(path.Count < 64, name + " hierarchy is too deep.");
                path.Add(new(cursor.GetSiblingIndex(), cursor.name));
                cursor = cursor.parent;
            }
            throw new InvalidOperationException(name + " requires an explicit retained rig role or attached entity anchor.");

            PoseData Finish(PoseAnchor anchor, ulong entityId, int prefabId, string role)
            {
                path.Reverse();
                return new(path.ToArray(), target.position, target.rotation, target.localScale,
                    target.localPosition, target.localRotation, anchor, entityId, prefabId, role);
            }
        }

        private static Transform ResolveAnchoredPose(Transform playerRoot, PoseData pose, string name,
            IReadOnlyDictionary<ulong, ulong> remaps, IReadOnlyDictionary<string, Transform> roots)
        {
            if (pose == null) return null;
            Transform root = playerRoot;
            if (pose.Anchor == PoseAnchor.Entity)
            {
                Require(remaps != null && remaps.TryGetValue(pose.EntityId, out _), name + " has no explicit external identity remap.");
                ulong mapped = remaps[pose.EntityId];
                Require(mapped != 0, name + " cannot discard an external transform anchor.");
                var entity = ResolveEntity(mapped);
                Require(entity.prefabId.Value == pose.PrefabId, name + " external prefab identity changed.");
                root = entity.transform;
            }
            else if (pose.Anchor == PoseAnchor.RetainedRig)
            {
                ValidateRigRoots(roots);
                Require(roots != null, name + " retained rig roles are unavailable.");
                root = null;
                foreach (var pair in roots)
                    if (string.Equals(pair.Key, pose.Role, StringComparison.Ordinal)) root = pair.Value;
                Require(root != null, name + " exact retained rig role is unavailable.");
            }
            Require(root != null && !root.WasCollected && root, name + " anchor is stale.");
            return ResolvePose(root, pose, name);
        }

        private static int TransformDepth(Transform target)
        {
            int depth = 0;
            for (var cursor = target; cursor != null; cursor = cursor.parent)
                Require(!cursor.WasCollected && cursor && ++depth <= 256, "Transform hierarchy changed or is too deep.");
            return depth;
        }

        private static void VerifyRestoredPose(Transform target, PoseData pose)
        {
            if (pose == null) { Require(target == null, "An unbound transform acquired a target."); return; }
            Require(target != null && !target.WasCollected && target
                && target.position.Equals(pose.Position) && target.rotation.Equals(pose.Rotation)
                && target.localScale.Equals(pose.Scale) && target.localPosition.Equals(pose.LocalPosition)
                && target.localRotation.Equals(pose.LocalRotation), "Transform pose readback differs; do not retry partial writes.");
        }

        // The parent serializer can normalize only anchor IDs before comparing a v2 readback.
        // Self anchors and retained-role keys are already stable across the identity boundary.
        private static TransformData[] RemapTransformReadback(TransformData[] data, IReadOnlyDictionary<ulong, ulong> remaps)
        {
            var result = new TransformData[data.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = data[i] with { Simulate = Remap(data[i].Simulate), Render = Remap(data[i].Render) };
            return result;

            PoseData Remap(PoseData pose)
            {
                if (pose == null || pose.Anchor != PoseAnchor.Entity) return pose;
                Require(remaps != null && remaps.TryGetValue(pose.EntityId, out _) && remaps[pose.EntityId] != 0,
                    "Transform readback requires the same explicit external identity mapping.");
                return pose with { EntityId = remaps[pose.EntityId] };
            }
        }

        private static void WriteStateBytes(BinaryWriter writer, byte[] bytes)
        {
            Require(bytes != null && bytes.Length <= MaximumPayloadBytes, "State schema exceeds its bound.");
            writer.Write(bytes.Length); writer.Write(bytes);
        }

        private static byte[] ReadStateBytes(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            Require(count >= 0 && count <= MaximumPayloadBytes && count <= reader.BaseStream.Length - reader.BaseStream.Position,
                "State schema is truncated or exceeds its bound.");
            return reader.ReadBytes(count);
        }
    }
}
