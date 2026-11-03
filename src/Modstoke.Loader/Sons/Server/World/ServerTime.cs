// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Environment;

namespace Modstoke.Sons
{
    /// <summary>Server clock changes through the game's registered time-of-day replication path.</summary>
    public static class ServerTime
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static bool _settingTime;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Checks the server, active clock, owned replication receiver, lock and transition requirements
        /// without changing time. Ready is an observation, not a reservation or authorization grant;
        /// each adjustment checks readiness again. Call on the loader's main thread.
        /// </summary>
        public static ServerTimeStatus CheckReadiness()
        {
            var status = CheckThread();
            if (status != ServerTimeStatus.Ready)
                return status;

            try
            {
                return CheckClockReadiness(out _);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerTimeStatus.Failed;
            }
        }

        /// <summary>
        /// Sets the server's time of day, preserving its day and normal clock speed.
        /// Uses native callbacks and replication, including for vanilla clients. Applied confirms only that
        /// the local native setter returned true, not client delivery or completion of every time receiver.
        /// The caller owns authorization and scheduling. Existing clock locks and transitions are not cleared.
        /// </summary>
        public static ServerTimeStatus SetTimeOfDay(int hour, int minute = 0, int second = 0)
        {
            var status = CheckThread();
            if (status != ServerTimeStatus.Ready)
                return status;
            if (hour < 0 || hour > 23 || minute < 0 || minute > 59 || second < 0 || second > 59)
                return ServerTimeStatus.InvalidTime;

            try
            {
                status = CheckClockReadiness(out _);
                return status == ServerTimeStatus.Ready
                    ? ApplyTimeOfDay(hour, minute, second, milliseconds: 0) : status;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerTimeStatus.Failed;
            }
        }

        /// <summary>
        /// Adjusts the current server clock by signed whole seconds, from -86399 through 86399.
        /// Wraps within the same day without advancing or reversing the native day count. Retains
        /// the current millisecond component at SetDirect's millisecond precision and normal clock speed.
        /// Zero returns NoChange after readiness checks and does not invoke the native setter.
        /// A negative underlying clock returns ClockUnavailable. Uses the same locks, transitions,
        /// authority and replication path as SetTimeOfDay; the caller owns authorization and scheduling.
        /// Applied confirms only that the local native setter returned true, not client delivery.
        /// </summary>
        public static ServerTimeStatus AdjustTimeOfDay(int seconds)
        {
            var status = CheckThread();
            if (status != ServerTimeStatus.Ready)
                return status;
            if (seconds <= -86400 || seconds >= 86400)
                return ServerTimeStatus.InvalidAdjustment;

            try
            {
                status = CheckClockReadiness(out var holder);
                if (status != ServerTimeStatus.Ready)
                    return status;

                var current = new TimeSpan(holder._timeOfDay.Ticks);
                if (current.Ticks < 0)
                    return ServerTimeStatus.ClockUnavailable;
                if (seconds == 0)
                    return ServerTimeStatus.NoChange;

                // Work only on the time-of-day components; passing days: -1 keeps midnight wrapping
                // from turning this adjustment into a day-count or elapsed-gameplay change.
                var target = (current.Hours * 3600 + current.Minutes * 60 + current.Seconds + seconds) % 86400;
                if (target < 0)
                    target += 86400;
                return ApplyTimeOfDay(target / 3600, target / 60 % 60, target % 60, milliseconds: -1);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerTimeStatus.Failed;
            }
        }

        private static ServerTimeStatus CheckThread()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return ServerTimeStatus.Unavailable;
            return Environment.CurrentManagedThreadId == ownerThread
                ? ServerTimeStatus.Ready : ServerTimeStatus.WrongThread;
        }

        private static ServerTimeStatus CheckClockReadiness(out TimeOfDayHolder holder)
        {
            holder = null;
            if (_settingTime)
                return ServerTimeStatus.Busy;
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                return ServerTimeStatus.NotServer;
            if (!TimeOfDayHolder.TryGetInstance(out holder)
                || holder is null || holder.WasCollected || !holder || !holder.isActiveAndEnabled)
                return ServerTimeStatus.ClockUnavailable;
            if (!HasOwnedReplicationReceiver())
                return ServerTimeStatus.ReplicationUnavailable;
            if (!TimeOfDayHolder.MatchesLock(null))
                return ServerTimeStatus.Locked;
            if (TimeOfDayHolder._networkManaged || holder._usingOverrideTime
                || holder._jumpTime.HasValue || holder._jumpTimeStarted
                || holder._holdTime > UnityEngine.Time.time)
                return ServerTimeStatus.Busy;
            return ServerTimeStatus.Ready;
        }

        private static ServerTimeStatus ApplyTimeOfDay(int hour, int minute, int second,
            int milliseconds)
        {
            _settingTime = true;
            try
            {
                // SetDirect preserves negative (unspecified) components and honors the native lock.
                // Suppress elapsed-time delta: this is a clock adjustment, not simulated hours of gameplay.
                return TimeOfDayHolder.SetDirect(days: -1, hours: hour, minutes: minute, seconds: second,
                    milliseconds: milliseconds, ignoreTimeSlicing: true, holdTime: -1, lockObject: null, ignoreDelta: true)
                    ? ServerTimeStatus.Applied : ServerTimeStatus.Rejected;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ServerTimeStatus.ApplyUncertain;
            }
            finally
            {
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
            var found = IntPtr.Zero;
            var count = receivers.Count;
            for (var index = 0; index < count; index++)
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
                var pointer = network.Pointer;
                if (found != IntPtr.Zero && found != pointer)
                    return false;
                found = pointer;
            }
            return found != IntPtr.Zero;
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server clock adjustment failed ("
                    + error.GetType().FullName + "). Further clock failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ServerTimeStatus
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
        ApplyUncertain,
        /// <summary>Readiness checks passed without mutation; this does not reserve or authorize the clock.</summary>
        Ready,
        /// <summary>The relative offset is not in the supported range of -86399 through 86399 seconds.</summary>
        InvalidAdjustment,
        /// <summary>A zero relative offset passed readiness checks; the native setter was not called.</summary>
        NoChange
    }
}
