// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class InventoryStorage
    {
        internal readonly record struct InteractionPose(ulong Actor, int Frame, ulong Revision,
            Vector3 CameraPosition, Quaternion CameraRotation);

        // The owning motor must supply its current camera frame, not a client-reported origin,
        // host LocalPlayer transform, or a root offset frozen at migration time.
        internal delegate bool ReadInteractionPose(out InteractionPose pose);

        private Collider[] _interactionTargets;
        private int[] _interactionTargetIds;
        private InWorldLayoutItemGroup _interactionGroup;

        private bool CapturePickupInteraction()
        {
            if (!Live(_pickup) || _router == null) return false;
            var colliders = _pickup.GetComponentsInChildren<Collider>(true);
            if (colliders == null || colliders.WasCollected || colliders.Length is < 1 or > 32) return false;
            int count = 0;
            for (int i = 0; i < colliders.Length; i++)
                if (PickupTriggerCurrent(colliders[i])) count++;
            if (count == 0) return false;
            _interactionTargets = new Collider[count];
            _interactionTargetIds = new int[count];
            for (int i = 0, next = 0; i < colliders.Length; i++)
            {
                var collider = colliders[i];
                if (!PickupTriggerCurrent(collider)) continue;
                _interactionTargets[next] = collider;
                _interactionTargetIds[next++] = collider.GetInstanceID();
            }
            return true;
        }

        private bool CaptureStorageInteraction()
        {
            if (!Live(_slot) || !Live(_world) || _router == null) return false;
            InWorldLayoutItemGroup selected = null;
            var groups = _world.GetComponentsInChildren<InWorldLayoutItemGroup>(true);
            if (groups == null || groups.WasCollected || groups.Length is < 1 or > 128) return false;
            for (int i = 0; i < groups.Length; i++)
            {
                var group = groups[i];
                if (!Live(group) || group._itemId != Action.ItemId || !Same(group._storage, _slot)) continue;
                if (selected != null) return false;
                selected = group;
            }
            if (!WorldGroupCurrent(selected, Kind == TransferKind.Deposit)
                || !selected.isActiveAndEnabled || IsWorldVariant && !Same(selected, _variant.Group)) return false;
            var collider = selected._interactionCollider;
            if (!InteractionTriggerCurrent(collider)) return false;
            _interactionGroup = selected;
            _interactionTargets = new[] { collider };
            _interactionTargetIds = new[] { collider.GetInstanceID() };
            return true;
        }

        private bool InteractionTriggerCurrent(Collider collider)
            => Live(collider) && collider.isTrigger && collider.gameObject.activeInHierarchy
                && collider.gameObject.layer == _router.InteractionTargetLayer
                && Same(collider.GetComponentInParent<BoltEntity>(true), _world);

        private bool PickupTriggerCurrent(Collider collider)
            => InteractionTriggerCurrent(collider) && collider.enabled
                && Same(collider.GetComponentInParent<PickUp>(true), _pickup);

        private bool InteractionCurrent()
        {
            if (_interactionTargets == null || _interactionTargetIds == null
                || _interactionTargets.Length == 0 || _interactionTargets.Length != _interactionTargetIds.Length) return false;
            bool pickup = Kind == TransferKind.Pickup;
            if (!pickup && (!WorldGroupCurrent(_interactionGroup, Kind == TransferKind.Deposit)
                || !_interactionGroup.isActiveAndEnabled || !Same(_interactionGroup._storage, _slot)
                || _interactionGroup._itemId != Action.ItemId
                || !Same(_interactionGroup._interactionCollider, _interactionTargets[0])
                || IsWorldVariant && !Same(_interactionGroup, _variant.Group))) return false;
            for (int i = 0; i < _interactionTargets.Length; i++)
            {
                var collider = _interactionTargets[i];
                if (!InteractionTriggerCurrent(collider) || collider.GetInstanceID() != _interactionTargetIds[i]
                    || pickup && !Same(collider.GetComponentInParent<PickUp>(true), _pickup)) return false;
            }
            return _router.CheckInteraction(_interactionTargets, pickup ? null : _interactionGroup);
        }

        internal sealed partial class RequestRouter
        {
            private readonly PlayerCarrierProfile _interactionProfile;
            private readonly ReadInteractionPose _readInteractionPose;
            private GameObject _interactionQueryObject;
            private BoxCollider _interactionQuery;
            internal int InteractionTargetLayer => _interactionProfile.Interaction.TargetLayer;

            internal bool PrepareInteractionQuery(out string detail)
            {
                detail = "The approved compound grabber and current authoritative camera frame are required.";
                try
                {
                    if (!Current() || !TryReadInteractionFrame(out _)) return false;
                    _interactionQueryObject = new GameObject("OpenACAI Interaction Query");
                    _interactionQueryObject.SetActive(false);
                    _interactionQueryObject.hideFlags = HideFlags.HideAndDontSave;
                    _interactionQuery = _interactionQueryObject.AddComponent<BoxCollider>();
                    _interactionQuery.enabled = false;
                    _interactionQuery.isTrigger = true;
                    _interactionQuery.center = Vector3.zero;
                    detail = "Compound interaction query prepared without enabling a scene collider.";
                    return Live(_interactionQuery) && Current();
                }
                catch (Exception error)
                { detail = "Interaction query preparation failed (" + error.GetType().Name + ")."; return false; }
            }

            private bool TryReadInteractionFrame(out InteractionPose pose)
            {
                pose = default;
                return _readInteractionPose != null && _readInteractionPose(out pose)
                    && pose.Actor == _actor.networkId.PackedValue && pose.Frame == Time.frameCount && pose.Revision != 0
                    && Finite(pose.CameraPosition) && UnitRotation(pose.CameraRotation);
            }

            internal bool CheckInteraction(Collider[] targets, InWorldLayoutItemGroup group)
            {
                if (_checking || !Current() || !Live(_interactionQuery) || !Live(_interactionQueryObject)
                    || _interactionQuery.enabled || _interactionQueryObject.activeSelf) return false;
                _checking = true;
                try
                {
                    if (!TryReadInteractionFrame(out var pose) || !Current()) return false;
                    var root = _actor.transform;
                    Vector3 rootPosition = root.position;
                    Quaternion rootRotation = root.rotation;
                    if (!Finite(rootPosition) || !UnitRotation(rootRotation)) return false;
                    bool facing = Live(group) && group._mustBeFacingInteraction;
                    if (facing)
                    {
                        float threshold = group._facingThreshold;
                        var forward = group.transform.forward;
                        if (!float.IsFinite(threshold) || threshold < 0 || threshold > 180 || !Finite(forward)
                            || forward.sqrMagnitude < 0.99f
                            || Vector3.Angle(forward, -(rootRotation * Vector3.forward)) > threshold) return false;
                    }
                    for (int i = 0; i < targets.Length; i++)
                    {
                        var target = targets[i];
                        // Native ValidateFacing toggles this collider using the HOST camera.
                        // ComputePenetration reads geometry without borrowing that global permission.
                        if (!Live(target) || !target.gameObject.activeInHierarchy || !target.isTrigger
                            || target.gameObject.layer != InteractionTargetLayer || !facing && !target.enabled) continue;
                        var transform = target.transform;
                        Vector3 position = transform.position;
                        Quaternion rotation = transform.rotation;
                        if (!Finite(position) || !UnitRotation(rotation)) return false;
                        var shapes = _interactionProfile.Interaction;
                        bool overlap = Overlaps(shapes.FirstBox.Center, shapes.FirstBox.HalfExtents,
                            shapes.FirstBox.Rotation, pose, target, position, rotation)
                            || Overlaps(shapes.SecondBox.Center, shapes.SecondBox.HalfExtents,
                                shapes.SecondBox.Rotation, pose, target, position, rotation);
                        if (!overlap) continue;
                        return Current() && root.position.Equals(rootPosition) && root.rotation.Equals(rootRotation)
                            && Live(target) && transform.position.Equals(position) && transform.rotation.Equals(rotation)
                            && TryReadInteractionFrame(out var after) && after == pose;
                    }
                    return false;
                }
                finally { _checking = false; }
            }

            private bool Overlaps(Vector3 center, Vector3 halfExtents, Quaternion rotation,
                in InteractionPose pose, Collider target, Vector3 targetPosition, Quaternion targetRotation)
            {
                _interactionQuery.size = halfExtents * 2;
                return Physics.ComputePenetration(_interactionQuery,
                    pose.CameraPosition + pose.CameraRotation * center, pose.CameraRotation * rotation,
                    target, targetPosition, targetRotation, out var direction, out float distance)
                    && Finite(direction) && float.IsFinite(distance) && distance >= 0;
            }

            private static bool UnitRotation(Quaternion value)
                => Finite(value) && MathF.Abs(value.x * value.x + value.y * value.y
                    + value.z * value.z + value.w * value.w - 1) < 0.0001f;

            private void ReleaseInteractionQuery()
            {
                if (Live(_interactionQueryObject)) UnityEngine.Object.Destroy(_interactionQueryObject);
                _interactionQuery = null;
                _interactionQueryObject = null;
            }
        }
    }
}
