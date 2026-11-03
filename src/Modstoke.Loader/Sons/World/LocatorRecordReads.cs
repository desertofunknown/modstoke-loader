// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.TerrainDetail;

namespace Modstoke.Sons;

/// <summary>Opt-in, read-only observations of current locator records. No observation is an enforcement verdict.</summary>
public static class LocatorRecordReads
{
    private const int StepsPerTick = 256;
    private static readonly long TickBudget = Math.Max(1, Stopwatch.Frequency / 1000);
    private static readonly object ObserverSync = new();
    private static readonly Dictionary<Guid, int> RequestedKeys = new();
    private static int _unfilteredObservers;
    private static int _ownerThreadId;
    private static int _observers;
    private static int _resetRequested;
    private static int _reportedFailure;
    private static long _nextGeneration;
    private static Scan _building;
    private static Scan _published;
    private static bool _ticking;

    internal static void Initialize()
    {
        if (Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0) != 0)
            return;
        GameRuntime.SceneChanged += _ => RequestReset();
        GameRuntime.WorldChanged += _ => RequestReset();
    }

    /// <summary>
    /// Enables shared incremental work while this lease remains open. Requires the loader's game thread.
    /// Success does not mean a world, cache or completed index is available.
    /// </summary>
    public static bool TryBeginObserving(out LocatorRecordObserver observer,
        out LocatorRecordReadStatus status) => BeginObserving(null, out observer, out status);

    /// <summary>
    /// Observes all matching addresses for one locator without retaining unrelated records.
    /// Requires the loader's game thread; the GUID is normalized to the native 12-byte key.
    /// A completed pass still visits every group; an empty partial pass is not evidence of absence.
    /// </summary>
    public static bool TryBeginObserving(Guid locatorId, out LocatorRecordObserver observer,
        out LocatorRecordReadStatus status) => BeginObserving(Canonicalize(locatorId), out observer, out status);

    private static bool BeginObserving(Guid? locatorId, out LocatorRecordObserver observer,
        out LocatorRecordReadStatus status)
    {
        observer = null;
        status = CheckThread();
        if (status != LocatorRecordReadStatus.Success)
            return false;
        observer = new LocatorRecordObserver(locatorId);
        lock (ObserverSync)
        {
            if (locatorId.HasValue)
            {
                RequestedKeys.TryGetValue(locatorId.Value, out var count);
                RequestedKeys[locatorId.Value] = count + 1;
                if (count == 0 && _unfilteredObservers == 0)
                    RequestReset();
            }
            else if (_unfilteredObservers++ == 0)
                RequestReset();
            Interlocked.Increment(ref _observers);
        }
        return true;
    }

    internal static void Release(Guid? locatorId)
    {
        lock (ObserverSync)
        {
            if (locatorId.HasValue)
            {
                var count = RequestedKeys[locatorId.Value] - 1;
                if (count == 0)
                {
                    RequestedKeys.Remove(locatorId.Value);
                    if (_unfilteredObservers == 0)
                        RequestReset();
                }
                else
                    RequestedKeys[locatorId.Value] = count;
            }
            else if (--_unfilteredObservers == 0)
                RequestReset();
            if (Interlocked.Decrement(ref _observers) == 0)
                RequestReset();
        }
    }

    internal static void RequestReset() => Interlocked.Exchange(ref _resetRequested, 1);

    private static void ConsumeReset()
    {
        if (Interlocked.Exchange(ref _resetRequested, 0) != 0)
            ClearIndex();
    }

    private static void ClearIndex()
    {
        _building = null;
        _published = null;
    }

    internal static void Tick()
    {
        if (CheckThread() != LocatorRecordReadStatus.Success || _ticking)
            return;
        ConsumeReset();
        if (Volatile.Read(ref _observers) == 0)
        {
            ClearIndex();
            return;
        }

        Scan scan = null;
        _ticking = true;
        try
        {
            if (!TryGetRoot(out var root, out _))
            {
                ClearIndex();
                return;
            }

            EnsureIndex(root.Stamp);
            scan = _building;
            var remaining = StepsPerTick;
            var deadline = Stopwatch.GetTimestamp() + TickBudget;
            do
            {
                var advanced = scan.Step(root, ref remaining, deadline);
                if (!ReferenceEquals(scan, _building))
                    return;
                if (!advanced)
                {
                    ClearIndex();
                    return;
                }
                if (!scan.CompletedAt.HasValue)
                    continue;
                _published = scan;
                _building = null;
                break;
            }
            while (remaining > 0 && Stopwatch.GetTimestamp() < deadline);
            ConsumeReset();
        }
        catch (Exception error)
        {
            if (scan is null || ReferenceEquals(scan, _building))
                ClearIndex();
            ReportFailure(error);
        }
        finally
        {
            _ticking = false;
        }
    }

    private static void EnsureIndex(RootStamp stamp)
    {
        if ((_building != null && _building.Stamp != stamp)
            || (_published != null && _published.Stamp != stamp))
            ClearIndex();
        if (_building == null)
        {
            lock (ObserverSync)
                _building = new Scan(++_nextGeneration, stamp,
                    _unfilteredObservers == 0 ? new HashSet<Guid>(RequestedKeys.Keys) : null);
        }
    }

    internal static bool Read(Guid value, long? expectedGeneration, int offset, int maxRecords,
        out LocatorRecordPage page, out LocatorRecordReadStatus status)
    {
        page = null;
        status = CheckThread();
        if (status != LocatorRecordReadStatus.Success)
            return false;
        if (maxRecords is < 1 or > 16 || offset < 0)
        {
            status = LocatorRecordReadStatus.InvalidRequest;
            return false;
        }

        ConsumeReset();
        Scan scan = null;
        try
        {
            if (!TryGetRoot(out var root, out status))
            {
                ClearIndex();
                return false;
            }
            EnsureIndex(root.Stamp);
            scan = _published ?? _building;
            if (expectedGeneration.HasValue && expectedGeneration.Value != scan.Generation)
            {
                status = LocatorRecordReadStatus.Changed;
                return false;
            }

            var key = Canonicalize(value);
            scan.Records.TryGetValue(key, out var addresses);
            var count = addresses?.Count ?? 0;
            if (offset > count)
            {
                status = LocatorRecordReadStatus.InvalidCursor;
                return false;
            }

            // Native callbacks may advance this scan; keep progress aligned with the captured count.
            var scanCompletedAt = scan.CompletedAt;
            var indexHadReadFailures = scan.HadReadFailures;
            var recordCount = Math.Min(maxRecords, count - offset);
            var records = recordCount == 0 ? Array.Empty<LocatorRecordObservation>()
                : new LocatorRecordObservation[recordCount];
            // Records on a page usually share one locator data asset; walk its prefab LODs once.
            var prefabs = recordCount == 0 ? null : new Dictionary<IntPtr, LocatorPrefabObservation[]>();
            for (var i = 0; i < records.Length; i++)
                records[i] = LocatorRecordCapture.Read(root, addresses[offset + i], key, prefabs);

            // Reacquire roots after component reads, which may cross native callback boundaries.
            var rootAvailable = TryGetRoot(out var after, out _);
            if (!ReferenceEquals(scan, _published ?? _building))
            {
                status = LocatorRecordReadStatus.Changed;
                return false;
            }
            if (Volatile.Read(ref _resetRequested) != 0 || !rootAvailable || after.Stamp != root.Stamp)
            {
                ConsumeReset();
                ClearIndex();
                status = LocatorRecordReadStatus.Changed;
                return false;
            }

            page = new LocatorRecordPage(key, scan.Generation, offset, count,
                scan.StartedAt, scanCompletedAt, indexHadReadFailures, records);
            status = LocatorRecordReadStatus.Success;
            return true;
        }
        catch (Exception error)
        {
            if (scan is null || ReferenceEquals(scan, _published ?? _building))
                ClearIndex();
            status = LocatorRecordReadStatus.Failed;
            ReportFailure(error);
            return false;
        }
    }

    private static LocatorRecordReadStatus CheckThread()
    {
        var owner = Volatile.Read(ref _ownerThreadId);
        if (owner == 0)
            return LocatorRecordReadStatus.Unavailable;
        return owner == Environment.CurrentManagedThreadId
            ? LocatorRecordReadStatus.Success : LocatorRecordReadStatus.WrongThread;
    }

    private static bool TryGetRoot(out Root root, out LocatorRecordReadStatus status)
    {
        root = default;
        status = LocatorRecordReadStatus.ManagerUnavailable;
        if (!WorldObjectLocatorManager.TryGetInstance(out var manager)
            || manager is null || manager.WasCollected || !manager || !manager.isActiveAndEnabled)
            return false;
        var cache = WorldObjectLocatorManager.GetCache();
        status = LocatorRecordReadStatus.CacheUnavailable;
        if (cache is null || cache.WasCollected || !cache)
            return false;
        root = new Root(manager, cache);
        status = LocatorRecordReadStatus.Success;
        return true;
    }

    internal static Guid Canonicalize(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        value.TryWriteBytes(bytes);
        bytes[12..].Clear();
        return new Guid(bytes);
    }

    internal static bool TryReadKey(WorldLocatorId value, out Guid key)
    {
        key = default;
        if (value is null || value.WasCollected)
            return false;
        var source = value._bytes;
        if (source is null || source.WasCollected || source.Length != 12)
            return false;
        Span<byte> bytes = stackalloc byte[16];
        bytes.Clear();
        source.AsSpan().CopyTo(bytes);
        GC.KeepAlive(source);
        key = new Guid(bytes);
        return true;
    }

    internal static IntPtr Pointer(Il2CppObjectBase value) =>
        value is null || value.WasCollected ? IntPtr.Zero : value.Pointer;

    internal static ListStamp Stamp<T>(Il2CppSystem.Collections.Generic.List<T> list)
    {
        if (list is null)
            return default;
        if (list.WasCollected)
            throw new InvalidOperationException("The native locator list is unavailable.");
        var items = list._items;
        var count = list._size;
        if (count < 0 || count > (items is null ? 0 : items.Length))
            throw new InvalidOperationException("The native locator list bounds are unavailable.");
        return new ListStamp(list.Pointer, Pointer(items), list._version, count);
    }

    internal static void ReportFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
            return;
        try
        {
            RuntimeEntryPoint.LogSource?.LogWarning("Locator-record observation failed ("
                + error.GetType().FullName + "). Further observation failures will not be logged.");
        }
        catch (Exception) { }
    }

    internal readonly record struct ListStamp(IntPtr List, IntPtr Items, int Version, int Count);
    internal readonly record struct RootStamp(IntPtr Manager, int ManagerInstanceId, IntPtr Cache,
        int CacheInstanceId, ListStamp Groups, ListStamp WorldGroups, IntPtr Ungrouped, ListStamp Assets);
    internal readonly record struct Address(LocatorRecordMembership Membership,
        int MembershipIndex, IntPtr Group, int RecordIndex, IntPtr Uid);

    // Root wrappers are call-local. Scans retain only the value-type stamp and numeric addresses.
    internal readonly struct Root
    {
        internal readonly WorldObjectLocatorGroupCache Cache;
        internal readonly RootStamp Stamp;

        internal Root(WorldObjectLocatorManager manager, WorldObjectLocatorGroupCache cache)
        {
            Cache = cache;
            Stamp = new RootStamp(manager.Pointer, manager.GetInstanceID(), cache.Pointer, cache.GetInstanceID(),
                LocatorRecordReads.Stamp(cache._groups),
                LocatorRecordReads.Stamp(cache._ungroupedWorldIds), Pointer(cache._ungrouped),
                LocatorRecordReads.Stamp(cache._locatorDataAssetCache));
        }

        internal WorldObjectLocatorGroup ResolveGroup(LocatorRecordMembership membership, int index)
        {
            if (index < 0)
                return null;
            if (membership == LocatorRecordMembership.Grouped)
            {
                var groups = Cache._groups;
                if (groups is null || groups.WasCollected || index >= groups.Count)
                    return null;
                var entry = groups[index];
                return entry is null || entry.WasCollected ? null : entry._value;
            }
            if (membership == LocatorRecordMembership.UngroupedWorldId)
            {
                var groups = Cache._ungroupedWorldIds;
                return groups is null || groups.WasCollected || index >= groups.Count ? null : groups[index];
            }
            return index == 0 ? Cache._ungrouped : null;
        }
    }

    private sealed class Scan
    {
        internal readonly long Generation;
        internal readonly RootStamp Stamp;
        internal readonly DateTimeOffset StartedAt = DateTimeOffset.UtcNow;
        internal DateTimeOffset? CompletedAt;
        internal bool HadReadFailures;
        internal readonly Dictionary<Guid, List<Address>> Records = new();

        private readonly HashSet<Guid> _requestedKeys;
        private readonly HashSet<IntPtr> _seenGroups = new();
        private int _membership;
        private int _membershipIndex;
        private bool _inGroup;
        private Address _address;
        private ListStamp _uids;
        private int _recordIndex;

        internal Scan(long generation, RootStamp stamp, HashSet<Guid> requestedKeys)
        {
            Generation = generation;
            Stamp = stamp;
            _requestedKeys = requestedKeys;
        }

        internal bool Step(Root root, ref int remaining, long deadline)
        {
            if (!_inGroup)
            {
                remaining--;
                while (_membership < 3)
                {
                    var count = _membership == 0 ? Stamp.Groups.Count
                        : _membership == 1 ? Stamp.WorldGroups.Count : Stamp.Ungrouped == IntPtr.Zero ? 0 : 1;
                    if (_membershipIndex >= count)
                    {
                        _membership++;
                        _membershipIndex = 0;
                        continue;
                    }

                    var membership = (LocatorRecordMembership)_membership;
                    var index = _membershipIndex++;
                    var group = root.ResolveGroup(membership, index);
                    var pointer = Pointer(group);
                    if (pointer == IntPtr.Zero)
                    {
                        HadReadFailures = true;
                        return true;
                    }
                    if (!_seenGroups.Add(pointer))
                        return true;

                    _uids = LocatorRecordReads.Stamp(group._uids);
                    if (_uids.List == IntPtr.Zero)
                    {
                        HadReadFailures = true;
                        return true;
                    }
                    _address = new Address(membership, index, pointer, 0, IntPtr.Zero);
                    _recordIndex = 0;
                    _inGroup = true;
                    return true;
                }

                CompletedAt = DateTimeOffset.UtcNow;
                return true;
            }

            var current = root.ResolveGroup(_address.Membership, _address.MembershipIndex);
            if (Pointer(current) != _address.Group)
                return false;
            var ids = current._uids;
            if (LocatorRecordReads.Stamp(ids) != _uids)
                return false;
            var items = ids._items;
            if (Pointer(items) != _uids.Items || _uids.Count > (items?.Length ?? 0))
                return false;

            // Reuse call-local group/array wrappers for this slice, never across frames.
            while (_recordIndex < _uids.Count && remaining > 0)
            {
                remaining--;
                var recordIndex = _recordIndex++;
                try
                {
                    // No game code runs inside this slice; the stamp check after it catches list changes.
                    var id = items[recordIndex];
                    if (!TryReadKey(id, out var key))
                        HadReadFailures = true;
                    else if (_requestedKeys == null || _requestedKeys.Contains(key))
                    {
                        if (!Records.TryGetValue(key, out var matches))
                            Records.Add(key, matches = new List<Address>());
                        matches.Add(_address with { RecordIndex = recordIndex, Uid = Pointer(id) });
                    }
                }
                catch (Exception error)
                {
                    HadReadFailures = true;
                    ReportFailure(error);
                }
                if ((remaining & 15) == 0 && Stopwatch.GetTimestamp() >= deadline)
                    break;
            }
            _inGroup = _recordIndex < _uids.Count;
            var after = root.ResolveGroup(_address.Membership, _address.MembershipIndex);
            return Pointer(after) == _address.Group && LocatorRecordReads.Stamp(after._uids) == _uids;
        }
    }
}

/// <summary>An independent lease on shared incremental observation work. Dispose when the consumer no longer needs it.</summary>
public sealed class LocatorRecordObserver : IDisposable
{
    private int _disposed;
    private readonly Guid? _locatorId;

    internal LocatorRecordObserver(Guid? locatorId) => _locatorId = locatorId;

    /// <summary>Reads at most 16 indexed addresses on the game thread, without scanning the world.</summary>
    public bool TryRead(Guid locatorId, out LocatorRecordPage page,
        out LocatorRecordReadStatus status, int maxRecords = 8)
    {
        page = null;
        status = LocatorRecordReadStatus.Disposed;
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        if (_locatorId.HasValue && LocatorRecordReads.Canonicalize(locatorId) != _locatorId.Value)
        {
            status = LocatorRecordReadStatus.InvalidRequest;
            return false;
        }
        return Read(locatorId, null, 0, maxRecords, out page, out status);
    }

    /// <summary>Continues the same index generation; Changed means restart from TryRead.</summary>
    public bool TryReadNext(LocatorRecordCursor cursor, out LocatorRecordPage page,
        out LocatorRecordReadStatus status, int maxRecords = 8)
    {
        page = null;
        status = LocatorRecordReadStatus.Disposed;
        if (Volatile.Read(ref _disposed) != 0)
            return false;
        if (cursor is null || (_locatorId.HasValue && cursor.LocatorId != _locatorId.Value))
        {
            status = LocatorRecordReadStatus.InvalidCursor;
            return false;
        }
        return Read(cursor.LocatorId, cursor.Generation, cursor.Offset,
            maxRecords, out page, out status);
    }

    private bool Read(Guid locatorId, long? expectedGeneration, int offset, int maxRecords,
        out LocatorRecordPage page, out LocatorRecordReadStatus status)
    {
        var read = LocatorRecordReads.Read(locatorId, expectedGeneration, offset,
            maxRecords, out page, out status);
        if (Volatile.Read(ref _disposed) == 0)
            return read;
        page = null;
        status = LocatorRecordReadStatus.Disposed;
        return false;
    }

    /// <summary>Releases only this lease. May be called from any thread.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            LocatorRecordReads.Release(_locatorId);
    }
}
