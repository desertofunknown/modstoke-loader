// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.TerrainDetail;

namespace OpenACAI.Sons
{
    /// <summary>Read-only local locator observations for servers, clients and single-player.</summary>
    public static class OpenACAISonsWorldLocatorReads
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Reads registered, recorded and unapplied state for the native 12-byte locator key.
        /// Success means the local sources were read, not that this ID exists or every peer agrees.
        /// </summary>
        public static bool TryRead(Guid locatorId, out OpenACAISonsWorldLocatorSnapshot snapshot,
            out OpenACAISonsWorldLocatorReadStatus status)
        {
            snapshot = null;
            status = CheckThread();
            if (status != OpenACAISonsWorldLocatorReadStatus.Success)
                return false;

            try
            {
                if (!WorldObjectLocatorManager.TryGetInstance(out var manager)
                    || manager is null || manager.WasCollected || !manager || !manager.isActiveAndEnabled)
                {
                    status = OpenACAISonsWorldLocatorReadStatus.ManagerUnavailable;
                    return false;
                }

                var holders = WorldObjectLocatorManager._locatorHolders;
                if (holders is null || holders.WasCollected)
                {
                    status = OpenACAISonsWorldLocatorReadStatus.RegistryUnavailable;
                    return false;
                }
                var data = manager._saveData;
                if (data is null || data.WasCollected)
                {
                    status = OpenACAISonsWorldLocatorReadStatus.StateDataUnavailable;
                    return false;
                }
                var recorded = data.GetStates();
                var unapplied = data.GetUnappliedStates();
                if (recorded is null || recorded.WasCollected || unapplied is null || unapplied.WasCollected)
                {
                    status = OpenACAISonsWorldLocatorReadStatus.StateDataUnavailable;
                    return false;
                }

                var id = CreateLookupId(locatorId, out var canonicalId);
                int? registeredState = null;
                Vector3? registeredPosition = null;
                if (manager.TryGetHolder(id, out var holder))
                {
                    if (holder is null || holder.WasCollected || !holder)
                    {
                        status = OpenACAISonsWorldLocatorReadStatus.LocatorUnavailable;
                        return false;
                    }
                    var locator = holder.GetLocator();
                    if (locator is null || locator.WasCollected)
                    {
                        status = OpenACAISonsWorldLocatorReadStatus.LocatorUnavailable;
                        return false;
                    }
                    var currentId = locator.UniqueId;
                    if (currentId is null || currentId.WasCollected || !currentId.Equals(id))
                    {
                        status = OpenACAISonsWorldLocatorReadStatus.LocatorUnavailable;
                        return false;
                    }

                    var position = locator.Position;
                    registeredPosition = new Vector3(position.x, position.y, position.z);
                    registeredState = (int)locator.GetState();
                }

                int? recordedState = recorded.TryGetValue(id, out var stored) ? (int)stored : null;
                int? unappliedState = unapplied.TryGetValue(id, out var pending) ? (int)pending : null;
                snapshot = new OpenACAISonsWorldLocatorSnapshot(canonicalId, registeredState,
                    registeredPosition, recordedState, unappliedState);
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldLocatorReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        internal static WorldLocatorId CreateLookupId(Guid value, out Guid canonicalId)
        {
            Span<byte> bytes = stackalloc byte[16];
            value.TryWriteBytes(bytes);
            bytes[12..].Clear();
            canonicalId = new Guid(bytes);

            // Use the native constructor's 12-byte key without string conversion or native object lookup.
            var nativeGuid = new Il2CppSystem.Guid
            {
                _a = BinaryPrimitives.ReadInt32LittleEndian(bytes),
                _b = BinaryPrimitives.ReadInt16LittleEndian(bytes[4..]),
                _c = BinaryPrimitives.ReadInt16LittleEndian(bytes[6..]),
                _d = bytes[8],
                _e = bytes[9],
                _f = bytes[10],
                _g = bytes[11]
            };
            return new WorldLocatorId(nativeGuid);
        }

        private static OpenACAISonsWorldLocatorReadStatus CheckThread()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsWorldLocatorReadStatus.Unavailable;
            return Environment.CurrentManagedThreadId == ownerThread
                ? OpenACAISonsWorldLocatorReadStatus.Success : OpenACAISonsWorldLocatorReadStatus.WrongThread;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("World-locator read failed ("
                    + error.GetType().FullName + "). Further locator read failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsWorldLocatorReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        ManagerUnavailable,
        RegistryUnavailable,
        StateDataUnavailable,
        LocatorUnavailable,
        Failed
    }

    /// <summary>Managed copies from separate local sources, not an atomic or authoritative world-state verdict.</summary>
    public sealed class OpenACAISonsWorldLocatorSnapshot
    {
        internal OpenACAISonsWorldLocatorSnapshot(Guid locatorId, int? registeredState, Vector3? registeredPosition,
            int? recordedState, int? unappliedState)
        {
            LocatorId = locatorId;
            RegisteredState = registeredState;
            RegisteredPosition = registeredPosition;
            RecordedState = recordedState;
            UnappliedState = unappliedState;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The native 12-byte key represented as a GUID with its final four bytes zeroed.</summary>
        public Guid LocatorId { get; }
        /// <summary>Current state of a matching registered holder's locator, or null when no holder was found.</summary>
        public int? RegisteredState { get; }
        public OpenACAISonsWorldLocatorState? KnownRegisteredState => KnownState(RegisteredState);
        /// <summary>Stored locator position, not the current transform of a spawned or moving object.</summary>
        public Vector3? RegisteredPosition { get; }
        /// <summary>Entry in the local recorded-state collection, not proof that it was persisted to disk.</summary>
        public int? RecordedState { get; }
        public OpenACAISonsWorldLocatorState? KnownRecordedState => KnownState(RecordedState);
        /// <summary>Entry in the local unapplied-state collection, not a predicted final state.</summary>
        public int? UnappliedState { get; }
        public OpenACAISonsWorldLocatorState? KnownUnappliedState => KnownState(UnappliedState);
        /// <summary>False means no entry in these sources, not proof that no such world object exists.</summary>
        public bool HasAnyObservation => RegisteredState.HasValue || RecordedState.HasValue || UnappliedState.HasValue;
        /// <summary>Read completion time, not a simulation, save or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }

        private static OpenACAISonsWorldLocatorState? KnownState(int? value) =>
            value is >= -1 and <= 4 ? (OpenACAISonsWorldLocatorState)value.Value : null;
    }
}
