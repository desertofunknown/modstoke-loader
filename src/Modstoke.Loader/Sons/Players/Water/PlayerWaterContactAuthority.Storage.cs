// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using Crest;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Physics;
using UnityEngine;
using V3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    internal sealed record WaterContactRuntimeState(bool SensorOnBody, bool SensorOnStay,
        IReadOnlyList<WaterColliderIdentity> BodyColliders, int HeightProvider, int FlowProvider,
        IReadOnlyList<V3> HeightPositions, IReadOnlyList<V3> Heights, IReadOnlyList<V3> Normals,
        IReadOnlyList<V3> Velocities, float HeightMinimum, IReadOnlyList<V3> FlowPositions,
        IReadOnlyList<V3> Flows, float FlowMinimum);

    public sealed partial class PlayerWaterContactSnapshot
    {
        private static void WriteRuntime(BinaryWriter w, WaterContactRuntimeState s)
        {
            if (s == null) throw new InvalidDataException("Water runtime state is required.");
            w.Write(s.SensorOnBody); w.Write(s.SensorOnStay); w.Write(s.BodyColliders.Count);
            foreach (var collider in s.BodyColliders) WriteIdentity(w, collider);
            w.Write(s.HeightProvider); w.Write(s.FlowProvider);
            WriteVectors(w, s.HeightPositions); WriteVectors(w, s.Heights); WriteVectors(w, s.Normals);
            WriteVectors(w, s.Velocities); w.Write(s.HeightMinimum);
            WriteVectors(w, s.FlowPositions); WriteVectors(w, s.Flows); w.Write(s.FlowMinimum);
        }

        private static WaterContactRuntimeState ReadRuntime(BinaryReader r)
        {
            bool body = B(r), stay = B(r);
            var colliders = new WaterColliderIdentity[Count(r, MaximumContacts, false)];
            for (int i = 0; i < colliders.Length; i++) colliders[i] = ReadIdentity(r) ?? throw new InvalidDataException();
            int height = r.ReadInt32(), flow = r.ReadInt32();
            if (height is < -1 or > 2 || flow is < -1 or > 1) throw new InvalidDataException();
            return new(body, stay, Array.AsReadOnly(colliders), height, flow, ReadVectors(r), ReadVectors(r),
                ReadVectors(r), ReadVectors(r), F(r), ReadVectors(r), ReadVectors(r), F(r));
        }

        private static void WriteVectors(BinaryWriter w, IReadOnlyList<V3> values)
        { w.Write(values?.Count ?? -1); if (values != null) foreach (var value in values) V(w, value); }

        private static IReadOnlyList<V3> ReadVectors(BinaryReader r)
        {
            int count = Count(r, 16, true);
            if (count < 0) return null;
            var values = new V3[count];
            for (int i = 0; i < count; i++) values[i] = V(r);
            return Array.AsReadOnly(values);
        }
    }

    public sealed partial class PlayerWaterContactAuthority
    {
        private static WaterContactRuntimeState CaptureRuntime(WaterLevelSensor s)
        {
            var colliders = new List<WaterColliderIdentity>();
            if (Alive(s._rigidBody))
                foreach (var collider in s._rigidBody.GetComponentsInChildren<Collider>(true))
                    if (Alive(collider) && Same(collider.attachedRigidbody, s._rigidBody))
                    {
                        if (colliders.Count == PlayerWaterContactSnapshot.MaximumContacts)
                            throw new InvalidOperationException("Source water topology exceeds its export bound.");
                        colliders.Add(Describe(collider));
                    }
            var ocean = OceanRenderer._Instance_k__BackingField;
            var height = s._sampleHeightHelper;
            var flow = s._sampleFlowHelper;
            return new(Alive(s._rigidBody) && Same(s.transform, s._rigidBody.transform),
                Alive(s._stayTriggerCollider) && Same(s.transform, s._stayTriggerCollider.transform),
                colliders.AsReadOnly(), HeightProvider(ocean), FlowProvider(ocean),
                CopyVectors(height?._queryPos), CopyVectors(height?._queryResult),
                CopyVectors(height?._queryResultNormal), CopyVectors(height?._queryResultVel), height?._minLength ?? 0,
                CopyVectors(flow?._queryPos), CopyVectors(flow?._queryResult), flow?._minLength ?? 0);
        }

        private static IReadOnlyList<V3> CopyVectors(Il2CppStructArray<Vector3> values)
        {
            if (values == null) return null;
            if (values.WasCollected || values.Length > 16) throw new InvalidOperationException("Unexpected ocean helper storage.");
            var copy = new V3[values.Length];
            for (int i = 0; i < copy.Length; i++) copy[i] = V(values[i]);
            return Array.AsReadOnly(copy);
        }

        private static bool Exact<T>(Il2CppObjectBase value) where T : Il2CppObjectBase
            => value is not null && !value.WasCollected
                && IL2CPP.il2cpp_object_get_class(value.Pointer) == Il2CppClassPointerStore<T>.NativeClassPtr;

        private static int HeightProvider(OceanRenderer ocean)
        {
            if (!Alive(ocean) || ocean._CollisionProvider_k__BackingField == null) return 0;
            var provider = ocean._CollisionProvider_k__BackingField;
            return Exact<ShapeGerstnerBatched>(provider) ? 1 : Exact<CollProviderNull>(provider) ? 2 : -1;
        }

        private static int FlowProvider(OceanRenderer ocean)
        {
            if (!Alive(ocean) || ocean._FlowProvider_k__BackingField == null) return 0;
            return Exact<FlowProviderNull>(ocean._FlowProvider_k__BackingField) ? 1 : -1;
        }

        private static Il2CppStructArray<Vector3> RestoreVectors(IReadOnlyList<V3> values)
        {
            if (values == null) return null;
            var array = new Il2CppStructArray<Vector3>(values.Count);
            for (int i = 0; i < values.Count; i++) array[i] = U(values[i]);
            return array;
        }

        private static Vector3 U(V3 value) => new(value.X, value.Y, value.Z);
        private static Quaternion U(System.Numerics.Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    }
}
