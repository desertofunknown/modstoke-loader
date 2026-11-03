// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using Modstoke.Loader;
using global::Sons.Items.Core;
using global::Sons.StatSystem;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Canonical native numerical storage for one held source. This is not server approval
    /// or an implementation of native coroutines, stat delegates, environment or life effects.
    /// Restore on the prepared actor only, before any progression or consumption owner starts.
    /// </summary>
    public sealed partial class PlayerSurvivalSnapshot
    {
        private readonly float[] _scalars;
        private readonly uint _flags;
        private readonly uint _actionFlags;
        public ulong SourceNetworkId { get; }
        public int PrefabId { get; }
        public int StateTypeId { get; }
        public PlayerAuthorityRuntimeState Runtime { get; }
        public PlayerWaterConfiguration Water { get; }
        public PlayerSurvivalSettings Settings { get; }
        public int StrengthLevel { get; }
        public int MaximumStrengthLevel { get; }
        public int LowHealthThreshold { get; }
        public int DeathCount { get; }
        public int MaximumDeathCount { get; }
        public int CookingRecipeId { get; }
        public ulong CookingRecipeFingerprint { get; }
        public bool WaterJumpTimerRunning => (_flags & (1u << 8)) != 0;
        public bool WaterJumpBlocked => (_flags & (1u << 9)) != 0;
        public PlayerAuthorityActionState CapturedActions => new(
            (_actionFlags & 1) != 0, (_actionFlags & 2) != 0, (_actionFlags & 4) != 0,
            (_actionFlags & 8) != 0, (_actionFlags & 16) != 0);

        private PlayerSurvivalSnapshot(ulong source, int prefab, int state,
            PlayerAuthorityRuntimeState runtime, float[] scalars, uint flags, uint actions,
            int strengthLevel, int maximumStrengthLevel, int lowHealthThreshold,
            int deathCount, int maximumDeathCount, int recipeId, ulong recipeFingerprint,
            PlayerWaterConfiguration water, PlayerSurvivalSettings settings)
        {
            SourceNetworkId = source; PrefabId = prefab; StateTypeId = state; Runtime = runtime;
            _scalars = scalars; _flags = flags; _actionFlags = actions;
            StrengthLevel = strengthLevel; MaximumStrengthLevel = maximumStrengthLevel;
            LowHealthThreshold = lowHealthThreshold; DeathCount = deathCount;
            MaximumDeathCount = maximumDeathCount; CookingRecipeId = recipeId;
            CookingRecipeFingerprint = recipeFingerprint;
            Water = water;
            Settings = settings;
        }

        public static bool TryCapture(BoltEntity source, PlayerLifecycle lifecycle,
            out PlayerSurvivalSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "The exact original source must remain paused by its lifecycle.";
            if (lifecycle == null || !lifecycle.IsPaused || !Same(lifecycle.OriginalEntity, source)) return false;
            if (!TryCaptureCore(source, lifecycle, out var candidate, out detail)) return false;
            if (!lifecycle.IsPaused || !Same(lifecycle.OriginalEntity, source)) return false;
            snapshot = candidate;
            return true;
        }

        /// <summary>
        /// Capture an explicitly disabled source under its native ingress freeze. The normal
        /// Harmony-held source must use the lifecycle overload, which proves its writer hold
        /// without requiring disabled components.
        /// </summary>
        public static bool TryCapture(BoltEntity source, out PlayerSurvivalSnapshot snapshot,
            out string detail)
            => TryCaptureCore(source, null, out snapshot, out detail);

        private static bool TryCaptureCore(BoltEntity source, PlayerLifecycle lifecycle,
            out PlayerSurvivalSnapshot snapshot, out string detail)
        {
            snapshot = null;
            detail = "An attached, held source with its exact nine registered stats is required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
            try
            {
                if (!Alive(source) || !source.isAttached || !source.isOwner
                    || source.prefabId != BoltPrefabs.PlayerNetwork) return false;
                int frame = Time.frameCount;
                var stats = source.GetComponentsInChildren<PlayerStats>(true);
                var vitals = source.GetComponentsInChildren<Vitals>(true);
                var characters = source.GetComponentsInChildren<FirstPersonCharacter>(true);
                var actions = source.GetComponentsInChildren<PlayerAnimatorControl>(true);
                var inventories = source.GetComponentsInChildren<PlayerInventory>(true);
                if (stats.Length != 1 || vitals.Length != 1 || characters.Length != 1
                    || actions.Length != 1 || inventories.Length != 1
                    || !Alive(stats[0]) || !Alive(vitals[0]) || !Alive(characters[0]) || !Alive(actions[0])
                    || (lifecycle == null && (characters[0].enabled || actions[0].enabled))
                    || !Same(stats[0]._vitals, vitals[0]) || !Same(stats[0]._animator, actions[0]._animator)
                    || !PlayerLifecycle.IsInventoryIngressFenced(inventories[0], out bool frozen)
                    || !frozen) return false;
                var serializer = source._entity?.Serializer?.TryCast<NetworkState>();
                if (serializer?.Meta == null || !RegisteredStats(vitals[0], out var ownedStats)
                    || characters[0]._swimmingJumpTimer == null || characters[0]._swimmingJumpTimer.WasCollected
                    || !PlayerAuthorityDynamics.TryCaptureRuntimeState(stats[0], lifecycle, out var runtime, out detail))
                    return false;
                var v = vitals[0];
                var recipe = v._activeCookingBuffRecipe;
                if (recipe != null && !Alive(recipe)) return false;
                if (!PlayerWaterConfiguration.TryCapture(source, lifecycle, characters[0], actions[0],
                    inventories[0], out var water, out var waterSensor, out detail)) return false;
                if (!PlayerSurvivalSettings.TryCapture(stats[0], lifecycle, runtime, out var settings, out detail)) return false;
                var candidate = new PlayerSurvivalSnapshot(source.networkId.PackedValue,
                    source.prefabId.Value, serializer.Meta.TypeId.Value, runtime,
                    CaptureScalars(stats[0], characters[0], actions[0], lifecycle?.PausedGameTime ?? Time.time), CaptureFlags(stats[0])
                        | (characters[0]._swimmingJumpTimer.IsRunning() ? 1u << 8 : 0)
                        | (characters[0]._blockWaterJump ? 1u << 9 : 0),
                    CaptureActions(stats[0], actions[0]), v._currentStrengthLevel, v._maxStrengthLevel,
                    v._lowHealthThreshold, stats[0]._playerDeathCount, stats[0]._maxDeathCount,
                    recipe == null ? -1 : recipe.Id, recipe == null ? 0 : RecipeFingerprint(recipe), water, settings);
                if (!candidate.Valid() || frame != Time.frameCount || !source.isAttached
                    || source.networkId.PackedValue != candidate.SourceNetworkId
                    || !source.isOwner || source.prefabId.Value != candidate.PrefabId
                    || source._entity?.Serializer?.Pointer != serializer.Pointer
                    || serializer.Meta.TypeId.Value != candidate.StateTypeId
                    || !Same(stats[0]._vitals, v) || !RegisteredStats(v, out var finalStats)
                    || !SameStats(ownedStats, finalStats)
                    || !water.IsCurrentSource(source, lifecycle, characters[0], actions[0], inventories[0], waterSensor)
                    || !PlayerLifecycle.IsInventoryIngressFenced(inventories[0], out frozen) || !frozen)
                    return false;
                snapshot = candidate;
                detail = "Canonical nine-stat storage, native configuration and durations captured under the source hold; approval remains required.";
                return true;
            }
            catch (Exception error)
            { detail = "Survival capture failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Approval must correlate the authenticated migration and all numerical/effect policy.
        /// An active cooking recipe requires a server-owned resolver. Existing modifiers are
        /// restored as values: never replay ApplyCookingBuffStats or load/save helpers. A failed
        /// partial write must retire the held carrier, not be retried or activated.
        /// </summary>
        public bool TryRestoreApproved(PlayerCarrier carrier,
            Func<PlayerCarrier, PlayerSurvivalSnapshot, bool> approve,
            out string detail, Func<int, StatRecipe> resolveCookingRecipe = null)
        {
            detail = "An exact prepared, held server carrier and explicit survival approval are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || carrier == null || carrier.IsDisposed || carrier.IsPhysicsActive || Time.inFixedTimeStep
                || approve == null || !Valid()) return false;
            bool writing = false;
            try
            {
                if (!Matches(carrier.Profile) || !PlayerRoles.IsCurrentCarrier(carrier.Entity)
                    || !carrier.TryReadServerStats(out var stats) || !RegisteredStats(stats._vitals, out var ownedStats)) return false;
                int frame = Time.frameCount;
                var v = stats._vitals;
                StatRecipe recipe = null;
                if (CookingRecipeId != -1)
                {
                    if (resolveCookingRecipe == null)
                    {
                        if (!TryResolveServerCookingRecipe(out recipe, out detail)) return false;
                    }
                    else recipe = resolveCookingRecipe(CookingRecipeId);
                    if (!Alive(recipe) || recipe.Id != CookingRecipeId
                        || RecipeFingerprint(recipe) != CookingRecipeFingerprint)
                    { detail = "The active cooking recipe requires an exact server-owned recipe resolver before restoration."; return false; }
                }
                if (!approve(carrier, this))
                { detail = "Server policy did not approve this survival snapshot."; return false; }
                if (frame != Time.frameCount || carrier.IsPhysicsActive || carrier.IsDisposed
                    || !carrier.TryReadServerStats(out var current) || !Same(current, stats) || !Same(stats._vitals, v)
                    || !RegisteredStats(v, out var beforeStats) || !SameStats(ownedStats, beforeStats)
                    || (CookingRecipeId != -1 && (!Alive(recipe) || RecipeFingerprint(recipe) != CookingRecipeFingerprint))) return false;
                writing = true;
                PlayerAuthorityDynamics.RestoreRuntime(stats, Runtime, Time.time);
                RestoreScalars(stats);
                if (stats._skunkSprayTime != Time.time - ReadScalar(PlayerSurvivalScalar.SkunkSprayAge))
                    throw new InvalidOperationException("The rebased native smell timestamp did not retain its value.");
                RestoreFlags(stats);
                v._currentStrengthLevel = StrengthLevel; v._maxStrengthLevel = MaximumStrengthLevel;
                v._lowHealthThreshold = LowHealthThreshold;
                stats._playerDeathCount = DeathCount; stats._maxDeathCount = MaximumDeathCount;
                v._activeCookingBuffRecipe = recipe;
                if (frame != Time.frameCount || !carrier.TryReadServerStats(out current) || !Same(current, stats)
                    || !RegisteredStats(v, out var afterStats) || !SameStats(ownedStats, afterStats)
                    || !MatchesRestored(stats))
                    throw new InvalidOperationException("Survival restoration did not retain the exact actor storage.");
                detail = "Approved survival storage restored once on the held actor; no native lifecycle or host-local effects were executed.";
                return true;
            }
            catch (Exception error)
            {
                detail = (writing ? "Partial survival restore; retire the held carrier. " : "Survival restore refused. ")
                    + error.GetType().Name + ": " + error.Message;
                return false;
            }
        }

        private bool Matches(PlayerCarrierProfile profile)
            => profile != null && profile.SourceNetworkId == SourceNetworkId
                && profile.PrefabId == PrefabId && profile.StateTypeId == StateTypeId;

        /// <summary>
        /// Requires the exact already-restored server carrier and a separate water-policy
        /// decision. Approval must qualify authenticated source settings and loaded scene
        /// water/provider support; inventory custody and survival approval do not imply it.
        /// </summary>
        public bool TryCreateMovementModeSettings(PlayerCarrier carrier,
            Func<PlayerCarrier, PlayerSurvivalSnapshot,
                PlayerMovementModeSettings, bool> approve,
            out PlayerMovementModeSettings settings, out string detail)
        {
            settings = default;
            detail = "Complete captured water configuration is required.";
            if (!Valid() || !Water.TryCreateSettings(out var candidate, out detail)
                || !QualifyMovementModeSettings(carrier, candidate, approve, out detail)) return false;
            settings = candidate;
            return true;
        }

        /// <summary>Qualifies an exact proposed mapping; never substitutes server or host defaults.</summary>
        public bool QualifyMovementModeSettings(PlayerCarrier carrier,
            PlayerMovementModeSettings settings,
            Func<PlayerCarrier, PlayerSurvivalSnapshot,
                PlayerMovementModeSettings, bool> approve, out string detail)
        {
            detail = "The exact restored, held server carrier and explicit water approval are required.";
            if (approve == null || !Valid() || !PlayerRoles.IsAvailable
                || !GameBuild.IsRecognized || Time.inFixedTimeStep) return false;
            try
            {
                if (!WaterQualificationCurrent(carrier)) return false;
                if (!Water.TryCreateSettings(out _, out detail)) return false;
                if (!Water.Matches(settings))
                { detail = "Proposed movement settings do not exactly match the captured native water configuration."; return false; }
                int frame = Time.frameCount;
                if (!approve(carrier, this, settings))
                { detail = "Server policy did not approve the captured water settings for this actor and scene."; return false; }
                if (frame != Time.frameCount || Time.inFixedTimeStep || !WaterQualificationCurrent(carrier))
                { detail = "The held carrier changed during water qualification."; return false; }
                detail = "Exact native water settings explicitly approved for the restored server carrier; no motor activated.";
                return true;
            }
            catch (Exception error)
            { detail = "Water qualification failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool WaterQualificationCurrent(PlayerCarrier carrier)
            => BoltNetwork.isServer && carrier != null && !carrier.IsDisposed && !carrier.IsPhysicsActive
                && ReferenceEquals(carrier.RestoredSurvival, this) && Matches(carrier.Profile)
                && carrier.IsCurrentForMotor && PlayerRoles.IsCurrentCarrier(carrier.Entity);

        /// <summary>
        /// Combines captured native settings without supplying controller defaults. The
        /// resulting profile is still an observation and requires explicit server approval.
        /// Active exhaustion/water-jump continuations are not converted to a zero timer.
        /// </summary>
        public bool TryCreateDynamicsProfile(PlayerCarrierProfile carrier,
            out PlayerAuthorityDynamicsProfile profile, out string detail)
        {
            profile = null;
            detail = "Matching complete native motion/survival captures and settled timers are required.";
            if (!Valid() || !Matches(carrier) || !PlayerCarrierProfile.ValidateProfile(carrier, out detail)) return false;
            var o = carrier.Observations;
            var m = carrier.Motion;
            if (o.ForceStopRun || WaterJumpTimerRunning || WaterJumpBlocked)
            { detail = "An active source exhaustion or water-jump continuation needs its own remaining-time handoff."; return false; }
            // DetermineVelocityChange selects the override at zero as well as positive values.
            float inputClamp = o.OverrideClampInput >= 0 ? o.OverrideClampInput : o.ClampInput;
            var ground = new PlayerNativeGroundDynamics(o.CurrentSpeed,
                m.WalkSpeed, m.RunSpeed, m.CrouchSpeed, m.BackwardSpeedMultiplier,
                m.MaximumVelocityChange, inputClamp, m.MaximumVelocity, m.Gravity,
                m.JumpHeight, m.JumpMultiplier, o.GroundStableForce, o.ExtremeAngleStart,
                o.ExtremeAngleMaximum, o.CanJump, o.CanRun, o.OverallSpeed, o.WalkToRunThreshold,
                o.Locked || o.MovementLocked, o.OnSlipperySlope, o.DisableGravity);
            var waterJump = new PlayerSwimJumpDynamics(m.JumpHeight, m.Gravity,
                ReadScalar(PlayerSurvivalScalar.EdgeWaterJumpMultiplier),
                ReadScalar(PlayerSurvivalScalar.WaterJumpInterval), m.JumpMultiplier,
                o.IsJumping, WaterJumpBlocked, o.OnSlipperySlope);
            var swim = new PlayerSwimDynamics(o.CurrentSpeed,
                ReadScalar(PlayerSurvivalScalar.SwimSpeed),
                ReadScalar(PlayerSurvivalScalar.SwimSpeedMultiplier),
                m.MaximumVelocityChange, inputClamp, m.DefaultMass, m.MaximumVelocity,
                ReadScalar(PlayerSurvivalScalar.MaximumDiveVelocity), WaterJumpTimerRunning,
                o.CanRun && Runtime.Stamina.Current > 0 && o.OverallSpeed >= o.WalkToRunThreshold, waterJump);
            var candidate = new PlayerAuthorityDynamicsProfile(ground, swim,
                new(o.RunCostPerSecond, o.TimeToRecoverFromRun, o.RecoveryRemaining, 0),
                o.TerrainExtremeAngleStart, o.TerrainExtremeAngleMaximum,
                ReadScalar(PlayerSurvivalScalar.MaximumAnimationInputSpeed),
                ReadScalar(PlayerSurvivalScalar.OverallSpeedBlend), o.AllowJump, o.CanRun, Runtime);
            if (!PlayerAuthorityDynamics.ValidProfile(candidate))
            { detail = "The captured native configuration does not satisfy the dynamics owner's supported ranges/state."; return false; }
            profile = candidate;
            detail = "Native dynamics profile composed without defaults; server policy and live action ownership remain required.";
            return true;
        }

        public PlayerAuthorityDynamicsProfile ToDynamicsProfile(PlayerCarrierProfile carrier)
        {
            if (!TryCreateDynamicsProfile(carrier, out var profile, out string detail))
                throw new InvalidOperationException(detail);
            return profile;
        }

        /// <summary>Reads loaded definition assets only; does not load assets or read any player's singleton.</summary>
        public bool TryResolveServerCookingRecipe(out StatRecipe recipe, out string detail)
        {
            recipe = null;
            detail = "The exact captured cooking definition is not loaded on this server.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized) return false;
            if (CookingRecipeId == -1) { detail = "No active cooking definition."; return true; }
            try
            {
                foreach (var asset in Resources.FindObjectsOfTypeAll<StatRecipe>())
                {
                    if (!Alive(asset) || asset.Id != CookingRecipeId) continue;
                    if (RecipeFingerprint(asset) != CookingRecipeFingerprint)
                    { detail = "A loaded cooking definition with this ID differs from the captured effects."; recipe = null; return false; }
                    if (recipe != null && recipe.Pointer != asset.Pointer)
                    { detail = "Multiple native cooking assets use this ID; explicit server resolution is required."; recipe = null; return false; }
                    recipe = asset;
                }
                if (recipe == null) return false;
                detail = "Loaded server cooking asset matches the captured ID and effect definition.";
                return true;
            }
            catch (Exception error)
            { recipe = null; detail = "Cooking definition resolution failed (" + error.GetType().Name + ")."; return false; }
        }

        private static ulong RecipeFingerprint(StatRecipe recipe)
        {
            if (!Alive(recipe) || !recipe._isACookingBuff || recipe.Id < 0
                || !float.IsFinite(recipe._cookingBuffDuration))
                throw new InvalidOperationException("The active cooking definition is unavailable.");
            var effects = recipe._statEffects;
            if (effects == null || effects.WasCollected || effects.Count is < 0 or > 64)
                throw new InvalidOperationException("The active cooking effects are unavailable or exceed the native transfer bound.");
            // A content comparison, not a trust credential. Authenticated snapshot approval
            // still decides whether this actor may possess these effects and durations.
            ulong hash = 14695981039346656037ul;
            Mix(unchecked((uint)recipe.Id));
            Mix(BitConverter.SingleToUInt32Bits(recipe._cookingBuffDuration));
            Mix((uint)effects.Count);
            for (int i = 0; i < effects.Count; i++)
            {
                var effect = effects[i];
                if (effect == null || effect.WasCollected || !float.IsFinite(effect._amount))
                    throw new InvalidOperationException("A cooking effect is unavailable or nonfinite.");
                Mix(unchecked((uint)effect._type)); Mix(BitConverter.SingleToUInt32Bits(effect._amount));
                Mix(effect._buff ? 1u : 0); Mix(effect._linkedToCookingBuff ? 1u : 0);
                Mix(unchecked((uint)effect._cookingBuffTier));
            }
            return hash;

            void Mix(uint value)
            {
                for (int i = 0; i < 4; i++)
                { hash = unchecked((hash ^ (byte)value) * 1099511628211ul); value >>= 8; }
            }
        }

        private static bool RegisteredStats(Vitals v, out Stat[] stats)
        {
            stats = null;
            if (!Alive(v) || v._statsManager == null || v._statsManager.WasCollected) return false;
            var registered = v._statsManager._stats;
            if (registered == null || registered.WasCollected || registered.Count != 9) return false;
            var values = new Stat[] { v._health, v._sickness, v._stamina, v._vitality,
                v._hydration, v._fullness, v._rested, v._strength, v._temperature };
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == null || values[i].WasCollected) return false;
                int matches = 0;
                for (int j = 0; j < values.Length; j++)
                {
                    if (i != j && values[j] != null && values[j].Pointer == values[i].Pointer) return false;
                    var stat = registered[j];
                    if (stat != null && !stat.WasCollected && stat.Pointer == values[i].Pointer) matches++;
                }
                if (matches != 1) return false;
            }
            stats = values;
            return true;
        }

        private static bool SameStats(Stat[] a, Stat[] b)
        {
            for (int i = 0; i < a.Length; i++) if (a[i].Pointer != b[i].Pointer) return false;
            return true;
        }

        private static uint CaptureFlags(PlayerStats s)
            => (s._healing ? 1u : 0) | (s._isHealing ? 2u : 0) | (s._isSeated ? 4u : 0)
                | (s._isExplode ? 8u : 0) | (s._isOnFire ? 16u : 0)
                | (s._IsBloody_k__BackingField ? 32u : 0) | (s._insideTemperatureModifierVolume ? 64u : 0)
                | (s._IsFightingBoss_k__BackingField ? 128u : 0)
                | (s._vitals._temperature._interiorSpaceWarmth ? 1024u : 0);

        private void RestoreFlags(PlayerStats s)
        {
            s._healing = (_flags & 1) != 0; s._isHealing = (_flags & 2) != 0;
            s._isSeated = (_flags & 4) != 0; s._isExplode = (_flags & 8) != 0;
            s._isOnFire = (_flags & 16) != 0; s._IsBloody_k__BackingField = (_flags & 32) != 0;
            s._insideTemperatureModifierVolume = (_flags & 64) != 0;
            s._IsFightingBoss_k__BackingField = (_flags & 128) != 0;
            s._vitals._temperature._interiorSpaceWarmth = (_flags & 1024) != 0;
        }

        private static uint CaptureActions(PlayerStats s, PlayerAnimatorControl a)
        {
            bool supported = !s._isSeated && !s._isExplode && !a.sitting && !a.onRope && !a.cliffClimb
                && !a.onRaft && !a.standingOnRaft && !a.fullyAttachedToSled && !a._IsSledding_k__BackingField
                && !a._isConnectedToLogSled && !a.enteringACave && !a.introCutScene && !a.endGameCutScene
                && !a.useRootMotion && !a.useRootRotation;
            bool free = supported && !a.doingFullBodyAction && !a.injured
                && !a._settingFullBodyWeightFromTransition && !a._doingGroundChop && !a._doingJumpCrouch;
            bool hands = free && !s._healing && !s._isHealing
                && !a._playerUpperBodyInTransition && !a._playerInventoryUpperBodyInTransition;
            return (free ? 7u : 0) | (hands ? 24u : 0);
        }

        private bool Valid()
        {
            if (SourceNetworkId == 0 || PrefabId <= 0 || StateTypeId <= 0
                || !PlayerAuthorityDynamics.ValidRuntimeStorage(Runtime)
                || Water == null || !Water.IsValid || Settings == null || !Settings.IsValid
                || _scalars == null || _scalars.Length != (int)PlayerSurvivalScalar.Count
                || (_flags & ~2047u) != 0 || (_actionFlags & ~31u) != 0
                || StrengthLevel < 0 || MaximumStrengthLevel < StrengthLevel || MaximumStrengthLevel > 1000000
                || LowHealthThreshold < 0 || DeathCount < 0 || MaximumDeathCount < 0 || CookingRecipeId < -1
                || (CookingRecipeId == -1 && CookingRecipeFingerprint != 0)) return false;
            foreach (float value in _scalars) if (!float.IsFinite(value)) return false;
            return ReadScalar(PlayerSurvivalScalar.MaximumAnimationInputSpeed) > 0
                && ReadScalar(PlayerSurvivalScalar.OverallSpeedBlend) >= 0
                && ReadScalar(PlayerSurvivalScalar.WaterJumpInterval) >= 0;
        }

        private static bool Alive(UnityEngine.Object o) => o != null && !o.WasCollected && o;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
    }
}
