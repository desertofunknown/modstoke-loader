// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Modstoke.Sons;

public enum LocatorRecordReadStatus
{
    Success,
    Unavailable,
    WrongThread,
    Disposed,
    InvalidRequest,
    InvalidCursor,
    ManagerUnavailable,
    CacheUnavailable,
    Changed,
    Failed
}

/// <summary>Progress through observed cache collections, never a whole-world coverage certificate.</summary>
public enum LocatorRecordScanStatus
{
    Building,
    PassCompleted
}

public enum LocatorRecordMembership
{
    Grouped,
    UngroupedWorldId,
    Ungrouped
}

public enum LocatorRecordStatus
{
    Observed,
    Unavailable,
    Changed,
    Failed
}

[Flags]
public enum LocatorRecordIssues
{
    None = 0,
    SourceUnavailable = 1,
    ListsUnavailable = 2,
    ListsMisaligned = 4,
    KeyChanged = 8,
    AssetSlotUnavailable = 16,
    DataUnavailable = 32,
    ReferencesChanged = 64,
    ReadFailed = 128
}

public enum LocatorDataProvenance
{
    Unavailable,
    CurrentCacheSlotOnly
}

[Flags]
public enum LocatorPrefabIssues
{
    None = 0,
    MissingLodPrefab = 1,
    MissingSwapper = 2,
    MissingCutPrefab = 4,
    MissingManager = 8,
    MissingFracturedObject = 16,
    MissingBoltEntity = 32,
    TraversalLimit = 64,
    ReadFailed = 128,
    ReferencesChanged = 256
}

/// <summary>A raw Bolt prefab ID observed through a referenced cut-tree hierarchy, not a permitted-ID verdict.</summary>
public sealed class FallingPrefabCandidate
{
    internal FallingPrefabCandidate(int prefabId) => PrefabId = prefabId;

    /// <summary>Raw prefab ID, before any sender-side wire clamping or other transformation.</summary>
    public int PrefabId { get; }
}

/// <summary>A bounded inspection of one currently referenced LOD prefab. Missing paths remain unknown.</summary>
public sealed class LocatorPrefabObservation
{
    internal LocatorPrefabObservation(int lodIndex, LocatorPrefabIssues issues,
        IEnumerable<FallingPrefabCandidate> candidates)
    {
        LodIndex = lodIndex;
        Issues = issues;
        Candidates = Array.AsReadOnly(candidates.ToArray());
    }

    public int LodIndex { get; }
    public LocatorPrefabIssues Issues { get; }
    public IReadOnlyList<FallingPrefabCandidate> Candidates { get; }
    public bool CoverageIsAuthoritative => false;
}

/// <summary>
/// Managed copies from an indexed address rechecked during this read.
/// The address and data slot describe current observations, not historical original-tree identity.
/// </summary>
public sealed class LocatorRecordObservation
{
    internal LocatorRecordObservation(Guid locatorId, LocatorRecordMembership membership,
        int membershipIndex, int recordIndex, LocatorRecordIssues issues,
        Vector3? position = null, Quaternion? rotation = null, Vector3? scale = null,
        int? assetSlot = null, int? dataInstanceId = null, int? dataType = null,
        LocatorPrefabObservation[] prefabs = null)
    {
        LocatorId = locatorId;
        Membership = membership;
        MembershipIndex = membershipIndex;
        RecordIndex = recordIndex;
        Issues = issues;
        StoredPosition = position;
        StoredRotation = rotation;
        StoredScale = scale;
        CurrentAssetSlot = assetSlot;
        CurrentDataInstanceId = dataInstanceId;
        CurrentDataType = dataType;
        Prefabs = Array.AsReadOnly(prefabs ?? Array.Empty<LocatorPrefabObservation>());
        ObservedAt = DateTimeOffset.UtcNow;
    }

    public Guid LocatorId { get; }
    /// <summary>Observed membership route; indexes are not durable world identifiers.</summary>
    public LocatorRecordMembership Membership { get; }
    public int MembershipIndex { get; }
    public int RecordIndex { get; }
    public LocatorRecordIssues Issues { get; }
    public LocatorRecordStatus Status =>
        (Issues & LocatorRecordIssues.ReadFailed) != 0 ? LocatorRecordStatus.Failed :
        (Issues & (LocatorRecordIssues.KeyChanged | LocatorRecordIssues.ReferencesChanged)) != 0
            ? LocatorRecordStatus.Changed :
        (Issues & (LocatorRecordIssues.SourceUnavailable | LocatorRecordIssues.ListsUnavailable
            | LocatorRecordIssues.ListsMisaligned)) != 0 ? LocatorRecordStatus.Unavailable :
        LocatorRecordStatus.Observed;
    /// <summary>Stored record root pose, not an active object's or falling tree's world pose.</summary>
    public Vector3? StoredPosition { get; }
    public Quaternion? StoredRotation { get; }
    public Vector3? StoredScale { get; }
    public int? CurrentAssetSlot { get; }
    /// <summary>Unity instance ID observed now, not a persistent or authenticated asset identifier.</summary>
    public int? CurrentDataInstanceId { get; }
    public int? CurrentDataType { get; }
    /// <summary>Matches the native Trees classification only; SmallTree is a separate category.</summary>
    public bool? IsTree => CurrentDataType.HasValue ? CurrentDataType.Value == 0 : null;
    public LocatorDataProvenance DataProvenance => CurrentDataInstanceId.HasValue
        ? LocatorDataProvenance.CurrentCacheSlotOnly : LocatorDataProvenance.Unavailable;
    public IReadOnlyList<LocatorPrefabObservation> Prefabs { get; }
    /// <summary>Local read completion time, not a simulation, packet, save or atomic-snapshot timestamp.</summary>
    public DateTimeOffset ObservedAt { get; }
}

/// <summary>A position in one local index generation. A replaced generation invalidates the cursor.</summary>
public sealed class LocatorRecordCursor
{
    internal LocatorRecordCursor(Guid locatorId, long generation, int offset)
    {
        LocatorId = locatorId;
        Generation = generation;
        Offset = offset;
    }

    public Guid LocatorId { get; }
    public long Generation { get; }
    public int Offset { get; }
}

/// <summary>
/// A bounded page of indexed addresses rechecked against current local records.
/// Empty results, a completed pass and a single observed match never establish absence or uniqueness.
/// </summary>
public sealed class LocatorRecordPage
{
    internal LocatorRecordPage(Guid locatorId, long generation, int offset, int indexedMatchCount,
        DateTimeOffset scanStartedAt, DateTimeOffset? scanCompletedAt, bool indexHadReadFailures,
        LocatorRecordObservation[] records)
    {
        LocatorId = locatorId;
        Generation = generation;
        Offset = offset;
        IndexedMatchCount = indexedMatchCount;
        ScanStartedAt = scanStartedAt;
        ScanCompletedAt = scanCompletedAt;
        IndexHadReadFailures = indexHadReadFailures;
        Records = Array.AsReadOnly(records);
        var next = offset + records.Length;
        NextCursor = next < indexedMatchCount ? new(locatorId, generation, next) : null;
        ReadAt = DateTimeOffset.UtcNow;
    }

    public Guid LocatorId { get; }
    public long Generation { get; }
    public int Offset { get; }
    /// <summary>Matches known to this index, including addresses that may now be changed or unavailable.</summary>
    public int IndexedMatchCount { get; }
    public DateTimeOffset ScanStartedAt { get; }
    public DateTimeOffset? ScanCompletedAt { get; }
    public LocatorRecordScanStatus ScanStatus => ScanCompletedAt.HasValue
        ? LocatorRecordScanStatus.PassCompleted : LocatorRecordScanStatus.Building;
    public bool IndexHadReadFailures { get; }
    public IReadOnlyList<LocatorRecordObservation> Records { get; }
    public LocatorRecordCursor NextCursor { get; }
    public DateTimeOffset ReadAt { get; }
    public bool CoverageIsAuthoritative => false;
}
