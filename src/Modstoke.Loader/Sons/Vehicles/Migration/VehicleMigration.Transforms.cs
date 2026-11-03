// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private static readonly string[] CartTransformNames =
            { "Transform", "FrontLeftWheel", "FrontRightWheel", "BackLeftWheel", "BackRightWheel" };
        private sealed record CartSeedPose(int[] Indices, string[] Names, System.Numerics.Vector3 Position,
            System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Scale);
        private sealed record CartNetworkSeed(string Name, byte[] Schema, CartSeedPose Simulate, CartSeedPose Render,
            System.Numerics.Vector3 Velocity);
        private sealed record CartSeedBinding(NetworkTransform Network, NetworkProperty_Transform Property,
            Transform Simulate, Transform Render, IntPtr Storage);

        private static NetworkTransform[] CartNetworks(GolfCartState state) => new[]
            { state.Transform, state.FrontLeftWheel, state.FrontRightWheel, state.BackLeftWheel, state.BackRightWheel };

        private static CartNetworkSeed[] CaptureCartNetworkSeeds(Binding binding)
        {
            var networks = CartNetworks(binding.CartState);
            var result = new CartNetworkSeed[5];
            for (int i = 0; i < 5; i++)
            {
                var property = CartTransformProperty(binding.CartState, CartTransformNames[i], networks[i]);
                var simulate = networks[i].Simulate;
                RequireCartVelocitySource(property, simulate);
                var velocity = simulate is not null && property.Extrapolation.Enabled
                    ? property.CalculateVelocity(networks[i], property.GetPosition(simulate)) : UnityEngine.Vector3.zero;
                var simulatePose = CaptureCartSeedPose(simulate, binding.Entity.transform);
                var renderPose = CaptureCartSeedPose(networks[i].Render, binding.Entity.transform);
                Require(renderPose == null || simulatePose != null, "Cart render transform has no authoritative simulator.");
                if (renderPose != null) renderPose = renderPose with { Position = simulatePose.Position, Rotation = simulatePose.Rotation };
                result[i] = new(CartTransformNames[i], CartTransformSchema(property, networks[i]), simulatePose, renderPose, V(velocity));
            }
            return result;
        }

        private static CartSeedPose CaptureCartSeedPose(Transform node, Transform root)
        {
            if (node is null) return null;
            Require(Live(node) && node.IsChildOf(root), "A cart network transform refers outside its native hierarchy.");
            var indices = new List<int>(); var names = new List<string>();
            var cursor = node;
            while (cursor.Pointer != root.Pointer)
            {
                Require(Live(cursor.parent) && indices.Count < 64, "Cart network transform path is invalid.");
                indices.Add(cursor.GetSiblingIndex()); names.Add(cursor.name); cursor = cursor.parent;
            }
            indices.Reverse(); names.Reverse();
            return new(indices.ToArray(), names.ToArray(), V(node.position), Q(node.rotation), V(node.localScale));
        }

        private static Transform ResolveCartSeedPose(CartSeedPose pose, Transform root)
        {
            if (pose == null) return null;
            for (int i = 0; i < pose.Indices.Length; i++)
            {
                Require(pose.Indices[i] < root.childCount, "Replacement cart transform path is absent.");
                root = root.GetChild(pose.Indices[i]);
                Require(Live(root) && root.name == pose.Names[i], "Replacement cart transform path differs.");
            }
            return root;
        }

        private static CartSeedBinding[] PrepareCartNetworkSeeds(Binding binding, CartNetworkSeed[] data)
        {
            ValidateCartNetworkSeeds(data);
            RequireFreshCartStorage(binding.CartState);
            var networks = CartNetworks(binding.CartState);
            var result = new CartSeedBinding[5];
            for (int i = 0; i < 5; i++)
            {
                var property = CartTransformProperty(binding.CartState, data[i].Name, networks[i]);
                Require(data[i].Schema.AsSpan().SequenceEqual(CartTransformSchema(property, networks[i])),
                    "Replacement cart transform descriptor/compression differs.");
                var simulate = ResolveCartSeedPose(data[i].Simulate, binding.Entity.transform);
                var render = ResolveCartSeedPose(data[i].Render, binding.Entity.transform);
                Require((networks[i].Simulate is null || PtrEquals(networks[i].Simulate, simulate))
                    && (networks[i].Render is null || PtrEquals(networks[i].Render, render)),
                    "Replacement has an unrelated native network-transform binding.");
                RequireCartVelocitySource(property, simulate);
                result[i] = new(networks[i], property, simulate, render, binding.CartState.Storage.Pointer);
            }
            return result;
        }

        private static bool PtrEquals(UnityEngine.Object left, UnityEngine.Object right)
            => left?.Pointer == right?.Pointer;

        private static unsafe void RestoreCartNetworkSeeds(Binding binding, CartNetworkSeed[] data, CartSeedBinding[] prepared)
        {
            for (int i = 0; i < 5; i++)
            {
                var seed = data[i]; var target = prepared[i];
                ApplyPose(target.Simulate, seed.Simulate); ApplyPose(target.Render, seed.Render);
                if (target.Simulate is not null) target.Network.SetTransforms(target.Simulate, target.Render);
                else Require(target.Network.Simulate is null && target.Network.Render is null, "Absent transform acquired a binding.");
                RequireFreshCartStorage(binding.CartState);
                Require(binding.CartState.Storage.Pointer == target.Storage && binding.Matches(), "Replacement storage changed during reseed.");
                // Matched native OnSimulateAfter 0x180961DC0 writes current owner pose. Its
                // storage is new, single-frame state; no old frame or render-history pointer is used.
                target.Property.OnSimulateAfter(binding.CartState);
                byte* values = CartTransformStorage(binding.CartState, target.Property);
                UnityEngine.Vector3 position = target.Simulate is null ? UnityEngine.Vector3.zero
                    : target.Property.Space == TransformSpaces.World ? target.Simulate.position : target.Simulate.localPosition;
                UnityEngine.Quaternion rotation = target.Simulate is null ? UnityEngine.Quaternion.identity
                    : target.Property.Space == TransformSpaces.World ? target.Simulate.rotation : target.Simulate.localRotation;
                if (target.Simulate is null) { *(UnityEngine.Vector3*)values = position; *(UnityEngine.Quaternion*)(values + 24) = rotation; }
                *(UnityEngine.Vector3*)(values + 48) = U(seed.Velocity);
                binding.CartState.Storage.Set(binding.CartState.OffsetProperties + target.Property.OffsetProperties);
                var renderPosition = target.Render is null ? UnityEngine.Vector3.zero : target.Simulate.position;
                var renderRotation = target.Render is null ? UnityEngine.Quaternion.identity : target.Simulate.rotation;
                target.Network.RenderDoubleBufferPosition = new DoubleBuffer<UnityEngine.Vector3> { Previous = renderPosition, Current = renderPosition };
                target.Network.RenderDoubleBufferRotation = new DoubleBuffer<UnityEngine.Quaternion> { Previous = renderRotation, Current = renderRotation };
                Require((*(UnityEngine.Vector3*)values).Equals(position) && (*(UnityEngine.Quaternion*)(values + 24)).Equals(rotation)
                    && (*(UnityEngine.Vector3*)(values + 48)).Equals(U(seed.Velocity)), "Serialized fresh cart pose/velocity differs.");
            }
            RequireCartNetworkSeeds(binding, data);

            static void ApplyPose(Transform node, CartSeedPose pose)
            {
                if (pose == null) return;
                node.position = U(pose.Position); node.rotation = U(pose.Rotation); node.localScale = U(pose.Scale);
            }
        }

        private static void RequireCartNetworkSeeds(Binding binding, CartNetworkSeed[] data)
        {
            var networks = CartNetworks(binding.CartState);
            for (int i = 0; i < 5; i++)
            {
                var simulate = ResolveCartSeedPose(data[i].Simulate, binding.Entity.transform);
                var render = ResolveCartSeedPose(data[i].Render, binding.Entity.transform);
                Require(PtrEquals(networks[i].Simulate, simulate) && PtrEquals(networks[i].Render, render), "Fresh cart transform binding changed.");
                Check(simulate, data[i].Simulate); Check(render, data[i].Render);
                var positions = networks[i].RenderDoubleBufferPosition;
                var rotations = networks[i].RenderDoubleBufferRotation;
                var position = render is null ? UnityEngine.Vector3.zero : simulate.position;
                var rotation = render is null ? UnityEngine.Quaternion.identity : simulate.rotation;
                Require(positions != null && rotations != null && positions.Previous.Equals(position) && positions.Current.Equals(position)
                    && rotations.Previous.Equals(rotation) && rotations.Current.Equals(rotation), "Fresh cart render seed changed.");
            }
            static void Check(Transform node, CartSeedPose pose)
            {
                if (pose == null) return;
                Require(Live(node) && V(node.position) == pose.Position && Q(node.rotation) == pose.Rotation
                    && V(node.localScale) == pose.Scale, "Authoritative cart pose readback differs.");
            }
        }

        private static void ValidateCartNetworkSeeds(CartNetworkSeed[] data)
        {
            Require(data?.Length == 5, "Cart requires exactly five transform seeds.");
            var paths = new Dictionary<string, CartSeedPose>();
            for (int i = 0; i < 5; i++)
            {
                var value = data[i];
                Require(value != null && value.Name == CartTransformNames[i] && value.Schema != null
                    && value.Schema.Length > 0 && value.Schema.Length <= 512 && (value.Render == null || value.Simulate != null),
                    "Cart transform seed schema is invalid.");
                Validate(value.Simulate); Validate(value.Render);
            }
            void Validate(CartSeedPose pose)
            {
                if (pose == null) return;
                Require(pose.Indices != null && pose.Names != null && pose.Indices.Length <= 64
                    && pose.Indices.Length == pose.Names.Length && pose.Rotation.LengthSquared() > 0.99f
                    && pose.Rotation.LengthSquared() < 1.01f, "Cart transform path or quaternion is invalid.");
                for (int i = 0; i < pose.Indices.Length; i++)
                    Require(pose.Indices[i] >= 0 && pose.Indices[i] < 1024 && pose.Names[i] != null && pose.Names[i].Length <= 256,
                        "Cart transform path exceeds its bound.");
                string key = string.Join("/", pose.Indices);
                if (paths.TryGetValue(key, out var prior))
                    Require(prior.Position == pose.Position && prior.Rotation == pose.Rotation && prior.Scale == pose.Scale
                        && Equal(prior.Names, pose.Names), "A cart transform path has conflicting seed values.");
                else paths.Add(key, pose);
            }
        }

        private static void RequireFreshCartStorage(GolfCartState state)
            => Require(state.Frames != null && state.Frames.count == 1 && state.Frames.first?.Pointer == state.Storage?.Pointer
                && state.Storage?.Root?.Pointer == state.Pointer, "Cart reseeding requires the new lifetime's single unpublished storage frame.");

        private static void RequireCartVelocitySource(NetworkProperty_Transform property, Transform simulate)
        {
            if (simulate is null || !property.Extrapolation.Enabled) return;
            Require(property.Extrapolation.VelocityMode == ExtrapolationVelocityModes.CalculateFromPosition
                || (property.Extrapolation.VelocityMode == ExtrapolationVelocityModes.CopyFromRigidbody && Live(simulate.GetComponent<Rigidbody>())),
                "Cart transform has an unsupported external velocity source.");
        }

        private static unsafe NetworkProperty_Transform CartTransformProperty(GolfCartState state, string name, NetworkTransform network)
        {
            BoltProperties.RequireLayout();
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Transform).TypeHandle);
            state.GetDynamicInternal(name, out int index);
            var properties = state.Meta?.Properties; var objects = state.RootObjects;
            Require(properties != null && properties.Length <= MaximumArrayLength && index >= 0 && index < properties.Length
                && objects != null, "Cart transform descriptor is unavailable.");
            byte* info = (byte*)properties.Pointer + 32 + 32L * index;
            int objectIndex = *(int*)(info + 16); IntPtr pointer = *(IntPtr*)(info + 24);
            Require(pointer != IntPtr.Zero && objectIndex >= 0 && objectIndex < objects.Count
                && objects[objectIndex]?.Pointer == state.Pointer && state.RootState?.Pointer == state.Pointer
                && IL2CPP.il2cpp_object_get_class(pointer) == Il2CppClassPointerStore<NetworkProperty_Transform>.NativeClassPtr,
                "Cart transform is not an exact root-state descriptor.");
            var property = Il2CppObjectPool.Get<NetworkProperty_Transform>(pointer);
            Require(property.PropertyName == name && (long)state.OffsetProperties + property.OffsetProperties == index
                && (property.Space == TransformSpaces.World || property.Space == TransformSpaces.Local), "Cart transform descriptor differs.");
            byte* values = CartTransformStorage(state, property);
            Require(network != null && !network.WasCollected && *(IntPtr*)(values + 16) == network.Pointer,
                "Cart transform getter and storage object differ.");
            return property;
        }

        private static unsafe byte* CartTransformStorage(GolfCartState state, NetworkProperty_Transform property)
        {
            RuntimeHelpers.RunClassConstructor(typeof(NetworkValue).TypeHandle);
            var type = Il2CppClassPointerStore<NetworkValue>.NativeClassPtr;
            foreach (string name in new[] { "Vector3", "Quaternion", "Object" })
            {
                var field = IL2CPP.il2cpp_class_get_field_from_name(type, name);
                Require(field != IntPtr.Zero && IL2CPP.il2cpp_field_get_offset(field) == (name == "Object" ? 32 : 16),
                    "Unsupported cart transform storage layout.");
            }
            Require(NetworkProperty_Transform.POSITION == 0 && NetworkProperty_Transform.ROTATION == 1
                && NetworkProperty_Transform.VELOCITY == 2, "Transform storage slot layout changed.");
            var values = state.Storage?.Values;
            long slot = (long)state.OffsetStorage + property.OffsetStorage;
            Require(state.OffsetStorage >= 0 && property.OffsetStorage >= 0 && values != null && slot + 2 < values.Length,
                "Cart transform storage is unavailable.");
            return (byte*)values.Pointer + 32 + 24 * slot;
        }

        private static byte[] CartTransformSchema(NetworkProperty_Transform property, NetworkTransform network)
        {
            var clamper = network.Clamper; var nativeDefault = NetworkTransform.__c.__9__14_0;
            Require(clamper == null || (nativeDefault != null && clamper.Pointer == nativeDefault.Pointer),
                "Custom cart transform clampers are not transferable.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write(clamper != null); writer.Write(property.PositionMask); writer.Write(property.RotationMask);
            writer.Write(property.PositionEnabled); writer.Write(property.RotationEnabled); writer.Write((int)property.Space);
            writer.Write(property.Interpolation.Enabled); writer.Write(Finite(property.Interpolation.SnapMagnitude));
            var extrapolation = property.Extrapolation;
            writer.Write(extrapolation.Enabled); writer.Write(extrapolation.MaxFrames);
            writer.Write(Finite(extrapolation.ErrorTolerance)); writer.Write(Finite(extrapolation.SnapMagnitude));
            writer.Write((int)extrapolation.VelocityMode); WriteVector(property.PositionCompression);
            var rotation = property.RotationCompression;
            writer.Write(rotation.QuaternionMode); writer.Write(rotation.QuaternionStrictComparison);
            WriteVector(rotation.Euler); WriteFloat(rotation.Quaternion);
            return stream.ToArray();
            void WriteVector(PropertyVectorCompressionSettings value)
            { writer.Write(value.StrictComparison); WriteFloat(value.X); WriteFloat(value.Y); WriteFloat(value.Z); }
            void WriteFloat(PropertyFloatCompressionSettings value)
            { writer.Write(value._bits); writer.Write(Finite(value._pack)); writer.Write(Finite(value._read)); writer.Write(Finite(value._shift)); }
        }
    }
}
