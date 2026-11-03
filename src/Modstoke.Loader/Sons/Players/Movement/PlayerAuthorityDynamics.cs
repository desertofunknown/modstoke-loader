// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Physics;
using global::Sons.StatSystem;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    /// <summary>
    /// Actor-specific conditions for the native ground/swim motor. This owner never reads
    /// LocalPlayer, executes PlayerStats.Update/Vitals.Update, or imports client permission
    /// flags. The aggregate must own the bound server stats and action writers, including
    /// survival progression. Pausing their native Update is necessary, not sufficient.
    /// </summary>
    public sealed partial class PlayerAuthorityDynamics : IDisposable
    {
        private static readonly Dictionary<nint, PlayerAuthorityDynamics> Owners = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly BoltEntity _entity;
        private readonly Transform _root;
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly Func<PlayerAuthorityActionState> _readActions;
        private readonly Animator _animator;
        private readonly CoopMecanimReplicator _replicator;
        private readonly HealthStat _health;
        private readonly StaminaStat _stamina;
        private readonly VitalityStat _vitality;
        private readonly HydrationStat _hydration;
        private readonly FullnessStat _fullness;
        private readonly RestedStat _rested;
        private readonly SicknessStat _sickness;
        private readonly StrengthStat _strength;
        private readonly TemperatureStat _temperature;
        private readonly int _entityId, _statsId, _vitalsId, _animatorId;
        private int _overallFrame = -1;
        private float _overallSpeed;
        private PlayerGroundMovement _movement;
        private bool _canRun, _wasCrouched, _exhaustionPending, _disposed;
        private uint _lastRunPress, _lastRunPressInput;

        public PlayerAuthorityDynamicsProfile Profile { get; }
        public PlayerStaminaProfile StaminaProfile => Profile.Stamina;
        public bool IsDisposed => _disposed;
        public string FailureReason { get; private set; }

        private PlayerAuthorityDynamics(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerStats stats,
            Func<PlayerAuthorityActionState> readActions,
            CoopMecanimReplicator replicator, PlayerAuthorityDynamicsProfile profile)
        {
            _bridge = bridge; _carrier = carrier; _entity = carrier.Entity; _root = _entity.transform;
            _stats = stats; _vitals = stats._vitals; _readActions = readActions; _animator = stats._animator;
            _replicator = replicator; Profile = profile;
            _health = _vitals._health; _stamina = _vitals._stamina; _vitality = _vitals._vitality;
            _hydration = _vitals._hydration; _fullness = _vitals._fullness; _rested = _vitals._rested;
            _sickness = _vitals._sickness; _strength = _vitals._strength; _temperature = _vitals._temperature;
            _entityId = _entity.GetInstanceID(); _statsId = stats.GetInstanceID();
            _vitalsId = _vitals.GetInstanceID();
            _animatorId = _animator.GetInstanceID(); _overallSpeed = profile.Ground.OverallSpeed;
            _wasCrouched = carrier.PrimaryCollider.height == carrier.Profile.Geometry.CrouchingHeight;
            _canRun = profile.InitialCanRun && !_wasCrouched;
        }

        /// <summary>
        /// Call on the game thread after exact replacement startup/restoration, before
        /// motor preparation. Approval must validate this actor's profile against server policy
        /// and the correlated migration, not bless an unseen fingerprint or captured flags.
        /// Native numerical state/configuration must already be restored by the carrier. No component
        /// is created, initialized, enabled or disabled. A partial restore remains held and
        /// must retire with the aggregate; do not retry or activate a failed candidate.
        /// </summary>
        public static bool TryCreate(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerStats serverStats, Func<PlayerAuthorityActionState> readActions,
            PlayerAuthorityDynamicsProfile profile,
            Func<PlayerCarrierProfile, PlayerAuthorityDynamicsProfile, bool> approveProfile,
            out PlayerAuthorityDynamics dynamics, out string detail)
        {
            dynamics = null;
            detail = "An explicitly approved profile and exact paused server stats/action owners are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || bridge?.IsServer != true || carrier == null || carrier.IsDisposed
                || carrier.IsPhysicsActive || Time.inFixedTimeStep || approveProfile == null || readActions == null
                || !ValidProfile(profile) || !carrier.IsSurvivalRestored
                || carrier.RestoredSurvival.Runtime != profile.Runtime) return false;
            PlayerAuthorityDynamics candidate = null;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var entity) || !Same(entity, carrier.Entity)
                    || carrier.ReadReadiness(out detail) != BoltMigrationAdmission.Ready
                    || !PlayerRoles.IsCurrentCarrier(entity) || Owners.ContainsKey(entity.Pointer)
                    || !Alive(serverStats) || !Alive(serverStats._vitals)
                    || !Alive(serverStats._animator) || serverStats.enabled || serverStats._vitals.enabled
                    || !serverStats.transform.IsChildOf(entity.transform)
                    || !serverStats._vitals.transform.IsChildOf(entity.transform)) return false;
                // The source boolean cannot establish how much of its hidden coroutine remains.
                if (carrier.Profile.Observations.ForceStopRun || carrier.Profile.Observations.IsJumping
                    || !carrier.Profile.Grounded || profile.Stamina.ExhaustionWaitRemaining != 0
                    || carrier.Profile.Geometry.StandingHeight <= carrier.Profile.Geometry.CrouchingHeight)
                { detail = "Authority enrollment requires settled ground and no pending source exhaustion wait."; return false; }
                if (!approveProfile(carrier.Profile, profile))
                { detail = "Server policy did not approve the actor's movement and stamina profile."; return false; }
                if (!carrier.TryReadServerStats(out var preparedStats) || !Same(preparedStats, serverStats))
                { detail = "The approved carrier's prepared native stats are no longer current."; return false; }
                var replicators = entity.GetComponentsInChildren<CoopMecanimReplicator>(true);
                if (replicators.Length != 1 || !Alive(replicators[0])
                    || !Same(replicators[0].GetTargetAnimator(), serverStats._animator)
                    || entity.GetComponentsInChildren<PlayerStats>(true).Length != 1
                    || entity.GetComponentsInChildren<Vitals>(true).Length != 1
                    || entity.GetComponentsInChildren<PlayerAnimatorControl>(true).Length != 0) return false;
                bool hasOverall = false;
                foreach (var parameter in serverStats._animator.parameters)
                    if (parameter != null && parameter.name == "overallSpeed"
                        && parameter.type == AnimatorControllerParameterType.Float) hasOverall = true;
                if (!hasOverall) { detail = "The carrier has no native overallSpeed float parameter."; return false; }
                candidate = new(bridge, carrier, serverStats, readActions, replicators[0], profile);
                if (serverStats._playerIsDowned || (int)serverStats._dyingState != 0
                    || serverStats._respawning || serverStats._doFinalDeath || serverStats._vitals._hasDied)
                { detail = "A native life transition prevents runtime-state restoration."; return false; }
                candidate.ValidateApprovedRuntime();
                if (!carrier.RestoredSurvival.MatchesRestored(serverStats))
                { detail = "The already-restored survival configuration changed before dynamics enrollment."; return false; }
                if (!candidate.ReadActionPermissions().CanMove)
                { detail = "The carrier's current life/action state is not ordinary locomotion."; return false; }
                candidate.ReadStaminaConditions();
                candidate.RequireCurrent();
                Owners.Add(entity.Pointer, candidate);
                if (!carrier.TryBindAuthorityDynamics(candidate))
                    throw new InvalidOperationException("The exact carrier could not bind its approved dynamics owner.");
                bridge.Retired += candidate.Dispose;
                dynamics = candidate;
                detail = "Live actor-local dynamics and native stamina condition providers bound; survival writers remain aggregate-owned.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Dispose();
                detail = error is InvalidOperationException ? error.Message
                    : "Authority dynamics enrollment failed (" + error.GetType().Name + ").";
                return false;
            }
        }

        /// <summary>
        /// Uses the carrier's already approved/restored snapshot. Numerical approval here is
        /// independent of live action permissions. On failure, retire this aggregate; this
        /// never repeats survival restore or starts physics, timers or local presentation.
        /// </summary>
        public static bool TryPrepareFromRestoredSnapshot(PlayerBridge bridge,
            PlayerCarrier carrier, PlayerMovementModeSettings waterSettings,
            Func<PlayerAuthorityActionState> readActions,
            Func<PlayerCarrierProfile, PlayerAuthorityDynamicsProfile, bool> approveProfile,
            out PlayerAuthorityDynamics dynamics, out PlayerGroundMovement movement,
            out string detail, StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null)
        {
            dynamics = null; movement = null;
            detail = "Restore and approve the exact carrier's canonical survival baseline before preparing dynamics.";
            if (carrier == null || !carrier.IsSurvivalRestored || !carrier.TryReadServerStats(out var stats)
                || !carrier.RestoredSurvival.TryCreateDynamicsProfile(carrier.Profile, out var profile, out detail)) return false;
            if (!TryCreate(bridge, carrier, stats, readActions, profile, approveProfile, out var owner, out detail)) return false;
            if (!owner.TryPrepareMovement(waterSettings, out movement, out detail, stepHelper, stepHelperAllowed))
            { owner.Dispose(); return false; }
            dynamics = owner;
            return true;
        }

        internal bool OwnsCarrier(PlayerCarrier carrier, PlayerBridge bridge)
            => !_disposed && Environment.CurrentManagedThreadId == _thread
                && ReferenceEquals(_carrier, carrier) && ReferenceEquals(_bridge, bridge)
                && Owners.TryGetValue(_entity.Pointer, out var owner) && ReferenceEquals(owner, this);

        internal bool OwnsYieldedMotor(PlayerCarrier carrier, PlayerBridge bridge)
        {
            if (!OwnsCarrier(carrier, bridge) || _movement?.IsActive != true
                || !_movement.IsYieldedToNativeMode || _movement.YieldedNativeMode is not { } key
                || key.Mode == NativeMode.Ground || key.TransitionSequence == 0
                || !PlayerBridgeProtocol.IsValidMovementModeKey(key)) return false;
            // The bridge permits only adjacent ground/native commits. These are the exact
            // entry-drain, committed native and exit-drain phases of this retained motor.
            return bridge.IsCommittedMovementMode(key)
                || bridge.IsCommittedMovementMode(NativeModeKey.Ground(key.TransitionSequence - 1))
                || (key.TransitionSequence < uint.MaxValue
                    && bridge.IsCommittedMovementMode(NativeModeKey.Ground(key.TransitionSequence + 1)));
        }

        /// <summary>
        /// Start and bind the exact motor without an identity gap or a caller-selected motor.
        /// The motor owns stamina debits, the 0.9-second wait and recovery countdown; this
        /// owner only observes that state. Dispose the returned motor before this owner and
        /// the carrier. A failed start must retire the aggregate, not retry held physics.
        /// </summary>
        public bool TryStartMovement(PlayerMovementModeSettings waterSettings,
            out PlayerGroundMovement movement, out string detail)
            => TryStartMovement(waterSettings, out movement, out detail, null, null);

        /// <summary>
        /// Forward an exact, separately approved native step helper and live permission
        /// provider. The caller owns its configuration and lifetime; no geometry is inferred.
        /// Both optional arguments must be supplied together or both left null.
        /// </summary>
        public bool TryStartMovement(PlayerMovementModeSettings waterSettings,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper, Func<bool> stepHelperAllowed)
        {
            movement = null;
            detail = "An approved held carrier with no previously started motor is required.";
            try
            {
                RequireCurrent();
                if (_movement != null || _carrier.IsPhysicsActive
                    || (stepHelper == null) != (stepHelperAllowed == null)) return false;
                if (!PlayerGroundMovement.TryStartNative(_bridge, _carrier, waterSettings,
                    SupportedModesAllowed, ReadGroundDynamics, ReadSwimDynamics, _stats,
                    StaminaProfile, ReadStaminaConditions, out movement, out detail, stepHelper, stepHelperAllowed))
                { FailureReason = detail; Dispose(); return false; }
                _movement = movement;
                if (!movement.TryEnableLocomotionAnimation(out detail))
                { movement.Dispose(); FailureReason = detail; Dispose(); return false; }
                detail = "Exact actor motor, locomotion animation and selected-input Run re-arm feedback bound.";
                return true;
            }
            catch (Exception error) { movement?.Dispose(); detail = Fail(error); return false; }
        }

        /// <summary>Prepare without releasing physics, starting timers or replaying survival restoration.</summary>
        public bool TryPrepareMovement(PlayerMovementModeSettings waterSettings,
            out PlayerGroundMovement movement, out string detail,
            StepHelper stepHelper = null, Func<bool> stepHelperAllowed = null)
        {
            movement = null;
            detail = "An approved held carrier with no existing motor is required.";
            try
            {
                RequireCurrent();
                if (_movement != null || _carrier.IsPhysicsActive
                    || (stepHelper == null) != (stepHelperAllowed == null)) return false;
                if (!PlayerGroundMovement.TryPrepare(_bridge, _carrier, waterSettings,
                    SupportedModesAllowed, ReadGroundDynamics, ReadSwimDynamics, _stats,
                    StaminaProfile, ReadStaminaConditions, out movement, out detail, stepHelper, stepHelperAllowed))
                { FailureReason = detail; Dispose(); return false; }
                _movement = movement;
                detail = "Actor-local motor prepared with physics and stamina timers held; aggregate activation is still required.";
                return true;
            }
            catch (Exception error) { movement?.Dispose(); detail = Fail(error); return false; }
        }

        /// <summary>
        /// Observe the authenticated last re-arming Run press's input-sequence marker.
        /// Sprint is effective intent, not the raw press; timeouts and stamina recovery must
        /// never synthesize a marker. Several physical presses before a sample may coalesce.
        /// The ordinary ground/swim dynamics route now consumes selected markers itself.
        /// This explicit route retains its current-input identity/expiry checks without
        /// selecting or acknowledging a simulation step or retaining a move command.
        /// </summary>
        public bool TryObserveRunPress(PlayerMotionInput acceptedInput, uint pressOrdinal, out string detail)
            => TryObserveRunPress(acceptedInput, pressOrdinal, true, out detail);

        private bool TryObserveRunPress(PlayerMotionInput acceptedInput, uint pressOrdinal,
            bool observeExhaustion, out string detail)
        {
            detail = "A current, newer authenticated Run press ordinal is required.";
            try
            {
                RequireCurrent();
                if (_movement == null || !_bridge.TryPeekCurrentInput(out var input)
                    || input != acceptedInput || input.MigrationId != _bridge.Plan.Id
                    || input.Generation != _bridge.Plan.Generation || input.NetworkId != _bridge.NetworkId
                    || input.Sequence == 0 || input.Sequence <= _lastRunPressInput
                    || pressOrdinal == 0 || pressOrdinal <= _lastRunPress || pressOrdinal > input.Sequence
                    || pressOrdinal != input.RunPressSequence) return false;
                var permissions = ReadPermissionsCore(observeExhaustion);
                _lastRunPress = pressOrdinal;
                _lastRunPressInput = input.Sequence;
                // UpdateRun 182F83B46: a press which activates run re-arms CanRun.
                // Crouch takes precedence. Toggle-off presses do not re-arm it.
                if (permissions.CanMove && !Crouched(input)
                    && (input.MoveButtons & PlayerMoveButtons.Sprint) != 0) _canRun = true;
                detail = "Run press consumed without changing native controls or acknowledging simulation.";
                return true;
            }
            catch (Exception error) { detail = Fail(error); return false; }
        }

        public PlayerNativeGroundDynamics ReadGroundDynamics()
        {
            RequireCurrent();
            ObserveSelectedRunPress();
            var permissions = ReadPermissionsCore();
            bool terrain = GroundTouchesTerrain();
            var ground = Profile.Ground;
            return ground with
            {
                CanRun = permissions.CanRun, CanJump = permissions.CanJump,
                OverallSpeed = ReadOverallSpeed(), InputBlocked = !permissions.CanMove,
                SlideStartAngle = terrain ? Profile.TerrainSlideStartAngle : ground.SlideStartAngle,
                SlideEndAngle = terrain ? Profile.TerrainSlideEndAngle : ground.SlideEndAngle,
                OnSlipperySlope = false, DisableGravity = false
            };
        }

        public PlayerSwimDynamics ReadSwimDynamics()
        {
            RequireCurrent();
            ObserveSelectedRunPress();
            var permissions = ReadPermissionsCore();
            // The current swim motor has no input-block field. Never pass blocked action
            // state through as an apparently valid swim step which still consumes intent.
            if (!permissions.CanMove) throw new InvalidOperationException("The actor's current action blocks swimming.");
            float overall = ReadOverallSpeed();
            return Profile.Swim with
            {
                RunningAllowed = permissions.CanRun && _stamina._currentValue > 0
                    && overall >= Profile.Ground.WalkToRunThreshold,
                SwimmingJumpTimerRunning = false
            };
        }

        public PlayerStaminaConditions ReadStaminaConditions()
        {
            RequireCurrent();
            var permissions = ReadPermissionsCore();
            // UpdateStamina 183116AA2..183116BD7: cold OR freezing, then independent
            // stat-regeneration penalty. The minimum-to-run field is not a re-arm gate.
            float multiplier = 1;
            if (permissions.Cold || permissions.Freezing)
                multiplier *= Penalty(GameSetupManager.GetColdPenaltiesSetting(),
                    _stats._staminaRegenColdModifierSurvivalNormal, _stats._staminaRegenColdModifierSurvivalHard);
            multiplier *= Penalty(GameSetupManager.GetStatRegenerationPenaltySetting(),
                _stats._staminaRegenStatModifierSurvivalNormal, _stats._staminaRegenStatModifierSurvivalHard);
            if (!Range(multiplier, 0, 1000)) throw new InvalidOperationException("The native recovery multiplier is invalid.");
            return new(permissions.CanRun, ReadOverallSpeed(), Profile.Ground.WalkToRunThreshold, multiplier);
        }

        public PlayerAuthorityPermissions ReadActionPermissions()
        {
            RequireCurrent();
            return ReadPermissionsCore();
        }

        /// <summary>
        /// Reads the exact server actor's committed native state and animator transitions.
        /// The aggregate must maintain IsInMidAction/CurrentView/seat/life fields and combine
        /// these gates with its pending custody and special-mode owners. These reads do not
        /// implement those action routes, nor authorize a client-supplied permission mask.
        /// </summary>
        public static bool TryReadCarrierActions(PlayerCarrier carrier,
            out PlayerAuthorityActionState actions, out string detail)
        {
            actions = default;
            detail = "The exact restored server actor and committed action state are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || carrier == null || carrier.IsDisposed || !carrier.IsSurvivalRestored) return false;
            try
            {
                if (!PlayerRoles.IsCurrentCarrier(carrier.Entity)
                    || !carrier.TryReadServerStats(out var stats) || !Alive(stats._animator)) return false;
                var state = carrier.Entity.GetState<IPlayerState>();
                if (state == null || state.WasCollected || stats._playerStateCache == null
                    || stats._playerStateCache.WasCollected || stats._playerStateCache.Pointer != state.Pointer) return false;
                var animator = stats._animator;
                var v = stats._vitals;
                bool living = !v._hasDied && v._health._currentValue > 0 && !stats._playerIsDowned
                    && stats._dyingState == PlayerStats.DyingState.Alive && !stats._respawning && !stats._doFinalDeath;
                bool free = living && state.Dying == 0 && state.TiedUpState == 0 && !state.IsInMidAction
                    && !state.IsWaitingToSleep && !state.InMenus && !state.DisableBodyColliders
                    && !state.DrivingGolfCart && !state.PassengerGolfCart && !state.onRaft && !state.zipLineAttach
                    && !stats._isSeated && !stats._isExplode;
                // CurrentView is native PlayerInventory.PlayerViews, not a guessed integer.
                bool world = state.CurrentView == (int)TheForest.Items.Inventory.PlayerInventory.PlayerViews.World;
                bool transition = false;
                int layers = animator.layerCount;
                if (layers is <= 0 or > 32) return false;
                for (int i = 0; i < layers; i++) transition |= animator.IsInTransition(i);
                // Ordinary locomotion itself blends animator states. A blend is not a
                // movement lock; only explicit actor action state can establish that gate.
                bool move = free && world;
                bool hands = move && !transition && !stats._healing && !stats._isHealing;
                if (!carrier.TryReadServerStats(out var current) || !Same(stats, current)
                    || !Same(animator, stats._animator) || carrier.Entity.GetState<IPlayerState>()?.Pointer != state.Pointer) return false;
                actions = new(move, move, move, hands, hands);
                detail = "Exact actor native action gates read; aggregate custody and special-mode restrictions still apply.";
                return true;
            }
            catch (Exception error)
            { detail = "Actor action state unavailable (" + error.GetType().Name + ")."; return false; }
        }

        private void ObserveSelectedRunPress()
        {
            if (_movement == null || !_bridge.TryPeekSelectedInput(out var input)
                || input.RunPressSequence <= _lastRunPress) return;
            // A source marker is the last genuine re-arming press's input sequence,
            // not a press count. Several presses before a sample coalesce into it.
            // Preserve native ordering: press re-arm precedes exhaustion handling.
            if (!TryObserveRunPress(input, input.RunPressSequence, false, out string detail))
                throw new InvalidOperationException(detail);
        }

        private PlayerAuthorityPermissions ReadPermissionsCore(bool observeExhaustion = true)
        {
            if (observeExhaustion) ObserveExhaustion();
            var actions = _readActions();
            // The callback may retire or rebind its aggregate. Never use its result across
            // that identity boundary, and do not require a local-only animator controller.
            RequireCurrent();
            bool living = !_vitals._hasDied && _health._currentValue > 0
                && !_stats._playerIsDowned && (int)_stats._dyingState == 0
                && !_stats._respawning && !_stats._doFinalDeath;
            bool canMove = living && !_stats._isExplode && actions.CanMove;
            bool crouched = _carrier.PrimaryCollider.height == _carrier.Profile.Geometry.CrouchingHeight;
            // Native UpdateRun clears CanRun on crouching. A routed uncrouch+Run press
            // may precede the motor's capsule update, so do not erase it on every read.
            if (crouched && !_wasCrouched) _canRun = false;
            _wasCrouched = crouched;
            bool canRun = canMove && actions.CanRun && !crouched && _canRun;
            bool canAct = living && !_stats._isExplode && !_stats._healing && !_stats._isHealing;
            return new PlayerAuthorityPermissions(living, canMove,
                canMove && actions.CanJump && !crouched && Profile.AllowJump, canRun,
                canAct && actions.CanUseInventory, canAct && actions.CanAttack,
                _vitals._isCold, _stats._IsFreezing_k__BackingField, _health._currentValue,
                _stamina._currentValue, _vitality._currentValue,
                StatFactor(_hydration), StatFactor(_fullness), StatFactor(_rested));
        }

        public bool SupportedModesAllowed()
        {
            try { return ReadActionPermissions().CanMove; }
            catch (Exception error) { Fail(error); return false; }
        }

        private void ObserveExhaustion()
        {
            if (_movement == null) return;
            // TryReadStaminaState -> Current checks identities/components only, never our
            // supported-mode predicate or condition delegates. Keep that contract on routing changes.
            if (!_movement.TryReadStaminaState(out var state))
                throw new InvalidOperationException("The bound motor's stamina ownership ended.");
            // DoForceStopRun.MoveNext 182F8BFA8: on wait completion, recovery is set,
            // CanRun is cleared and ForceStopRun is cleared. Recovery does not re-arm it.
            if (_exhaustionPending && !state.ForceStopRun) _canRun = false;
            _exhaustionPending = state.ForceStopRun;
        }

        private float ReadOverallSpeed()
        {
            // PlayerAnimatorControl.UpdatePlayerVelocity 1835A75E6..1835A76D4 uses the
            // real body's horizontal velocity, including while swimming. Its native writer
            // is disabled here; reading its last Animator float would freeze run eligibility.
            // The motor's stamina coroutine reads every rendered frame. Fixed-step reads
            // reuse that frame's scalar, matching Update's cadence without an extra writer.
            if (_movement?.IsActive == true && !_movement.IsYieldedToNativeMode
                && !Time.inFixedTimeStep && _overallFrame != Time.frameCount)
            {
                var velocity = _carrier.Body.velocity;
                float dt = Time.deltaTime;
                float speed = MathF.Sqrt(velocity.x * velocity.x + velocity.z * velocity.z)
                    / Profile.MaximumAnimationInputSpeed;
                if (!Range(speed, 0, 1000) || !Range(dt, 0, 1))
                    throw new InvalidOperationException("The carrier's live locomotion speed or time is invalid.");
                _overallSpeed = Mathf.Lerp(_overallSpeed, speed, dt * Profile.OverallSpeedBlend);
                _overallFrame = Time.frameCount;
            }
            return _overallSpeed;
        }

        private bool GroundTouchesTerrain()
        {
            var contacts = _carrier.Contacts._contacts;
            if (contacts == null || contacts.WasCollected || contacts.Count > 256)
                throw new InvalidOperationException("The carrier's native contact list is unavailable.");
            for (int i = 0; i < contacts.Count; i++)
            {
                var collider = contacts[i].otherCollider;
                if (Alive(collider) && collider.TryCast<TerrainCollider>() != null) return true;
            }
            return false;
        }

        private static float Penalty(SurvivalSetting setting, float normal, float hard)
        {
            if (!Range(normal, 0, 1000) || !Range(hard, 0, 1000))
                throw new InvalidOperationException("The actor's native stamina penalties are invalid.");
            if (setting._intValue == SurvivalSetting.Normal._intValue) return normal;
            if (setting._intValue == SurvivalSetting.Hard._intValue) return hard;
            return 1;
        }

        // Same inlined stat calculation used by StrictCombatLifeHooks.StatFactor.
        private static float StatFactor(Stat stat) => (stat._currentValue - stat._min) / (stat._max - stat._min);
        private static bool Crouched(PlayerMotionInput input)
            => (input.MoveButtons & PlayerMoveButtons.Crouch) != 0;

        private void RequireCurrent(bool requireState = true)
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread || !GameBuild.IsRecognized
                || _bridge.IsDisposed || !_bridge.IsServer || !_bridge.TryReadCurrentTarget(out var entity)
                || !Same(entity, _entity) || !Alive(_entity) || _entity.GetInstanceID() != _entityId
                || !_carrier.IsCurrentForMotor || !Alive(_root) || !Same(_entity.transform, _root)
                || !Alive(_stats) || _stats.GetInstanceID() != _statsId || _stats.enabled
                || !Alive(_vitals) || _vitals.GetInstanceID() != _vitalsId || _vitals.enabled
                || !Alive(_animator) || _animator.GetInstanceID() != _animatorId
                || !Same(_stats._vitals, _vitals) || !Same(_stats._animator, _animator)
                || !Alive(_replicator)
                || !Same(_replicator.entity, _entity) || !Same(_replicator.GetTargetAnimator(), _animator)
                || !_stats.transform.IsChildOf(_root) || !_vitals.transform.IsChildOf(_root)
                || !_animator.transform.IsChildOf(_root)
                || !SameStat(_vitals._health, _health, requireState) || !SameStat(_vitals._stamina, _stamina, requireState)
                || !SameStat(_vitals._vitality, _vitality, requireState) || !SameStat(_vitals._hydration, _hydration, requireState)
                || !SameStat(_vitals._fullness, _fullness, requireState) || !SameStat(_vitals._rested, _rested, requireState)
                || !SameStat(_vitals._sickness, _sickness, requireState) || !SameStat(_vitals._strength, _strength, requireState)
                || !SameStat(_vitals._temperature, _temperature, requireState)
                || (_movement != null && _movement.IsDisposed))
                throw new InvalidOperationException("The actor's approved authority dynamics binding is no longer current.");
        }

        private static bool SameStat(Stat actual, Stat expected, bool requireState = true)
            => actual != null && expected != null && !actual.WasCollected && !expected.WasCollected
                && actual.Pointer == expected.Pointer && (!requireState || (float.IsFinite(actual._min) && float.IsFinite(actual._max)
                && actual._max > actual._min && Range(actual._currentValue, actual._min, actual._max)));
        private static bool Alive(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
        private static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;

        private string Fail(Exception error)
        {
            FailureReason ??= error is InvalidOperationException ? error.Message
                : "Authority dynamics failed (" + error.GetType().Name + ").";
            Dispose();
            return FailureReason;
        }

        public void Dispose()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
            _survival?.Dispose();
            _disposed = true;
            _carrier.ReleaseAuthorityDynamics(this);
            _bridge.Retired -= Dispose;
            if (Owners.TryGetValue(_entity.Pointer, out var owner) && ReferenceEquals(owner, this))
                Owners.Remove(_entity.Pointer);
            // Borrowed native objects and the separately owned motor are never resumed here.
        }

        internal static bool ValidProfile(PlayerAuthorityDynamicsProfile p)
        {
            if (p == null || !ValidRuntime(p.Runtime)) return false;
            var g = p.Ground;
            var s = p.Stamina;
            return Range(g.CurrentSpeed, 0, 100) && Range(g.WalkSpeed, 0, 100) && Range(g.RunSpeed, 0, 100)
                && Range(g.CrouchSpeed, 0, 100) && Range(g.BackwardSpeedMultiplier, 0, 10)
                && Range(g.MaximumVelocityChange, 0, 1000) && Range(g.InputClamp, 0, 100)
                && Range(g.MaximumVelocity, 0.01f, 1000) && Range(g.Gravity, 0, 1000)
                && Range(g.JumpHeight, 0, 100) && Range(g.JumpMultiplier, 0, 100)
                && Range(g.GroundStickiness, 0, 1000) && Range(g.WalkToRunThreshold, 0, 1000)
                && Range(g.OverallSpeed, 0, 1000) && Range(p.MaximumAnimationInputSpeed, 0.001f, 1000)
                && Range(p.OverallSpeedBlend, 0, 1000)
                && Range(g.SlideStartAngle, 0, 180) && Range(g.SlideEndAngle, 0, 180)
                && g.SlideEndAngle > g.SlideStartAngle && !g.OnSlipperySlope && !g.DisableGravity
                && Range(p.TerrainSlideStartAngle, 0, 180) && Range(p.TerrainSlideEndAngle, 0, 180)
                && p.TerrainSlideEndAngle > p.TerrainSlideStartAngle
                && PlayerMovementModes.ValidDynamics(p.Swim)
                && !p.Swim.SwimmingJumpTimerRunning
                && p.Swim.WaterJump is not { IsJumping: true } and not { BlockWaterJump: true } and not { OnSlipperySlope: true }
                && Range(s.RunCostPerSecond, 0, 10000) && Range(s.TimeToRecover, 0, 600)
                && Range(s.RecoveryRemaining, -600, 600) && s.ExhaustionWaitRemaining == 0;
        }
    }
}
