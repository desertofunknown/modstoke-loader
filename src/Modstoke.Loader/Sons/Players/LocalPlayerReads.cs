// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Areas;
using global::Sons.StatSystem;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>On-demand, copied observations of the local player's location and cached survival state.</summary>
    public static partial class LocalPlayerReads
    {
        /// <summary>Largest stat registry inspected by a capture. Larger registries fail without partial results.</summary>
        public const int MaximumRegisteredStats = 64;

        /// <summary>
        /// Copies world-space position, forward, cave state and area mask on the initialized Unity thread.
        /// Requires the SDK's current-world manager root and a live in-world local player, but no
        /// vital-stat caches or server authority. Values are sequential observations, not an atomic
        /// state, a life-state assessment or permission to move. False returns a null snapshot;
        /// successful snapshots retain no native objects.
        /// </summary>
        public static bool TryReadLocation(out LocalPlayerLocationSnapshot snapshot,
            out LocalPlayerReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckReadiness();
                if (status != LocalPlayerReadStatus.Success)
                    return false;

                var world = GameRuntime.State;
                if (!world.IsInWorld || !LocalPlayer.IsInWorld)
                {
                    status = LocalPlayerReadStatus.WorldUnavailable;
                    return false;
                }

                var local = LocalPlayer._instance;
                var transform = LocalPlayer._Transform_k__BackingField;
                if (!Live(local) || !Live(transform) || !Same(local._transform, transform))
                {
                    status = LocalPlayerReadStatus.PlayerUnavailable;
                    return false;
                }

                var scene = local.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded || transform.gameObject.scene.handle != scene.handle)
                {
                    status = LocalPlayerReadStatus.WorldUnavailable;
                    return false;
                }

                int frame = Time.frameCount;
                Vector3 position = transform.position, forward = transform.forward;
                bool inCaves = LocalPlayer.IsInCaves;
                int areaMask = (int)CaveEntranceManager.CurrentAreaMask;
                if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)
                    || !float.IsFinite(forward.x) || !float.IsFinite(forward.y) || !float.IsFinite(forward.z))
                {
                    status = LocalPlayerReadStatus.InvalidState;
                    return false;
                }

                var currentWorld = GameRuntime.State;
                if (!currentWorld.IsInWorld || currentWorld.WorldEventCount != world.WorldEventCount
                    || !LocalPlayer.IsInWorld || frame != Time.frameCount
                    || !Same(local, LocalPlayer._instance)
                    || !Same(transform, LocalPlayer._Transform_k__BackingField)
                    || !Same(local._transform, transform))
                {
                    status = LocalPlayerReadStatus.ContextChanged;
                    return false;
                }
                var currentScene = local.gameObject.scene;
                if (!currentScene.IsValid() || !currentScene.isLoaded || currentScene.handle != scene.handle
                    || transform.gameObject.scene.handle != scene.handle)
                {
                    status = LocalPlayerReadStatus.ContextChanged;
                    return false;
                }
                status = CheckReadiness();
                if (status != LocalPlayerReadStatus.Success)
                    return false;

                snapshot = new LocalPlayerLocationSnapshot(frame, position, forward, inCaves, areaMask);
                return true;
            }
            catch (Exception)
            {
                status = LocalPlayerReadStatus.Failed;
                return false;
            }
        }

        /// <summary>
        /// Copies six cached vital stats and local life/activity flags on the initialized Unity thread.
        /// Requires the SDK's current-world manager root and a live in-world local player; works without
        /// server authority or strict-combat enrollment. Missing caches are unavailable, never initialized
        /// by this read. Values are sequential field observations, not an atomic or authoritative state.
        /// False always returns a null snapshot; no native objects are retained by a successful snapshot.
        /// </summary>
        public static bool TryReadVitals(out LocalPlayerVitalsSnapshot snapshot,
            out LocalPlayerReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckReadiness();
                if (status != LocalPlayerReadStatus.Success)
                    return false;

                var world = GameRuntime.State;
                if (!world.IsInWorld || !LocalPlayer.IsInWorld)
                {
                    status = LocalPlayerReadStatus.WorldUnavailable;
                    return false;
                }

                var local = LocalPlayer._instance;
                var playerStats = LocalPlayer._Stats_k__BackingField;
                var vitals = LocalPlayer._Vitals_k__BackingField;
                if (!Live(local) || !Live(playerStats) || !Live(vitals)
                    || !Same(local._stats, playerStats) || !Same(local._vitals, vitals)
                    || !Same(playerStats._vitals, vitals))
                {
                    status = LocalPlayerReadStatus.PlayerUnavailable;
                    return false;
                }

                var scene = local.gameObject.scene;
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    status = LocalPlayerReadStatus.WorldUnavailable;
                    return false;
                }

                int frame = Time.frameCount;
                var manager = vitals._statsManager;
                if (!Live(manager))
                {
                    status = LocalPlayerReadStatus.StatsUnavailable;
                    return false;
                }

                // Copy stored stat fields without invoking the game's stat accessors.
                Stat[] stats = { vitals._health, vitals._vitality, vitals._stamina,
                    vitals._hydration, vitals._fullness, vitals._rested };
                var copied = new LocalVitalSnapshot[stats.Length];
                for (int index = 0; index < stats.Length; index++)
                {
                    var stat = stats[index];
                    if (!Live(stat))
                    {
                        status = LocalPlayerReadStatus.StatsUnavailable;
                        return false;
                    }

                    float current = stat._currentValue, minimum = stat._min, maximum = stat._max;
                    if (!float.IsFinite(current) || !float.IsFinite(minimum) || !float.IsFinite(maximum)
                        || minimum > maximum)
                    {
                        status = LocalPlayerReadStatus.InvalidState;
                        return false;
                    }
                    copied[index] = new LocalVitalSnapshot(current, minimum, maximum);
                }

                int dyingState = (int)playerStats._dyingState;
                bool hasDied = vitals._hasDied, downed = playerStats._playerIsDowned,
                    respawning = playerStats._respawning, seated = playerStats._isSeated,
                    onFire = playerStats._isOnFire, cold = vitals._isCold,
                    freezing = playerStats._IsFreezing_k__BackingField;

                // Require each cached stat to still belong to the manager's current registry.
                if (!TryValidateRegisteredStats(manager, stats, out status))
                    return false;

                var currentWorld = GameRuntime.State;
                if (!currentWorld.IsInWorld || currentWorld.WorldEventCount != world.WorldEventCount
                    || !LocalPlayer.IsInWorld || frame != Time.frameCount)
                {
                    status = LocalPlayerReadStatus.ContextChanged;
                    return false;
                }
                if (!Same(local, LocalPlayer._instance)
                    || !Same(playerStats, LocalPlayer._Stats_k__BackingField)
                    || !Same(vitals, LocalPlayer._Vitals_k__BackingField)
                    || !Same(local._stats, playerStats) || !Same(local._vitals, vitals)
                    || !Same(playerStats._vitals, vitals) || !Same(manager, vitals._statsManager)
                    || !Same(stats[0], vitals._health) || !Same(stats[1], vitals._vitality)
                    || !Same(stats[2], vitals._stamina) || !Same(stats[3], vitals._hydration)
                    || !Same(stats[4], vitals._fullness) || !Same(stats[5], vitals._rested))
                {
                    status = LocalPlayerReadStatus.ContextChanged;
                    return false;
                }
                var currentScene = local.gameObject.scene;
                if (!currentScene.IsValid() || !currentScene.isLoaded || currentScene.handle != scene.handle)
                {
                    status = LocalPlayerReadStatus.ContextChanged;
                    return false;
                }
                status = CheckReadiness();
                if (status != LocalPlayerReadStatus.Success)
                    return false;

                snapshot = new LocalPlayerVitalsSnapshot(frame, copied[0], copied[1], copied[2],
                    copied[3], copied[4], copied[5], dyingState, hasDied, downed, respawning,
                    seated, onFire, cold, freezing);
                return true;
            }
            catch (Exception)
            {
                status = LocalPlayerReadStatus.Failed;
                return false;
            }
        }

        private static LocalPlayerReadStatus CheckReadiness()
            => GameManagers.Instance.GetReadiness() switch
            {
                ManagerAccessStatus.Ready => LocalPlayerReadStatus.Success,
                ManagerAccessStatus.Unavailable => LocalPlayerReadStatus.Unavailable,
                ManagerAccessStatus.WrongThread => LocalPlayerReadStatus.WrongThread,
                ManagerAccessStatus.WorldUnavailable => LocalPlayerReadStatus.WorldUnavailable,
                ManagerAccessStatus.RootUnavailable => LocalPlayerReadStatus.ManagerRootUnavailable,
                _ => LocalPlayerReadStatus.Failed
            };

        private static bool TryValidateRegisteredStats(StatsManager manager, Stat[] expected,
            out LocalPlayerReadStatus status)
        {
            status = LocalPlayerReadStatus.StatsUnavailable;
            if (!Live(manager))
                return false;
            var registered = manager._stats;
            if (!Live(registered))
                return false;
            int count = registered.Count;
            if (count > MaximumRegisteredStats)
            {
                status = LocalPlayerReadStatus.StatLimitExceeded;
                return false;
            }

            int found = 0;
            for (int index = 0; index < count; index++)
            {
                var stat = registered[index];
                if (stat is null)
                    continue;
                if (!Live(stat))
                    return false;
                for (int expectedIndex = 0; expectedIndex < expected.Length; expectedIndex++)
                    if (Same(stat, expected[expectedIndex]))
                        found |= 1 << expectedIndex;
            }

            if (found != (1 << expected.Length) - 1)
                return false;
            if (!Same(registered, manager._stats) || registered.Count != count)
            {
                status = LocalPlayerReadStatus.ContextChanged;
                return false;
            }
            status = LocalPlayerReadStatus.Success;
            return true;
        }

        private static bool Live(Il2CppObjectBase value) => value is not null && !value.WasCollected;
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => Live(first) && Live(second) && first.Pointer == second.Pointer;
    }

    public enum LocalPlayerReadStatus
    {
        Success,
        /// <summary>The existing SDK manager service has not initialized.</summary>
        Unavailable,
        /// <summary>No native objects were inspected because the caller is not on the initialized Unity thread.</summary>
        WrongThread,
        WorldUnavailable,
        ManagerRootUnavailable,
        PlayerUnavailable,
        /// <summary>A cached stat, its manager, or its exact registry membership is unavailable.</summary>
        StatsUnavailable,
        /// <summary>The registry exceeds MaximumRegisteredStats. No partial snapshot is returned.</summary>
        StatLimitExceeded,
        ContextChanged,
        /// <summary>An observed value is non-finite or a stat's stored range is inverted.</summary>
        InvalidState,
        Failed
    }

    /// <summary>Copied local location observations without native references or movement authority.</summary>
    public sealed class LocalPlayerLocationSnapshot
    {
        internal LocalPlayerLocationSnapshot(int frame, Vector3 position, Vector3 forward, bool inCaves, int areaMask)
        {
            Frame = frame;
            Position = position;
            Forward = forward;
            IsInCaves = inCaves;
            AreaMask = areaMask;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public int Frame { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
        public Vector3 Position { get; }
        /// <summary>World-space transform forward, preserved without horizontal projection or normalization.</summary>
        public Vector3 Forward { get; }
        public bool IsInCaves { get; }
        /// <summary>
        /// The local player's raw Sons.Areas.AreaMask bits; the game's GPS counts the player as underground wherever
        /// they are not zero. Read apart from <see cref="IsInCaves"/>, which may disagree at the edges.
        /// </summary>
        public int AreaMask { get; }
    }

    /// <summary>Raw cached stat fields, not resolved limits, normalized UI values or an authority decision.</summary>
    public sealed class LocalVitalSnapshot
    {
        internal LocalVitalSnapshot(float current, float minimum, float maximum)
        {
            Current = current;
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>Stored _currentValue, left unchanged even when outside the stored range.</summary>
        public float Current { get; }
        /// <summary>Stored _min, without applying modifiers or clamping.</summary>
        public float Minimum { get; }
        /// <summary>Stored _max, not a computed or resolved capacity.</summary>
        public float Maximum { get; }
    }

    /// <summary>
    /// Managed-only local observations. Life/activity flags are copied independently and can disagree;
    /// they do not infer permission to act, remote health, synchronized state or a strict-combat outcome.
    /// </summary>
    public sealed class LocalPlayerVitalsSnapshot
    {
        internal LocalPlayerVitalsSnapshot(int frame, LocalVitalSnapshot health,
            LocalVitalSnapshot vitality, LocalVitalSnapshot stamina,
            LocalVitalSnapshot hydration, LocalVitalSnapshot fullness,
            LocalVitalSnapshot rested, int dyingState, bool hasDied, bool downed,
            bool respawning, bool seated, bool onFire, bool cold, bool freezing)
        {
            Frame = frame;
            Health = health;
            Vitality = vitality;
            Stamina = stamina;
            Hydration = hydration;
            Fullness = fullness;
            Rested = rested;
            RawDyingState = dyingState;
            HasDied = hasDied;
            IsDowned = downed;
            IsRespawning = respawning;
            IsSeated = seated;
            IsOnFire = onFire;
            IsCold = cold;
            IsFreezing = freezing;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        public int Frame { get; }
        /// <summary>Read completion time, not a simulation or replication timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
        public LocalVitalSnapshot Health { get; }
        public LocalVitalSnapshot Vitality { get; }
        public LocalVitalSnapshot Stamina { get; }
        public LocalVitalSnapshot Hydration { get; }
        public LocalVitalSnapshot Fullness { get; }
        public LocalVitalSnapshot Rested { get; }
        /// <summary>Raw PlayerStats._dyingState; unknown native values are preserved.</summary>
        public int RawDyingState { get; }
        public PlayerLifeState? LifeState => RawDyingState switch
        {
            (int)PlayerStats.DyingState.Alive => PlayerLifeState.Alive,
            (int)PlayerStats.DyingState.Dying => PlayerLifeState.Dying,
            (int)PlayerStats.DyingState.Dead => PlayerLifeState.Dead,
            _ => null
        };
        /// <summary>Vitals._hasDied, independent of the current raw dying state.</summary>
        public bool HasDied { get; }
        public bool IsDowned { get; }
        public bool IsRespawning { get; }
        public bool IsSeated { get; }
        public bool IsOnFire { get; }
        /// <summary>Vitals._isCold.</summary>
        public bool IsCold { get; }
        /// <summary>PlayerStats' stored IsFreezing flag, not inferred from IsCold.</summary>
        public bool IsFreezing { get; }
    }
}
