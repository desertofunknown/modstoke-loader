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
        private sealed record PathStep(int Index, string Name);
        private enum PoseAnchor : byte { Player, Entity, RetainedRig }
        private sealed record PoseData(PathStep[] Path, Vector3 Position, Quaternion Rotation, Vector3 Scale,
            Vector3 LocalPosition, Quaternion LocalRotation,
            PoseAnchor Anchor = PoseAnchor.Player, ulong EntityId = 0, int PrefabId = 0, string Role = null);
        private sealed record TransformData(PoseData Simulate, PoseData Render, byte[] Schema);
        private sealed record TransformBinding(Transform Root, Transform Simulate, Transform Render,
            NetworkProperty_Transform Property, NetworkTransform Network, IntPtr Storage);

        private static TransformData CaptureTransform(Transform root, NetworkTransform source, string name,
            IReadOnlyDictionary<string, Transform> retainedRigRoots = null)
        {
            Require(source != null && !source.WasCollected, name + " is missing.");
            var state = RequireState(root.GetComponent<BoltEntity>());
            var property = ReadTransformProperty(state, name, source);
            return new(CaptureAnchoredPose(root, source.Simulate, name + ".Simulate", retainedRigRoots),
                CaptureAnchoredPose(root, source.Render, name + ".Render", retainedRigRoots),
                CaptureTransformSchema(property, source));
        }

        private static PoseData CapturePose(Transform root, Transform target, string name)
        {
            if (target == null) return null;
            Require(!target.WasCollected && target, name + " binding is stale.");
            var path = new List<PathStep>();
            var cursor = target;
            while (cursor.Pointer != root.Pointer)
            {
                Require(path.Count < 64, name + " hierarchy is too deep.");
                Require(cursor.GetComponent<BoltEntity>() == null, name + " crosses another entity's hierarchy.");
                path.Add(new(cursor.GetSiblingIndex(), cursor.name));
                cursor = cursor.parent;
                Require(cursor != null && !cursor.WasCollected && cursor,
                    name + " is bound outside the player hierarchy; external transform restoration is unsupported.");
            }
            path.Reverse();
            return new(path.ToArray(), target.position, target.rotation, target.localScale, target.localPosition, target.localRotation);
        }

        private static TransformBinding[] PrepareTransforms(BoltEntity replacement, TransformData[] data,
            IReadOnlyDictionary<ulong, ulong> referenceRemaps = null,
            IReadOnlyDictionary<string, Transform> retainedRigRoots = null)
        {
            Require(data.Length == Transforms.Length, "The player transform schema changed.");
            var state = RequireState(replacement);
            RequireFreshTransformStorage(state);
            var result = new TransformBinding[data.Length];
            var root = replacement.transform;
            var aliases = new Dictionary<IntPtr, PoseData>();
            for (int i = 0; i < data.Length; i++)
            {
                var simulate = ResolveAnchoredPose(root, data[i].Simulate, Transforms[i].Name + ".Simulate", referenceRemaps, retainedRigRoots);
                var render = ResolveAnchoredPose(root, data[i].Render, Transforms[i].Name + ".Render", referenceRemaps, retainedRigRoots);
                Require(render == null || simulate != null, Transforms[i].Name + " has a render binding without a simulation binding.");
                var network = Transforms[i].Read(state);
                var property = ReadTransformProperty(state, Transforms[i].Name, network);
                Require(CaptureTransformSchema(property, network).AsSpan().SequenceEqual(data[i].Schema),
                    Transforms[i].Name + " compression, space, masks or extrapolation schema changed.");
                Require(network.Simulate == null || Same(network.Simulate, simulate), Transforms[i].Name + " has a different live simulation binding.");
                Require(network.Render == null || Same(network.Render, render), Transforms[i].Name + " has a different live render binding.");
                CheckAlias(aliases, simulate, data[i].Simulate);
                CheckAlias(aliases, render, data[i].Render);
                if (simulate != null) TransformDepth(simulate);
                if (render != null) TransformDepth(render);
                RequireVelocitySource(property, simulate);
                result[i] = new(root, simulate, render, property, network, state.Storage.Pointer);
            }
            return result;
        }

        private static void CheckAlias(Dictionary<IntPtr, PoseData> aliases, Transform target, PoseData pose)
        {
            if (target == null) return;
            if (aliases.TryGetValue(target.Pointer, out var previous))
                Require(previous.Position.Equals(pose.Position) && previous.Rotation.Equals(pose.Rotation)
                    && previous.Scale.Equals(pose.Scale) && previous.LocalPosition.Equals(pose.LocalPosition)
                    && previous.LocalRotation.Equals(pose.LocalRotation), "Shared player transform bindings carry inconsistent poses.");
            else aliases.Add(target.Pointer, pose);
        }

        private static void RequireBindingCompatible(PoseData source, PoseData current, string name)
        {
            if (current == null) return;
            Require(source != null && source.Path.Length == current.Path.Length,
                name + " would discard an existing transform binding.");
            Require(source.Anchor == current.Anchor && source.PrefabId == current.PrefabId && source.Role == current.Role,
                name + " transform anchor schema changed.");
            for (int i = 0; i < current.Path.Length; i++)
                Require(source.Path[i] == current.Path[i], name + " would replace a different transform binding.");
        }

        private static Transform ResolvePose(Transform root, PoseData pose, string name)
        {
            if (pose == null) return null;
            Require(pose.Path.Length <= 64, name + " hierarchy is too deep.");
            RequireRotation(pose.Rotation, false);
            var target = root;
            foreach (var step in pose.Path)
            {
                Require(step.Index >= 0 && step.Index < target.childCount, name + " hierarchy does not match the replacement.");
                target = target.GetChild(step.Index);
                Require(target != null && !target.WasCollected && target && target.name == step.Name
                    && target.GetComponent<BoltEntity>() == null, name + " hierarchy does not match the replacement.");
            }
            return target;
        }

        private static void RestoreTransforms(global::PlayerState state, TransformData[] data, TransformBinding[] bindings,
            IReadOnlyDictionary<ulong, ulong> referenceRemaps = null,
            IReadOnlyDictionary<string, Transform> retainedRigRoots = null)
        {
            // World poses must be restored parent-first, including aliases shared by several Bolt fields.
            var poses = new List<(Transform Target, PoseData Data)>();
            var seen = new HashSet<IntPtr>();
            for (int i = 0; i < data.Length; i++)
            {
                var binding = bindings[i];
                Require(Same(ResolveAnchoredPose(binding.Root, data[i].Simulate, Transforms[i].Name, referenceRemaps, retainedRigRoots), binding.Simulate)
                    && Same(ResolveAnchoredPose(binding.Root, data[i].Render, Transforms[i].Name, referenceRemaps, retainedRigRoots), binding.Render),
                    Transforms[i].Name + " binding changed before restoration.");
                RequireTransformBinding(state, binding, data[i], Transforms[i].Name);
                if (binding.Simulate != null && seen.Add(binding.Simulate.Pointer)) poses.Add((binding.Simulate, data[i].Simulate));
                if (binding.Render != null && seen.Add(binding.Render.Pointer)) poses.Add((binding.Render, data[i].Render));
            }
            poses.Sort((left, right) => TransformDepth(left.Target).CompareTo(TransformDepth(right.Target)));
            foreach (var pose in poses)
            {
                pose.Target.localScale = pose.Data.Scale;
                // Preserve local-space samples without a world-to-local round trip. The world-pose
                // readback below also requires the replacement's parent relationship to be restored.
                pose.Target.localPosition = pose.Data.LocalPosition;
                pose.Target.localRotation = pose.Data.LocalRotation;
            }
            for (int i = 0; i < data.Length; i++)
            {
                var binding = bindings[i];
                RequireTransformBinding(state, binding, data[i], Transforms[i].Name);
                Require(Same(ResolveAnchoredPose(binding.Root, data[i].Simulate, Transforms[i].Name, referenceRemaps, retainedRigRoots), binding.Simulate)
                    && Same(ResolveAnchoredPose(binding.Root, data[i].Render, Transforms[i].Name, referenceRemaps, retainedRigRoots), binding.Render),
                    Transforms[i].Name + " hierarchy changed during pose restoration.");
                VerifyRestoredPose(binding.Simulate, data[i].Simulate);
                VerifyRestoredPose(binding.Render, data[i].Render);
                var target = binding.Network;
                // Both-null records were preflighted against empty target bindings. No native clear is needed.
                if (bindings[i].Simulate != null) target.SetTransforms(bindings[i].Simulate, bindings[i].Render);
                else Require(target.Simulate == null && target.Render == null, Transforms[i].Name + " unexpectedly acquired a binding.");
                // SetTransforms seeds render samples; owner OnSimulateAfter separately publishes
                // the configured-space pose. No old frame or interpolation sample crosses identities.
                ReseedTransformStorage(state, binding, data[i], Transforms[i].Name);
            }
        }

        private static bool Same(Transform left, Transform right) => (left?.Pointer ?? IntPtr.Zero) == (right?.Pointer ?? IntPtr.Zero);

        private static void WriteTransform(BinaryWriter writer, TransformData data)
        {
            WritePose(writer, data.Simulate); WritePose(writer, data.Render);
            WriteStateBytes(writer, data.Schema);
        }
        private static TransformData ReadTransform(BinaryReader reader)
        {
            var data = new TransformData(ReadPose(reader), ReadPose(reader), ReadStateBytes(reader));
            Require(data.Render == null || data.Simulate != null, "A render binding requires a simulation binding.");
            ValidateTransformSchema(data.Schema);
            return data;
        }

        private static void RequireRotation(Quaternion rotation, bool allowZero)
        {
            float norm = rotation.x * rotation.x + rotation.y * rotation.y
                + rotation.z * rotation.z + rotation.w * rotation.w;
            Require(float.IsFinite(norm) && ((allowZero && norm == 0f) || MathF.Abs(norm - 1f) <= 0.0001f),
                "Player transform rotation must be unit length (or an unbound zero buffer).");
        }

        private static void WritePose(BinaryWriter writer, PoseData pose)
        {
            writer.Write(pose != null);
            if (pose == null) return;
            RequireRotation(pose.Rotation, false);
            Require(pose.Path.Length <= 64, "Player transform hierarchy is too deep.");
            writer.Write((byte)pose.Anchor); writer.Write(pose.EntityId); writer.Write(pose.PrefabId); WriteString(writer, pose.Role);
            writer.Write(pose.Path.Length);
            foreach (var step in pose.Path) { writer.Write(step.Index); WriteString(writer, step.Name); }
            WriteVector(writer, pose.Position); WriteRotation(writer, pose.Rotation); WriteVector(writer, pose.Scale);
            WriteVector(writer, pose.LocalPosition); RequireRotation(pose.LocalRotation, false); WriteRotation(writer, pose.LocalRotation);
        }
        private static PoseData ReadPose(BinaryReader reader)
        {
            if (!ReadBoolean(reader)) return null;
            var anchor = (PoseAnchor)reader.ReadByte();
            ulong entityId = reader.ReadUInt64(); int prefabId = reader.ReadInt32(); string role = ReadString(reader);
            Require(anchor == PoseAnchor.Player ? entityId == 0 && prefabId == 0 && role == null
                : anchor == PoseAnchor.Entity ? entityId != 0 && role == null
                : anchor == PoseAnchor.RetainedRig && entityId == 0 && prefabId == 0 && !string.IsNullOrEmpty(role),
                "Player transform anchor is invalid.");
            int length = reader.ReadInt32();
            Require(length >= 0 && length <= 64, "Player transform path length is unsupported.");
            Require((long)length * 8 + 68 <= reader.BaseStream.Length - reader.BaseStream.Position,
                "Player transform pose is truncated.");
            var path = new PathStep[length];
            for (int i = 0; i < length; i++)
            {
                int index = reader.ReadInt32(); string name = ReadString(reader);
                Require(index >= 0 && name != null, "Player transform path is invalid.");
                path[i] = new(index, name);
            }
            var pose = new PoseData(path, ReadVector(reader), ReadRotation(reader), ReadVector(reader),
                ReadVector(reader), ReadRotation(reader), anchor, entityId, prefabId, role);
            RequireRotation(pose.Rotation, false);
            RequireRotation(pose.LocalRotation, false);
            return pose;
        }
    }
}
