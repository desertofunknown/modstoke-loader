// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using global::Sons.Gameplay;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Animations;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        public enum NativeMode : byte
        {
            Ground = 0, HangGlider = 1, GolfCartDriver = 2, GolfCartPassenger = 3,
            RopeClimb = 4, WallClimb = 5, CraneClimb = 6,
            ZiplineToAnchorA = 7, ZiplineToAnchorB = 8
        }

        private static bool IsCartMode(NativeMode mode) => mode is NativeMode.GolfCartDriver or NativeMode.GolfCartPassenger;
        private static bool IsClimbMode(NativeMode mode) => mode is NativeMode.RopeClimb or NativeMode.WallClimb or NativeMode.CraneClimb;
        internal static bool IsZiplineMode(NativeMode mode) => mode is NativeMode.ZiplineToAnchorA or NativeMode.ZiplineToAnchorB;

        /// <summary>
        /// Identity from the aggregate's committed, ordered enter/exit stream. A glider has
        /// no networked guide body: use the action's validated object ID as AnchorNetworkId.
        /// Golf carts use their attached Bolt ID and exact seat index. Never derive this
        /// identity from proximity, a sampled pose or a continuous-input button edge.
        /// </summary>
        public readonly record struct NativeModeKey(NativeMode Mode, uint TransitionSequence,
            ulong AnchorNetworkId, int SeatIndex)
        {
            public static NativeModeKey Ground(uint transitionSequence = 0)
                => new(NativeMode.Ground, transitionSequence, 0, -1);
        }

        /// <summary>
        /// Continuous native controls only. Glider Movement is (_currentInput.x, .y),
        /// not camera look; carts use GolfCartPlayerState.MovementInput. Boost and brake
        /// are independent levels. Enter/exit and other one-shot actions are NOT carried
        /// here and must remain in the aggregate's reliable ordered action stream.
        /// SeatSettled is a level, not an edge: withhold seat pose ACKs while false.
        /// Cart/climb Input.DeltaTime is zero: these are native control levels, not a
        /// simulation timestep. Climb has no buttons, boost, brake or settled-seat flag.
        /// </summary>
        public readonly record struct NativeInput(NativeModeKey Key, PlayerMotionInput Input,
            bool CartBoost, bool CartHandBrake, bool SeatSettled);

        /// <summary>
        /// Glider motion is the guide rigidbody's world pose and instantaneous velocity
        /// after physics, before LateUpdate damping. Cart motion is the settled player's
        /// pose in cart-local coordinates with zero relative velocity; the cart's world
        /// pose is reconciled by its own motor, never by this player acknowledgement.
        /// Climb motion is the retained root's native world pose and body velocity.
        /// Flags must be None; the bridge carries the exact key in its native-state packet.
        /// </summary>
        public readonly record struct NativeState(NativeModeKey Key, PlayerMotionState Motion);

        private NativeBinding _nativeMode;
        private NativeInput _nativeSample, _nativeUnsent;
        private NativeState _nativeState;
        private readonly NativePrediction[] _nativePredictions = new NativePrediction[PlayerMotionHistory.Capacity];
        private uint _modeSequence, _groundInputFloor, _lastNativeState, _lastNativeAck;
        private int _nativeHead, _nativeCount;
        private bool _sendingNative;
        private static bool _nativeModesInstalled;

        /// <summary>
        /// Call immediately after the original entry has completed AND the aggregate has
        /// committed its validated enter transition. Does not enter gliding, register
        /// inputs, enable an authority capability or replace the original guide body.
        /// The bridge must already have selected this exact committed key.
        /// </summary>
        public bool TryBeginHangGlider(NativeModeKey key, PlayerHangGliderAction action,
            out string detail)
        {
            detail = "A committed glider identity and the original live native guide are required.";
            try
            {
                if (!CanBindNative(key) || key.Mode != NativeMode.HangGlider || key.SeatIndex != -1) return false;
                if (!Alive(action) || !action.enabled || !action._isGliding
                    || !action.transform.IsChildOf(_rig.Root.transform) || !Alive(action._rigidbody)
                    || Same(action._rigidbody, _rig.Body) || action._rigidbody.isKinematic
                    || !Alive(action._guideTransform) || !Same(action._rigidbody.transform, action._guideTransform)
                    || !Alive(action._hangGliderTransform)) return false;
                return BindNative(new NativeBinding(key, _rig, action), out detail);
            }
            catch (Exception error) { detail = "Glider binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Bind after the native seat has initialized and the aggregate has committed
        /// the exact cart/seat transition. Native driver LateUpdate and
        /// passenger LastUpdate remain the sole player pose/camera-frame writers.
        /// Continuous controls are observed during entry interpolation, but seat pose
        /// ACKs are accepted only for samples marked SeatSettled.
        /// </summary>
        public bool TryBeginGolfCartSeat(NativeModeKey key, PlayerGolfCartAction action,
            GolfCartController cart, out string detail)
        {
            detail = "A committed local cart seat, original action and native locator are required.";
            try
            {
                if (!CanBindNative(key) || key.SeatIndex < 0
                    || (key.Mode != NativeMode.GolfCartDriver && key.Mode != NativeMode.GolfCartPassenger)) return false;
                if (!Alive(action) || !Alive(cart) || !Alive(cart.entity) || !cart.entity.isAttached
                    || cart.entity.networkId.PackedValue != key.AnchorNetworkId
                    || !action.transform.IsChildOf(_rig.Root.transform)
                    || !Same(LocalPlayer.Transform, _rig.Root.transform)) return false;
                bool driver = action.TryCast<PlayerGolfCartDriverAction>() != null;
                bool passenger = action.TryCast<PlayerGolfCartPassengerAction>() != null;
                if (driver == passenger || driver != (key.Mode == NativeMode.GolfCartDriver)
                    || driver != (key.SeatIndex == cart.GetDriverSlotIndex())) return false;
                return BindNative(new NativeBinding(key, _rig, action, cart), out detail);
            }
            catch (Exception error) { detail = "Seat binding failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool CanBindNative(NativeModeKey key)
            => !CartMigrationHeld && _nativeModesInstalled && _activated && Environment.CurrentManagedThreadId == _thread && !_disposed && !_sendingNative
                && _nativeMode == null && !_insideFixed && !_awaitingPhysics && !Time.inFixedTimeStep
                && key.TransitionSequence > _modeSequence && key.AnchorNetworkId != 0
                && _bridge.IsGameplayActive && _bridge.IsCommittedMovementMode(key)
                && _bridge.TryReadCurrentTarget(out var entity) && _rig.Matches(entity, true, true);

        private bool BindNative(NativeBinding binding, out string detail)
        {
            detail = "The native action's exact identity, physics layout or seat changed.";
            if (!binding.Matches(_rig) || !_bridge.IsCommittedMovementMode(binding.Key)) return false;
            ResetModeHistory(binding.Key.TransitionSequence);
            if (_disposed || !_bridge.IsCommittedMovementMode(binding.Key)) return false;
            _modeSequence = binding.Key.TransitionSequence;
            _nativeMode = binding;
            detail = "Original native controls retained; committed-mode bridge transport and reconciliation bound.";
            return true;
        }

        /// <summary>
        /// Call after the reliable exit is committed and the original action has restored
        /// the ground rig. Does not invoke exit twice or restore captured camera/controls.
        /// All pre-exit ground/native ACKs are excluded from the new prediction history.
        /// </summary>
        public bool TryResumeGround(uint committedExitSequence, out string detail)
        {
            detail = "Commit the ordered exit and let the original action restore the ground rig first.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || CartMigrationHeld
                || (_nativeMode == null && !HasCommittedNativeExit(committedExitSequence))
                || _sendingNative || _insideFixed || _awaitingPhysics || Time.inFixedTimeStep
                || committedExitSequence <= _modeSequence || !_bridge.IsGameplayActive
                || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(committedExitSequence))) return false;
            try
            {
                if (!_bridge.TryReadCurrentTarget(out var entity) || !_rig.Matches(entity, true)
                    || !SupportedMode() || !_groundModeAllowed()
                    || !_bridge.TryReadCurrentTarget(out entity) || !_rig.Matches(entity, true) || !SupportedMode()
                    || _disposed || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(committedExitSequence))) return false;
                ResetModeHistory(committedExitSequence);
                if (_disposed || !_bridge.IsCommittedMovementMode(NativeModeKey.Ground(committedExitSequence))) return false;
                _nativeMode = null;
                _modeSequence = committedExitSequence;
                detail = "Original ground prediction resumed with a fresh history and pre-exit ACK cutoff.";
                return true;
            }
            catch (Exception error) { detail = "Ground resumption failed (" + error.GetType().Name + ")."; return false; }
        }

        private void ResetModeHistory(uint transitionSequence)
        {
            if (!_history.TryBeginMode(transitionSequence))
            { Stop("A pending correction or retired history prevents the movement transition."); return; }
            ClearPredictionEpoch();
        }

        private void ClearPredictionEpoch()
        {
            if (!_history.TryClearPredictions())
            { Stop("A pending correction or retired history prevents resetting predictions."); return; }
            _groundInputFloor = _sequence;
            _sample = _unsent = default;
            _nativeSample = _nativeUnsent = default;
            _nativeState = default;
            Array.Clear(_nativePredictions);
            _nativeHead = _nativeCount = 0;
            _lastNativeState = _lastNativeAck = 0;
            _jumpPending = _jumpLatched = false;
            _fixedTime = double.NaN;
            _bridge.TryTakeState(out _);
            _bridge.TryTakeNativeState(out _);
        }

        /// <summary>
        /// Pull only from the authenticated bridge inbox, never a public state injection
        /// callback. Native writes still occur only at the original pose boundary.
        /// </summary>
        private void ReceiveNativeState()
        {
            if (_disposed || _transition != null || !_bridge.IsGameplayActive || !_bridge.TryTakeNativeState(out var state)) return;
            if (!TryQueueNativeState(state, out var detail)) Stop(detail);
        }

        private bool TryQueueNativeState(NativeState state, out string detail)
        {
            detail = "State must acknowledge an actually sent input of this exact committed native mode.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _sendingNative
                || !_bridge.IsGameplayActive || _nativeMode == null || state.Key != _nativeMode.Key
                || !_bridge.IsCommittedMovementMode(state.Key)) return false;
            var motion = state.Motion;
            if (motion.InputSequence <= _groundInputFloor) return true;
            Span<byte> wireValidation = stackalloc byte[PlayerBridgeProtocol.NativeStateLength];
            if (motion.MigrationId != _bridge.Plan.Id || motion.Generation != _bridge.Plan.Generation
                || motion.NetworkId != _bridge.NetworkId || motion.Flags != PlayerMotionFlags.None
                || !PlayerBridgeProtocol.TryWriteNativeState(state, wireValidation, out _)
                || motion.Sequence <= _lastNativeState || motion.InputSequence <= _lastNativeAck
                || (_nativeState.Motion.Sequence != 0 && (motion.Sequence <= _nativeState.Motion.Sequence
                    || motion.InputSequence < _nativeState.Motion.InputSequence))) return false;
            int index = FindNativePrediction(motion.InputSequence);
            if (index < 0 || !_nativePredictions[index].Sent
                || (IsCartMode(state.Key.Mode) && !_nativePredictions[index].SeatSettled)) return false;
            _nativeState = state;
            detail = "Correlated native state queued for its original pose boundary.";
            return true;
        }

        private static void InstallNativeModes(HarmonyLib.Harmony harmony)
        {
            var glider = AccessTools.DeclaredMethod(typeof(PlayerHangGliderAction), nameof(PlayerHangGliderAction.FixedUpdate), Type.EmptyTypes);
            var driver = AccessTools.DeclaredMethod(typeof(PlayerGolfCartDriverAction), nameof(PlayerGolfCartDriverAction.LateUpdate), Type.EmptyTypes);
            var passenger = AccessTools.DeclaredMethod(typeof(PlayerGolfCartPassengerAction), nameof(PlayerGolfCartPassengerAction.LastUpdate), Type.EmptyTypes);
            foreach (var method in new[] { glider, driver, passenger })
                if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                    throw new MissingMethodException("Recognized native movement boundaries are unavailable.");
            harmony.Patch(glider,
                prefix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(BeforeGliderFixed)) { priority = Priority.Last },
                postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterGliderFixed)) { priority = Priority.Last });
            harmony.Patch(driver, postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterDriverSeat)) { priority = Priority.Last });
            harmony.Patch(passenger, postfix: new HarmonyMethod(typeof(PlayerClientMovement), nameof(AfterPassengerSeat)) { priority = Priority.Last });
            InstallNativeActionTransitions(harmony);
            InstallNativeClimb(harmony);
            InstallZiplineActions(harmony);
            _nativeModesInstalled = true;
        }

        private static PlayerClientMovement ForNative(Behaviour action)
        {
            var active = _active;
            return active != null && !active._disposed && !active.CartMigrationHeld && Environment.CurrentManagedThreadId == _hookThread
                && active._transition == null && active._nativeMode != null && Same(active._nativeMode.Action, action) ? active : null;
        }

        private static void BeforeGliderFixed(PlayerHangGliderAction __instance, bool __runOriginal)
        {
            var active = ForNative(__instance);
            if (active == null) return;
            try
            {
                double now = Time.fixedTimeAsDouble;
                float dt = Time.fixedDeltaTime;
                if (!__runOriginal || !active.Current(true) || !Time.inFixedTimeStep
                    || active._insideFixed || active._awaitingPhysics || !double.IsFinite(now)
                    || (!double.IsNaN(active._fixedTime) && now <= active._fixedTime) || !Range(dt, 0.001f, 0.1f))
                { active.Stop("Native glider fixed-step ordering or binding changed."); return; }
                var controls = __instance._currentInput;
                if (!Finite(controls) || controls.y != controls.z)
                { active.Stop("Native glider controls no longer fit their two-axis contract."); return; }
                active._fixedTime = now;
                active._fixedDeltaTime = dt;
                active._nativeSample = active.CaptureNativeInput(new Vector2(controls.x, controls.y), false, false, false, dt);
                active._insideFixed = true;
            }
            catch (Exception error) { active.Stop("Native glider capture failed (" + error.GetType().Name + ")."); }
        }

        private static void AfterGliderFixed(PlayerHangGliderAction __instance, bool __runOriginal)
        {
            var active = ForNative(__instance);
            if (active == null) return;
            try
            {
                if (!__runOriginal || !active._insideFixed || !active.Current()
                    || Time.fixedTimeAsDouble != active._fixedTime || Time.fixedDeltaTime != active._fixedDeltaTime)
                { active.Stop("Native glider did not complete its captured physics step."); return; }
                active._insideFixed = false;
                active._awaitingPhysics = true;
            }
            catch (Exception error) { active.Stop("Native glider completion failed (" + error.GetType().Name + ")."); }
        }

        private void CompleteNativePhysics()
        {
            if (!Current(true)) { Stop("Native movement binding changed after physics."); return; }
            if (_nativeMode.Climb != null) { CompleteClimbPhysics(); return; }
            if (_nativeMode.Key.Mode != NativeMode.HangGlider || !_awaitingPhysics) return;
            if (_insideFixed || Time.fixedTimeAsDouble != _fixedTime || Time.fixedDeltaTime != _fixedDeltaTime)
            { Stop("The glider prediction no longer belongs to its captured input."); return; }
            _awaitingPhysics = false;
            var body = _nativeMode.MotionBody;
            RecordNativePrediction(_nativeSample, body.position, body.rotation, body.velocity);
            if (!_disposed) ReconcileNativeState();
        }

        private static void AfterDriverSeat(PlayerGolfCartDriverAction __instance, bool __runOriginal)
            => AfterNativeSeat(__instance, __runOriginal);

        private static void AfterPassengerSeat(PlayerGolfCartPassengerAction __instance, bool __runOriginal)
            => AfterNativeSeat(__instance, __runOriginal);

        private static void AfterNativeSeat(PlayerGolfCartAction action, bool ranOriginal)
        {
            var active = ForNative(action);
            if (active == null) return;
            try
            {
                if (!ranOriginal || !active.Current(true))
                { active.Stop("The original seat writer or exact cart binding changed."); return; }
                // Native seat writers run once per render frame, not in FPC.FixedUpdate.
                // Vehicle simulation owns its timestep; this input is a continuous level sample.
                if (active._nativeMode.LastSeatFrame == Time.frameCount) return;
                active._nativeMode.LastSeatFrame = Time.frameCount;
                if (!action._hasSetupInputs) return;
                var seat = action._state;
                var input = active.CaptureNativeInput(seat.MovementInput, seat.BoostInput, seat.HandBrakeInput,
                    active._nativeMode.SeatLerp == 1, 0);
                var frame = active._nativeMode.Cart.transform;
                var root = active._rig.Root.transform;
                active.RecordNativePrediction(input, frame.InverseTransformPoint(root.position),
                    Quaternion.Inverse(frame.rotation) * root.rotation, Vector3.zero);
                if (!active._disposed) active.ReconcileNativeState();
            }
            catch (Exception error) { active.Stop("Native seat capture failed (" + error.GetType().Name + ")."); }
        }

        private NativeInput CaptureNativeInput(Vector2 controls, bool boost, bool brake, bool seatSettled, float dt)
        {
            var view = _rig.CameraTransform.forward;
            if (!Range(controls.x, -1, 1) || !Range(controls.y, -1, 1) || !Finite(view)
                || !Range(dt, 0, 0.1f) || _sequence == uint.MaxValue)
                throw new InvalidOperationException("Native input cannot be represented without loss.");
            float yaw = MathF.Atan2(view.x, view.z) * (180f / MathF.PI);
            float pitch = -MathF.Atan2(view.y, MathF.Sqrt(view.x * view.x + view.z * view.z)) * (180f / MathF.PI);
            var input = new PlayerMotionInput(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                ++_sequence, controls, pitch, yaw, PlayerMoveButtons.None, dt)
            { JumpSequence = 0, MovementTransitionSequence = _nativeMode.Key.TransitionSequence };
            return new NativeInput(_nativeMode.Key, input, boost, brake, seatSettled);
        }

        private void RecordNativePrediction(NativeInput input, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            if (!Finite(position) || !Finite(velocity) || !UnitRotation(rotation)
                || ((_nativeMode.Key.Mode == NativeMode.HangGlider || IsClimbMode(_nativeMode.Key.Mode))
                    && !_history.TryRecordPrediction(input.Input.Sequence, position, velocity)))
            { Stop("The native prediction boundary could not be retained."); return; }
            int index = (_nativeHead + _nativeCount) % _nativePredictions.Length;
            if (_nativeCount == _nativePredictions.Length)
            {
                index = _nativeHead;
                _nativeHead = (_nativeHead + 1) % _nativePredictions.Length;
            }
            else _nativeCount++;
            _nativePredictions[index] = new NativePrediction(input.Input.Sequence, position, rotation, input.SeatSettled, false);
            FlushNativeInput();
            if (_disposed) return;
            // Only unsent continuous levels coalesce. Transition identity is repeated in
            // every sample; the durable transition itself belongs to the action stream.
            _nativeUnsent = input;
            FlushNativeInput();
        }

        private void FlushNativeInput()
        {
            if (_disposed || _transition != null || !_bridge.IsGameplayActive || _nativeUnsent.Input.Sequence == 0 || _sendingNative) return;
            var binding = _nativeMode;
            var input = _nativeUnsent;
            // A prolonged pre-send refusal may outlive history. This is only an unsent
            // continuous sample, so replace it with the next sample, never send an ACK
            // target whose prediction has already been overwritten.
            if (FindNativePrediction(input.Input.Sequence) < 0) { _nativeUnsent = default; return; }
            _sendingNative = true;
            DatagramSendResult sent;
            try { sent = _bridge.SendNativeInput(input); }
            finally { _sendingNative = false; }
            if (_disposed) return;
            if (!ReferenceEquals(_nativeMode, binding) || !Current())
            { Stop("Native mode changed during input publication."); return; }
            if (sent == DatagramSendResult.Queued)
            {
                int index = FindNativePrediction(input.Input.Sequence);
                if (index < 0) { Stop("Sent native input outlived its bounded prediction history."); return; }
                _nativePredictions[index] = _nativePredictions[index] with { Sent = true };
                _nativeUnsent = default;
            }
            else if (sent != DatagramSendResult.WindowFull)
                Stop("Native intent publication failed (" + sent + "); it will not be replayed.");
        }

        private int FindNativePrediction(uint sequence)
        {
            for (int i = 0; i < _nativeCount; i++)
            {
                int index = (_nativeHead + i) % _nativePredictions.Length;
                if (_nativePredictions[index].Sequence == sequence) return index;
            }
            return -1;
        }

        private readonly record struct NativePrediction(uint Sequence, Vector3 Position, Quaternion Rotation, bool SeatSettled, bool Sent);

        private sealed class NativeBinding
        {
            internal readonly NativeModeKey Key;
            internal readonly Behaviour Action;
            internal readonly PlayerHangGliderAction Glider;
            internal readonly PlayerGolfCartAction Seat;
            internal readonly GolfCartController Cart;
            internal readonly ClimbBinding Climb;
            internal readonly ZiplineBinding Zipline;
            internal readonly Rigidbody MotionBody;
            internal readonly Collider[] Colliders;
            internal readonly Collider Obstacle;
            internal readonly ParentConstraint GuideConstraint;
            internal readonly Vector3 GuideTranslationOffset;
            internal int LastSeatFrame = -1;
            private readonly Transform _guide, _glider, _seatEnd;
            private readonly GolfCartPlayerState _seatState;
            private readonly BoltEntity _driverGhost;
            private readonly IGolfCartState _driverGhostState;
            private readonly ulong _driverGhostId;
            private readonly int _seatInitializedFrame;
            private readonly float _seatInitializedTime;
            private readonly RigPhysics _rigPhysics;
            private readonly RigPhysics _guidePhysics;
            private readonly Behaviour _obstacleProxy;
            private readonly bool[] _colliderEnabled, _colliderTrigger;

            internal float SeatLerp => Key.Mode == NativeMode.GolfCartDriver
                ? _seatState.DriverPositionLerpFactor : _seatState.PassengerAPositionLerpFactor;

            internal NativeBinding(NativeModeKey key, RetainedRig rig, PlayerHangGliderAction glider)
                : this(key, rig)
            {
                Action = Glider = glider;
                MotionBody = glider._rigidbody;
                _guide = glider._guideTransform;
                _glider = glider._hangGliderTransform;
                Obstacle = glider._obstacleCollider;
                _obstacleProxy = glider._obstacleTriggerProxy;
                GuideConstraint = glider._parentConstraint;
                GuideTranslationOffset = GuideConstraint.GetTranslationOffset(0);
                _guidePhysics = new RigPhysics(true, MotionBody.isKinematic, MotionBody.detectCollisions,
                    MotionBody.useGravity, MotionBody.constraints, MotionBody.interpolation, MotionBody.collisionDetectionMode);
                var colliders = MotionBody.GetComponentsInChildren<Collider>(true);
                if (colliders.Length > 16) throw new InvalidOperationException("Too many native guide colliders.");
                Colliders = new Collider[colliders.Length];
                for (int i = 0; i < Colliders.Length; i++) Colliders[i] = colliders[i];
            }

            internal NativeBinding(NativeModeKey key, RetainedRig rig, PlayerGolfCartAction seat, GolfCartController cart)
                : this(key, rig)
            {
                Action = Seat = seat;
                Cart = cart;
                _seatState = seat._state;
                _seatInitializedFrame = _seatState.InitializedFrame;
                _seatInitializedTime = _seatState.InitializedTime;
                if (key.Mode == NativeMode.GolfCartDriver)
                {
                    _driverGhost = cart._ownedGolfCartGhostEntity;
                    if (!Alive(_driverGhost) || !_driverGhost.isAttached || !_driverGhost.isOwner
                        || !_driverGhost.TryFindState<IGolfCartState>(out var ghostState))
                        throw new InvalidOperationException("The original local driver ghost is unavailable.");
                    _driverGhostState = ghostState;
                    _driverGhostId = _driverGhost.networkId.PackedValue;
                }
                var ends = cart._playerLocatorEnds;
                if (ends == null || key.SeatIndex >= ends.Length) throw new InvalidOperationException("Seat locator unavailable.");
                _seatEnd = ends[key.SeatIndex];
            }

            internal NativeBinding(NativeModeKey key, RetainedRig rig, ClimbBinding climb)
                : this(key, rig)
            {
                Action = rig.Animation;
                MotionBody = rig.Body;
                Climb = climb;
            }

            internal NativeBinding(NativeModeKey key, RetainedRig rig, ZiplineBinding zipline)
                : this(key, rig)
            {
                Action = zipline.Action;
                MotionBody = zipline.Hook._rigidbody;
                Zipline = zipline;
            }

            private NativeBinding(NativeModeKey key, RetainedRig rig)
            {
                Key = key;
                _rigPhysics = RigPhysics.Read(rig);
                _colliderEnabled = new bool[rig.Colliders.Length];
                _colliderTrigger = new bool[rig.Colliders.Length];
                for (int i = 0; i < rig.Colliders.Length; i++)
                { _colliderEnabled[i] = rig.Colliders[i].enabled; _colliderTrigger[i] = rig.Colliders[i].isTrigger; }
            }

            internal bool Matches(RetainedRig rig)
            {
                if (!Alive(Action) || !Action.enabled || !Action.gameObject.activeInHierarchy
                    || !Action.transform.IsChildOf(rig.Root.transform) || _rigPhysics != RigPhysics.Read(rig)) return false;
                for (int i = 0; i < rig.Colliders.Length; i++)
                    if (!Alive(rig.Colliders[i]) || rig.Colliders[i].enabled != _colliderEnabled[i]
                        || rig.Colliders[i].isTrigger != _colliderTrigger[i]) return false;
                if (Climb != null) return Climb.TryRead(rig, Key, out _);
                if (Zipline != null) return Zipline.Matches(rig, Key);
                if (Glider != null)
                    return Glider._isGliding && !Glider._hasHitObstacle && Alive(_glider) && Alive(_guide)
                        && Same(Glider._hangGliderTransform, _glider) && Same(Glider._guideTransform, _guide)
                        && Same(Glider._rigidbody, MotionBody) && Alive(MotionBody) && !MotionBody.isKinematic
                        && Same(MotionBody.transform, _guide) && _guide.parent == null && UnitScale(_guide.lossyScale)
                        && MotionBody.gameObject.scene.handle == rig.Root.scene.handle
                        && _guidePhysics == new RigPhysics(true, MotionBody.isKinematic, MotionBody.detectCollisions,
                            MotionBody.useGravity, MotionBody.constraints, MotionBody.interpolation, MotionBody.collisionDetectionMode)
                        && Alive(Obstacle) && Same(Glider._obstacleCollider, Obstacle) && Obstacle.enabled
                        && Obstacle.gameObject.activeInHierarchy && Obstacle.transform.IsChildOf(rig.Root.transform)
                        && Alive(_obstacleProxy) && Same(Glider._obstacleTriggerProxy, _obstacleProxy) && _obstacleProxy.enabled
                        && Alive(GuideConstraint) && Same(Glider._parentConstraint, GuideConstraint)
                        && Same(GuideConstraint.transform, rig.Root.transform) && GuideConstraint.enabled && GuideConstraint.constraintActive
                        && GuideConstraint.weight == 1 && GuideConstraint.sourceCount == 1
                        && Same(GuideConstraint.GetSource(0).sourceTransform, _guide) && GuideConstraint.GetSource(0).weight == 1
                        && GuideConstraint.translationAxis == (Axis.X | Axis.Y | Axis.Z)
                        && Finite(GuideTranslationOffset) && GuideTranslationOffset.sqrMagnitude <= 100
                        && Exact(GuideConstraint.GetTranslationOffset(0), GuideTranslationOffset);
                if (!Alive(Seat) || !Alive(Cart) || !Alive(Cart.entity) || !Cart.entity.isAttached
                    || Cart.entity.networkId.PackedValue != Key.AnchorNetworkId || !Same(Seat._state, _seatState)
                    || !Live(_seatState) || !_seatState.IsConnected || !Alive(_seatState.Definition)
                    || !Same(_seatState.ConnectedGolfCartController, Cart) || _seatState.ConnectedSlotIndex != Key.SeatIndex
                    || _seatState.InitializedFrame != _seatInitializedFrame || _seatState.InitializedTime != _seatInitializedTime
                    || !Alive(_seatEnd) || !_seatEnd.IsChildOf(Cart.transform)
                    || !UnitScale(Cart.transform.lossyScale)) return false;
                var ends = Cart._playerLocatorEnds;
                var actions = Cart._localConnectedActions;
                var slots = Cart._isLocalConnectedSlots;
                return Range(SeatLerp, 0, 1) && ends != null && Key.SeatIndex < ends.Length && Same(ends[Key.SeatIndex], _seatEnd)
                    && (Key.Mode == NativeMode.GolfCartDriver ? Same(Cart._localPlayerDriverAction, Seat)
                        : actions != null && Key.SeatIndex < actions.Length && Same(actions[Key.SeatIndex], Seat))
                    && slots != null && Key.SeatIndex < slots.Length && slots[Key.SeatIndex]
                    && (Key.Mode == NativeMode.GolfCartDriver ? DriverGhostMatches() : Same(Cart.GetConnectedSlot(Key.SeatIndex), LocalPlayer.Entity));
            }

            private bool DriverGhostMatches() => Alive(_driverGhost) && _driverGhost.isAttached && _driverGhost.isOwner
                && _driverGhost.networkId.PackedValue == _driverGhostId
                && Same(Cart._ownedGolfCartGhostEntity, _driverGhost) && Same(Cart.GetConnectedSlot(Key.SeatIndex), _driverGhost)
                && Live(_driverGhostState) && Same(_driverGhost.GetState<IGolfCartState>(), _driverGhostState)
                && _driverGhostState.ConnectedSlots.GetValueNetworkId(0).PackedValue == LocalPlayer.Entity.networkId.PackedValue;
        }

        private readonly record struct RigPhysics(bool CharacterEnabled, bool Kinematic, bool Collisions,
            bool Gravity, RigidbodyConstraints Constraints, RigidbodyInterpolation Interpolation, CollisionDetectionMode CollisionMode)
        {
            internal static RigPhysics Read(RetainedRig rig) => new(rig.Character.enabled, rig.Body.isKinematic,
                rig.Body.detectCollisions, rig.Body.useGravity, rig.Body.constraints, rig.Body.interpolation, rig.Body.collisionDetectionMode);
        }

        private static bool UnitRotation(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w)
            && MathF.Abs(value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w - 1) <= 0.001f;
    }
}
