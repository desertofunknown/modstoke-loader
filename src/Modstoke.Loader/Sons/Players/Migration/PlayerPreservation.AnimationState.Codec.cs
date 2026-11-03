// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private const uint AnimationStateMagic = 0x32415041;

        private static byte[] EncodeAnimation(AnimationData data)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(AnimationStateMagic); writer.Write(SettledStateVersion); writer.Write(data.SourceId); writer.Write(data.StateType);
            WriteStateBytes(writer, data.ControllerSchema); WriteFloat(writer, data.Speed);
            writer.Write(Count(data.Parameters.Length));
            foreach (var p in data.Parameters)
            {
                ValidateAnimationValue(p.Kind, p.DefaultValue); ValidateAnimationValue(p.Kind, p.Value);
                WriteString(writer, p.Name); writer.Write(p.Hash); writer.Write((int)p.Kind); writer.Write(p.DefaultValue);
                writer.Write(p.Curve); writer.Write(p.Value);
            }
            writer.Write(Count(data.Layers.Length));
            foreach (var layer in data.Layers)
            {
                WriteString(writer, layer.Name); writer.Write(layer.State); writer.Write(layer.ShortName); writer.Write(layer.Tag); writer.Write(layer.Loop);
                WriteFloat(writer, layer.Time); WriteFloat(writer, layer.Weight); WriteFloat(writer, layer.Length);
                WriteFloat(writer, layer.Speed); WriteFloat(writer, layer.Multiplier);
            }
            Require(stream.Length <= MaximumPayloadBytes, "Animation payload exceeds its bound.");
            return stream.ToArray();
        }

        private static AnimationData DecodeAnimation(ReadOnlySpan<byte> payload, ulong sourceId)
        {
            Require(sourceId != 0 && payload.Length >= 20 && payload.Length <= MaximumPayloadBytes, "Invalid animation payload size or identity.");
            using var stream = new MemoryStream(payload.ToArray(), false);
            using var reader = new BinaryReader(stream, Utf8, true);
            Require(reader.ReadUInt32() == AnimationStateMagic && reader.ReadUInt32() == SettledStateVersion
                && reader.ReadUInt64() == sourceId, "Animation payload version or source identity differs.");
            int stateType = reader.ReadInt32();
            byte[] schema = ReadStateBytes(reader); ValidateControllerSchema(schema);
            float speed = ReadFloat(reader);
            var parameters = new AnimationParameter[ReadCount(reader, 21)];
            var names = new HashSet<string>(StringComparer.Ordinal);
            var hashes = new HashSet<int>();
            for (int i = 0; i < parameters.Length; i++)
            {
                string name = ReadString(reader); int hash = reader.ReadInt32();
                var kind = (AnimatorControllerParameterType)reader.ReadInt32(); int defaultValue = reader.ReadInt32();
                bool curve = ReadBoolean(reader); int value = reader.ReadInt32();
                Require(!string.IsNullOrEmpty(name) && names.Add(name) && hashes.Add(hash) && IsAnimationParameterKind(kind),
                    "Invalid or duplicate Animator parameter schema.");
                Require(kind != AnimatorControllerParameterType.Trigger || !curve, "A trigger cannot carry curve state.");
                ValidateAnimationValue(kind, defaultValue); ValidateAnimationValue(kind, value);
                parameters[i] = new(name, hash, kind, defaultValue, curve, value);
            }
            var layers = new AnimationLayer[ReadCount(reader, 37)];
            Require(layers.Length > 0, "Animation payload has no layers.");
            names.Clear();
            for (int i = 0; i < layers.Length; i++)
            {
                string name = ReadString(reader); int state = reader.ReadInt32(); int shortName = reader.ReadInt32(); int tag = reader.ReadInt32();
                bool loop = ReadBoolean(reader); float time = ReadFloat(reader); float weight = ReadFloat(reader); float length = ReadFloat(reader);
                float stateSpeed = ReadFloat(reader); float multiplier = ReadFloat(reader);
                Require(!string.IsNullOrEmpty(name) && names.Add(name) && weight >= 0f && weight <= 1f && length >= 0f,
                    "Invalid Animator layer schema or values.");
                layers[i] = new(name, state, shortName, tag, loop, time, weight, length, stateSpeed, multiplier);
            }
            Require(stream.Position == stream.Length, "Animation payload contains trailing data.");
            return new(sourceId, stateType, schema, speed, parameters, layers);
        }

        private static void ValidateAnimationValue(AnimatorControllerParameterType kind, int value)
        {
            Require(IsAnimationParameterKind(kind), "Unsupported Animator parameter type.");
            if (kind == AnimatorControllerParameterType.Float)
                Require(float.IsFinite(BitConverter.Int32BitsToSingle(value)), "Animation parameter is not finite.");
            if (kind == AnimatorControllerParameterType.Bool) Require(value == 0 || value == 1, "Non-canonical Animator boolean.");
            if (kind == AnimatorControllerParameterType.Trigger) Require(value == 0, "Pending trigger state must be drained, not serialized.");
        }

        private static byte[] CaptureControllerSchema(Animator animator)
        {
            var controller = animator.runtimeAnimatorController;
            // An override controller needs its exact source-to-override mapping, not just a clip list.
            Require(controller.TryCast<AnimatorOverrideController>() == null, "Animator override mappings are not supported by the native player schema.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            WriteString(writer, controller.GetIl2CppType().FullName); WriteString(writer, controller.name);
            var avatar = animator.avatar;
            Require(avatar == null || (!avatar.WasCollected && avatar), "Animator avatar is stale.");
            WriteString(writer, avatar?.name);
            writer.Write(animator.isHuman); writer.Write(animator.applyRootMotion);
            writer.Write((int)animator.updateMode); writer.Write((int)animator.cullingMode);
            writer.Write(animator.linearVelocityBlending); writer.Write(animator.stabilizeFeet);
            writer.Write(animator.layersAffectMassCenter); writer.Write(animator.fireEvents);
            var clips = controller.animationClips;
            Require(clips != null && !clips.WasCollected && clips.Length <= MaximumElements, "Animator clip schema is unavailable.");
            writer.Write(clips.Length);
            for (int i = 0; i < clips.Length; i++)
            {
                var clip = clips[i];
                Require(clip != null && !clip.WasCollected && clip, "Animator clip schema is stale.");
                WriteString(writer, clip.name); WriteFloat(writer, clip.length); WriteFloat(writer, clip.frameRate);
                writer.Write((int)clip.wrapMode); writer.Write(clip.legacy); writer.Write(clip.humanMotion);
            }
            Require(stream.Length <= MaximumPayloadBytes, "Animator controller schema exceeds its bound.");
            return stream.ToArray();
        }

        private static void ValidateControllerSchema(byte[] schema)
        {
            using var stream = new MemoryStream(schema, false);
            using var reader = new BinaryReader(stream, Utf8, true);
            Require(!string.IsNullOrEmpty(ReadString(reader)) && !string.IsNullOrEmpty(ReadString(reader)),
                "Controller schema identity is absent.");
            ReadString(reader); ReadBoolean(reader); ReadBoolean(reader);
            int updateMode = reader.ReadInt32(); int cullingMode = reader.ReadInt32();
            Require(updateMode >= 0 && updateMode <= 2 && cullingMode >= 0 && cullingMode <= 2, "Invalid Animator update/culling mode.");
            ReadBoolean(reader); ReadBoolean(reader); ReadBoolean(reader); ReadBoolean(reader);
            int count = ReadCount(reader, 18);
            for (int i = 0; i < count; i++)
            {
                Require(ReadString(reader) != null, "Missing Animator clip name.");
                Require(ReadFloat(reader) >= 0f && ReadFloat(reader) >= 0f, "Invalid Animator clip timing.");
                reader.ReadInt32(); ReadBoolean(reader); ReadBoolean(reader);
            }
            Require(stream.Position == stream.Length, "Controller schema has trailing data.");
        }
    }
}
