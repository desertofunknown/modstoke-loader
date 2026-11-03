// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay.Grabber;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Center is an offset in MainCam rotation axes; HalfExtents are box-axis lengths.
    /// Both are already in world units. Rotation is relative to MainCam rotation.
    /// Apply an independently qualified actor camera pose, with no additional scaling.
    /// </summary>
    public readonly record struct PlayerInteractionBox(
        Vector3 Center, Vector3 HalfExtents, Quaternion Rotation);

    /// <summary>
    /// The two native grabber boxes and distance pivot, flattened into camera-local world
    /// units. TargetLayer is the provider's PickUp target layer, not an obstruction mask.
    /// No camera world pose, frozen root offset, live readiness or client approval is encoded.
    /// </summary>
    public readonly record struct PlayerInteractionProfile(
        int TargetLayer, PlayerInteractionBox FirstBox,
        PlayerInteractionBox SecondBox, Vector3 DistancePivot);

    public sealed partial class PlayerCarrierProfile
    {
        // Same-stack capture identities only; these never enter the immutable/wire profile.
        private readonly record struct InteractionSourceIdentity(
            IntPtr Grabber, IntPtr Provider, IntPtr Camera, IntPtr CameraTransform,
            IntPtr Follow, IntPtr Head, IntPtr HeadJoint, IntPtr Body,
            IntPtr FirstBox, IntPtr SecondBox, IntPtr CachedCollider, IntPtr DistancePivot);

        private static bool TryCaptureInteraction(Transform root,
            out PlayerInteractionProfile interaction,
            out InteractionSourceIdentity identity, out string detail)
        {
            interaction = default;
            identity = default;
            detail = "The native grabber must belong to the exact local camera and animated head-follow rig.";
            var grabber = LocalPlayer.Grabber;
            var camera = LocalPlayer.MainCam;
            var cameraTransform = LocalPlayer.MainCamTr;
            var follow = LocalPlayer.CamFollowHead;
            var head = LocalPlayer.HeadTr;
            if (!Alive(root) || !Same(root, LocalPlayer.Transform)
                || !Alive(grabber) || !Alive(camera) || !Alive(cameraTransform)
                || !Alive(follow) || !Alive(head) || !head.IsChildOf(root)
                || !Same(camera.transform, cameraTransform) || !follow.transform.IsChildOf(root)
                || !Same(cameraTransform.parent, follow.transform)
                || !Same(grabber.transform.parent, cameraTransform)) return false;
            var headJoint = follow.headJnt;
            if (!Alive(headJoint) || !Same(headJoint.parent, head)) return false;

            detail = "Exactly one native grabber provider and two enabled trigger boxes on its own kinematic body are required.";
            var providers = root.GetComponentsInChildren<GrabberSimplePhysicsTriggers>(true);
            if (providers.Length != 1 || !Alive(providers[0])) return false;
            var provider = providers[0];
            if (!Same(provider.gameObject, grabber.gameObject) || !provider.enabled || !grabber.enabled
                || !grabber.gameObject.activeInHierarchy) return false;
            var colliders = grabber.GetComponentsInChildren<Collider>(true);
            var bodies = grabber.GetComponents<Rigidbody>();
            if (colliders.Length != 2 || bodies.Length != 1 || !Alive(bodies[0])) return false;
            var body = bodies[0];
            var first = colliders[0]?.TryCast<BoxCollider>();
            var second = colliders[1]?.TryCast<BoxCollider>();
            if (!Alive(first) || !Alive(second) || Same(first, second)
                || !Same(first.gameObject, grabber.gameObject) || !Same(second.gameObject, grabber.gameObject)
                || !Same(body.gameObject, grabber.gameObject) || !body.isKinematic || !body.detectCollisions
                || !Same(first.attachedRigidbody, body) || !Same(second.attachedRigidbody, body)
                || !first.enabled || !second.enabled || !first.isTrigger || !second.isTrigger) return false;
            var cachedCollider = provider._collider;
            if (!Same(cachedCollider, first) && !Same(cachedCollider, second)) return false;

            detail = "The native distance pivot must be a direct grabber child and its target layer must match PickUp.";
            var pivot = provider._distanceCheckTransform;
            int targetLayer = provider._pickupLayer;
            if (!Alive(pivot) || !Same(pivot.parent, grabber.transform)
                || targetLayer < 0 || targetLayer > 31 || targetLayer != LayerMask.NameToLayer("PickUp")) return false;

            detail = "Native interaction geometry requires finite poses and positive uniform scale without mirrored or sheared ancestors.";
            var grabberTransform = grabber.transform;
            var grabberPosition = grabberTransform.localPosition;
            var grabberRotation = grabberTransform.localRotation;
            var pivotPosition = pivot.localPosition;
            if (!TryInteractionWorldScale(cameraTransform, out float cameraScale)
                || !UniformPositiveScale(grabberTransform.localScale, out float grabberScale)
                || !Finite(grabberPosition) || !Rotation(grabberRotation) || !Finite(pivotPosition)) return false;
            float shapeScale = cameraScale * grabberScale;
            if (!Positive(shapeScale)) return false;

            // Only local authored values and uniform scales participate. Animated camera/head
            // translations and rotations are deliberately not sampled into a frozen root offset.
            var origin = grabberPosition * cameraScale;
            var firstBox = new PlayerInteractionBox(
                origin + grabberRotation * (first.center * shapeScale), first.size * (0.5f * shapeScale), grabberRotation);
            var secondBox = new PlayerInteractionBox(
                origin + grabberRotation * (second.center * shapeScale), second.size * (0.5f * shapeScale), grabberRotation);
            var candidate = new PlayerInteractionProfile(targetLayer, firstBox, secondBox,
                origin + grabberRotation * (pivotPosition * shapeScale));
            if (!ValidInteraction(candidate)) return false;
            identity = new(grabber.Pointer, provider.Pointer, camera.Pointer, cameraTransform.Pointer,
                follow.Pointer, head.Pointer, headJoint.Pointer, body.Pointer,
                first.Pointer, second.Pointer, cachedCollider.Pointer, pivot.Pointer);
            interaction = candidate;
            detail = "The native two-box interaction geometry is captured; live camera ownership and source approval remain separate.";
            return true;
        }

        private static bool TryInteractionWorldScale(Transform transform, out float scale)
        {
            scale = 1;
            // A lossyScale alone cannot rule out shear or cancelling negative axes.
            for (var current = transform; Alive(current); current = current.parent)
            {
                if (!UniformPositiveScale(current.localScale, out float localScale)) return false;
                scale *= localScale;
                if (!Positive(scale)) return false;
            }
            return true;
        }

        private static bool UniformPositiveScale(Vector3 scale, out float value)
        {
            value = scale.x;
            return Positive(value) && scale.y == value && scale.z == value;
        }

        private static bool ValidInteraction(PlayerInteractionProfile profile)
            => profile.TargetLayer >= 0 && profile.TargetLayer <= 31
                && ValidInteractionBox(profile.FirstBox) && ValidInteractionBox(profile.SecondBox)
                && profile.FirstBox.Rotation.Equals(profile.SecondBox.Rotation) && Finite(profile.DistancePivot);

        private static bool ValidInteractionBox(PlayerInteractionBox box)
            => Finite(box.Center) && Positive(box.HalfExtents.x) && Positive(box.HalfExtents.y)
                && Positive(box.HalfExtents.z) && Rotation(box.Rotation);
    }
}
