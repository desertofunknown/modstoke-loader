// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Physics;
using UnityEngine;
using V3 = System.Numerics.Vector3;
using Q4 = System.Numerics.Quaternion;

namespace Modstoke.Sons
{
    public sealed partial class PlayerWaterContactAuthority
    {
        private static WaterColliderIdentity Describe(Collider collider)
        {
            if (collider is null) return null;
            if (!Alive(collider))
                return new(WaterColliderIdentityKind.DestroyedCollider, 0, 0, 0,
                    null, null, 0, 0, null, false, false, false, false,
                    default, default, default, default, default, 0, 0, 0);
            var transform = collider.transform;
            var entity = collider.GetComponentInParent<BoltEntity>(true);
            ulong id = 0; int prefab = 0, state = 0;
            var kind = WaterColliderIdentityKind.UnresolvedSceneCollider;
            if (Alive(entity) && entity.isAttached && entity.networkId.PackedValue != 0
                && Same(BoltNetwork.FindEntity(entity.networkId), entity))
            {
                var serializer = entity._entity?.Serializer?.TryCast<NetworkState>();
                if (serializer?.Meta != null)
                {
                    kind = Same(transform, entity.transform) ? WaterColliderIdentityKind.BoltRootCollider
                        : WaterColliderIdentityKind.BoltDescendantCollider;
                    id = entity.networkId.PackedValue; prefab = entity.prefabId.Value; state = serializer.Meta.TypeId.Value;
                }
            }
            else if (!Alive(entity) && collider.gameObject.scene.IsValid()
                && collider.gameObject.scene.isLoaded && !string.IsNullOrEmpty(collider.gameObject.scene.path))
                kind = WaterColliderIdentityKind.SceneCollider;
            int shape = 0, direction = 0;
            V3 center = default, size = default;
            float radius = 0, height = 0;
            var box = collider.TryCast<BoxCollider>();
            var sphere = collider.TryCast<SphereCollider>();
            var capsule = collider.TryCast<CapsuleCollider>();
            if (Alive(box)) { shape = 1; center = V(box.center); size = V(box.size); }
            else if (Alive(sphere)) { shape = 2; center = V(sphere.center); radius = sphere.radius; }
            else if (Alive(capsule))
            { shape = 3; center = V(capsule.center); radius = capsule.radius; height = capsule.height; direction = capsule.direction; }
            else if (Alive(collider.TryCast<MeshCollider>())) shape = 4;
            return new(kind, id, prefab, state, collider.gameObject.scene.path, DescribePath(transform), shape,
                collider.gameObject.layer, collider.tag, collider.enabled, collider.gameObject.activeInHierarchy,
                collider.isTrigger, Alive(collider.GetComponent<IsOcean>()), V(transform.position), Q(transform.rotation),
                V(transform.lossyScale), center, size, radius, height, direction)
            {
                Route = DescribeRoute(transform, id != 0 ? entity.transform : null),
                ComponentIndex = ColliderIndex(collider),
                GeometryDigest = GeometryDigest(collider, shape)
            };
        }

        private static string DescribePath(Transform transform)
        {
            var parts = new List<string>();
            while (Alive(transform))
            {
                if (parts.Count == 64) throw new InvalidOperationException("Water collider hierarchy exceeds its export bound.");
                parts.Add(transform.name + "[" + transform.GetSiblingIndex() + "]");
                transform = transform.parent;
            }
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static V3 V(Vector3 value) => new(value.x, value.y, value.z);
        private static Q4 Q(Quaternion value) => new(value.x, value.y, value.z, value.w);

        /// <summary>
        /// Resolves identities only on the prepared server carrier's game thread. Does not
        /// approve history, restore native sensor references or return a dry/wet result.
        /// A successful inspection may contain unresolved entries and never grants readiness.
        /// </summary>
        public static bool TryInspectServerAdmission(PlayerCarrier carrier,
            PlayerWaterContactSnapshot snapshot,
            out PlayerWaterContactAdmission admission, out string detail)
        {
            admission = null;
            detail = "Water history inspection requires the exact prepared server carrier and source profile.";
            if (!PlayerRoles.IsAvailable || !BoltNetwork.isServer || snapshot == null
                || carrier == null || carrier.IsDisposed || carrier.IsPhysicsActive || Time.inFixedTimeStep
                || !PlayerRoles.IsCurrentCarrier(carrier.Entity)
                || carrier.Profile.SourceNetworkId != snapshot.SourceNetworkId
                || carrier.Profile.PrefabId != snapshot.PrefabId || carrier.Profile.StateTypeId != snapshot.StateTypeId)
                return false;
            try
            {
                int frame = Time.frameCount;
                var results = new WaterContactIdentityStatus[snapshot.Identities.Count];
                var resolved = new Collider[results.Length];
                var seen = new HashSet<nint>();
                var issues = WaterContactContinuationIssues.LoadedWaterLifetimeUnresolved
                    | WaterContactContinuationIssues.SensorTopologyUnresolved
                    | WaterContactContinuationIssues.AuthenticatedHistoryRequired;
                for (int i = 0; i < results.Length; i++)
                {
                    results[i] = Resolve(snapshot.Identities[i], out resolved[i]);
                    if (results[i] == WaterContactIdentityStatus.Resolved && !seen.Add(resolved[i].Pointer))
                    { results[i] = WaterContactIdentityStatus.AmbiguousIdentity; resolved[i] = null; }
                    if (results[i] != WaterContactIdentityStatus.Resolved)
                        issues |= WaterContactContinuationIssues.ContactIdentityUnresolved;
                }
                if (snapshot.State.HasCaveOverride) issues |= WaterContactContinuationIssues.CaveOverrideUnresolved;
                if (snapshot.State.HasHeightHelper || snapshot.State.HasFlowHelper || snapshot.State.InOcean)
                    issues |= WaterContactContinuationIssues.OceanQueryContinuationUnresolved;
                if (!snapshot.State.HasBody || snapshot.Tracked == null || snapshot.TrackedOcean == null || snapshot.Scratch == null)
                    issues |= WaterContactContinuationIssues.NativeInitializationPending;
                if (HasNull(snapshot.Tracked) || HasNull(snapshot.TrackedOcean))
                    issues |= WaterContactContinuationIssues.NativeCleanupPending;
                foreach (var identity in snapshot.Identities)
                    if (!identity.Enabled || !identity.Active)
                        issues |= WaterContactContinuationIssues.NativeCleanupPending;
                // Re-resolve every entry against server objects before publishing the result.
                for (int i = 0; i < results.Length; i++)
                    if (results[i] == WaterContactIdentityStatus.Resolved
                        && (Resolve(snapshot.Identities[i], out var current) != results[i] || !Same(current, resolved[i])))
                        return false;
                if (Time.frameCount != frame || carrier.IsDisposed || carrier.IsPhysicsActive
                    || !PlayerRoles.IsCurrentCarrier(carrier.Entity)) return false;
                admission = new(snapshot, frame, results, issues);
                detail = "Native contact order retained; server identity inspection completed. Continuation remains unresolved: " + issues + ".";
                return true;
            }
            catch (Exception error)
            { detail = "Server water identity inspection failed (" + error.GetType().Name + ")."; return false; }
        }

        private static bool HasNull(IReadOnlyList<int> indices)
        { if (indices != null) foreach (int index in indices) if (index < 0) return true; return false; }

        private static WaterContactIdentityStatus Resolve(WaterColliderIdentity identity, out Collider collider)
        {
            collider = null;
            if (identity.Kind is WaterColliderIdentityKind.UnresolvedSceneCollider
                or WaterColliderIdentityKind.DestroyedCollider)
                return WaterContactIdentityStatus.NoStableIdentity;
            if (identity.Shape is < 1 or > 4 || identity.GeometryDigest == null)
                return WaterContactIdentityStatus.UnsupportedShape;
            if (!TryResolveTransform(identity, out var transform, out var status)) return status;
            var direct = transform.GetComponents<Collider>();
            var candidate = identity.ComponentIndex >= direct.Length ? null : direct[identity.ComponentIndex];
            if (!Alive(candidate))
                return WaterContactIdentityStatus.IdentityChanged;
            if (!candidate.gameObject.scene.IsValid() || !candidate.gameObject.scene.isLoaded)
                return WaterContactIdentityStatus.NotLoaded;
            var current = Describe(candidate);
            // A route only selects a candidate. The entire native shape, pose, filters,
            // ocean marker and mesh content must agree before any history is restored.
            if (current != identity)
                return WaterContactIdentityStatus.ConfigurationChanged;
            collider = candidate;
            return WaterContactIdentityStatus.Resolved;
        }
    }

    public enum WaterContactIdentityStatus
    { Resolved, NoStableIdentity, NotLoaded, IdentityChanged, AmbiguousIdentity, ConfigurationChanged, UnsupportedShape }

    [Flags]
    public enum WaterContactContinuationIssues
    {
        None = 0, ContactIdentityUnresolved = 1, LoadedWaterLifetimeUnresolved = 2,
        SensorTopologyUnresolved = 4, CaveOverrideUnresolved = 8, OceanQueryContinuationUnresolved = 16,
        AuthenticatedHistoryRequired = 32, NativeInitializationPending = 64, NativeCleanupPending = 128
    }

    /// <summary>
    /// Same-frame identity inspection, not a completeness lease or a water sample. Snapshot
    /// indices preserve tracked, ocean, scratch and last-contact order, including unresolved entries.
    /// </summary>
    public sealed class PlayerWaterContactAdmission
    {
        public PlayerWaterContactSnapshot Snapshot { get; }
        public int ServerFrame { get; }
        public IReadOnlyList<WaterContactIdentityStatus> Identities { get; }
        public WaterContactContinuationIssues Unresolved { get; }
        internal PlayerWaterContactAdmission(PlayerWaterContactSnapshot snapshot,
            int frame, WaterContactIdentityStatus[] identities, WaterContactContinuationIssues issues)
        { Snapshot = snapshot; ServerFrame = frame; Identities = Array.AsReadOnly(identities); Unresolved = issues; }
    }
}
