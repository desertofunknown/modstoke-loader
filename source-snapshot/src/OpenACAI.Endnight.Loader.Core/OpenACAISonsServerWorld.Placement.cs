// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorld
    {
        /// <summary>
        /// Resolves a blueprint request's world pose during its synchronous policy callback.
        /// This observes the current parent transform; it does not validate the recipe, sender or placement permissions.
        /// </summary>
        public static bool TryResolveBlueprintPose(OpenACAISonsBlueprintPlacementRequest request,
            out OpenACAISonsBlueprintPoseSnapshot snapshot, out OpenACAISonsPlacementReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPlacementContext(request);
                if (status != OpenACAISonsPlacementReadStatus.Success)
                    return false;

                var position = request.RequestedPosition;
                var rotation = request.RequestedRotation;
                if (!IsFinitePlacementPose(position, rotation))
                {
                    status = OpenACAISonsPlacementReadStatus.NonFinitePose;
                    return false;
                }

                var parentId = request.RequestedParentNetworkId;
                if (parentId != 0)
                {
                    status = CaptureBlueprintParent(parentId, out var transform, out var before);
                    if (status != OpenACAISonsPlacementReadStatus.Success)
                        return false;

                    // Match PlaceStructureNode: full parent transform for position, parent-first quaternion product.
                    var worldPosition = transform.TransformPoint(new UnityEngine.Vector3(position.X, position.Y, position.Z));
                    var parentRotation = new UnityEngine.Quaternion(before.Rotation.X, before.Rotation.Y,
                        before.Rotation.Z, before.Rotation.W);
                    var worldRotation = parentRotation * new UnityEngine.Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
                    position = new Vector3(worldPosition.x, worldPosition.y, worldPosition.z);
                    rotation = new Quaternion(worldRotation.x, worldRotation.y, worldRotation.z, worldRotation.w);

                    status = CaptureBlueprintParent(parentId, out _, out var after);
                    if (status != OpenACAISonsPlacementReadStatus.Success)
                        return false;
                    if (before != after)
                    {
                        status = OpenACAISonsPlacementReadStatus.ParentChanged;
                        return false;
                    }
                    if (!IsFinitePlacementPose(position, rotation))
                    {
                        status = OpenACAISonsPlacementReadStatus.NonFinitePose;
                        return false;
                    }
                }

                snapshot = new OpenACAISonsBlueprintPoseSnapshot(position, rotation, parentId);
                status = OpenACAISonsPlacementReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsPlacementReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static OpenACAISonsPlacementReadStatus CaptureBlueprintParent(ulong networkId,
            out UnityEngine.Transform transform, out PlacementTransformFrame frame)
        {
            transform = null;
            frame = default;
            var parent = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (parent is null || parent.WasCollected || !parent || !parent.isAttached
                || parent.networkId.PackedValue != networkId)
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;
            transform = parent.transform;
            return CapturePlacementTransform(parent.Pointer, transform, out frame);
        }

        private static OpenACAISonsPlacementReadStatus CheckPlacementContext(OpenACAISonsConstructionRequest request)
        {
            var context = CheckContext();
            if (context != OpenACAISonsWorldReadStatus.Success)
                return context switch
                {
                    OpenACAISonsWorldReadStatus.WrongThread => OpenACAISonsPlacementReadStatus.WrongThread,
                    OpenACAISonsWorldReadStatus.NotServer => OpenACAISonsPlacementReadStatus.NotServer,
                    _ => OpenACAISonsPlacementReadStatus.Unavailable
                };
            if (request == null)
                return OpenACAISonsPlacementReadStatus.InvalidRequest;
            return request.IsDispatchOpen ? OpenACAISonsPlacementReadStatus.Success : OpenACAISonsPlacementReadStatus.RequestClosed;
        }

        private static OpenACAISonsPlacementReadStatus CapturePlacementTransform(IntPtr ownerPointer,
            UnityEngine.Transform transform, out PlacementTransformFrame frame)
        {
            frame = default;
            if (transform is null || transform.WasCollected || !transform)
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;

            var nativeMatrix = transform.localToWorldMatrix;
            var matrix = new Matrix4x4(
                nativeMatrix.m00, nativeMatrix.m01, nativeMatrix.m02, nativeMatrix.m03,
                nativeMatrix.m10, nativeMatrix.m11, nativeMatrix.m12, nativeMatrix.m13,
                nativeMatrix.m20, nativeMatrix.m21, nativeMatrix.m22, nativeMatrix.m23,
                nativeMatrix.m30, nativeMatrix.m31, nativeMatrix.m32, nativeMatrix.m33);
            var nativeRotation = transform.rotation;
            var rotation = new Quaternion(nativeRotation.x, nativeRotation.y, nativeRotation.z, nativeRotation.w);
            if (!IsFinitePlacementMatrix(matrix) || !IsFinitePlacementPose(Vector3.Zero, rotation))
                return OpenACAISonsPlacementReadStatus.NonFinitePose;

            frame = new PlacementTransformFrame(ownerPointer, transform.Pointer, matrix, rotation);
            return OpenACAISonsPlacementReadStatus.Success;
        }

        private static bool IsFinitePlacementPose(Vector3 position, Quaternion rotation) =>
            float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            && float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);

        private static bool IsFinitePlacementMatrix(Matrix4x4 value) =>
            float.IsFinite(value.M11) && float.IsFinite(value.M12) && float.IsFinite(value.M13) && float.IsFinite(value.M14)
            && float.IsFinite(value.M21) && float.IsFinite(value.M22) && float.IsFinite(value.M23) && float.IsFinite(value.M24)
            && float.IsFinite(value.M31) && float.IsFinite(value.M32) && float.IsFinite(value.M33) && float.IsFinite(value.M34)
            && float.IsFinite(value.M41) && float.IsFinite(value.M42) && float.IsFinite(value.M43) && float.IsFinite(value.M44);

        private readonly record struct PlacementTransformFrame(IntPtr OwnerPointer, IntPtr TransformPointer,
            Matrix4x4 Matrix, Quaternion Rotation);
    }

    public enum OpenACAISonsPlacementReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidRequest,
        RequestClosed,
        ParentUnavailable,
        ParentChanged,
        NonFinitePose,
        Failed,
        InvalidArgument,
        SourceUnavailable,
        TargetUnavailable,
        PredictionUnavailable,
        StaleStructure,
        FrameChanged
    }

    /// <summary>Managed world pose resolved from a blueprint request. No native parent or transform is retained.</summary>
    public sealed class OpenACAISonsBlueprintPoseSnapshot
    {
        internal OpenACAISonsBlueprintPoseSnapshot(Vector3 position, Quaternion rotation, ulong parentNetworkId)
        {
            Position = position;
            Rotation = rotation;
            ParentNetworkId = parentNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public Vector3 Position { get; }
        /// <summary>Native composition result, without normalization or proof of a valid orientation.</summary>
        public Quaternion Rotation { get; }
        /// <summary>The matched current-world parent ID, or zero when the request already supplies a world pose.</summary>
        public ulong ParentNetworkId { get; }
        /// <summary>Read completion time. Later native placement can observe a different transform.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
