// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using Bolt;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modstoke.Sons
{
    public sealed partial class PlayerWaterContactAuthority
    {
        private static int ColliderIndex(Collider collider)
        {
            var components = collider.GetComponents<Collider>();
            for (int i = 0; i < components.Length; i++) if (Same(components[i], collider)) return i;
            throw new InvalidOperationException("Water collider component detached during capture.");
        }

        private static string DescribeRoute(Transform transform, Transform root)
        {
            var parts = new List<string>();
            while (Alive(transform) && !Same(transform, root))
            {
                if (parts.Count == 64) throw new InvalidOperationException("Water collider route exceeds its bound.");
                parts.Add(transform.GetSiblingIndex().ToString(CultureInfo.InvariantCulture));
                transform = transform.parent;
            }
            if (Alive(root) && !Same(transform, root)) throw new InvalidOperationException("Water collider left its Bolt root.");
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static bool TryResolveTransform(WaterColliderIdentity identity,
            out Transform transform, out WaterContactIdentityStatus status)
        {
            transform = null; status = WaterContactIdentityStatus.NotLoaded;
            if (identity.Route == null) return false;
            string[] parts = identity.Route.Length == 0 ? Array.Empty<string>() : identity.Route.Split('/');
            if (parts.Length > 64) return false;
            int first = 0;
            if (identity.Kind == WaterColliderIdentityKind.SceneCollider)
            {
                if (parts.Length == 0 || string.IsNullOrEmpty(identity.ScenePath)) return false;
                UnityEngine.SceneManagement.Scene selected = default; int matches = 0;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && scene.isLoaded && scene.path == identity.ScenePath)
                    { selected = scene; matches++; }
                }
                if (matches != 1)
                { if (matches > 1) status = WaterContactIdentityStatus.AmbiguousIdentity; return false; }
                if (!Index(parts[0], out int rootIndex)) return false;
                foreach (var root in selected.GetRootGameObjects())
                    if (Alive(root) && root.transform.GetSiblingIndex() == rootIndex)
                    {
                        if (Alive(transform)) { status = WaterContactIdentityStatus.AmbiguousIdentity; return false; }
                        transform = root.transform;
                    }
                first = 1;
            }
            else
            {
                var entity = BoltNetwork.FindEntity(new NetworkId(identity.NetworkId));
                if (!Alive(entity) || !entity.isAttached || entity.networkId.PackedValue != identity.NetworkId) return false;
                var serializer = entity._entity?.Serializer?.TryCast<NetworkState>();
                if (entity.prefabId.Value != identity.PrefabId || serializer?.Meta == null
                    || serializer.Meta.TypeId.Value != identity.StateTypeId)
                { status = WaterContactIdentityStatus.IdentityChanged; return false; }
                transform = entity.transform;
            }
            for (int i = first; i < parts.Length; i++)
            {
                if (!Alive(transform) || !Index(parts[i], out int index) || index >= transform.childCount) return false;
                transform = transform.GetChild(index);
            }
            return Alive(transform);
        }

        private static bool Index(string text, out int index)
            => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out index) && index >= 0;

        private static string GeometryDigest(Collider collider, int shape)
        {
            if (shape == 0) return null;
            var meshCollider = shape == 4 ? collider.TryCast<MeshCollider>() : null;
            var mesh = meshCollider?.sharedMesh;
            // Bounds or a mesh name cannot prove native triangle geometry. Non-readable
            // meshes need an asset identity owner; never relabel them as boxes/convex hulls.
            if (shape == 4 && (!Alive(mesh) || !mesh.isReadable || mesh.vertexCount > 1_000_000)) return null;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(int value)
            {
                Span<byte> bytes = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(bytes, value); hash.AppendData(bytes);
            }
            // Lossy scale alone cannot represent a sheared parent hierarchy.
            var matrix = collider.transform.localToWorldMatrix;
            for (int i = 0; i < 16; i++)
            {
                if (!float.IsFinite(matrix[i])) return null;
                Add(BitConverter.SingleToInt32Bits(matrix[i]));
            }
            if (shape == 4)
            {
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                if (triangles.Length > 3_000_000) return null;
                Add(meshCollider.convex ? 1 : 0); Add((int)meshCollider.cookingOptions);
                Add(vertices.Length); Add(triangles.Length);
                Span<byte> buffer = stackalloc byte[3072];
                int used = 0;
                foreach (var vertex in vertices)
                {
                    if (!float.IsFinite(vertex.x) || !float.IsFinite(vertex.y) || !float.IsFinite(vertex.z)) return null;
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(used), vertex.x);
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(used + 4), vertex.y);
                    BinaryPrimitives.WriteSingleLittleEndian(buffer.Slice(used + 8), vertex.z);
                    used += 12;
                    if (used == buffer.Length) { hash.AppendData(buffer); used = 0; }
                }
                if (used != 0) hash.AppendData(buffer.Slice(0, used));
                used = 0;
                foreach (int triangle in triangles)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(used), triangle); used += 4;
                    if (used == buffer.Length) { hash.AppendData(buffer); used = 0; }
                }
                if (used != 0) hash.AppendData(buffer.Slice(0, used));
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static bool SameShape(WaterColliderIdentity source,
            WaterColliderIdentity target)
            => source != null && target != null && source.Shape != 0
                && source.Shape == target.Shape && source.Layer == target.Layer && source.Tag == target.Tag
                && source.IsTrigger == target.IsTrigger && source.IsOcean == target.IsOcean
                && source.Position == target.Position && source.Rotation == target.Rotation && source.Scale == target.Scale
                && source.Center == target.Center && source.Size == target.Size && source.Radius == target.Radius
                && source.Height == target.Height && source.Direction == target.Direction
                && source.GeometryDigest == target.GeometryDigest
                && source.GeometryDigest != null;
    }
}
