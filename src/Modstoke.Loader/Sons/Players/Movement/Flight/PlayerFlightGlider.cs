// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using global::Sons.Gameplay;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public readonly record struct PlayerFlightGliderState(
        float Heading, float ForwardMomentum, float Pitch, float Tilt);

    /// <summary>
    /// Actor-local dynamics for an already admitted glide. This does not enter/exit the native
    /// player action, approve held-item custody, create its driver/guide, or migrate its callbacks.
    /// The aggregate must suspend competing writers and route obstacle/drop effects to the
    /// original controlling client. Never call native BeginGliding/StopGliding on a remote actor.
    /// </summary>
    public sealed partial class PlayerFlightGlider : IDisposable
    {
        private static readonly Dictionary<IntPtr, PlayerFlightGlider> Active = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly BoltEntity _entity;
        private readonly Rigidbody _body, _riderBody;
        private readonly Transform _root, _guide;
        private readonly HangGliderSettings _settings;
        private readonly Func<bool> _hasExclusiveAuthority;
        private readonly int _entityId, _bodyId, _riderBodyId, _guideId, _scene;
        private readonly Vector3 _guideOffset;
        private readonly Quaternion _guideRotation;
        private PlayerFlightGliderState _state;
        private double _fixedTime = double.NaN;
        private int _inputFrame = -1, _lateFrame = -1;
        private bool _disposed;

        public PlayerFlightGliderState State => _state;
        public bool ExitRequired { get; private set; }
        public bool DropRequired => _dropRequired;
        public bool IsDisposed => _disposed;
        public string StopReason { get; private set; }
        public NativeModeKey Key { get; }

        private PlayerFlightGlider(PlayerBridge bridge, NativeModeKey key, BoltEntity entity,
            Rigidbody body, Rigidbody riderBody, Transform guide, HangGliderSettings settings,
            PlayerFlightGliderState state, Func<bool> hasExclusiveAuthority)
        {
            _bridge = bridge; _entity = entity; _root = entity.transform; _body = body;
            Key = key; _riderBody = riderBody; _riderBodyId = riderBody.GetInstanceID();
            _guide = guide; _settings = settings; _state = state;
            _hasExclusiveAuthority = hasExclusiveAuthority;
            _entityId = entity.GetInstanceID(); _bodyId = body.GetInstanceID();
            _guideId = guide.GetInstanceID(); _scene = entity.gameObject.scene.handle;
            _guideOffset = guide.InverseTransformPoint(_root.position);
            _guideRotation = Quaternion.Inverse(guide.rotation) * _root.rotation;
        }

        /// <summary>
        /// Adopt the separate server guide body and held carrier after the aggregate has
        /// approved the original glide state and server settings. The guide must be retained
        /// for that actor. No initial uplift is replayed for an already active glide.
        /// The guide must already have the approved native driver physics profile, not ground
        /// constraints. hasExclusiveAuthority must remain true only while this mode owns it.
        /// </summary>
        public static bool TryAdopt(PlayerBridge bridge, NativeModeKey key, Rigidbody body, Transform guide,
            HangGliderSettings settings, PlayerFlightGliderState state,
            Func<bool> hasExclusiveAuthority, out PlayerFlightGlider motor, out string detail)
        {
            motor = null;
            detail = "An admitted glide, actor-bound driver/guide and exclusive server movement authority are required.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true
                || hasExclusiveAuthority == null || !Finite(state) || key.Mode != NativeMode.HangGlider
                || key.TransitionSequence == 0 || key.AnchorNetworkId == 0 || key.SeatIndex != -1) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var entity) || Time.inFixedTimeStep || !Alive(entity) || !Alive(body)
                    || !Alive(guide) || !Alive(settings) || Active.ContainsKey(entity.Pointer)
                    || !bridge.IsCommittedMovementMode(key) || !Same(body.transform, guide)
                    || Same(body.gameObject, entity.gameObject) || guide.parent != null
                    || !Valid(settings) || MathF.Abs(state.Pitch) > settings.MinMaxPitch
                    || MathF.Abs(state.Tilt) > settings.MinMaxTilt) return false;
                var riderBody = entity.GetComponent<Rigidbody>();
                if (!Alive(riderBody) || !riderBody.isKinematic) return false;
                var candidate = new PlayerFlightGlider(bridge, key, entity, body, riderBody, guide,
                    settings, state, hasExclusiveAuthority);
                if (!candidate.Current()) return false;
                Active.Add(entity.Pointer, candidate);
                bridge.Retired += candidate.Dispose;
                if (!candidate.BindDispatch(out detail)) return false;
                motor = candidate;
                detail = "Existing glide dynamics adopted without local-player actions, audio or entry impulse.";
                return true;
            }
            catch (Exception error)
            { detail = "Glider adoption failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Once per ordinary Update, with authenticated Horizontal/Vertical intent. Preserve the
        /// native Update/FixedUpdate/LateUpdate phases; do not collapse damping into a fixed tick.
        /// </summary>
        private bool TryUpdateInput(Vector2 move, out string detail)
        {
            detail = "Glider input requires the current Update phase.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _inputFrame == Time.frameCount || _lateFrame == Time.frameCount || ExitRequired) return false;
            try
            {
                if (!Current()) return Stop("Glider actor or exclusive writer binding changed.", out detail);
                if (_awaitingPhysics) return false;
                float dt = Time.deltaTime;
                if (!Delta(dt) || !float.IsFinite(move.x) || !float.IsFinite(move.y)
                    || MathF.Abs(move.x) > 1 || MathF.Abs(move.y) > 1) return false;
                // PlayerHangGliderAction.UpdateInput: Vertical -> pitch, Horizontal -> heading and tilt.
                var next = new PlayerFlightGliderState(
                    _state.Heading + move.x * dt * _settings.HeadingInputRate, _state.ForwardMomentum,
                    Mathf.Clamp(_state.Pitch + move.y * dt * _settings.PitchInputRate,
                        -_settings.MinMaxPitch, _settings.MinMaxPitch),
                    Mathf.Clamp(_state.Tilt + move.x * dt * _settings.TiltInputRate,
                        -_settings.MinMaxTilt, _settings.MinMaxTilt));
                if (!Finite(next)) return Stop("Glider input produced non-finite state.", out detail);
                _state = next; _inputFrame = Time.frameCount;
                detail = "Authenticated glide input integrated.";
                return true;
            }
            catch (Exception error) { return Stop("Glider input failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>
        /// Once per FixedUpdate. Applies native acceleration, then checks the guide's ground
        /// clearance. ExitRequired is latched; the aggregate must finish the drop/exit transaction
        /// before another mode runs. Publish authoritative pose/velocity only after physics.
        /// </summary>
        public bool TryFixedUpdate(out string detail)
        {
            detail = "Glider forces require a new native fixed step.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || !Time.inFixedTimeStep) return false;
            try
            {
                if (!Current()) return Stop("Glider actor or exclusive writer binding changed.", out detail);
                double now = Time.fixedTimeAsDouble;
                float dt = Time.deltaTime;
                if (!double.IsFinite(now) || !Delta(dt) || now == _fixedTime) return false;
                if (!double.IsNaN(_fixedTime) && now < _fixedTime)
                    return Stop("Glider fixed-step time moved backwards.", out detail);
                if (ExitRequired && _inputSequence == 0)
                {
                    _fixedTime = now;
                    _awaitingPhysics = true;
                    detail = "Glider exit is pending; retain coasting physics without accepting more control.";
                    return true;
                }
                if (_inputSequence != 0 && (!_bridge.TryPeekCurrentNativeInput(out var current)
                    || current.Key != Key || current.Input.Sequence < _inputSequence))
                    return Stop("Glider selected input expired before simulation completed.", out detail);
                Vector3 forward = _guide.forward;
                float factor = _settings.ShapedPitchFactorCurve.Evaluate(_state.Pitch / _settings.MinMaxPitch);
                float momentum = _state.ForwardMomentum + (factor > 0 ? dt * _settings.BaseDownPitchForce * factor : 0);
                float down = dt * _settings.ConstantDownForce
                    + (factor < 0 ? dt * _settings.UpPitchDownForce * -factor : 0);
                Vector3 force = forward * momentum + Vector3.down * down
                    + new Vector3(forward.x, 0, forward.z) * _settings.ConstantForwardForce;
                if (!float.IsFinite(factor) || !float.IsFinite(momentum) || !Finite(force))
                    return Stop("Glider settings produced non-finite forces.", out detail);
                _fixedTime = now;
                // UpdateGliding uses Acceleration, even though its vertical terms already contain dt.
                _body.AddForce(force, ForceMode.Acceleration);
                _awaitingPhysics = true;
                _state = _state with { ForwardMomentum = momentum };
                if (Physics.Raycast(_guide.position, Vector3.down, out RaycastHit hit,
                    _settings.HeightFromGroundDisconnect, _settings.ObstacleLayers, QueryTriggerInteraction.Ignore))
                    if (Alive(hit.collider)) { ExitRequired = true; _dropRequired = true; }
                detail = ExitRequired ? "Glider ground contact requires the actor's drop/exit transaction."
                    : "Native glide acceleration applied; physics completion is still pending.";
                return true;
            }
            catch (Exception error) { return Stop("Glider fixed step failed (" + error.GetType().Name + ").", out detail); }
        }

        /// <summary>Route only this actor's server obstacle-trigger callback, never a client hit assertion.</summary>
        public bool TryReportObstacle(Collider obstacle, out string detail)
        {
            detail = "A current server obstacle contact is required.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                if (!Current()) return Stop("Glider obstacle binding changed.", out detail);
                if (!Alive(obstacle) || obstacle.gameObject.scene.handle != _scene
                    || (_settings.ObstacleLayers.value & (1 << obstacle.gameObject.layer)) == 0) return false;
                ExitRequired = true;
                _dropRequired = true;
                detail = "Glider obstacle latched; actor inventory/drop and presentation effects remain adapter-owned.";
                return true;
            }
            catch (Exception error) { return Stop("Glider obstacle failed (" + error.GetType().Name + ").", out detail); }
        }

        public bool TryLateUpdate(out string detail)
        {
            detail = "Glider rotation/damping requires a new LateUpdate.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _lateFrame == Time.frameCount || _inputFrame != Time.frameCount || ExitRequired || _awaitingPhysics) return false;
            try
            {
                if (!Current()) return Stop("Glider actor or exclusive writer binding changed.", out detail);
                float dt = Time.deltaTime;
                if (!Delta(dt)) return false;
                Quaternion rotation = Quaternion.Euler(_state.Pitch,
                    _body.transform.eulerAngles.y + _state.Heading, -_state.Tilt * _settings.TiltRotationScale);
                Vector3 velocity = _body.velocity;
                // Native FadeValue is linear at 60 Hz, not exponential and not Clamp01/Lerp.
                velocity -= velocity * (1 - _settings.VelocityFade) * (dt * 60);
                if (velocity.sqrMagnitude < 0.0001f) velocity = Vector3.zero;
                var next = new PlayerFlightGliderState(
                    Fade(_state.Heading, _settings.HeadingFade, dt),
                    Fade(_state.ForwardMomentum, _settings.ForwardMomentumFade, dt), _state.Pitch,
                    Fade(_state.Tilt, _settings.TiltFade, dt));
                if (!Finite(velocity) || !Finite(next) || !Finite(rotation))
                    return Stop("Glider damping produced non-finite state.", out detail);
                _lateFrame = Time.frameCount;
                _body.MoveRotation(rotation);
                _rotationPending = true;
                _body.velocity = velocity;
                _state = next;
                detail = "Native glide rotation and frame damping applied.";
                return true;
            }
            catch (Exception error) { return Stop("Glider late step failed (" + error.GetType().Name + ").", out detail); }
        }

        private bool Current(bool requireCommittedMode = true)
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread || !Physics.autoSimulation
                || !PlayerRoles.IsAvailable || !_hasExclusiveAuthority()) return false;
            return _bridge.IsServer && _bridge.TryReadCurrentTarget(out var entity) && Same(entity, _entity)
                && (!requireCommittedMode || _bridge.IsCommittedMovementMode(Key))
                && _entity.GetInstanceID() == _entityId && PlayerRoles.IsCurrentCarrier(entity)
                && Alive(_root) && _root.parent == null && UnitScale(_root.lossyScale)
                && Alive(_body) && _body.GetInstanceID() == _bodyId && !_body.useGravity
                && !_body.isKinematic && Finite(_body.velocity) && Finite(_body.angularVelocity)
                && Alive(_riderBody) && _riderBody.GetInstanceID() == _riderBodyId && _riderBody.isKinematic
                && Same(_riderBody.gameObject, entity.gameObject)
                && Alive(_guide) && _guide.GetInstanceID() == _guideId && Same(_body.transform, _guide)
                && _guide.parent == null && UnitScale(_guide.lossyScale)
                && Finite(_body.position) && Finite(_body.rotation) && Finite(_root.position) && Finite(_root.rotation)
                && _guide.gameObject.scene.handle == _scene && _guide.gameObject.activeInHierarchy
                && entity.gameObject.scene.handle == _scene && entity.gameObject.activeInHierarchy
                && PhysicsSceneExtensions.GetPhysicsScene(entity.gameObject.scene) == Physics.defaultPhysicsScene
                && Valid(_settings);
        }

        /// <summary>Retires this writer and holds its borrowed body; does not destroy it or invoke native exit.</summary>
        public void Dispose()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
            Stop("Glider motor retired; borrowed body held for aggregate cleanup.", out _);
        }

        private bool Stop(string reason, out string detail)
        {
            detail = StopReason = reason;
            if (Environment.CurrentManagedThreadId != _thread) return false;
            Retire();
            try { if (Alive(_body) && _body.GetInstanceID() == _bodyId) _body.isKinematic = true; }
            catch (Exception) { StopReason += " Body hold could not be confirmed."; detail = StopReason; }
            try { if (Alive(_riderBody) && _riderBody.GetInstanceID() == _riderBodyId) _riderBody.isKinematic = true; }
            catch (Exception) { StopReason += " Rider hold could not be confirmed."; detail = StopReason; }
            return false;
        }

        private void Retire()
        {
            _disposed = true;
            UnbindDispatch();
            _bridge.Retired -= Dispose;
            if (Active.TryGetValue(_entity.Pointer, out var active) && ReferenceEquals(active, this))
                Active.Remove(_entity.Pointer);
        }

        private static float Fade(float value, float rate, float dt)
        { value -= value * (1 - rate) * (dt * 60); return MathF.Abs(value) < 0.0001f ? 0 : value; }
        private static bool Valid(HangGliderSettings s)
            => Alive(s) && s.ShapedPitchFactorCurve != null && !s.ShapedPitchFactorCurve.WasCollected
                && Positive(s.MinMaxPitch) && Positive(s.MinMaxTilt) && Positive(s.HeightFromGroundDisconnect)
                && Nonnegative(s.BaseDownPitchForce) && Nonnegative(s.ConstantDownForce)
                && Nonnegative(s.UpPitchDownForce) && Nonnegative(s.ConstantForwardForce)
                && Nonnegative(s.HeadingInputRate) && Nonnegative(s.PitchInputRate) && Nonnegative(s.TiltInputRate)
                && float.IsFinite(s.TiltRotationScale) && Rate(s.VelocityFade) && Rate(s.ForwardMomentumFade)
                && Rate(s.HeadingFade) && Rate(s.TiltFade);
        private static bool Rate(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
        private static bool Nonnegative(float value) => float.IsFinite(value) && value >= 0;
        private static bool Positive(float value) => float.IsFinite(value) && value > 0;
        private static bool Delta(float value) => float.IsFinite(value) && value > 0;
        private static bool Finite(PlayerFlightGliderState s)
            => float.IsFinite(s.Heading) && float.IsFinite(s.ForwardMomentum) && float.IsFinite(s.Pitch) && float.IsFinite(s.Tilt);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool Finite(Quaternion v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z) && float.IsFinite(v.w)
            && v.x * v.x + v.y * v.y + v.z * v.z + v.w * v.w > 0.0001f;
        private static bool UnitScale(Vector3 v) => Finite(v) && (v - Vector3.one).sqrMagnitude < 0.00000001f;
        private static bool Alive(UnityEngine.Object v) => v is not null && !v.WasCollected && v.Pointer != IntPtr.Zero && v != null;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b) => Alive(a) && Alive(b) && a.Pointer == b.Pointer;
    }
}
