// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;
using static Modstoke.Sons.LocatorRecordReads;

namespace Modstoke.Sons;

internal static class LocatorRecordCapture
{
    internal static LocatorRecordObservation Read(Root root, Address address, Guid key,
        Dictionary<IntPtr, LocatorPrefabObservation[]> prefabCache)
    {
        LocatorRecordObservation Empty(LocatorRecordIssues issues) =>
            new(key, address.Membership, address.MembershipIndex, address.RecordIndex, issues);

        try
        {
            var group = root.ResolveGroup(address.Membership, address.MembershipIndex);
            if (Pointer(group) == IntPtr.Zero)
                return Empty(LocatorRecordIssues.SourceUnavailable);
            if (Pointer(group) != address.Group)
                return Empty(LocatorRecordIssues.ReferencesChanged);

            var ids = group._uids;
            var positions = group._positions;
            var rotations = group._rotations;
            var scales = group._scales;
            var indexes = group._locatorDataCacheIndexes;
            var idStamp = Stamp(ids);
            var positionStamp = Stamp(positions);
            var rotationStamp = Stamp(rotations);
            var scaleStamp = Stamp(scales);
            var indexStamp = Stamp(indexes);
            if (idStamp.List == IntPtr.Zero || positionStamp.List == IntPtr.Zero
                || rotationStamp.List == IntPtr.Zero || scaleStamp.List == IntPtr.Zero || indexStamp.List == IntPtr.Zero)
                return Empty(LocatorRecordIssues.ListsUnavailable);

            var index = address.RecordIndex;
            if (index < 0 || index >= idStamp.Count)
                return Empty(LocatorRecordIssues.ReferencesChanged);
            if (positionStamp.Count != idStamp.Count || rotationStamp.Count != idStamp.Count
                || scaleStamp.Count != idStamp.Count || indexStamp.Count != idStamp.Count)
                return Empty(LocatorRecordIssues.ListsMisaligned);
            var id = ids[index];
            if (Pointer(id) != address.Uid || !TryReadKey(id, out var observedKey) || observedKey != key)
                return Empty(LocatorRecordIssues.KeyChanged);

            var position = Copy(positions[index]);
            var rotation = Copy(rotations[index]);
            var scale = Copy(scales[index]);
            var slot = indexes[index];
            var assets = root.Cache._locatorDataAssetCache;
            var assetStamp = Stamp(assets);
            var issues = LocatorRecordIssues.None;
            WorldObjectLocatorData data = null;
            int? dataInstanceId = null;
            int? dataType = null;
            LocatorPrefabObservation[] prefabs = null;
            if (assetStamp.List == IntPtr.Zero || slot < 0 || slot >= assetStamp.Count)
            {
                issues |= LocatorRecordIssues.AssetSlotUnavailable;
            }
            else
            {
                data = assets[slot];
                if (data is null || data.WasCollected || !data)
                    issues |= LocatorRecordIssues.DataUnavailable;
                else
                {
                    dataInstanceId = data.GetInstanceID();
                    dataType = (int)data._type;
                    if (!prefabCache.TryGetValue(Pointer(data), out prefabs))
                        prefabCache.Add(Pointer(data), prefabs = LocatorPrefabReads.Capture(data));
                }
            }

            // List versions miss direct array and referenced-object writes, so recheck the selected values too.
            var currentGroup = root.ResolveGroup(address.Membership, address.MembershipIndex);
            if (Pointer(currentGroup) != address.Group
                || Stamp(currentGroup._uids) != idStamp
                || Stamp(currentGroup._positions) != positionStamp
                || Stamp(currentGroup._rotations) != rotationStamp
                || Stamp(currentGroup._scales) != scaleStamp
                || Stamp(currentGroup._locatorDataCacheIndexes) != indexStamp
                || Stamp(root.Cache._locatorDataAssetCache) != assetStamp)
                return Empty(LocatorRecordIssues.ReferencesChanged);

            if (Pointer(ids[index]) != address.Uid || !TryReadKey(ids[index], out var finalKey) || finalKey != key)
                return Empty(LocatorRecordIssues.KeyChanged);
            if (!Copy(positions[index]).Equals(position) || !Copy(rotations[index]).Equals(rotation)
                || !Copy(scales[index]).Equals(scale) || indexes[index] != slot)
                return Empty(LocatorRecordIssues.ReferencesChanged);
            if (slot >= 0 && slot < assetStamp.Count && Pointer(assets[slot]) != Pointer(data))
                return Empty(LocatorRecordIssues.ReferencesChanged);
            if (dataInstanceId.HasValue && (data.WasCollected || !data
                || data.GetInstanceID() != dataInstanceId.Value || (int)data._type != dataType.Value))
                return Empty(LocatorRecordIssues.ReferencesChanged);

            return new LocatorRecordObservation(key, address.Membership, address.MembershipIndex,
                index, issues, position, rotation, scale, slot, dataInstanceId, dataType, prefabs);
        }
        catch (Exception error)
        {
            ReportFailure(error);
            return Empty(LocatorRecordIssues.ReadFailed);
        }
    }

    private static Vector3 Copy(UnityEngine.Vector3 value) => new(value.x, value.y, value.z);
    private static Quaternion Copy(UnityEngine.Quaternion value) => new(value.x, value.y, value.z, value.w);
}
