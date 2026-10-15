// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using Construction;
using Construction.Multiplayer;
using Endnight.Utilities;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorld
    {
        /// <summary>
        /// Resolves the free-form request's position, axis and look-at point into world space during Receiving.
        /// Preserves native target/prediction selection without executing placement or its rejection path.
        /// </summary>
        public static bool TryResolveFreeFormGeometry(OpenACAISonsFreeFormRequest request, ulong expectedSteamId,
            out OpenACAISonsFreeFormGeometrySnapshot snapshot, out OpenACAISonsPlacementReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPlacementContext(request);
                if (status != OpenACAISonsPlacementReadStatus.Success)
                    return false;
                if (expectedSteamId == 0)
                {
                    status = OpenACAISonsPlacementReadStatus.InvalidArgument;
                    return false;
                }

                var position = request.RequestedPosition;
                var axis = request.RequestedAxis;
                var lookAt = request.RequestedLookAtPosition;
                if (!IsFiniteFreeFormGeometry(position, axis, lookAt))
                {
                    status = OpenACAISonsPlacementReadStatus.NonFinitePose;
                    return false;
                }
                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceBefore, out var connection).HasValue)
                {
                    status = OpenACAISonsPlacementReadStatus.SourceUnavailable;
                    return false;
                }

                status = CaptureFreeFormFrame(request, connection, sourceBefore, out var transform, out var before);
                if (status != OpenACAISonsPlacementReadStatus.Success)
                    return false;
                if (before.Transform.HasValue)
                {
                    var worldPosition = transform.TransformPoint(new UnityEngine.Vector3(position.X, position.Y, position.Z));
                    var worldAxis = transform.TransformDirection(new UnityEngine.Vector3(axis.X, axis.Y, axis.Z));
                    var worldLookAt = transform.TransformPoint(new UnityEngine.Vector3(lookAt.X, lookAt.Y, lookAt.Z));
                    position = new Vector3(worldPosition.x, worldPosition.y, worldPosition.z);
                    axis = new Vector3(worldAxis.x, worldAxis.y, worldAxis.z);
                    lookAt = new Vector3(worldLookAt.x, worldLookAt.y, worldLookAt.z);
                }

                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceAfter, out connection).HasValue
                    || sourceBefore != sourceAfter)
                {
                    status = OpenACAISonsPlacementReadStatus.SourceUnavailable;
                    return false;
                }
                status = CaptureFreeFormFrame(request, connection, sourceAfter, out _, out var after);
                if (status != OpenACAISonsPlacementReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = OpenACAISonsPlacementReadStatus.FrameChanged;
                    return false;
                }
                if (!IsFiniteFreeFormGeometry(position, axis, lookAt))
                {
                    status = OpenACAISonsPlacementReadStatus.NonFinitePose;
                    return false;
                }

                snapshot = new OpenACAISonsFreeFormGeometrySnapshot(position, axis, lookAt, before.Kind,
                    before.StructureTypeId, before.StructureInstanceId, before.ParentNetworkId, sourceBefore.PlayerNetworkId);
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

        private static OpenACAISonsPlacementReadStatus CaptureFreeFormFrame(OpenACAISonsFreeFormRequest request,
            BoltConnection connection, RangeBinding source, out UnityEngine.Transform transform, out FreeFormGeometryFrame frame)
        {
            transform = null;
            frame = default;
            var proxy = request.DispatchProxy;
            if (proxy is null || proxy.WasCollected || !proxy)
                return OpenACAISonsPlacementReadStatus.Unavailable;
            frame = new FreeFormGeometryFrame { ProxyPointer = proxy.Pointer };

            var target = request.RequestedTarget;
            if (target.TypeId > 0 && target.InstanceId > 0)
            {
                Structure structure;
                var instanceId = target.InstanceId;
                var anticipated = (request.RequestedAnticipatedStructuresFlags & (int)AnticipatedStructureFlags.Targeted) != 0;
                if (anticipated)
                {
                    var userData = connection.UserData;
                    var player = userData is null || userData.WasCollected ? null : userData.TryCast<BoltEntity>();
                    if (!MatchesPlayerSource(player, connection) || player.Pointer != source.PlayerPointer
                        || player.networkId.PackedValue != source.PlayerNetworkId)
                        return OpenACAISonsPlacementReadStatus.SourceUnavailable;
                    if (!proxy.TryGetPlayerNetworkAnimation(player, out var animation)
                        || animation is null || animation.WasCollected || !animation)
                        return OpenACAISonsPlacementReadStatus.PredictionUnavailable;
                    var predictions = animation.RemotelyAnticipatedStructures;
                    if (predictions is null || predictions.WasCollected)
                        return OpenACAISonsPlacementReadStatus.PredictionUnavailable;
                    frame = frame with { AnimationPointer = animation.Pointer, AnticipationPointer = predictions.Pointer };
                    if (!predictions.TryFindAnticipatedStructure(instanceId, target.TypeId, out structure))
                        return OpenACAISonsPlacementReadStatus.PredictionUnavailable;
                }
                else
                {
                    // Preserve the native stale-version check and its ref argument before database lookup.
                    if (proxy.IsDestroyedOrOutdatedStructure(request.RequestedDatabaseVersion, target.TypeId, ref instanceId))
                        return OpenACAISonsPlacementReadStatus.StaleStructure;
                    if (!RuntimeStructureDatabase.TryGetStructure(target.TypeId, instanceId, out structure))
                        return OpenACAISonsPlacementReadStatus.TargetUnavailable;
                }

                if (structure is null || structure.WasCollected || !structure || structure.TypeID != target.TypeId
                    || (!anticipated && structure.InstanceID != unchecked((uint)instanceId)))
                    return OpenACAISonsPlacementReadStatus.TargetUnavailable;
                transform = structure.transform;
                var status = CapturePlacementTransform(structure.Pointer, transform, out var transformFrame);
                if (status != OpenACAISonsPlacementReadStatus.Success)
                    return status;
                frame = frame with
                {
                    Kind = OpenACAISonsFreeFormCoordinateFrame.TargetStructure,
                    StructureTypeId = structure.TypeID,
                    StructureInstanceId = structure.InstanceID,
                    Transform = transformFrame
                };
                return OpenACAISonsPlacementReadStatus.Success;
            }

            if (request.RequestedPickupNetworkId == 0)
                return OpenACAISonsPlacementReadStatus.Success;

            var pickup = BoltNetwork.FindEntity(new Bolt.NetworkId(request.RequestedPickupNetworkId));
            if (!IsLiveRangeEntity(pickup) || pickup.networkId.PackedValue != request.RequestedPickupNetworkId)
                return OpenACAISonsPlacementReadStatus.TargetUnavailable;
            var targetTransform = pickup.transform;
            if (targetTransform is null || targetTransform.WasCollected || !targetTransform)
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;
            frame = frame with { PickupPointer = pickup.Pointer, PickupTransformPointer = targetTransform.Pointer };

            if (targetTransform.TryGetComponent<INestingParentProvider>(out var provider))
            {
                if (provider is null || provider.WasCollected)
                    return OpenACAISonsPlacementReadStatus.ParentUnavailable;
                frame = frame with { ProviderPointer = provider.Pointer };
                targetTransform = provider.GetParentTransform();
                if (targetTransform is null || targetTransform.WasCollected || !targetTransform)
                    return OpenACAISonsPlacementReadStatus.ParentUnavailable;
            }
            frame = frame with { TargetTransformPointer = targetTransform.Pointer };

            if (!TryGetComponentExtensions.TryGetComponentInParent<BoltEntity>(targetTransform.gameObject, out var parent))
                return OpenACAISonsPlacementReadStatus.Success;
            if (!IsLiveRangeEntity(parent))
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;
            var parentId = parent.networkId.PackedValue;
            if (parentId == 0)
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;
            var registeredParent = BoltNetwork.FindEntity(new Bolt.NetworkId(parentId));
            if (!IsLiveRangeEntity(registeredParent) || registeredParent.Pointer != parent.Pointer
                || registeredParent.networkId.PackedValue != parentId)
                return OpenACAISonsPlacementReadStatus.ParentUnavailable;

            transform = parent.transform;
            var parentStatus = CapturePlacementTransform(parent.Pointer, transform, out var parentFrame);
            if (parentStatus != OpenACAISonsPlacementReadStatus.Success)
                return parentStatus;
            frame = frame with
            {
                Kind = OpenACAISonsFreeFormCoordinateFrame.PickupParent,
                ParentNetworkId = parentId,
                Transform = parentFrame
            };
            return OpenACAISonsPlacementReadStatus.Success;
        }

        private static bool IsFiniteFreeFormGeometry(Vector3 position, Vector3 axis, Vector3 lookAt) =>
            float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z)
            && float.IsFinite(axis.X) && float.IsFinite(axis.Y) && float.IsFinite(axis.Z)
            && float.IsFinite(lookAt.X) && float.IsFinite(lookAt.Y) && float.IsFinite(lookAt.Z);

        private readonly record struct FreeFormGeometryFrame
        {
            public OpenACAISonsFreeFormCoordinateFrame Kind { get; init; }
            public IntPtr ProxyPointer { get; init; }
            public IntPtr AnimationPointer { get; init; }
            public IntPtr AnticipationPointer { get; init; }
            public int? StructureTypeId { get; init; }
            public uint? StructureInstanceId { get; init; }
            public IntPtr PickupPointer { get; init; }
            public IntPtr PickupTransformPointer { get; init; }
            public IntPtr ProviderPointer { get; init; }
            public IntPtr TargetTransformPointer { get; init; }
            public ulong? ParentNetworkId { get; init; }
            public PlacementTransformFrame? Transform { get; init; }
        }
    }

    /// <summary>The native-selected frame of the input vectors. Snapshot vectors are always in world space.</summary>
    public enum OpenACAISonsFreeFormCoordinateFrame
    {
        World,
        TargetStructure,
        PickupParent
    }

    /// <summary>Managed world geometry, not authorization, placement success or a complete TargetInfo snapshot.</summary>
    public sealed class OpenACAISonsFreeFormGeometrySnapshot
    {
        internal OpenACAISonsFreeFormGeometrySnapshot(Vector3 position, Vector3 axis, Vector3 lookAtPosition,
            OpenACAISonsFreeFormCoordinateFrame inputFrame, int? structureTypeId, uint? structureInstanceId,
            ulong? parentNetworkId, ulong playerNetworkId)
        {
            Position = position;
            Axis = axis;
            LookAtPosition = lookAtPosition;
            InputFrame = inputFrame;
            StructureTypeId = structureTypeId;
            StructureInstanceId = structureInstanceId;
            ParentNetworkId = parentNetworkId;
            PlayerNetworkId = playerNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public Vector3 Position { get; }
        /// <summary>World direction, not a translated/scaled point or normalized orientation.</summary>
        public Vector3 Axis { get; }
        public Vector3 LookAtPosition { get; }
        public OpenACAISonsFreeFormCoordinateFrame InputFrame { get; }
        /// <summary>Resolved construction type, present for TargetStructure. Not a Bolt prefab ID.</summary>
        public int? StructureTypeId { get; }
        /// <summary>Resolved construction instance, not the requested prediction action number or a Bolt ID.</summary>
        public uint? StructureInstanceId { get; }
        /// <summary>Matched parent Bolt ID for PickupParent; null for other input frames.</summary>
        public ulong? ParentNetworkId { get; }
        public ulong PlayerNetworkId { get; }
        /// <summary>Local completion time. Source, frame and native placement can change after return.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
