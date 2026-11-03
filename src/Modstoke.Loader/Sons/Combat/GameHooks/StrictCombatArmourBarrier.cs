// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Wearable.Armour;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatArmourStatus
    {
        Success, Unavailable, WrongThread, AlreadyInWorld, StaleObservation,
        LifeBarrierRequired, NativeHistoryIncomplete, AlreadyOwned, CapacityReached, Busy, Failed,
        RespawnRequired, Unprovisioned, LimitReached, Retired
    }

    // Retirement ends projection authority without releasing the retained native receiver.
    internal sealed class StrictCombatArmourBarrier
    {
        private int _retired;
        private uint _attemptedBreaks;

        internal StrictCombatArmourBarrier(StrictCombatLocalArmourBinding capture,
            StrictCombatLifeBarrier life)
        {
            Capture = capture;
            Life = life;
            System = capture.Original.Native.System;
            SystemPointer = System.Pointer;
            Stats = life.Stats;
            StatsPointer = Stats.Pointer;
            ProjectedCursor = capture.Original.NextAvailableOrderIndex;
        }

        internal StrictCombatLocalArmourBinding Capture { get; }
        internal StrictCombatLifeBarrier Life { get; }
        internal StrictCombatClient Owner => Capture.Owner;
        internal PlayerArmourSystem System { get; }
        internal nint SystemPointer { get; }
        internal PlayerStats Stats { get; }
        internal nint StatsPointer { get; }
        internal int ProjectedCursor { get; private set; }
        internal bool IsRetired => Volatile.Read(ref _retired) != 0 || Capture.IsRetired || Life.IsRetired;

        internal void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
            Capture.Retire();
        }

        internal bool TryBeginBreak(int resourceIndex)
        {
            if (IsRetired || !Capture.InitialState.TryGetPiece(resourceIndex, out _)
                || Capture.HasCompletedBreak(resourceIndex)) return false;
            uint bit = 1u << resourceIndex;
            if ((_attemptedBreaks & bit) != 0) return false;
            _attemptedBreaks |= bit;
            return true;
        }

        internal void CompleteBreakCursor(int orderIndex) => ProjectedCursor = orderIndex;
    }

    internal static partial class StrictCombatArmourHooks
    {
        private const int MaximumOwners = 32;
        private const int MaximumMutationDepth = 64;
        private static HarmonySupportSnapshot _support;
        private static Action<string> _warning;
        private static int _thread, _mutationDepth, _acquireFailureReported;
        private static bool _patchAttempted, _hooksInstalled, _prepared;
        private static volatile bool _historyIncomplete;
        private static StrictCombatArmourBarrier[] _owners = Array.Empty<StrictCombatArmourBarrier>();

        internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _thread, Environment.CurrentManagedThreadId, 0) != 0) return;
            _support = support;
            _warning = warning;
        }

        internal static StrictCombatArmourStatus PrepareForNextStart()
        {
            if (_thread == 0) return StrictCombatArmourStatus.Unavailable;
            if (!OnOwnerThread) return StrictCombatArmourStatus.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (GameRuntime.State.IsInWorld || LocalPlayer.IsInWorld || BoltNetwork.isRunning
                    || (socket != null && !socket.WasCollected && socket.State != UdpKit.UdpSocketState.Shutdown))
                    return StrictCombatArmourStatus.AlreadyInWorld;
                if (_historyIncomplete) return StrictCombatArmourStatus.NativeHistoryIncomplete;
                if (!InstallHooks()) return StrictCombatArmourStatus.Unavailable;
                _prepared = true;
                return StrictCombatArmourStatus.Success;
            }
            catch (Exception) { return StrictCombatArmourStatus.Failed; }
        }

        internal static bool TryAcquire(StrictCombatClient owner, StrictCombatLocalArmourBinding capture,
            StrictCombatLifeBarrier life, out StrictCombatArmourBarrier barrier,
            out StrictCombatArmourStatus status)
        {
            barrier = null;
            status = StrictCombatArmourStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatArmourStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatArmourStatus.NativeHistoryIncomplete;
            if (_historyIncomplete) return false;
            status = StrictCombatArmourStatus.Busy;
            if (Volatile.Read(ref _mutationDepth) != 0) return false;
            try
            {
                status = StrictCombatArmourStatus.StaleObservation;
                if (owner == null || !owner.IsCurrentArmourBarrierContext(capture, life)) return false;
                var system = capture.Original.Native.System;
                var owners = Volatile.Read(ref _owners);
                foreach (var existing in owners)
                {
                    if (existing.SystemPointer != system.Pointer) continue;
                    if (!existing.IsRetired && ReferenceEquals(existing.Owner, owner)
                        && ReferenceEquals(existing.Capture, capture) && ReferenceEquals(existing.Life, life))
                    {
                        barrier = existing;
                        status = StrictCombatArmourStatus.Success;
                        return true;
                    }
                    status = StrictCombatArmourStatus.AlreadyOwned;
                    return false;
                }
                if (owners.Length == MaximumOwners)
                { status = StrictCombatArmourStatus.CapacityReached; return false; }
                if (!owner.IsCurrentArmourBarrierContext(capture, life)
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                status = StrictCombatArmourStatus.NativeHistoryIncomplete;
                if (_historyIncomplete) return false;
                status = StrictCombatArmourStatus.Busy;
                if (Volatile.Read(ref _mutationDepth) != 0) return false;
                barrier = new StrictCombatArmourBarrier(capture, life);
                var next = new StrictCombatArmourBarrier[owners.Length + 1];
                Array.Copy(owners, next, owners.Length);
                next[^1] = barrier;
                Volatile.Write(ref _owners, next);
                if (_historyIncomplete || barrier.IsRetired)
                {
                    barrier.Retire();
                    status = StrictCombatArmourStatus.NativeHistoryIncomplete;
                    return false;
                }
                status = StrictCombatArmourStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _acquireFailureReported, "armour barrier acquisition", error);
                status = StrictCombatArmourStatus.Failed;
                return false;
            }
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _thread;
        private static bool IsObserving => _prepared || Volatile.Read(ref _owners).Length != 0;

        private static bool EnterMutation(PlayerArmourSystem system, bool runOriginal, out bool entered)
        {
            entered = false;
            if (!runOriginal || !IsObserving) return true;
            try
            {
                var owners = Volatile.Read(ref _owners);
                // Retained receiver addresses are safe to compare before any native reads.
                foreach (var owner in owners)
                    if (system is not null && owner.SystemPointer == system.Pointer) return false;
                if (!OnOwnerThread || system is null || system.WasCollected || !system)
                    return AllowUnreadableOwner();

                // A replacement assigned to the same owned Stats must not become a new
                // route around suppression. Unrelated remote armour remains unowned.
                if (owners.Length != 0)
                {
                    var stats = LocalPlayer.Stats;
                    if (stats is not null && !stats.WasCollected && stats)
                    {
                        foreach (var owner in owners)
                        {
                            if (owner.StatsPointer != stats.Pointer) continue;
                            var current = stats._armourSystem;
                            if (current is not null && current.Pointer == system.Pointer)
                            { owner.Retire(); return false; }
                        }
                    }
                }

                if (Volatile.Read(ref _mutationDepth) >= MaximumMutationDepth)
                { InvalidateHistory(); return false; }
                Interlocked.Increment(ref _mutationDepth);
                entered = true;
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static Exception FinishMutation(bool entered, Exception error)
        {
            if (entered && Interlocked.Decrement(ref _mutationDepth) < 0) InvalidateHistory();
            if (error != null && IsObserving) InvalidateHistory();
            return error;
        }

        private static bool AllowUnreadableOwner()
        {
            InvalidateHistory();
            return Volatile.Read(ref _owners).Length == 0;
        }

        private static void InvalidateHistory()
        {
            _historyIncomplete = true;
            foreach (var owner in Volatile.Read(ref _owners)) owner.Retire();
        }
    }
}
