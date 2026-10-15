// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Environment;

namespace OpenACAI.Sons
{
    /// <summary>Server clock changes through the game's registered time-of-day replication path.</summary>
    public static class OpenACAISonsServerTime
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _settingTime;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Sets the server's time of day, preserving its day and normal clock speed.
        /// Uses native callbacks and replication, including for vanilla clients. Applied confirms only that
        /// the local native setter returned true, not client delivery or completion of every time receiver.
        /// The caller owns authorization and scheduling. Existing clock locks and transitions are not cleared.
        /// </summary>
        public static OpenACAISonsServerTimeStatus SetTimeOfDay(int hour, int minute = 0, int second = 0)
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsServerTimeStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsServerTimeStatus.WrongThread;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
                return OpenACAISonsServerTimeStatus.InvalidTime;
            if (_settingTime)
                return OpenACAISonsServerTimeStatus.Busy;

            var mutationStarted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsServerTimeStatus.NotServer;
                if (!TimeOfDayHolder.TryGetInstance(out var holder)
                    || holder is null || holder.WasCollected || !holder || !holder.isActiveAndEnabled)
                    return OpenACAISonsServerTimeStatus.ClockUnavailable;
                if (!HasOwnedReplicationReceiver())
                    return OpenACAISonsServerTimeStatus.ReplicationUnavailable;
                if (!TimeOfDayHolder.MatchesLock(null))
                    return OpenACAISonsServerTimeStatus.Locked;
                if (TimeOfDayHolder._networkManaged || holder._usingOverrideTime
                    || holder._jumpTime.HasValue || holder._jumpTimeStarted
                    || holder._holdTime > UnityEngine.Time.time)
                    return OpenACAISonsServerTimeStatus.Busy;

                // SetDirect preserves negative (unspecified) components and honors the native lock.
                // Suppress elapsed-time delta: this is a clock adjustment, not simulated hours of gameplay.
                _settingTime = true;
                mutationStarted = true;
                return TimeOfDayHolder.SetDirect(days: -1, hours: hour, minutes: minute, seconds: second,
                    milliseconds: 0, ignoreTimeSlicing: true, holdTime: -1, lockObject: null, ignoreDelta: true)
                    ? OpenACAISonsServerTimeStatus.Applied : OpenACAISonsServerTimeStatus.Rejected;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return mutationStarted ? OpenACAISonsServerTimeStatus.ApplyUncertain : OpenACAISonsServerTimeStatus.Failed;
            }
            finally
            {
                if (mutationStarted)
                    _settingTime = false;
            }
        }

        private static bool HasOwnedReplicationReceiver()
        {
            var receivers = TimeOfDayHolder._connections;
            if (receivers is null || receivers.WasCollected)
                return false;

            // The owner registers here in Attached and unregisters in OnDisable. A scene object alone
            // does not prove that clock changes can reach the native network publisher.
            NetworkTimeOfDay found = null;
            for (var index = 0; index < receivers.Count; index++)
            {
                var receiver = receivers[index];
                if (receiver is null || receiver.WasCollected)
                    continue;
                var network = receiver.TryCast<NetworkTimeOfDay>();
                if (network is null || network.WasCollected || !network || !network.isActiveAndEnabled)
                    continue;
                var entity = network.entity;
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached || !entity.isOwner)
                    continue;
                var state = network.state;
                if (state is null || state.WasCollected)
                    continue;
                if (found != null && found.Pointer != network.Pointer)
                    return false;
                found = network;
            }
            return found != null;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server clock adjustment failed ("
                    + error.GetType().FullName + "). Further clock failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsServerTimeStatus
    {
        /// <summary>The native local setter returned true; this is not a client synchronization receipt.</summary>
        Applied,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidTime,
        ClockUnavailable,
        /// <summary>No single active, attached, locally owned native time publisher was registered.</summary>
        ReplicationUnavailable,
        Locked,
        /// <summary>A clock override, hold, time jump, network-managed clock or nested SDK adjustment is active.</summary>
        Busy,
        /// <summary>The native setter returned false without accepting the adjustment.</summary>
        Rejected,
        /// <summary>Preflight failed before calling the native setter.</summary>
        Failed,
        /// <summary>The native setter threw; the clock or some receivers may already have changed.</summary>
        ApplyUncertain
    }
}
