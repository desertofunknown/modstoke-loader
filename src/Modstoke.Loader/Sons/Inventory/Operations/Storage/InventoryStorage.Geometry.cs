// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        private bool AccessCurrent()
        {
            if (_router == null || !_router.Current()) return false;
            if (Kind == TransferKind.Drop)
                return Live(_dropPrefab) && Finite(_dropRotation)
                    && _router.CheckDrop(_dropPrefab, _dropPosition);
            if (!WorldCurrent()) return false;
            return InteractionCurrent();
        }

        internal sealed partial class RequestRouter
        {
            private const int MaximumOverlaps = 64;
            private const int MaximumPrefabColliders = 32;
            private Il2CppReferenceArray<Collider> _overlaps;
            private bool _checking;

            internal bool CheckDrop(Transform prefab, Vector3 position)
                => TryDropRadius(prefab, out float radius)
                    && CheckClearance(position, System.Math.Max(radius, _clearanceRadius));

            private bool CheckClearance(Vector3 destination, float radius)
            {
                if (_checking || !Current() || !Finite(destination) || !float.IsFinite(radius)
                    || radius <= 0 || radius > _maximumReach) return false;
                _checking = true;
                int count = 0;
                try
                {
                    if (!Physics.defaultPhysicsScene.IsValid()) return false;
                    // All reads occur on the game thread; synchronize once for this decision,
                    // never accept a client-supplied position as the server interaction origin.
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    if (!_body.enabled || !_body.gameObject.activeInHierarchy) return false;
                    var bounds = _body.bounds;
                    Vector3 origin = bounds.center;
                    var extents = bounds.extents;
                    if (!Finite(origin) || !Finite(extents) || extents.x <= 0 || extents.y <= 0 || extents.z <= 0
                        || !WithinReach(origin, destination)) return false;
                    _overlaps ??= new Il2CppReferenceArray<Collider>(MaximumOverlaps);
                    count = origin.Equals(destination)
                        ? Physics.OverlapSphereNonAlloc(destination, radius, _overlaps,
                            _obstructionLayers, QueryTriggerInteraction.Ignore)
                        : Physics.OverlapCapsuleNonAlloc(origin, destination, radius, _overlaps,
                            _obstructionLayers, QueryTriggerInteraction.Ignore);
                    // NonAlloc does not establish completeness when the buffer is full.
                    if (count < 0 || count >= MaximumOverlaps) return false;
                    for (int i = 0; i < count; i++)
                    {
                        var collider = _overlaps[i];
                        if (!Live(collider) || !collider.enabled || collider.isTrigger
                            || !collider.gameObject.activeInHierarchy) return false;
                        var owner = collider.GetComponentInParent<BoltEntity>(true);
                        if (Same(owner, _actor)) continue;
                        return false;
                    }
                    return Current() && origin.Equals(_body.bounds.center);
                }
                finally
                {
                    if (_overlaps != null)
                        for (int i = 0; i < System.Math.Min(System.Math.Max(count, 0), MaximumOverlaps); i++)
                            _overlaps[i] = null;
                    _checking = false;
                }
            }

            private bool WithinReach(Vector3 origin, Vector3 destination)
            {
                double x = (double)origin.x - destination.x;
                double y = (double)origin.y - destination.y;
                double z = (double)origin.z - destination.z;
                return x * x + y * y + z * z <= (double)_maximumReach * _maximumReach;
            }

            private static bool TryDropRadius(Transform prefab, out float radius)
            {
                radius = 0;
                if (!Live(prefab) || prefab.parent != null || !Finite(prefab.position)) return false;
                var colliders = prefab.GetComponentsInChildren<Collider>(true);
                if (colliders == null || colliders.WasCollected || colliders.Length == 0
                    || colliders.Length > MaximumPrefabColliders) return false;
                Vector3 root = prefab.position;
                for (int i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    if (!Live(collider)) return false;
                    if (!collider.enabled || collider.isTrigger) continue;
                    // Prefab Collider.bounds is empty when inactive. Bound authored shape data,
                    // including nonuniformly scaled sphere/capsule physics, not a guessed size.
                    if (!TryLocalBounds(collider, out var bounds)) return false;
                    var center = bounds.center;
                    var extents = bounds.extents;
                    if (!Finite(center) || !Finite(extents) || extents.x < 0 || extents.y < 0 || extents.z < 0)
                        return false;
                    var transform = collider.transform;
                    if (!Live(transform)) return false;
                    var point = transform.TransformPoint(center);
                    if (!Finite(point)) return false;
                    float shapeRadius = transform.TransformVector(Vector3.right * extents.x).magnitude
                        + transform.TransformVector(Vector3.up * extents.y).magnitude
                        + transform.TransformVector(Vector3.forward * extents.z).magnitude;
                    float bound = (point - root).magnitude + shapeRadius;
                    if (!float.IsFinite(bound)) return false;
                    radius = System.Math.Max(radius, bound);
                }
                return float.IsFinite(radius) && radius > 0;
            }

            private static bool TryLocalBounds(Collider collider, out Bounds bounds)
            {
                bounds = default;
                var box = collider.TryCast<BoxCollider>();
                if (box != null) { bounds = new Bounds(box.center, box.size); return true; }
                var sphere = collider.TryCast<SphereCollider>();
                if (sphere != null)
                {
                    if (!float.IsFinite(sphere.radius) || sphere.radius <= 0) return false;
                    bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2));
                    return true;
                }
                var capsule = collider.TryCast<CapsuleCollider>();
                if (capsule != null)
                {
                    if (!float.IsFinite(capsule.radius) || capsule.radius <= 0
                        || !float.IsFinite(capsule.height) || capsule.height <= 0
                        || capsule.direction is < 0 or > 2) return false;
                    var size = Vector3.one * (capsule.radius * 2);
                    float length = System.Math.Max(capsule.height, capsule.radius * 2);
                    if (capsule.direction == 0) size.x = length;
                    else if (capsule.direction == 1) size.y = length;
                    else size.z = length;
                    bounds = new Bounds(capsule.center, size);
                    return true;
                }
                var mesh = collider.TryCast<MeshCollider>();
                if (mesh == null || !Live(mesh.sharedMesh)) return false;
                bounds = mesh.sharedMesh.bounds;
                return true;
            }
        }
    }
}
