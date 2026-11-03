// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using global::Sons.StatSystem;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatLifeStatus
    {
        Success, Unavailable, WrongThread, AlreadyInWorld, StaleObservation,
        NativeTransitionObserved, NativeStateUnavailable, AlreadyOwned, CapacityReached, Failed,
        NativeInitializationPending, Busy
    }

    // A retired owner remains a barrier for its native Stats object. Falling back to native
    // death after losing the server binding would let old invokes and coroutines run again.
    internal sealed class StrictCombatLifeBarrier
    {
        internal StrictCombatLifeBarrier(StrictCombatClient owner, StrictCombatLocalPlayer player,
            PlayerStats stats, MultiplayerRespawnTimer timer, Vitals vitals, HealthStat health,
            SicknessStat sickness, ulong lifeGeneration)
        {
            Owner = owner;
            Player = player;
            Stats = stats;
            Timer = timer;
            Vitals = vitals;
            Health = health;
            HealthPointer = health.Pointer;
            Sickness = sickness;
            LifeGeneration = lifeGeneration;
        }

        internal StrictCombatClient Owner { get; }
        internal StrictCombatLocalPlayer Player { get; }
        internal PlayerStats Stats { get; }
        internal MultiplayerRespawnTimer Timer { get; }
        internal Vitals Vitals { get; }
        internal HealthStat Health { get; }
        internal nint HealthPointer { get; }
        internal SicknessStat Sickness { get; }
        internal ulong LifeGeneration { get; private set; }
        private StrictCombatBreathingBarrier _breathing;
        internal StrictCombatBreathingBarrier Breathing => Volatile.Read(ref _breathing);
        internal bool TryPublishBreathing(StrictCombatBreathingBarrier previous,
            StrictCombatBreathingBarrier next)
            => ReferenceEquals(Interlocked.CompareExchange(ref _breathing, next, previous), previous);
        private int _retired;
        internal bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal bool UpdatingVitals { get; set; }
        internal bool InitializingVitals { get; set; }
        internal bool LoadingVitals { get; set; }
        internal void Retire()
        {
            Interlocked.Exchange(ref _retired, 1);
            Breathing?.Retire();
        }

        // Only the correlated client handoff calls this on the game thread. Retain
        // this registry entry so callbacks from the old life stay suppressed.
        internal bool TryAdvanceLife(ulong previousLife, ulong nextLife)
        {
            if (IsRetired || previousLife == 0 || previousLife == ulong.MaxValue
                || LifeGeneration != previousLife || nextLife != previousLife + 1)
                return false;
            LifeGeneration = nextLife;
            return !IsRetired;
        }
    }

    internal static partial class StrictCombatLifeHooks
    {
        private const int MaximumOwners = 32;
        private static HarmonySupportSnapshot _support;
        private static Action<string> _warning;
        private static int _thread, _acquireFailureReported;
        private static bool _patchAttempted, _hooksInstalled, _prepared;
        private static volatile bool _historyIncomplete, _cutsceneObserved;
        private static readonly List<PlayerStats> Observed = new();
        private static StrictCombatLifeBarrier[] _owners = Array.Empty<StrictCombatLifeBarrier>();

        internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _thread, Environment.CurrentManagedThreadId, 0) != 0) return;
            _support = support;
            _warning = warning;
        }

        internal static StrictCombatLifeStatus PrepareForNextStart()
        {
            if (_thread == 0) return StrictCombatLifeStatus.Unavailable;
            if (!OnOwnerThread) return StrictCombatLifeStatus.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (GameRuntime.State.IsInWorld || LocalPlayer.IsInWorld || BoltNetwork.isRunning
                    || (socket != null && !socket.WasCollected && socket.State != UdpKit.UdpSocketState.Shutdown))
                    return StrictCombatLifeStatus.AlreadyInWorld;
                if (_historyIncomplete || _cutsceneObserved)
                    return StrictCombatLifeStatus.NativeTransitionObserved;
                if (!InstallHooks()) return StrictCombatLifeStatus.Unavailable;
                _prepared = true;
                return StrictCombatLifeStatus.Success;
            }
            catch (Exception) { return StrictCombatLifeStatus.Failed; }
        }

        internal static bool TryAcquire(StrictCombatClient owner, StrictCombatLocalSnapshot observation,
            bool requireCompletedInitialization, out StrictCombatLifeBarrier barrier,
            out StrictCombatLifeStatus status)
        {
            barrier = null;
            status = StrictCombatLifeStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatLifeStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatLifeStatus.StaleObservation;
            if (!owner.IsCurrentLocalPlayerSnapshot(observation)) return false;
            try
            {
                // Re-read native values: a same-frame observation is not a frozen health record.
                if (!owner.TryReadLocalPlayer(out var current, out _)) return false;
                if (current.ServerState.LifeStage != StrictCombatLifeStage.Alive
                    || current.RawDyingState != 0 || current.HasDied || current.IsDowned
                    || current.IsRespawning || current.Health <= 0
                    || !current.Player.TryGetLifeBarrierObjects(out var stats, out var timer,
                        out var vitals, out var health, out var sickness))
                { status = StrictCombatLifeStatus.NativeStateUnavailable; return false; }

                if (requireCompletedInitialization && !VitalsStartupReady(vitals, health, out status)) return false;

                var owners = Volatile.Read(ref _owners);
                foreach (var existing in owners)
                {
                    if (!Same(existing.Stats, stats)) continue;
                    if (!existing.IsRetired && ReferenceEquals(existing.Owner, owner)
                        && ReferenceEquals(existing.Player, current.Player)
                        && existing.LifeGeneration == current.ServerState.LifeGeneration)
                    {
                        barrier = existing;
                        status = StrictCombatLifeStatus.Success;
                        return true;
                    }
                    status = StrictCombatLifeStatus.AlreadyOwned;
                    return false;
                }
                if (_historyIncomplete || _cutsceneObserved || WasObserved(stats))
                { status = StrictCombatLifeStatus.NativeTransitionObserved; return false; }
                if (owners.Length == MaximumOwners)
                { status = StrictCombatLifeStatus.CapacityReached; return false; }
                status = StrictCombatLifeStatus.StaleObservation;
                if (!owner.IsCurrentLocalPlayerSnapshot(current)) return false;
                if (requireCompletedInitialization && !VitalsStartupReady(vitals, health, out status)) return false;
                if (requireCompletedInitialization && !ReferenceEquals(owners, Volatile.Read(ref _owners)))
                { status = StrictCombatLifeStatus.NativeStateUnavailable; return false; }

                barrier = new StrictCombatLifeBarrier(owner, current.Player, stats, timer, vitals, health, sickness,
                    current.ServerState.LifeGeneration);
                var next = new StrictCombatLifeBarrier[owners.Length + 1];
                Array.Copy(owners, next, owners.Length);
                next[^1] = barrier;
                // Publish only after every precondition. Partial hook installation never owns a player.
                Volatile.Write(ref _owners, next);
                if (requireCompletedInitialization && (!VitalsStartupReady(vitals, health, out status)
                    || _historyIncomplete || _cutsceneObserved || WasObserved(stats)
                    || !owner.IsCurrentLocalPlayerSnapshot(current)))
                {
                    barrier.Retire();
                    barrier = null;
                    status = StrictCombatLifeStatus.NativeStateUnavailable;
                    return false;
                }
                status = StrictCombatLifeStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _acquireFailureReported, "life barrier acquisition", error);
                if (requireCompletedInitialization)
                {
                    barrier?.Retire();
                    barrier = null;
                }
                status = StrictCombatLifeStatus.Failed;
                return false;
            }
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _thread;
        private static bool IsObserving => _prepared || Volatile.Read(ref _owners).Length != 0;

        private static bool Same(PlayerStats first, PlayerStats second)
            => first is not null && second is not null && first.Pointer == second.Pointer;

        private static bool Owns(PlayerStats stats)
        {
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Stats, stats)) return true;
            return false;
        }

        private static bool BlocksLocalLife(PlayerStats stats)
        {
            if (Owns(stats)) return true;
            if (Volatile.Read(ref _owners).Length == 0) return false;
            if (!OnOwnerThread) { _historyIncomplete = true; return true; }
            try
            {
                // These native routines also use the current global LocalPlayer. An old
                // Stats receiver must not use that route to mutate a newly owned player.
                return Owns(LocalPlayer.Stats);
            }
            catch (Exception) { _historyIncomplete = true; return true; }
        }

        private static bool WasObserved(PlayerStats stats)
        {
            foreach (var previous in Observed) if (Same(previous, stats)) return true;
            return false;
        }

        private static void Remember(PlayerStats stats)
        {
            if (!_prepared) return;
            if (!OnOwnerThread || stats == null || stats.WasCollected)
            { _historyIncomplete = true; return; }
            if (WasObserved(stats)) return;
            if (Observed.Count == MaximumOwners) { _historyIncomplete = true; return; }
            Observed.Add(stats);
        }

        private static bool AllowStats(PlayerStats stats, bool runOriginal)
        {
            if (!runOriginal) return true;
            if (BlocksLocalLife(stats)) return false;
            Remember(stats);
            if (_prepared && OnOwnerThread)
            {
                try
                {
                    var local = LocalPlayer.Stats;
                    if (local is not null) Remember(local);
                }
                catch (Exception) { _historyIncomplete = true; }
            }
            return true;
        }

        private static bool AllowCheckDeath(PlayerStats stats, bool runOriginal)
        {
            // Healthy checks run during normal gameplay. StartDeathRoutine and the native
            // hasDied/downed flags cover an actual transition before ownership can be acquired.
            return !runOriginal || !BlocksLocalLife(stats);
        }

        private static bool AllowDyingState(PlayerStats stats, PlayerStats.DyingState state, bool runOriginal)
        {
            if (!runOriginal) return true;
            if ((int)state != 0) return AllowStats(stats, true);
            return !BlocksLocalLife(stats);
        }

        private static bool AllowUnreadableOwner()
        {
            if (_prepared) _historyIncomplete = true;
            return Volatile.Read(ref _owners).Length == 0;
        }

        private static bool AllowTimer(MultiplayerRespawnTimer timer, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                var owners = Volatile.Read(ref _owners);
                foreach (var owner in owners)
                    if (timer is not null && owner.Timer is not null && timer.Pointer == owner.Timer.Pointer) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                foreach (var owner in owners)
                {
                    var stats = owner.Stats;
                    if (stats == null || stats.WasCollected || !stats) continue;
                    var current = stats._mpRespawnTimerGui;
                    if (timer is not null && current is not null && timer.Pointer == current.Pointer) return false;
                }
                Remember(LocalPlayer.Stats);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowCutscene(bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            if (!OnOwnerThread) return AllowUnreadableOwner();
            try
            {
                if (Owns(LocalPlayer.Stats)) return false;
                // Native replies contain no request/life nonce. A later healthy-looking player
                // cannot establish that an earlier cutscene and all its callbacks have ended.
                if (_prepared) _cutsceneObserved = true;
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }
    }
}
