// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Construction;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Maximum entries copied from each direct-link or support-direction array in one read.</summary>
        public const int MaximumFreeFormStructureLinkEntries = 64;

        /// <summary>
        /// Copies direct link slots and declared support directions for one free-form selection during Receiving.
        /// Uses the existing source-bound database/prediction resolution, without traversing neighboring structures.
        /// maximumEntries must be between 1 and MaximumFreeFormStructureLinkEntries and bounds each array separately.
        /// Success may contain unavailable slots or truncated arrays; inspect HasPartialDirectCoverage and entry states.
        /// This is not a support-stability, placement-permission, reciprocal-link or complete-graph verdict.
        /// Supporting and SupportedBy dispatch native virtual getters. This helper invokes no graph setters,
        /// but does not guarantee side-effect-free behavior of native overrides or class initialization.
        /// </summary>
        public static bool TryReadFreeFormStructureLinks(FreeFormRequest request, ulong expectedSteamId,
            FreeFormSelectionKind selection, int maximumEntries,
            out FreeFormStructureLinksSnapshot snapshot, out PlacementReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckPlacementContext(request);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (expectedSteamId == 0 || maximumEntries < 1 || maximumEntries > MaximumFreeFormStructureLinkEntries)
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
                if (requested.TypeId <= 0 || (selection == FreeFormSelectionKind.Target && requested.InstanceId <= 0))
                {
                    status = PlacementReadStatus.SelectionAbsent;
                    return false;
                }
                if (!RuntimeStructureDatabase.TryGetInstance(out var databaseBefore)
                    || databaseBefore is null || databaseBefore.WasCollected || !databaseBefore
                    || RuntimeStructureDatabase.IsLoadingSave || RuntimeStructureDatabase.IsLoadingPrefab)
                {
                    status = PlacementReadStatus.Unavailable;
                    return false;
                }
                var databasePointer = databaseBefore.Pointer;

                var anticipated = (request.RequestedAnticipatedStructuresFlags & anticipationFlag) != 0;
                status = CaptureFreeFormStructure(request, requested, anticipated, connection, sourceBefore,
                    out var structureBefore, out var structure);
                if (status != PlacementReadStatus.Success)
                    return false;
                var linksBefore = CaptureFreeFormStructureLinks(structure, maximumEntries);

                if (CaptureRangeSource(request.SourceSession, expectedSteamId, out var sourceAfter, out connection).HasValue
                    || sourceBefore != sourceAfter || request.ClaimedPlayerNetworkId != sourceAfter.PlayerNetworkId)
                {
                    status = PlacementReadStatus.SourceUnavailable;
                    return false;
                }
                status = CaptureFreeFormStructure(request, requested, anticipated, connection, sourceAfter,
                    out var structureAfter, out structure);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (structureBefore != structureAfter)
                {
                    status = PlacementReadStatus.FrameChanged;
                    return false;
                }
                var linksAfter = CaptureFreeFormStructureLinks(structure, maximumEntries);
                if (!SameFreeFormStructureLinks(linksBefore, linksAfter))
                {
                    status = PlacementReadStatus.FrameChanged;
                    return false;
                }

                status = CheckPlacementContext(request);
                if (status != PlacementReadStatus.Success)
                    return false;
                if (!RuntimeStructureDatabase.TryGetInstance(out var databaseAfter)
                    || databaseAfter is null || databaseAfter.WasCollected || !databaseAfter
                    || RuntimeStructureDatabase.IsLoadingSave || RuntimeStructureDatabase.IsLoadingPrefab)
                {
                    status = PlacementReadStatus.Unavailable;
                    return false;
                }
                if (databaseAfter.Pointer != databasePointer)
                {
                    status = PlacementReadStatus.FrameChanged;
                    return false;
                }

                var links = linksBefore.Links.Length == 0 ? Array.Empty<FreeFormStructureLinkSnapshot>()
                    : new FreeFormStructureLinkSnapshot[linksBefore.Links.Length];
                var unavailableLinks = false;
                for (var i = 0; i < links.Length; i++)
                {
                    var link = linksBefore.Links[i];
                    links[i] = new FreeFormStructureLinkSnapshot(i, link.Status,
                        link.StructureTypeId, link.StructureInstanceId);
                    unavailableLinks |= link.Status == FreeFormStructureLinkReadStatus.Unavailable;
                }
                snapshot = new FreeFormStructureLinksSnapshot(selection, structureBefore.StructureTypeId,
                    structureBefore.StructureInstanceId, anticipated, sourceBefore.PlayerNetworkId,
                    linksBefore.LinkSlotCount, links, unavailableLinks,
                    new FreeFormStructureDirectionsSnapshot(linksBefore.Supporting.Count, linksBefore.Supporting.Values),
                    new FreeFormStructureDirectionsSnapshot(linksBefore.SupportedBy.Count, linksBefore.SupportedBy.Values));
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

        private static FreeFormStructureLinksFrame CaptureFreeFormStructureLinks(Structure structure, int maximumEntries)
        {
            var nativeLinks = structure._structures;
            var linksPointer = IntPtr.Zero;
            int? linkSlotCount = null;
            var links = Array.Empty<FreeFormStructureLinkFrame>();
            if (nativeLinks is not null && !nativeLinks.WasCollected)
            {
                linksPointer = nativeLinks.Pointer;
                linkSlotCount = nativeLinks.Length;
                links = linkSlotCount.Value == 0 ? Array.Empty<FreeFormStructureLinkFrame>()
                    : new FreeFormStructureLinkFrame[Math.Min(linkSlotCount.Value, maximumEntries)];
                for (var i = 0; i < links.Length; i++)
                {
                    var link = nativeLinks[i];
                    if (link is null)
                    {
                        links[i] = new FreeFormStructureLinkFrame(IntPtr.Zero,
                            FreeFormStructureLinkReadStatus.Empty, null, null);
                    }
                    else if (link.WasCollected || !link)
                    {
                        links[i] = new FreeFormStructureLinkFrame(IntPtr.Zero,
                            FreeFormStructureLinkReadStatus.Unavailable, null, null);
                    }
                    else
                    {
                        links[i] = new FreeFormStructureLinkFrame(link.Pointer,
                            FreeFormStructureLinkReadStatus.Observed, link.TypeID, link.InstanceID);
                    }
                }
            }

            // Copy the declared directions, not fallback, repair-priority or stability calculations.
            var supporting = CaptureFreeFormStructureDirections(structure.Supporting, maximumEntries);
            var supportedBy = CaptureFreeFormStructureDirections(structure.SupportedBy, maximumEntries);
            return new FreeFormStructureLinksFrame(linksPointer, linkSlotCount, links, supporting, supportedBy);
        }

        private static FreeFormStructureDirectionsFrame CaptureFreeFormStructureDirections(
            Il2CppStructArray<Directions> directions, int maximumEntries)
        {
            if (directions is null || directions.WasCollected)
                return new FreeFormStructureDirectionsFrame(IntPtr.Zero, null, Array.Empty<int>());

            var count = directions.Length;
            var values = count == 0 ? Array.Empty<int>() : new int[Math.Min(count, maximumEntries)];
            for (var i = 0; i < values.Length; i++)
                values[i] = (int)directions[i];
            return new FreeFormStructureDirectionsFrame(directions.Pointer, count, values);
        }

        private static bool SameFreeFormStructureLinks(FreeFormStructureLinksFrame before, FreeFormStructureLinksFrame after) =>
            before.LinksPointer == after.LinksPointer && before.LinkSlotCount == after.LinkSlotCount
            && before.Links.AsSpan().SequenceEqual(after.Links)
            && SameFreeFormStructureDirections(before.Supporting, after.Supporting)
            && SameFreeFormStructureDirections(before.SupportedBy, after.SupportedBy);

        private static bool SameFreeFormStructureDirections(FreeFormStructureDirectionsFrame before,
            FreeFormStructureDirectionsFrame after) =>
            before.Pointer == after.Pointer && before.Count == after.Count
            && before.Values.AsSpan().SequenceEqual(after.Values);

        private readonly record struct FreeFormStructureLinkFrame(IntPtr Pointer,
            FreeFormStructureLinkReadStatus Status, int? StructureTypeId, uint? StructureInstanceId);

        private readonly record struct FreeFormStructureDirectionsFrame(IntPtr Pointer, int? Count, int[] Values);

        private readonly record struct FreeFormStructureLinksFrame(IntPtr LinksPointer, int? LinkSlotCount,
            FreeFormStructureLinkFrame[] Links, FreeFormStructureDirectionsFrame Supporting,
            FreeFormStructureDirectionsFrame SupportedBy);
    }

    public enum FreeFormStructureLinkReadStatus
    {
        /// <summary>The native slot was null, not an unavailable collection or destroyed object.</summary>
        Empty,
        /// <summary>The slot held a live structure whose IDs were copied, without checking reciprocal links or registration.</summary>
        Observed,
        /// <summary>The slot held a collected or destroyed object; its identity was not read.</summary>
        Unavailable
    }

    /// <summary>One raw direct-link slot. IDs are construction IDs, not Bolt IDs or prediction action numbers.</summary>
    public readonly record struct FreeFormStructureLinkSnapshot(
        int SlotIndex, FreeFormStructureLinkReadStatus Status, int? StructureTypeId, uint? StructureInstanceId);

    /// <summary>Copied prefix of a structure's declared direction array, not a list of confirmed support relationships.</summary>
    public sealed class FreeFormStructureDirectionsSnapshot
    {
        internal FreeFormStructureDirectionsSnapshot(int? count, int[] values)
        {
            Count = count;
            Values = Array.AsReadOnly(values);
        }

        /// <summary>Full native array length when available; null is unknown, not an empty direction set.</summary>
        public int? Count { get; }
        /// <summary>
        /// Raw direction numbers in native order, preserving duplicates and unknown values.
        /// The base structure accessor uses direction numbers as slot indices; type-specific accessors are not evaluated.
        /// Names and support rules are structure-specific. A declared direction need not have an occupied sampled slot.
        /// </summary>
        public IReadOnlyList<int> Values { get; }
        public bool IsAvailable => Count.HasValue;
        public bool Truncated => Count.HasValue && Count.Value > Values.Count;
    }

    /// <summary>
    /// Copied one-hop construction connectivity. No native objects are retained. Capturing support-direction
    /// arrays evaluates native virtual getters; the snapshot does not certify their implementation as pure.
    /// Covers only Structure._structures, Supporting and SupportedBy. Does not include fallback supports,
    /// screw links, superstructure membership, type-specific extra relationships, repair priority or transitive links.
    /// </summary>
    public sealed class FreeFormStructureLinksSnapshot
    {
        internal FreeFormStructureLinksSnapshot(FreeFormSelectionKind selection,
            int structureTypeId, uint structureInstanceId, bool usedPrediction, ulong playerNetworkId,
            int? linkSlotCount, FreeFormStructureLinkSnapshot[] links, bool hasUnavailableLinks,
            FreeFormStructureDirectionsSnapshot supportingDirections,
            FreeFormStructureDirectionsSnapshot supportedByDirections)
        {
            Selection = selection;
            StructureTypeId = structureTypeId;
            StructureInstanceId = structureInstanceId;
            UsedPrediction = usedPrediction;
            PlayerNetworkId = playerNetworkId;
            LinkSlotCount = linkSlotCount;
            Links = Array.AsReadOnly(links);
            HasUnavailableLinks = hasUnavailableLinks;
            SupportingDirections = supportingDirections;
            SupportedByDirections = supportedByDirections;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public FreeFormSelectionKind Selection { get; }
        public int StructureTypeId { get; }
        public uint StructureInstanceId { get; }
        /// <summary>Whether the selected root resolved through the source player's remote anticipation lookup.</summary>
        public bool UsedPrediction { get; }
        /// <summary>Source-bound player ID, also matched to the request's claimed player ID.</summary>
        public ulong PlayerNetworkId { get; }
        /// <summary>Raw slot count, including empty slots. Null means the native array was unavailable.</summary>
        public int? LinkSlotCount { get; }
        /// <summary>Bounded slot prefix in native order. Empty slots and repeated neighbors are preserved.</summary>
        public IReadOnlyList<FreeFormStructureLinkSnapshot> Links { get; }
        public bool LinksAvailable => LinkSlotCount.HasValue;
        public bool LinksTruncated => LinkSlotCount.HasValue && LinkSlotCount.Value > Links.Count;
        public bool HasUnavailableLinks { get; }
        public FreeFormStructureDirectionsSnapshot SupportingDirections { get; }
        public FreeFormStructureDirectionsSnapshot SupportedByDirections { get; }
        /// <summary>
        /// Indicates unavailable or omitted data within the three sampled arrays only.
        /// False does not imply a complete support graph, valid supports or structural stability.
        /// </summary>
        public bool HasPartialDirectCoverage => !LinksAvailable || LinksTruncated || HasUnavailableLinks
            || !SupportingDirections.IsAvailable || SupportingDirections.Truncated
            || !SupportedByDirections.IsAvailable || SupportedByDirections.Truncated;
        /// <summary>Local completion time. Two matching bounded reads do not provide an atomic or persistent graph snapshot.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
