// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static bool _breathingPrepared, _breathingPatchAttempted, _breathingHooksInstalled;
        private static volatile bool _breathingHistoryIncomplete;
        private static int _breathingCallDepth;
        private static readonly List<(PlayerStats Stats, BreathingData Breathing)> BreathingObserved = new();

        internal static StrictCombatBreathingStatus PrepareBreathingForNextStart()
        {
            if (_thread == 0) return StrictCombatBreathingStatus.Unavailable;
            if (!OnOwnerThread) return StrictCombatBreathingStatus.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (GameRuntime.State.IsInWorld || LocalPlayer.IsInWorld || BoltNetwork.isRunning
                    || (socket != null && !socket.WasCollected && socket.State != UdpKit.UdpSocketState.Shutdown))
                    return StrictCombatBreathingStatus.AlreadyInWorld;
                if (_breathingHistoryIncomplete) return StrictCombatBreathingStatus.NativeActionObserved;
                if (!InstallBreathingHooks()) return StrictCombatBreathingStatus.Unavailable;
                _breathingPrepared = true;
                return StrictCombatBreathingStatus.Success;
            }
            catch (Exception) { return StrictCombatBreathingStatus.Failed; }
        }

        internal static bool TryAcquireBreathing(StrictCombatBreathingBarrier candidate,
            StrictCombatBreathingBarrier previous, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, StrictCombatOwnedBreathingState air, int frame,
            out StrictCombatBreathingStatus status)
        {
            status = StrictCombatBreathingStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatBreathingStatus.Unavailable;
            if (!_breathingPrepared || !_breathingHooksInstalled) return false;
            if (_breathingCallDepth != 0)
            { status = StrictCombatBreathingStatus.Busy; return false; }
            status = StrictCombatBreathingStatus.UnsupportedContext;
            if (candidate == null || !candidate.Owner.IsCurrentBreathingAcquisition(candidate, previous,
                in binding, in state, air, frame)) return false;
            if (!BreathingHistoryAllows(candidate))
            { status = StrictCombatBreathingStatus.NativeActionObserved; return false; }
            var owners = Volatile.Read(ref _owners);
            bool foundLife = false;
            foreach (var life in owners)
            {
                if (ReferenceEquals(life, candidate.Life)) { foundLife = true; continue; }
                var existing = life.Breathing;
                if (existing != null && (existing.StatsPointer == candidate.StatsPointer
                    || existing.VitalsPointer == candidate.VitalsPointer
                    || existing.BreathingPointer == candidate.BreathingPointer))
                { status = StrictCombatBreathingStatus.AlreadyOwned; return false; }
            }
            if (!foundLife || !ReferenceEquals(owners, Volatile.Read(ref _owners))
                || !candidate.Owner.IsCurrentBreathingAcquisition(candidate, previous,
                    in binding, in state, air, frame)) return false;
            if (_breathingCallDepth != 0)
            { status = StrictCombatBreathingStatus.Busy; return false; }
            if (!BreathingHistoryAllows(candidate))
            { status = StrictCombatBreathingStatus.NativeActionObserved; return false; }
            if (!ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
            if (!candidate.Life.TryPublishBreathing(previous, candidate))
            { status = StrictCombatBreathingStatus.AlreadyOwned; return false; }

            // One retained life entry changes owners atomically; there is no gap in
            // suppression during a correlated handoff of the same native objects.
            previous?.Retire();
            if (_breathingCallDepth != 0 || !BreathingHistoryAllows(candidate)
                || candidate.Life.IsRetired || candidate.IsRetired
                || !candidate.Owner.IsCurrentBreathingOperation(candidate, in binding, in state, air, frame))
            {
                candidate.Retire();
                status = StrictCombatBreathingStatus.UnsupportedContext;
                return false;
            }
            status = StrictCombatBreathingStatus.Success;
            return true;
        }

        private static StrictCombatBreathingBarrier FindBreathingOwner(nint stats, nint breathing)
        {
            foreach (var life in Volatile.Read(ref _owners))
            {
                var owner = life.Breathing;
                if (owner != null && ((stats != 0 && owner.StatsPointer == stats)
                    || (breathing != 0 && owner.BreathingPointer == breathing))) return owner;
            }
            return null;
        }

        private static bool HasBreathingOwner()
        {
            foreach (var life in Volatile.Read(ref _owners))
                if (life.Breathing != null) return true;
            return false;
        }

        private static bool BreathingWasObserved(StrictCombatBreathingBarrier candidate)
        {
            foreach (var item in BreathingObserved)
                if (item.Stats.Pointer == candidate.StatsPointer || item.Breathing.Pointer == candidate.BreathingPointer)
                    return true;
            return false;
        }

        private static bool BreathingHistoryAllows(StrictCombatBreathingBarrier candidate)
            => !_breathingHistoryIncomplete
                && (candidate.AllowsCompletedNativeActivity || !BreathingWasObserved(candidate));

        private static int BeginBreathingCall()
        {
            // A native call can reenter through the capacity getter, gasp setup or
            // health callbacks before its final write. Only a balanced normal exit
            // permits a later explicit takeover of completed breathing activity.
            if (!OnOwnerThread || _breathingCallDepth >= 32)
            { _breathingHistoryIncomplete = true; return 0; }
            return ++_breathingCallDepth;
        }

        private static Exception FinishBreathingCall(Exception __exception, int __state)
        {
            if (__state == 0) return __exception;
            if (!OnOwnerThread || _breathingCallDepth != __state)
            { _breathingHistoryIncomplete = true; return __exception; }
            _breathingCallDepth--;
            if (__exception != null) _breathingHistoryIncomplete = true;
            return __exception;
        }

        private static void RememberBreathing(PlayerStats stats, BreathingData breathing)
        {
            if (!_breathingPrepared) return;
            if (!OnOwnerThread || stats == null || stats.WasCollected || breathing == null || breathing.WasCollected)
            { _breathingHistoryIncomplete = true; return; }
            foreach (var item in BreathingObserved)
                if (item.Stats.Pointer == stats.Pointer && item.Breathing.Pointer == breathing.Pointer) return;
            if (BreathingObserved.Count >= MaximumOwners) { _breathingHistoryIncomplete = true; return; }
            BreathingObserved.Add((stats, breathing));
        }

        private static bool AllowUnknownBreathingOwner()
        {
            _breathingHistoryIncomplete = true;
            return !HasBreathingOwner();
        }
    }
}
