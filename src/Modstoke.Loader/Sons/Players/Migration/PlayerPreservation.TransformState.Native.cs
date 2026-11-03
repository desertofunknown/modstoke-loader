// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private static unsafe NetworkProperty_Transform ReadTransformProperty(global::PlayerState state, string name, NetworkTransform network)
        {
            BoltProperties.RequireLayout();
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Transform).TypeHandle);
            state.GetDynamicInternal(name, out int index);
            var properties = state.Meta?.Properties;
            var objects = state.RootObjects;
            Require(properties != null && !properties.WasCollected && properties.Length <= MaximumElements
                && index >= 0 && index < properties.Length && objects != null && !objects.WasCollected,
                name + " transform descriptor is unavailable.");
            byte* info = (byte*)properties.Pointer + 32 + 32L * index;
            int objectIndex = *(int*)(info + 16);
            IntPtr pointer = *(IntPtr*)(info + 24);
            Require(pointer != IntPtr.Zero && objectIndex >= 0 && objectIndex < objects.Count
                && objects[objectIndex]?.Pointer == state.Pointer && state.RootState?.Pointer == state.Pointer
                && IL2CPP.il2cpp_object_get_class(pointer) == Il2CppClassPointerStore<NetworkProperty_Transform>.NativeClassPtr,
                name + " is not an exact root transform property.");
            var property = Il2CppObjectPool.Get<NetworkProperty_Transform>(pointer);
            Require(property.PropertyName == name && (long)state.OffsetProperties + property.OffsetProperties == index,
                name + " transform descriptor changed.");
            Require(property.Space == TransformSpaces.World || property.Space == TransformSpaces.Local,
                name + " transform space is unsupported.");
            byte* values = TransformStorage(state, property);
            // NetworkValue contains primitive pose fields followed by an object reference at +16.
            Require(network != null && !network.WasCollected && *(IntPtr*)(values + 16) == network.Pointer,
                name + " transform getter and storage binding disagree.");
            return property;
        }

        private static unsafe byte* TransformStorage(global::PlayerState state, NetworkProperty_Transform property)
        {
            var type = Il2CppClassPointerStore<NetworkValue>.NativeClassPtr;
            foreach (string field in new[] { "Vector3", "Quaternion" })
            {
                IntPtr nativeField = IL2CPP.il2cpp_class_get_field_from_name(type, field);
                Require(nativeField != IntPtr.Zero && IL2CPP.il2cpp_field_get_offset(nativeField) == 16,
                    "Unsupported transform storage layout.");
            }
            IntPtr objectField = IL2CPP.il2cpp_class_get_field_from_name(type, "Object");
            Require(objectField != IntPtr.Zero && IL2CPP.il2cpp_field_get_offset(objectField) == 32,
                "Unsupported transform reference storage layout.");
            Require(NetworkProperty_Transform.POSITION == 0 && NetworkProperty_Transform.ROTATION == 1
                && NetworkProperty_Transform.VELOCITY == 2, "Transform storage offsets changed.");
            var values = state.Storage?.Values;
            long slot = (long)state.OffsetStorage + property.OffsetStorage;
            Require(state.OffsetStorage >= 0 && property.OffsetStorage >= 0 && values != null && !values.WasCollected
                && slot + 2 < values.Length, "Transform storage is unavailable.");
            return (byte*)values.Pointer + 32 + 24 * slot;
        }

        private static void RequireFreshTransformStorage(global::PlayerState state)
        {
            var frames = state.Frames;
            Require(frames != null && !frames.WasCollected && frames.count == 1
                && frames.first?.Pointer == state.Storage?.Pointer && state.Storage?.Root?.Pointer == state.Pointer,
                "Transform reseeding requires the replacement's single current storage frame before publication.");
        }

        private static void RequireTransformBinding(global::PlayerState state, TransformBinding binding, TransformData data, string name)
        {
            var entity = binding.Root.GetComponent<BoltEntity>();
            RequireCurrent(entity, state, entity.networkId.PackedValue);
            RequireFreshTransformStorage(state);
            var property = ReadTransformProperty(state, name, binding.Network);
            Require(property.Pointer == binding.Property.Pointer && state.Storage.Pointer == binding.Storage
                && CaptureTransformSchema(property, binding.Network).AsSpan().SequenceEqual(data.Schema),
                name + " transform descriptor or storage changed during restoration.");
        }

        private static void RequireVelocitySource(NetworkProperty_Transform property, Transform simulate)
        {
            if (simulate == null || !property.Extrapolation.Enabled) return;
            switch (property.Extrapolation.VelocityMode)
            {
                case ExtrapolationVelocityModes.CalculateFromPosition: break;
                case ExtrapolationVelocityModes.CopyFromRigidbody:
                    Require(simulate.GetComponent<Rigidbody>() != null, "Transform extrapolation requires its exact Rigidbody."); break;
                case ExtrapolationVelocityModes.CopyFromRigidbody2D:
                    throw new InvalidOperationException("The native 3D player transform schema does not support Rigidbody2D extrapolation.");
                case ExtrapolationVelocityModes.CopyFromCharacterController:
                    Require(simulate.GetComponent<CharacterController>() != null, "Transform extrapolation requires its exact CharacterController."); break;
                default: throw new InvalidOperationException("Unsupported transform velocity mode.");
            }
        }

        private static unsafe void ReseedTransformStorage(global::PlayerState state, TransformBinding binding, TransformData data, string name)
        {
            var property = binding.Property;
            var network = binding.Network;
            var simulate = binding.Simulate;
            // Qualified GameAssembly: OnInit 0x180960FC0 allocates the NetworkTransform and identity
            // rotation. Do not call it again. OnSimulateAfter 0x180961DC0 writes owner pose slots 0/1
            // in Space and marks changes. Write 0x180961700 serializes those slots, not render samples.
            property.OnSimulateAfter(state);
            RequireTransformBinding(state, binding, data, name);
            Require(Same(network.Simulate, simulate) && Same(network.Render, binding.Render), name + " native transform bindings differ.");
            // Owner storage holds the full pose. GetPosition/GetRotation instead apply axis masks;
            // compression applies the matching masks later when Write packs the storage values.
            Vector3 position = simulate == null ? Vector3.zero
                : property.Space == TransformSpaces.World ? simulate.position : simulate.localPosition;
            Quaternion rotation = simulate == null ? Quaternion.identity
                : property.Space == TransformSpaces.World ? simulate.rotation : simulate.localRotation;
            RequireRotation(rotation, false);
            // CalculateVelocity 0x1809632C0 uses current-minus-supplied position or the configured
            // native physics source. Its position input must use the same masks as GetPosition.
            Vector3 velocity = simulate != null && property.Extrapolation.Enabled
                ? property.CalculateVelocity(network, property.GetPosition(simulate)) : Vector3.zero;
            Require(float.IsFinite(velocity.x) && float.IsFinite(velocity.y) && float.IsFinite(velocity.z),
                name + " native velocity seed is not finite.");
            RequireTransformBinding(state, binding, data, name);
            byte* values = TransformStorage(state, property);
            if (simulate == null)
            {
                *(Vector3*)values = position;
                *(Quaternion*)(values + 24) = rotation;
            }
            *(Vector3*)(values + 48) = velocity;
            state.Storage.Set(state.OffsetProperties + property.OffsetProperties);
            RequireTransformBinding(state, binding, data, name);
            values = TransformStorage(state, property);
            Require((*(Vector3*)values).Equals(position) && (*(Quaternion*)(values + 24)).Equals(rotation)
                && (*(Vector3*)(values + 48)).Equals(velocity), name + " serialized transform seed differs.");
            if (simulate != null)
            {
                var positions = network.RenderDoubleBufferPosition;
                var rotations = network.RenderDoubleBufferRotation;
                Vector3 renderPosition = binding.Render == null ? Vector3.zero : simulate.position;
                Quaternion renderRotation = binding.Render == null ? Quaternion.identity : simulate.rotation;
                Require(positions != null && rotations != null
                    && positions.Previous.Equals(renderPosition) && positions.Current.Equals(renderPosition)
                    && rotations.Previous.Equals(renderRotation) && rotations.Current.Equals(renderRotation),
                    name + " native render sample reseeding differs.");
            }
        }

        private static byte[] CaptureTransformSchema(NetworkProperty_Transform property, NetworkTransform network)
        {
            var clamper = network.Clamper;
            var nativeDefault = NetworkTransform.__c.__9__14_0;
            Require(clamper == null || (!clamper.WasCollected && nativeDefault != null
                && clamper.Pointer == nativeDefault.Pointer), "Custom transform clamper delegates are not transferable.");
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Utf8, true);
            writer.Write(clamper != null);
            writer.Write(property.PositionMask); writer.Write(property.RotationMask);
            writer.Write(property.PositionEnabled); writer.Write(property.RotationEnabled); writer.Write((int)property.Space);
            writer.Write(property.Interpolation.Enabled); WriteFloat(writer, property.Interpolation.SnapMagnitude);
            var extrapolation = property.Extrapolation;
            writer.Write(extrapolation.Enabled); writer.Write(extrapolation.MaxFrames);
            WriteFloat(writer, extrapolation.ErrorTolerance); WriteFloat(writer, extrapolation.SnapMagnitude);
            writer.Write((int)extrapolation.VelocityMode);
            WriteCompression(writer, property.PositionCompression);
            var rotation = property.RotationCompression;
            writer.Write(rotation.QuaternionMode); writer.Write(rotation.QuaternionStrictComparison);
            WriteCompression(writer, rotation.Euler); WriteCompression(writer, rotation.Quaternion);
            return stream.ToArray();
        }

        private static void WriteCompression(BinaryWriter writer, PropertyVectorCompressionSettings value)
        {
            writer.Write(value.StrictComparison);
            WriteCompression(writer, value.X); WriteCompression(writer, value.Y); WriteCompression(writer, value.Z);
        }

        private static void WriteCompression(BinaryWriter writer, PropertyFloatCompressionSettings value)
        {
            writer.Write(value._bits); WriteFloat(writer, value._pack); WriteFloat(writer, value._read); WriteFloat(writer, value._shift);
        }

        private static void ValidateTransformSchema(byte[] schema)
        {
            using var stream = new MemoryStream(schema, false);
            using var reader = new BinaryReader(stream, Utf8, true);
            ReadBoolean(reader); reader.ReadInt32(); reader.ReadInt32(); ReadBoolean(reader); ReadBoolean(reader);
            int space = reader.ReadInt32(); Require(space == 0 || space == 1, "Invalid transform space.");
            ReadBoolean(reader); ReadFloat(reader); ReadBoolean(reader); reader.ReadInt32(); ReadFloat(reader); ReadFloat(reader);
            int mode = reader.ReadInt32(); Require(mode >= 0 && mode <= 3, "Invalid extrapolation mode.");
            ReadVectorCompression(); ReadBoolean(reader); ReadBoolean(reader); ReadVectorCompression(); ReadCompression();
            Require(stream.Position == stream.Length, "Transform schema contains trailing data.");
            void ReadVectorCompression() { ReadBoolean(reader); ReadCompression(); ReadCompression(); ReadCompression(); }
            void ReadCompression() { reader.ReadInt32(); ReadFloat(reader); ReadFloat(reader); ReadFloat(reader); }
        }
    }
}
