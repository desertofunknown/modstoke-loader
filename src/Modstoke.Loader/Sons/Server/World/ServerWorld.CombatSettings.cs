// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

extern alias SonsGame;

using System;
using global::Sons.Gameplay.GameSetup;
using NativeServerInfo = SonsGame::CoopServerInfo;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the host's combat game settings and, when a server-owned server-info entity is readable,
        /// the replicated copies clients receive. Each read is a snapshot only: a listen host can change
        /// these settings mid-session. Which copy the native player-hit path applies is not established,
        /// and none of these values proves how much damage any hit dealt or that a player was protected.
        /// </summary>
        public static bool TryReadCombatSettings(out CombatSettingsSnapshot snapshot, out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                var pvp = GameSetupManager.GetPvpDamageSetting();
                if (pvp is null || pvp.WasCollected)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }
                var hostPvp = pvp._intValue;
                var hostGodMode = GameSetupManager.GetPlayersGodModeSetting();
                var enemyDamage = GameSetupManager.GetVailEnemyDamageSetting();
                var enemyHealth = GameSetupManager.GetVailEnemyHealthSetting();
                int? hostEnemyDamage = enemyDamage is null || enemyDamage.WasCollected ? null : enemyDamage._intValue;
                int? hostEnemyHealth = enemyHealth is null || enemyHealth.WasCollected ? null : enemyHealth._intValue;
                var hostTraps = GameSetupManager.GetPlayersTriggerTrapsSetting();

                ulong networkId = 0;
                int? replicatedPvp = null;
                bool? replicatedImmortal = null;
                bool? replicatedRealistic = null;
                var info = NativeServerInfo._instance;
                var entity = info is null || info.WasCollected || !info ? null : info.entity;
                if (entity is not null && !entity.WasCollected && entity && entity.isAttached && entity.isOwner)
                {
                    var state = info.state;
                    if (state is not null && !state.WasCollected)
                    {
                        networkId = entity.networkId.PackedValue;
                        replicatedPvp = state.PvpDamageSetting;
                        replicatedImmortal = state.PlayersImmortalSetting;
                        replicatedRealistic = state.RealisticPlayerDamage;

                        var currentInfo = NativeServerInfo._instance;
                        var currentEntity = currentInfo is null || currentInfo.WasCollected || !currentInfo
                            || currentInfo.Pointer != info.Pointer ? null : currentInfo.entity;
                        var currentState = currentEntity is null ? null : currentInfo.state;
                        if (currentEntity is null || currentEntity.WasCollected
                            || !currentEntity || !currentEntity.isAttached || !currentEntity.isOwner
                            || currentEntity.Pointer != entity.Pointer || currentEntity.networkId.PackedValue != networkId
                            || currentState is null || currentState.WasCollected || currentState.Pointer != state.Pointer
                            || currentState.PvpDamageSetting != replicatedPvp.Value
                            || currentState.PlayersImmortalSetting != replicatedImmortal.Value
                            || currentState.RealisticPlayerDamage != replicatedRealistic.Value)
                        {
                            status = WorldReadStatus.BindingChanged;
                            return false;
                        }
                    }
                }

                var currentPvp = GameSetupManager.GetPvpDamageSetting();
                if (currentPvp is null || currentPvp.WasCollected || currentPvp._intValue != hostPvp
                    || GameSetupManager.GetPlayersGodModeSetting() != hostGodMode
                    || GameSetupManager.GetPlayersTriggerTrapsSetting() != hostTraps)
                {
                    status = WorldReadStatus.BindingChanged;
                    return false;
                }
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;

                snapshot = new CombatSettingsSnapshot(hostPvp, hostGodMode, hostEnemyDamage, hostEnemyHealth,
                    hostTraps, replicatedPvp, replicatedImmortal, replicatedRealistic, networkId);
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }
    }

    /// <summary>Known raw values of the game's PvpDamageSetting.</summary>
    public enum PvpDamageMode
    {
        Off = 0,
        Reduced = 1,
        Normal = 2
    }

    /// <summary>
    /// Immutable copies of combat setting values at read time, with no native references. These are
    /// configuration observations, not evidence of the damage a hit dealt or of what a client applied locally.
    /// </summary>
    public sealed class CombatSettingsSnapshot
    {
        internal CombatSettingsSnapshot(int hostPvpDamageRaw, bool hostPlayersGodMode, int? hostEnemyDamageRaw,
            int? hostEnemyHealthRaw, bool hostPlayersTriggerTraps, int? replicatedPvpDamageRaw,
            bool? replicatedPlayersImmortal, bool? replicatedRealisticPlayerDamage, ulong serverInfoNetworkId)
        {
            HostPvpDamageRaw = hostPvpDamageRaw;
            HostPlayersGodMode = hostPlayersGodMode;
            HostEnemyDamageRaw = hostEnemyDamageRaw;
            HostEnemyHealthRaw = hostEnemyHealthRaw;
            HostPlayersTriggerTraps = hostPlayersTriggerTraps;
            ReplicatedPvpDamageRaw = replicatedPvpDamageRaw;
            ReplicatedPlayersImmortal = replicatedPlayersImmortal;
            ReplicatedRealisticPlayerDamage = replicatedRealisticPlayerDamage;
            ReplicatedMatchesHost = replicatedPvpDamageRaw == hostPvpDamageRaw
                && replicatedPlayersImmortal == hostPlayersGodMode;
            ServerInfoNetworkId = serverInfoNetworkId;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Raw GameSetupManager PvP damage setting value on this host.</summary>
        public int HostPvpDamageRaw { get; }
        /// <summary>The known mode for HostPvpDamageRaw, or null for an unknown raw value.</summary>
        public PvpDamageMode? HostPvpDamage => HostPvpDamageRaw is >= (int)PvpDamageMode.Off and <= (int)PvpDamageMode.Normal
            ? (PvpDamageMode)HostPvpDamageRaw : null;
        /// <summary>
        /// The host's players-god-mode game setting. It does not block all damage, and where native
        /// player-hit handling consults it is not established.
        /// </summary>
        public bool HostPlayersGodMode { get; }
        /// <summary>Raw enemy damage setting value, or null when the setting was unreadable. Not a multiplier.</summary>
        public int? HostEnemyDamageRaw { get; }
        /// <summary>Raw enemy health setting value, or null when the setting was unreadable. Not a multiplier.</summary>
        public int? HostEnemyHealthRaw { get; }
        /// <summary>The host's setting, not proof that any trap fired or skipped a player.</summary>
        public bool HostPlayersTriggerTraps { get; }
        /// <summary>
        /// The server-info state's replicated PvP value, or null without a readable server-owned entity.
        /// Clients receive this copy; whether a client applies it locally is not established.
        /// </summary>
        public int? ReplicatedPvpDamageRaw { get; }
        /// <summary>The replicated PlayersImmortalSetting, or null without a readable server-owned entity.</summary>
        public bool? ReplicatedPlayersImmortal { get; }
        /// <summary>The replicated RealisticPlayerDamage flag, or null without a readable server-owned entity.</summary>
        public bool? ReplicatedRealisticPlayerDamage { get; }
        /// <summary>
        /// True when the replicated PvP and immortal values equal the host's. False also when the replicated
        /// copy is unavailable. A short mismatch can be an in-flight setting change, not tampering.
        /// </summary>
        public bool ReplicatedMatchesHost { get; }
        /// <summary>Current-world server-info entity ID, or zero when no server-owned entity was readable.</summary>
        public ulong ServerInfoNetworkId { get; }
        /// <summary>Read completion time, not the time a setting last changed.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
