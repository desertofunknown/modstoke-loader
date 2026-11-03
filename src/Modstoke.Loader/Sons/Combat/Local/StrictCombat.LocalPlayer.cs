// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.StatSystem;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatLocalReadStatus
    {
        Success, Unavailable, WrongThread, Retired, StateUnavailable, PlayerUnavailable,
        ActorMismatch, AttachmentChanged, WorldChanged, InvalidState, Failed
    }

    /// <summary>
    /// A copied native observation paired with the latest accepted server state.
    /// The two states may disagree. Reading them does not apply health or life transitions.
    /// </summary>
    public sealed class StrictCombatLocalSnapshot
    {
        internal StrictCombatLocalSnapshot(StrictCombatClient owner, StrictCombatLocalPlayer player,
            int frame, StrictCombatBinding binding, StrictCombatSnapshot serverState,
            float health, float minimumHealth, float maximumHealth, float targetHealth, float sickness,
            int dyingState, bool hasDied, bool downed, bool respawning)
        {
            Owner = owner;
            Player = player;
            Frame = frame;
            Binding = binding;
            ServerState = serverState;
            Health = health;
            MinimumHealth = minimumHealth;
            MaximumHealth = maximumHealth;
            TargetHealth = targetHealth;
            Sickness = sickness;
            RawDyingState = dyingState;
            HasDied = hasDied;
            IsDowned = downed;
            IsRespawning = respawning;
        }

        internal StrictCombatClient Owner { get; }
        internal StrictCombatLocalPlayer Player { get; }
        public int Frame { get; }
        public StrictCombatBinding Binding { get; }
        public StrictCombatSnapshot ServerState { get; }
        public float Health { get; }
        public float MinimumHealth { get; }
        public float MaximumHealth { get; }
        public float TargetHealth { get; }
        public float Sickness { get; }
        public int RawDyingState { get; }
        public PlayerLifeState? LifeState => RawDyingState switch
        {
            0 => PlayerLifeState.Alive,
            1 => PlayerLifeState.Dying,
            2 => PlayerLifeState.Dead,
            _ => null
        };
        public bool HasDied { get; }
        public bool IsDowned { get; }
        public bool IsRespawning { get; }
    }

    // Access is confined to StrictCombatClient after its enrollment/game-thread checks.
    internal sealed partial class StrictCombatLocalPlayer
    {
        private readonly StrictCombatBinding _binding;
        private readonly int _worldEventCount;
        private readonly int _sceneHandle;
        private LocalPlayer _local;
        private BoltEntity _entity;
        private Bolt.Entity _attachment;
        private PlayerStats _stats;
        private Vitals _vitals;
        private StatsManager _statsManager;
        private HealthStat _health;
        private SicknessStat _sickness;
        private bool _retired;
        private static int _readFailureReported, _currentFailureReported;

        private StrictCombatLocalPlayer(StrictCombatBinding binding, int worldEventCount, int sceneHandle,
            LocalPlayer local, BoltEntity entity, Bolt.Entity attachment, PlayerStats stats,
            Vitals vitals, HealthStat health, SicknessStat sickness)
        {
            _binding = binding;
            _worldEventCount = worldEventCount;
            _sceneHandle = sceneHandle;
            _local = local;
            _entity = entity;
            _attachment = attachment;
            _stats = stats;
            _vitals = vitals;
            _statsManager = vitals._statsManager;
            _health = health;
            _sickness = sickness;
        }

        internal bool TryReadPresentationPosition(out Vector3 position)
        {
            position = default;
            if (!IsCurrent(out _)) return false;
            var transform = _entity.transform;
            if (!Live(transform)) return false;
            var value = transform.position;
            if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z)
                || !IsCurrent(out _) || !Same(transform, _entity.transform)) return false;
            position = value;
            return true;
        }

        internal static bool TryCapture(in StrictCombatBinding binding,
            out StrictCombatLocalPlayer player, out StrictCombatLocalReadStatus status)
        {
            player = null;
            status = StrictCombatLocalReadStatus.PlayerUnavailable;
            try
            {
                var state = GameRuntime.State;
                if (!state.IsInWorld || !LocalPlayer.IsInWorld || !BoltNetwork.isRunning || !BoltNetwork.isClient)
                    return false;
                int frame = Time.frameCount;
                var local = LocalPlayer._instance;
                var entity = LocalPlayer.Entity;
                var stats = LocalPlayer.Stats;
                var vitals = LocalPlayer.Vitals;
                if (!Live(local) || !Live(entity) || !entity.isAttached || !Live(stats) || !Live(vitals)
                    || !Same(stats._vitals, vitals)) return false;
                var attachment = entity._entity;
                var health = vitals._health;
                var sickness = vitals._sickness;
                if (!Live(attachment) || !Live(health) || !Live(sickness)) return false;
                if (entity.networkId.PackedValue != binding.ActorNetworkId)
                { status = StrictCombatLocalReadStatus.ActorMismatch; return false; }
                var scene = entity.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded) return false;
                var capture = new StrictCombatLocalPlayer(binding, state.WorldEventCount, scene.handle,
                    local, entity, attachment, stats, vitals, health, sickness);
                if (!capture.IsCurrent(out status) || frame != Time.frameCount)
                {
                    capture.Retire();
                    if (status == StrictCombatLocalReadStatus.Success) status = StrictCombatLocalReadStatus.Unavailable;
                    return false;
                }
                player = capture;
                status = StrictCombatLocalReadStatus.Success;
                return true;
            }
            catch (Exception) { status = StrictCombatLocalReadStatus.Failed; return false; }
        }

        internal bool Matches(in StrictCombatBinding binding)
            => binding.SameBinding(in _binding);

        internal bool TryGetArmour(out global::Sons.Wearable.Armour.PlayerArmourSystem armour,
            out StrictCombatLocalReadStatus status)
        {
            armour = null;
            if (!IsCurrent(out status)) return false;
            try
            {
                var current = _stats._armourSystem;
                if (!Live(current))
                { status = StrictCombatLocalReadStatus.PlayerUnavailable; return false; }
                if (!IsCurrent(out status)) return false;
                if (!Same(current, _stats._armourSystem))
                { status = StrictCombatLocalReadStatus.AttachmentChanged; return false; }
                armour = current;
                return true;
            }
            catch (Exception) { status = StrictCombatLocalReadStatus.Failed; return false; }
        }

        internal bool TryGetLifeBarrierObjects(out PlayerStats stats,
            out global::Sons.Multiplayer.MultiplayerRespawnTimer timer,
            out Vitals vitals, out HealthStat health, out SicknessStat sickness)
        {
            stats = null;
            timer = null;
            vitals = null;
            health = null;
            sickness = null;
            if (!IsCurrent(out _)) return false;
            if (!Live(_vitals._strength) || !Live(_vitals._vitality) || !Live(_vitals._stamina)
                || !Live(_vitals._hydration) || !Live(_vitals._fullness) || !Live(_vitals._rested))
                return false;
            if (_stats._healing || _stats._isHealing || _stats._doFinalDeath) return false;
            foreach (string name in StrictCombatLifePendingCalls.Names)
                if (_stats.IsInvoking(name)) return false;
            var nativeTimer = _stats._mpRespawnTimerGui;
            if (nativeTimer != null && (nativeTimer.WasCollected || !nativeTimer
                || nativeTimer.gameObject.activeInHierarchy)) return false;
            stats = _stats;
            timer = nativeTimer;
            vitals = _vitals;
            health = _health;
            sickness = _sickness;
            return IsCurrent(out _);
        }

        internal bool TryRead(StrictCombatClient owner, in StrictCombatBinding binding,
            in StrictCombatSnapshot state, out StrictCombatLocalSnapshot snapshot,
            out StrictCombatLocalReadStatus status)
        {
            snapshot = null;
            if (!Matches(in binding))
            { status = StrictCombatLocalReadStatus.AttachmentChanged; Retire(); return false; }
            if (!IsCurrent(out status)) return false;
            try
            {
                int frame = Time.frameCount;
                float health = _health._currentValue, minimum = _health._min, maximum = _health._max;
                float target = _vitals._targetHealth, sickness = _sickness._currentValue;
                int dying = (int)_stats._dyingState;
                bool hasDied = _vitals._hasDied, downed = _stats._playerIsDowned, respawning = _stats._respawning;
                if (!float.IsFinite(health) || !float.IsFinite(minimum) || !float.IsFinite(maximum)
                    || !float.IsFinite(target) || !float.IsFinite(sickness) || maximum <= 0
                    || minimum > maximum || health < minimum || health > maximum || sickness < 0)
                { status = StrictCombatLocalReadStatus.InvalidState; return false; }
                if (!IsCurrent(out status)) return false;
                if (frame != Time.frameCount)
                { status = StrictCombatLocalReadStatus.Unavailable; return false; }
                snapshot = new StrictCombatLocalSnapshot(owner, this, frame, binding, state,
                    health, minimum, maximum, target, sickness, dying, hasDied, downed, respawning);
                status = StrictCombatLocalReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _readFailureReported, "local player read", error);
                Retire();
                status = StrictCombatLocalReadStatus.Failed;
                return false;
            }
        }

        internal bool IsCurrent(out StrictCombatLocalReadStatus status)
        {
            status = StrictCombatLocalReadStatus.AttachmentChanged;
            if (_retired) return false;
            try
            {
                var world = GameRuntime.State;
                if (!world.IsInWorld || world.WorldEventCount != _worldEventCount || !LocalPlayer.IsInWorld
                    || !BoltNetwork.isRunning || !BoltNetwork.isClient)
                { Retire(); status = StrictCombatLocalReadStatus.WorldChanged; return false; }
                if (!Same(_local, LocalPlayer._instance) || !Same(_entity, LocalPlayer.Entity)
                    || !_entity.isAttached || !Same(_attachment, _entity._entity)
                    || _entity.networkId.PackedValue != _binding.ActorNetworkId
                    || !Same(_stats, LocalPlayer.Stats) || !Same(_vitals, LocalPlayer.Vitals)
                    || !Same(_stats._vitals, _vitals) || !Same(_health, _vitals._health)
                    || !Same(_sickness, _vitals._sickness)
                    || !Same(_statsManager, _vitals._statsManager) || !HasRegisteredHealth())
                { Retire(); return false; }
                var scene = _entity.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded || scene.handle != _sceneHandle)
                { Retire(); status = StrictCombatLocalReadStatus.WorldChanged; return false; }
                status = StrictCombatLocalReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _currentFailureReported, "local player check", error);
                Retire();
                status = StrictCombatLocalReadStatus.Failed;
                return false;
            }
        }

        internal void Retire()
        {
            _retired = true;
            _local = null;
            _entity = null;
            _attachment = null;
            _stats = null;
            _vitals = null;
            _statsManager = null;
            _health = null;
            _sickness = null;
        }

        private bool HasRegisteredHealth()
        {
            // ValidateStats can rebuild this list without clearing Vitals' lazy caches.
            // A detached cache or second health stat cannot share this ownership.
            var stats = _statsManager._stats;
            if (!Live(stats)) return false;
            int count = stats.Count;
            bool foundHealth = false, foundSickness = false;
            for (int i = 0; i < count; i++)
            {
                var stat = stats[i];
                if (stat == null) continue;
                if (!Live(stat)) return false;
                if (stat.TryCast<HealthStat>() != null)
                {
                    if (foundHealth || !Same(stat, _health)) return false;
                    foundHealth = true;
                }
                else if (stat.TryCast<SicknessStat>() != null)
                {
                    if (foundSickness || !Same(stat, _sickness)) return false;
                    foundSickness = true;
                }
            }
            return foundHealth && foundSickness && count == stats.Count
                && Same(stats, _statsManager._stats);
        }

        private static bool Live(Il2CppObjectBase value) => value != null && !value.WasCollected;
        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
    }

    internal static class StrictCombatLifePendingCalls
    {
        internal static readonly string[] Names =
        {
            "CheckFirstDeathRoutine", "CheckFirstDeathRoutineNoPrompt", "KillPlayer",
            "EndDeathRoutine", "EndSwimmingDeathRoutine", "resetInjuredBool",
            "disablePlayerControl", "ResetHit"
        };
    }
}
