// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using Construction;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Resolves one free-form structure selection during Receiving on the server.
        /// Requires the claimed player ID to match the exact source session and expected game identity.
        /// This read does not describe the whole mutation footprint or establish permission, inventory or completion.
        /// </summary>
        public static bool TryResolveFreeFormStructure(FreeFormRequest request, ulong expectedSteamId,
            FreeFormSelectionKind selection, out FreeFormStructureSnapshot snapshot,
            out PlacementReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPlacementContext(request);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (expectedSteamId == 0)
                {
                    status = PlacementReadStatus.InvalidArgument;
                    return false;
                }
                if (!request.ReceiverReadSucceeded)
                {
                    status = PlacementReadStatus.Unavailable;
                    return false;
                }
                if (!request.IsServerReceiver)
                {
                    status = PlacementReadStatus.InvalidRequest;
                    return false;
                }

                // Each native reference has its own anticipation bit; it does not select coordinate space.
                var (requested, anticipationFlag) = selection switch
                {
                    FreeFormSelectionKind.Target => (request.RequestedTarget, 2),
                    FreeFormSelectionKind.Editing => (request.RequestedEditing, 4),
                    FreeFormSelectionKind.Support1 => (request.RequestedSupport1, 8),
                    FreeFormSelectionKind.Support2 => (request.RequestedSupport2, 16),
                    FreeFormSelectionKind.Support3 => (request.RequestedSupport3, 32),
                    FreeFormSelectionKind.Support4 => (request.RequestedSupport4, 64),
                    _ => (default(FreeFormStructureSelection), 0)
                };
                if (anticipationFlag == 0)
                {
                    status = PlacementReadStatus.InvalidArgument;
                    return false;
                }

                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceBefore, out var connection).HasValue
                    || request.ClaimedPlayerNetworkId != sourceBefore.PlayerNetworkId)
                {
                    status = PlacementReadStatus.SourceUnavailable;
                    return false;
                }

                // Native Target requires both positive IDs. Editing/supports only require a positive type.
                if (requested.TypeId <= 0 || (selection == FreeFormSelectionKind.Target && requested.InstanceId <= 0))
                {
                    status = PlacementReadStatus.SelectionAbsent;
                    return false;
                }

                var anticipated = (request.RequestedAnticipatedStructuresFlags & anticipationFlag) != 0;
                status = CaptureFreeFormStructure(request, requested, anticipated, connection, sourceBefore, out var before);
                if (status != PlacementReadStatus.Success)
                    return false;

                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceAfter, out connection).HasValue
                    || sourceBefore != sourceAfter || request.ClaimedPlayerNetworkId != sourceAfter.PlayerNetworkId)
                {
                    status = PlacementReadStatus.SourceUnavailable;
                    return false;
                }
                status = CaptureFreeFormStructure(request, requested, anticipated, connection, sourceAfter, out var after);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = PlacementReadStatus.FrameChanged;
                    return false;
                }

                snapshot = new FreeFormStructureSnapshot(selection, before.StructureTypeId,
                    before.StructureInstanceId, before.Position, anticipated, sourceBefore.PlayerNetworkId);
                status = PlacementReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = PlacementReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PlacementReadStatus CaptureFreeFormStructure(FreeFormRequest request,
            FreeFormStructureSelection requested, bool anticipated, BoltConnection connection,
            RangeBinding source, out FreeFormStructureFrame frame)
        {
            return CaptureFreeFormStructure(request, requested, anticipated, connection, source, out frame, out _);
        }

        /// <summary>
        /// Reads the existing element selected by TargetElementTransformIndex during Receiving on the server.
        /// SelectionAbsent means no target structure was selected, or the index is outside its element root's children.
        /// Does not resolve pickup/tree targets, infer a new element's pose, or establish placement permission.
        /// </summary>
        public static bool TryResolveFreeFormTargetElement(FreeFormRequest request, ulong expectedSteamId,
            out FreeFormTargetElementSnapshot snapshot, out PlacementReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPlacementContext(request);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (expectedSteamId == 0)
                {
                    status = PlacementReadStatus.InvalidArgument;
                    return false;
                }
                if (!request.ReceiverReadSucceeded)
                {
                    status = PlacementReadStatus.Unavailable;
                    return false;
                }
                if (!request.IsServerReceiver)
                {
                    status = PlacementReadStatus.InvalidRequest;
                    return false;
                }
                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceBefore, out var connection).HasValue
                    || request.ClaimedPlayerNetworkId != sourceBefore.PlayerNetworkId)
                {
                    status = PlacementReadStatus.SourceUnavailable;
                    return false;
                }

                var requested = request.RequestedTarget;
                var elementIndex = request.RequestedTargetElementIndex;
                if (requested.TypeId <= 0 || requested.InstanceId <= 0 || elementIndex < 0)
                {
                    status = PlacementReadStatus.SelectionAbsent;
                    return false;
                }

                var anticipated = (request.RequestedAnticipatedStructuresFlags & 2) != 0;
                status = CaptureFreeFormTargetElement(request, requested, anticipated, connection, sourceBefore,
                    elementIndex, out var before);
                if (status != PlacementReadStatus.Success)
                    return false;

                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceAfter, out connection).HasValue
                    || sourceBefore != sourceAfter || request.ClaimedPlayerNetworkId != sourceAfter.PlayerNetworkId)
                {
                    status = PlacementReadStatus.SourceUnavailable;
                    return false;
                }
                status = CaptureFreeFormTargetElement(request, requested, anticipated, connection, sourceAfter,
                    elementIndex, out var after);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (before != after)
                {
                    status = PlacementReadStatus.FrameChanged;
                    return false;
                }

                // CapturePlacementTransform retains Unity's column-vector layout for comparison.
                // Transpose once so callers can use System.Numerics.Vector3.Transform directly.
                snapshot = new FreeFormTargetElementSnapshot(before.Structure.StructureTypeId,
                    before.Structure.StructureInstanceId, elementIndex, before.RootChildCount, before.Position,
                    before.Transform.Rotation, Matrix4x4.Transpose(before.Transform.Matrix), anticipated,
                    sourceBefore.PlayerNetworkId);
                status = PlacementReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = PlacementReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PlacementReadStatus CaptureFreeFormTargetElement(FreeFormRequest request,
            FreeFormStructureSelection requested, bool anticipated, BoltConnection connection,
            RangeBinding source, int elementIndex, out FreeFormTargetElementFrame frame)
        {
            frame = default;
            var status = CaptureFreeFormStructure(request, requested, anticipated, connection, source,
                out var structureFrame, out var structure);
            if (status != PlacementReadStatus.Success)
                return status;

            var root = structure.ElementRoot;
            if (root is null || root.WasCollected || !root)
                return PlacementReadStatus.TargetUnavailable;
            var childCount = root.childCount;
            if (elementIndex >= childCount)
                return PlacementReadStatus.SelectionAbsent;

            // Native selection is an immediate child index, not a filtered StructureElement ordinal.
            var element = root.GetChild(elementIndex);
            if (element is null || element.WasCollected || !element)
                return PlacementReadStatus.TargetUnavailable;
            status = CapturePlacementTransform(root.Pointer, element, out var transformFrame);
            if (status != PlacementReadStatus.Success)
                return status;
            var nativePosition = element.position;
            var position = new Vector3(nativePosition.x, nativePosition.y, nativePosition.z);
            if (!IsFinitePlacementPose(position, transformFrame.Rotation))
                return PlacementReadStatus.NonFinitePose;

            frame = new FreeFormTargetElementFrame(structureFrame, childCount, transformFrame, position);
            return PlacementReadStatus.Success;
        }

        private static PlacementReadStatus CaptureFreeFormStructure(FreeFormRequest request,
            FreeFormStructureSelection requested, bool anticipated, BoltConnection connection,
            RangeBinding source, out FreeFormStructureFrame frame, out Structure structure)
        {
            frame = default;
            structure = null;
            var proxy = request.DispatchProxy;
            if (proxy is null || proxy.WasCollected || !proxy)
                return PlacementReadStatus.Unavailable;
            frame = new FreeFormStructureFrame { ProxyPointer = proxy.Pointer };

            var instanceId = requested.InstanceId;
            if (anticipated)
            {
                var userData = connection.UserData;
                var player = userData is null || userData.WasCollected ? null : userData.TryCast<BoltEntity>();
                if (!MatchesPlayerSource(player, connection) || player.Pointer != source.PlayerPointer
                    || player.networkId.PackedValue != source.PlayerNetworkId)
                    return PlacementReadStatus.SourceUnavailable;
                if (!proxy.TryGetPlayerNetworkAnimation(player, out var animation)
                    || animation is null || animation.WasCollected || !animation)
                    return PlacementReadStatus.PredictionUnavailable;
                var predictions = animation.RemotelyAnticipatedStructures;
                if (predictions is null || predictions.WasCollected)
                    return PlacementReadStatus.PredictionUnavailable;
                frame = frame with { AnimationPointer = animation.Pointer, AnticipationPointer = predictions.Pointer };
                if (!predictions.TryFindAnticipatedStructure(instanceId, requested.TypeId, out structure))
                    return PlacementReadStatus.PredictionUnavailable;
            }
            else
            {
                // Keep the native ref contract: database lookup and identity checks use its effective instance ID.
                if (proxy.IsDestroyedOrOutdatedStructure(request.RequestedDatabaseVersion, requested.TypeId, ref instanceId))
                    return PlacementReadStatus.StaleStructure;
                if (!RuntimeStructureDatabase.TryGetStructure(requested.TypeId, instanceId, out structure))
                    return PlacementReadStatus.TargetUnavailable;
            }

            if (structure is null || structure.WasCollected || !structure)
                return PlacementReadStatus.TargetUnavailable;
            var resolvedTypeId = structure.TypeID;
            var resolvedInstanceId = structure.InstanceID;
            if (resolvedTypeId != requested.TypeId || (!anticipated && resolvedInstanceId != unchecked((uint)instanceId)))
                return PlacementReadStatus.TargetUnavailable;

            var transform = structure.transform;
            var status = CapturePlacementTransform(structure.Pointer, transform, out var transformFrame);
            if (status != PlacementReadStatus.Success)
                return status;
            var nativePosition = transform.position;
            var position = new Vector3(nativePosition.x, nativePosition.y, nativePosition.z);
            if (!Finite(position))
                return PlacementReadStatus.NonFinitePose;

            frame = frame with
            {
                StructureTypeId = resolvedTypeId,
                StructureInstanceId = resolvedInstanceId,
                Transform = transformFrame,
                Position = position
            };
            return PlacementReadStatus.Success;
        }

        private readonly record struct FreeFormStructureFrame
        {
            public IntPtr ProxyPointer { get; init; }
            public IntPtr AnimationPointer { get; init; }
            public IntPtr AnticipationPointer { get; init; }
            public int StructureTypeId { get; init; }
            public uint StructureInstanceId { get; init; }
            public PlacementTransformFrame Transform { get; init; }
            public Vector3 Position { get; init; }
        }

        private readonly record struct FreeFormTargetElementFrame(FreeFormStructureFrame Structure,
            int RootChildCount, PlacementTransformFrame Transform, Vector3 Position);
    }

    /// <summary>One requested native construction reference, independent of the input geometry's coordinate frame.</summary>
    public enum FreeFormSelectionKind
    {
        Target,
        Editing,
        Support1,
        Support2,
        Support3,
        Support4
    }

    /// <summary>Managed observation of one resolved structure, not the complete mutation footprint or a placement verdict.</summary>
    public sealed class FreeFormStructureSnapshot
    {
        internal FreeFormStructureSnapshot(FreeFormSelectionKind selection, int structureTypeId,
            uint structureInstanceId, Vector3 position, bool usedPrediction, ulong playerNetworkId)
        {
            Selection = selection;
            StructureTypeId = structureTypeId;
            StructureInstanceId = structureInstanceId;
            Position = position;
            UsedPrediction = usedPrediction;
            PlayerNetworkId = playerNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public FreeFormSelectionKind Selection { get; }
        /// <summary>Resolved construction type, not a Bolt prefab ID.</summary>
        public int StructureTypeId { get; }
        /// <summary>Resolved construction instance, not the requested prediction action number or a Bolt ID.</summary>
        public uint StructureInstanceId { get; }
        /// <summary>The structure transform's world origin, not the request point or selected element's position.</summary>
        public Vector3 Position { get; }
        /// <summary>True when the selected reference resolved through this source player's remote anticipation lookup.</summary>
        public bool UsedPrediction { get; }
        /// <summary>The source-bound player ID, also matched to the request's claimed player ID.</summary>
        public ulong PlayerNetworkId { get; }
        /// <summary>Local completion time. Source, structure and native placement can change after return.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>
    /// Managed geometry of the selected element-root child. No native references are retained.
    /// Describes an existing transform, not collision bounds, support strength or a placement verdict.
    /// </summary>
    public sealed class FreeFormTargetElementSnapshot
    {
        internal FreeFormTargetElementSnapshot(int structureTypeId, uint structureInstanceId,
            int elementIndex, int rootChildCount, Vector3 position, Quaternion rotation, Matrix4x4 localToWorldMatrix,
            bool usedPrediction, ulong playerNetworkId)
        {
            StructureTypeId = structureTypeId;
            StructureInstanceId = structureInstanceId;
            ElementIndex = elementIndex;
            RootChildCount = rootChildCount;
            Position = position;
            Rotation = rotation;
            LocalToWorldMatrix = localToWorldMatrix;
            UsedPrediction = usedPrediction;
            PlayerNetworkId = playerNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Resolved construction type, not a Bolt prefab ID.</summary>
        public int StructureTypeId { get; }
        /// <summary>Full resolved construction instance ID, not a prediction action number or Bolt ID.</summary>
        public uint StructureInstanceId { get; }
        /// <summary>Zero-based immediate child index. Not a persistent identity or StructureElement ordinal.</summary>
        public int ElementIndex { get; }
        /// <summary>Immediate child count observed on the element root, not a completed-piece count.</summary>
        public int RootChildCount { get; }
        /// <summary>World origin of the selected child, not the structure origin or requested placement point.</summary>
        public Vector3 Position { get; }
        /// <summary>Native world rotation, without normalization or inferred orientation validity.</summary>
        public Quaternion Rotation { get; }
        /// <summary>
        /// Element-local to world affine matrix in System.Numerics row-vector convention.
        /// Use Vector3.Transform for points; includes native scale and shear without inventing element dimensions.
        /// </summary>
        public Matrix4x4 LocalToWorldMatrix { get; }
        /// <summary>True when this source player's remote anticipation lookup resolved the target structure.</summary>
        public bool UsedPrediction { get; }
        /// <summary>Source-bound player ID, also matched to the request's claimed player ID.</summary>
        public ulong PlayerNetworkId { get; }
        /// <summary>Local completion time. Geometry and child indices can change immediately after return.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
