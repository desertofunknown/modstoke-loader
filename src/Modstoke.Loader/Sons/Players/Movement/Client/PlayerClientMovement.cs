// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using HarmonyLib;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public readonly record struct PlayerClientCorrectionLimits(
        float Position, float Velocity, float Clearance);

    /// <summary>
    /// Opt-in transport and bounded reconciliation for the original local rigidbody/FPC.
    /// This is not deterministic replay or a vanilla server motor: ground speed, stamina,
    /// jump/landing history, animation and action parity remain the migration adapter's job.
    /// No input, camera, rotation, FPC cache, ownership or capability is written here.
    /// </summary>
    public sealed partial class PlayerClientMovement : IDisposable
    {
        private static PlayerClientMovement _active;
        private static PlayerClientMovement _prepared;
        private static bool _installAttempted;
        private static bool _installed;
        private static bool _starting;
        private static int _hookThread;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly RetainedRig _rig;
        private readonly PlayerMotionHistory _history;
        private readonly PlayerClientCorrectionLimits _limits;
        private readonly Func<bool> _groundModeAllowed;
        private Coroutine _coroutine;
        private ReplicaPresentation _presentation;
        private PlayerMotionInput _sample;
        private PlayerMotionInput _unsent;
        private double _fixedTime = double.NaN;
        private float _fixedDeltaTime;
        private Vector3 _stepPosition;
        private int _fixedFrame;
        private uint _sequence;
        private uint _jumpSequence;
        private bool _insideFixed;
        private bool _sampled;
        private bool _awaitingPhysics;
        private bool _insideCoroutine;
        private bool _jumpPending;
        private bool _jumpLatched;
        private bool _disposed;
        private bool _swimmingAllowed;
        private bool _activated;

        public bool IsDisposed => _disposed;
        public bool IsPrepared => !_disposed && !_activated;
        public bool IsActive => !_disposed && _activated;
        public string StopReason { get; private set; }

        /// <summary>
        /// Enable only with the matching server swim motor and a policy that owns water transitions,
        /// inventory stashing and actions. Keeps the original FPC as predictor; it does not create
        /// server authority or make unsupported climb and vehicle modes available. Surface jumps
        /// require the matching server water-jump profile and native geometry qualification.
        /// </summary>
        public bool TryEnableSwimming(out string detail)
        {
            detail = "Enable swimming outside a physics step while the exact retained rig is on dry ground.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || LifeMovementBlocked || _insideFixed || _awaitingPhysics
                || Time.inFixedTimeStep || !RetainedTargetCurrent(true)
                || (_activated && (!Current() || !_rig.GroundMode()))) return false;
            _swimmingAllowed = true;
            detail = "Native swimming prediction and mode-aware history enabled; server simulation remains authoritative.";
            return true;
        }

        private PlayerClientMovement(PlayerBridge bridge, RetainedRig rig,
            PlayerClientCorrectionLimits limits, Func<bool> groundModeAllowed)
        {
            _bridge = bridge;
            _rig = rig;
            _limits = limits;
            _groundModeAllowed = groundModeAllowed;
            _history = new PlayerMotionHistory(bridge.Plan.Id, bridge.Plan.Generation, bridge.NetworkId);
        }

        /// <summary>Install once with the other recognized-build hooks. Inert until TryStart.</summary>
        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                _hookThread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-client-movement");
                var fixedUpdate = AccessTools.DeclaredMethod(typeof(FirstPersonCharacter),
                    nameof(FirstPersonCharacter.FixedUpdate), Type.EmptyTypes);
                var targetVelocity = AccessTools.DeclaredMethod(typeof(FirstPersonCharacter),
                    nameof(FirstPersonCharacter.GetTargetVelocity), Type.EmptyTypes);
                var jumpInput = AccessTools.DeclaredMethod(typeof(FirstPersonCharacter),
                    nameof(FirstPersonCharacter.OnJumpInput), new[] { typeof(bool) });
                var swimJump = AccessTools.DeclaredMethod(typeof(FirstPersonCharacter),
                    nameof(FirstPersonCharacter.HandleSwimmingJumpInput), new[] { typeof(bool) });
                if (fixedUpdate == null || fixedUpdate.IsStatic || fixedUpdate.ReturnType != typeof(void)
                    || targetVelocity == null || targetVelocity.IsStatic || targetVelocity.ReturnType != typeof(Vector3)
                    || jumpInput == null || jumpInput.IsStatic || jumpInput.ReturnType != typeof(void)
                    || swimJump == null || swimJump.IsStatic || swimJump.ReturnType != typeof(void))
                    throw new MissingMethodException("Recognized FPC sampling methods are unavailable.");
                harmony.Patch(fixedUpdate,
                    prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeFixed)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterFixed)) { priority = Priority.Last });
                harmony.Patch(targetVelocity,
                    postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterTargetVelocity)) { priority = Priority.Last });
                harmony.Patch(jumpInput,
                    postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterJumpInput)) { priority = Priority.Last });
                harmony.Patch(swimJump,
                    prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeSwimmingJump)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterSwimmingJump)) { priority = Priority.Last });
                InstallRunInput(harmony);
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Retained player movement hooks unavailable (" + error.GetType().Name + ").");
            }
            if (!_installed) return;
            // A missing special-mode boundary must not retire the existing ground sampler.
            HarmonyLib.Harmony nativeModes = null;
            try
            {
                nativeModes = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-client-native-movement");
                InstallNativeModes(nativeModes);
            }
            catch (Exception error)
            {
                _nativeModesInstalled = false;
                try { nativeModes?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Retained native-mode movement hooks unavailable (" + error.GetType().Name + ").");
            }
        }

        /// <summary>
        /// Prepare after rig-cache repair, including while the lifecycle holds the original
        /// controller. No coroutine, renderer suppression, sampling or native writer starts here.
        /// Capture RetainedRig before detaching the original. Activate only after all domains
        /// have completed admission and the lifecycle owner restores the native prediction rig.
        /// </summary>
        public static bool TryPrepare(PlayerBridge bridge, RetainedRig rig,
            PlayerClientCorrectionLimits limits, Func<bool> groundModeAllowed,
            out PlayerClientMovement movement, out string detail)
        {
            movement = null;
            detail = "An installed client sampler, prepared bridge and repaired retained rig are required.";
            if (!_installed || _starting || Environment.CurrentManagedThreadId != _hookThread
                || !PlayerRoles.IsAvailable || _active != null || _prepared != null || bridge == null || bridge.IsServer
                || bridge.IsDisposed || rig == null || rig.Consumed || groundModeAllowed == null
                || !Range(limits.Position, 0.001f, 1) || !Range(limits.Velocity, 0, 10)
                || !Range(limits.Clearance, 0.001f, 0.1f)) return false;
            _starting = true;
            PlayerClientMovement candidate = null;
            try
            {
                if (Time.inFixedTimeStep || !bridge.TryReadCurrentTarget(out var entity)
                    || rig.OriginalNetworkId != bridge.Plan.OldNetworkId || !rig.Matches(entity, true, true)) return false;
                candidate = new PlayerClientMovement(bridge, rig, limits, groundModeAllowed);
                bridge.Retired += candidate.OnBridgeRetired;
                rig.Consumed = true;
                _prepared = candidate;
                movement = candidate;
                detail = "Retained rig prepared without starting movement or presentation writers.";
                return true;
            }
            catch (Exception error)
            {
                candidate?.Stop("Retained movement preparation failed (" + error.Message + ").");
                detail = candidate?.StopReason ?? "Retained rig validation failed.";
                return false;
            }
            finally { _starting = false; }
        }

        public bool TryActivate(out string detail)
        {
            detail = "Complete gameplay admission and restore the original native prediction rig before activation.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _starting || LifeMovementBlocked
                || Time.inFixedTimeStep || !_bridge.IsGameplayActive) return false;
            if (!WaterReadyForActivation(out var waterDetail)) { detail = waterDetail; return false; }
            if (_activated)
            {
                bool current = Current();
                detail = current ? "Retained movement is already active." : "The active retained movement lifetime changed.";
                return current;
            }
            if (!ReferenceEquals(_prepared, this) || _active != null) return false;
            _starting = true;
            try
            {
                if (!_bridge.IsCommittedMovementMode(NativeModeKey.Ground())
                    || !_bridge.TryReadCurrentTarget(out var entity) || !_rig.Matches(entity, true)
                    || !SupportedMode() || !_groundModeAllowed()
                    // Re-check after the adapter callback, which can change the binding.
                    || !_bridge.TryReadCurrentTarget(out entity) || !_rig.Matches(entity, true)
                    || !SupportedMode() || _disposed) return false;
                _activated = true;
                _prepared = null;
                _active = this;
                _presentation = new ReplicaPresentation(this, entity);
                _presentation.Start();
                if (!global::Modstoke.Loader.Coroutines.TryStartObserved(ObservePhysics(), OnCoroutineCompleted,
                    out var token, out detail))
                { Stop(detail); detail = StopReason; return false; }
                _coroutine = token;
                if (_disposed && token != null)
                {
                    _coroutine = null;
                    global::Modstoke.Loader.Coroutines.StopOwned(token);
                }
                if (token == null || _disposed || !Current())
                { Stop("Post-physics observation did not start."); detail = StopReason; return false; }
                detail = "Original FPC sampling and reconciliation active; original controls and camera retained.";
                return true;
            }
            catch (Exception error)
            { Stop("Retained movement activation failed (" + error.Message + ")."); detail = StopReason; return false; }
            finally { _starting = false; }
        }

        /// <summary>Prepare and activate an already admitted player in one call.</summary>
        public static bool TryStart(PlayerBridge bridge, RetainedRig rig,
            PlayerClientCorrectionLimits limits, Func<bool> groundModeAllowed,
            out PlayerClientMovement movement, out string detail)
        {
            movement = null;
            detail = "An active gameplay bridge is required.";
            if (bridge == null || !bridge.IsGameplayActive
                || !TryPrepare(bridge, rig, limits, groundModeAllowed, out var candidate, out detail)) return false;
            if (!candidate.TryActivate(out detail)) { candidate.Dispose(); return false; }
            movement = candidate;
            return true;
        }

        /// <summary>
        /// Call from driver Update for lifetime/presentation checks and pre-send backpressure retries.
        /// Physics samples come from the owned fixed-wait coroutine, never this render-frame tick.
        /// </summary>
        internal static void Tick()
        {
            var active = _active;
            if (active == null || Environment.CurrentManagedThreadId != _hookThread) return;
            try
            {
                if (active._sourceHold != null)
                {
                    if (active.PumpSourceInputHold(out var holdDetail) == BoltMigrationAdmission.Rejected)
                    { active.Stop(holdDetail); return; }
                    if (active.SourceMovementHeld)
                    {
                        if (active.SourceHoldCurrent(false)) active._presentation.Refresh();
                        return;
                    }
                }
                if (active.HoldLifeMovement())
                {
                    if (!active._disposed) active._presentation.Refresh();
                    return;
                }
                if (active.CartMigrationHeld) return;
                if (!active.Current()) { active.Stop("Retained player lifetime ended."); return; }
                if (active._insideFixed || active._awaitingPhysics)
                { active.Stop("Post-physics observation was missed; prediction ordering is unknown."); return; }
                active.PumpNativeTransition();
                if (active._disposed) return;
                active._presentation.Refresh();
                if (active._transition != null) return;
                if (active._nativeMode != null)
                {
                    active.ReceiveNativeState();
                    if (!active._disposed) active.FlushNativeInput();
                }
                else active.FlushInput();
            }
            catch (Exception error) { active.Stop("Retained movement tick failed (" + error.Message + ")."); }
        }

        private static void BeforeFixed(FirstPersonCharacter __instance, bool __runOriginal)
        {
            var active = For(__instance);
            if (active == null || active._transition != null || active._nativeMode != null) return;
            try
            {
                if (!__runOriginal || !active.Current(true) || !active.CheckMode())
                { active.Stop("The native FPC or supported ground mode was suspended."); return; }
                double now = Time.fixedTimeAsDouble;
                float dt = Time.fixedDeltaTime;
                if (!Time.inFixedTimeStep || active._insideFixed || active._awaitingPhysics || !double.IsFinite(now)
                    || (!double.IsNaN(active._fixedTime) && now <= active._fixedTime) || !Range(dt, 0.001f, 0.1f))
                { active.Stop("Native fixed-step ordering changed."); return; }
                active._fixedTime = now;
                active._fixedDeltaTime = dt;
                active._stepPosition = active._rig.Body.position;
                if (!Finite(active._stepPosition)) { active.Stop("The native body position is not finite."); return; }
                active._insideFixed = true;
                active._sampled = false;
            }
            catch (Exception error) { active.Stop("FPC pre-step validation failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterTargetVelocity(FirstPersonCharacter __instance, bool __runOriginal)
        {
            var active = For(__instance);
            if (active == null || active._transition != null || active._nativeMode != null || !active._insideFixed) return;
            try
            {
                if (!__runOriginal || active._sampled || !active.Current() || active._sequence == uint.MaxValue
                    || Time.fixedTimeAsDouble != active._fixedTime || Time.fixedDeltaTime != active._fixedDeltaTime)
                { active.Stop("Native input sampling did not have one unmodified boundary."); return; }
                // In the recognized GetTargetVelocity, inputVelocity is the local-space input
                // after InputHack, dead-zone and magnitude processing, BEFORE TransformDirection.
                // Reading it avoids a second native call (which would consume the cached input).
                var input = __instance.inputVelocity;
                var forward = __instance.transform.forward;
                if (!Finite(input) || input.y != 0 || !Range(input.x, -1, 1) || !Range(input.z, -1, 1)
                    || !Finite(forward) || !Upright(__instance.transform.up))
                { active.Stop("Native movement cannot be represented by the movement intent packet."); return; }
                // The native getter includes autorun. _isRunSpeedActive is the previous
                // stamina/speed decision here; it is updated later in FixedUpdate.
                var buttons = (__instance.IsRunInputActive ? PlayerMoveButtons.Sprint : 0)
                    | (__instance._IsCrouchActive_k__BackingField ? PlayerMoveButtons.Crouch : 0);
                float yaw = MathF.Atan2(forward.x, forward.z) * (180f / MathF.PI);
                var view = active._rig.CameraTransform.forward;
                if (!Finite(view)) { active.Stop("Retained view direction is not finite."); return; }
                float pitch = -MathF.Atan2(view.y, MathF.Sqrt(view.x * view.x + view.z * view.z)) * (180f / MathF.PI);
                active._sample = new PlayerMotionInput(active._bridge.Plan.Id, active._bridge.Plan.Generation,
                    active._bridge.NetworkId, ++active._sequence, new Vector2(input.x, input.z), pitch, yaw,
                    buttons, active._fixedDeltaTime)
                { MovementTransitionSequence = active._modeSequence };
                active._fixedFrame = __instance._currentFixedFrame;
                active._sampled = true;
            }
            catch (Exception error) { active.Stop("Native input capture failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterFixed(FirstPersonCharacter __instance, bool __runOriginal)
        {
            var active = For(__instance);
            if (active == null || active._transition != null || active._nativeMode != null) return;
            try
            {
                if (!__runOriginal || !active._insideFixed || !active._sampled)
                { active.Stop("Native FPC skipped the captured movement step."); return; }
                // OnJumpInput can run before or within this fixed step. _isJumping also
                // describes falling and cannot identify a newly accepted jump command.
                if (active._jumpPending) active._jumpLatched = true;
                else if (!__instance._isJumping) active._jumpLatched = false;
                // Keep the accepted edge asserted through its native jump lifetime, so a
                // single dropped unreliable packet does not necessarily lose the command.
                if (active._jumpLatched)
                    active._sample = active._sample with
                    { MoveButtons = active._sample.MoveButtons | PlayerMoveButtons.Jump };
                active._sample = active._sample with
                { JumpSequence = active._jumpSequence, RunPressSequence = active._runPressSequence };
                active._jumpPending = false;
                active._insideFixed = false;
                active._awaitingPhysics = true;
            }
            catch (Exception error) { active.Stop("Native post-step capture failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterJumpInput(FirstPersonCharacter __instance, bool __0, bool __runOriginal)
        {
            var active = For(__instance);
            if (active == null || active._transition != null || active._nativeMode != null || !__runOriginal) return;
            try
            {
                // The native true branch applies vertical velocity, records the jump frame
                // and charges stamina. Observe only; never run it twice or charge locally again.
                if (!__0) return;
                if (active._jumpPending || active._awaitingPhysics || !active.Current()
                    || active._jumpSequence == uint.MaxValue)
                { active.Stop("The native jump cannot be paired with one retained physics boundary."); return; }
                active._jumpSequence++;
                active._jumpPending = true;
            }
            catch (Exception error) { active.Stop("Native jump observation failed (" + error.GetType().Name + ")."); }
        }

        private readonly record struct SwimJumpObservation(bool Observed, float PreviousTime);

        private static void BeforeSwimmingJump(FirstPersonCharacter __instance, bool __0,
            bool __runOriginal, out SwimJumpObservation __state)
        {
            __state = default;
            var active = For(__instance);
            if (active == null || active._transition != null || active._nativeMode != null || !__runOriginal || !__0 || !active._swimmingAllowed) return;
            try
            {
                if (!active._insideFixed || !active.Current())
                { active.Stop("Native water-jump intent escaped the captured physics boundary."); return; }
                __state = new(true, __instance._LastJumpOutOfWaterTime_k__BackingField);
            }
            catch (Exception error) { active.Stop("Water-jump observation failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterSwimmingJump(FirstPersonCharacter __instance, bool __runOriginal,
            SwimJumpObservation __state)
        {
            var active = For(__instance);
            if (active == null || active._transition != null || !__runOriginal || !__state.Observed) return;
            try
            {
                float acceptedTime = __instance._LastJumpOutOfWaterTime_k__BackingField;
                if (acceptedTime == __state.PreviousTime) return;
                // In the recognized native method this timestamp is written only on launch,
                // after its contact/cooldown gates. Observing input alone would invent jumps.
                if (!float.IsFinite(acceptedTime) || !__instance._isJumping || !active._insideFixed
                    || !active.Current() || active._jumpPending || active._jumpSequence == uint.MaxValue)
                { active.Stop("Accepted water jump cannot be paired with one retained physics boundary."); return; }
                active._jumpSequence++;
                active._jumpPending = true;
            }
            catch (Exception error) { active.Stop("Water-jump acceptance observation failed (" + error.GetType().Name + ")."); }
        }

        private IEnumerator ObservePhysics()
        {
            var wait = new WaitForFixedUpdate();
            while (!_disposed)
            {
                yield return wait;
                if (_disposed) yield break;
                _insideCoroutine = true;
                try { CompletePhysics(); }
                finally { _insideCoroutine = false; }
            }
        }

        private void CompletePhysics()
        {
            try
            {
                if (SourceMovementHeld) return;
                if (HoldLifeMovement() || CartMigrationHeld) return;
                if (_transition != null) return;
                if (_nativeMode != null) { CompleteNativePhysics(); return; }
                if (!Current(true) || !CheckMode()) { Stop("Retained movement mode, topology or lifetime ended after physics."); return; }
                if (!_awaitingPhysics) return;
                if (_insideFixed || Time.fixedTimeAsDouble != _fixedTime || Time.fixedDeltaTime != _fixedDeltaTime
                    || _rig.Character._currentFixedFrame != _fixedFrame)
                { Stop("The post-physics sample no longer belongs to the captured input."); return; }
                _awaitingPhysics = false;
                // Match the server motor's displacement-per-step velocity metric. The body's
                // instantaneous velocity and native FPC histories are not equivalent to it.
                var position = _rig.Body.position;
                var resolvedVelocity = (position - _stepPosition) / _fixedDeltaTime;
                var flags = _rig.ReadMotionFlags();
                if (!_history.TryRecordPrediction(_sample.Sequence, position, resolvedVelocity, flags))
                { Stop("The resolved prediction boundary could not be retained."); return; }
                FlushInput();
                if (_disposed || HoldLifeMovement()) return;
                // WindowFull is a pre-send refusal: replace only that unsent continuous
                // intent with the latest sample. Catch-up physics after a render hitch can
                // exceed the 50 Hz transport budget without ending the player's bridge.
                // History keeps every sample; only an actually sent sequence can be ACKed.
                _unsent = _sample;
                FlushInput();
                if (!_disposed && !HoldLifeMovement() && _bridge.TryTakeState(out var state)) Reconcile(state);
            }
            catch (Exception error) { Stop("Post-physics reconciliation failed (" + error.GetType().Name + ")."); }
        }

        private void FlushInput()
        {
            if (_disposed || HoldLifeMovement() || _transition != null || _unsent.Sequence == 0) return;
            if (!_history.ContainsPrediction(_unsent.Sequence)) { _unsent = default; return; }
            var sent = _bridge.SendInput(_unsent);
            if (sent == DatagramSendResult.Queued) _unsent = default;
            else if (sent != DatagramSendResult.WindowFull)
                Stop("Client intent publication failed (" + sent + "); it will not be replayed.");
        }

        private static PlayerClientMovement For(FirstPersonCharacter character)
        {
            var active = _active;
            return active != null && !active._disposed && Environment.CurrentManagedThreadId == _hookThread
                && Same(character, active._rig.Character) && !active.SourceMovementHeld && !active.HoldLifeMovement() ? active : null;
        }

        private bool RetainedTargetCurrent(bool validateTopology = false) => !_disposed && !LifeMovementBlocked
            && Environment.CurrentManagedThreadId == _thread && !_bridge.IsDisposed
            && _bridge.TryReadCurrentTarget(out var entity) && _rig.Matches(entity, validateTopology, true);

        private bool Current(bool validateTopology = false) => !_disposed && _activated && Environment.CurrentManagedThreadId == _thread
            && _bridge.IsGameplayActive && (_life == null || (!_life.IsDisposed && _life.FailureReason == null))
            && (SourceMovementHeld ? SourceHoldCurrent(validateTopology)
                : _life?.BlocksMovement == true ? LifeCurrent()
                : _transition != null ? TransitionCurrent(validateTopology)
                : _bridge.IsCommittedMovementMode(_nativeMode?.Key ?? NativeModeKey.Ground(_modeSequence))
                    && _bridge.TryReadCurrentTarget(out var entity)
                    && _rig.Matches(entity, validateTopology, _nativeMode != null)
                    && (_nativeMode == null || _nativeMode.Matches(_rig)));

        private bool SupportedMode() => !LifeMovementBlocked
            && (_rig.GroundMode() || (_swimmingAllowed && _rig.SwimMode()));
        // The adapter's callback can retire or rebind state, so the binding is checked again after it.
        private bool CheckMode() => Current() && SupportedMode() && _groundModeAllowed() && Current() && SupportedMode();

        private void OnBridgeRetired() => Stop("The completed player bridge was retired.");
        private void OnCoroutineCompleted(bool completed, Exception error)
        {
            _coroutine = null;
            if (!_disposed) Stop("Post-physics observation ended" + (error == null ? "." : " (" + error.GetType().Name + ")."));
        }

        private void Stop(string reason) { StopReason ??= reason; Dispose(); }

        /// <summary>Releases observation and owned replica suppression; never changes the original rig.</summary>
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose retained movement on its game thread.");
            if (_disposed) return;
            _sourceHold?.Retire("Retained movement retired; source hold writes were not released.");
            _disposed = true;
            _life = null;
            _lifeHeld = false;
            _water = null;
            _waterCameraRotator = _waterMainRotator = null;
            DisposeZipline();
            _bridge.Retired -= OnBridgeRetired;
            if (ReferenceEquals(_active, this)) _active = null;
            if (ReferenceEquals(_prepared, this)) _prepared = null;
            _activated = false;
            _transition = null;
            _deferredNativeExit = null;
            _resolveGliderAnchor = null;
            _history.Invalidate();
            _nativeMode = null;
            _nativeUnsent = default;
            _nativeState = default;
            _sample = _unsent = default;
            _insideFixed = _awaitingPhysics = false;
            _jumpPending = false;
            _jumpLatched = false;
            var presentation = _presentation;
            _presentation = null;
            string cleanup;
            try { cleanup = presentation?.Release(); }
            catch (Exception error) { cleanup = "Replica presentation cleanup unconfirmed (" + error.GetType().Name + ")."; }
            if (cleanup != null) StopReason = (StopReason == null ? "" : StopReason + " ") + cleanup;
            var token = _coroutine;
            _coroutine = null;
            if (token != null && !_insideCoroutine)
                try { global::Modstoke.Loader.Coroutines.StopOwned(token); }
                catch (Exception error) { StopReason ??= "Observation cleanup unconfirmed (" + error.GetType().Name + ")."; }
        }

        private static bool Range(float value, float min, float max) => float.IsFinite(value) && value >= min && value <= max;
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Upright(Vector3 v) => MathF.Abs(v.x) < 0.0001f
            && MathF.Abs(v.y - 1) < 0.0001f && MathF.Abs(v.z) < 0.0001f;
    }
}
