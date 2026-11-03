// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Actor-local Ground/Swim admission and qualified FPC swim forces for one completed
    /// server bridge. The adapter owns collision-body creation, exclusive root writers,
    /// ground simulation, native state publication and retained-client prediction.
    /// The integrated GroundMovement router owns qualified surface water jumps.
    /// Climb, glider, zipline and vehicles are not implemented here.
    /// </summary>
    public sealed partial class PlayerMovementModes : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly BoltEntity _entity;
        private readonly Transform _root;
        private readonly int _entityId, _rootId, _scene;
        private readonly PlayerMovementModeSettings _settings;
        private readonly PlayerWaterContactAuthority.ServerContinuation _waterContacts;
        private readonly PlayerWaterConfiguration _nativeWaterConfiguration;
        private bool _nativeWaterReady, _underwater;
        private float WaterlineOffset => _nativeWaterConfiguration?.WaterlineOffset ?? _settings.WaterlineOffset;
        private float SwimEnterOffset => _nativeWaterConfiguration?.SwimEnterOffset ?? _settings.SwimEnterOffset;
        private float SwimExitOffset => _nativeWaterConfiguration?.SwimExitOffset ?? _settings.SwimExitOffset;
        private float DisableSwimmingAfter => _nativeWaterConfiguration?.ForceDisableSwimmingAfter ?? _settings.ForceDisableSwimmingAfter;
        private PlayerMovementMode _mode;
        private bool _waterBlocked, _wasInWater, _swimmingUnderwater;
        private double _exitStarted = double.NaN;
        private double _lastResolvedTime = double.NaN, _lastAppliedTime = double.NaN;
        private WaterObservation _water;
        private PlayerMovementModeState _state;
        private bool _disposed;

        private PlayerMovementModes(PlayerBridge bridge, BoltEntity entity,
            PlayerMovementModeSettings settings,
            PlayerWaterContactAuthority.ServerContinuation waterContacts = null)
        {
            _bridge = bridge;
            _entity = entity;
            _root = entity.transform;
            _entityId = entity.GetInstanceID();
            _rootId = _root.GetInstanceID();
            _scene = entity.gameObject.scene.handle;
            _settings = settings;
            _waterContacts = waterContacts;
            _nativeWaterConfiguration = waterContacts?.Carrier.RestoredSurvival.Water;
        }

        public bool IsDisposed => _disposed;
        public PlayerMovementMode Mode => _mode;
        public string StopReason { get; private set; }

        /// <summary>
        /// Starts at a qualified dry ground boundary, never reseeds an in-flight swim or
        /// water-jump history. Call TryResolveMode before selecting the ground or swim
        /// body each fixed step. Creating this object installs no hooks or ticking loop.
        /// </summary>
        public static bool TryCreate(PlayerBridge bridge,
            PlayerMovementModeSettings settings,
            out PlayerMovementModes modes, out string detail)
        {
            modes = null;
            detail = "A completed server bridge and native server-owned water profile are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || bridge?.IsServer != true || !Valid(settings)) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var entity)) return false;
                var candidate = new PlayerMovementModes(bridge, entity, settings);
                if (!candidate.Current() || !candidate.TryObserveWater(out var water, out detail)) return false;
                if (water.Height is float height && water.Position.y + settings.SwimExitOffset <= height)
                {
                    detail = "Start at a dry ground boundary; existing swim and water-jump history is not captured by this API.";
                    return false;
                }
                bridge.Retired += candidate.Dispose;
                modes = candidate;
                detail = "Ground/Swim admission bound to the server carrier; no native client writer was changed.";
                return true;
            }
            catch (Exception error)
            { detail = "Movement-mode creation failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Samples the server carrier, never the client's replicated pose. Native entry
        /// and exit use separate height thresholds, water-block history and delayed exit.
        /// Grounded/jumping must come from the server motor. False is not a dry verdict.
        /// A successful observation is usable only in this fixed step at this root pose.
        /// </summary>
        public bool TryResolveMode(bool serverGrounded, bool serverJumping,
            out PlayerMovementModeState state, out string detail)
        {
            state = default;
            detail = "A current server carrier and fixed-step water observation are required.";
            if (!Current() || (_waterContacts != null && !_nativeWaterReady)) return false;
            try
            {
                double now = Time.fixedTimeAsDouble;
                if (!double.IsFinite(now) || (!double.IsNaN(_lastResolvedTime) && now < _lastResolvedTime)
                    || !Time.inFixedTimeStep)
                    return Stop("Movement-mode resolution requires monotonic native fixed-step time.", out detail);
                if (now == _lastAppliedTime)
                { detail = "This fixed step has already applied swim forces."; return false; }
                if (!TryObserveWater(out var water, out detail)) { _water = null; return false; }
                var previous = _mode;
                bool inWater = water.InWater;

                // PlayerAnimatorControl.UpdateInWaterControl, including the dead band.
                if (!double.IsNaN(_exitStarted) && now - _exitStarted > DisableSwimmingAfter)
                    LeaveSwimming();
                if (inWater && water.Position.y + SwimEnterOffset < water.Height.Value)
                {
                    if (!_waterBlocked)
                    {
                        _mode = PlayerMovementMode.Swim;
                        _waterBlocked = true;
                        _exitStarted = double.NaN;
                    }
                    _wasInWater = true;
                    if (_waterContacts != null)
                        _underwater = water.Position.y + WaterlineOffset - water.Height.Value < 0;
                }
                else if (!inWater || water.Position.y + SwimExitOffset > water.Height.Value)
                {
                    if (_waterBlocked)
                    {
                        if ((serverGrounded || serverJumping) && (inWater || _wasInWater)) LeaveSwimming();
                        else _exitStarted = now;
                        _waterBlocked = false;
                    }
                }

                // Native UpdateUnderSurface runs in the admitted entry branch only;
                // the deadband and delayed exit retain its latch until DisableSwimming.
                bool underwater = _waterContacts != null ? _underwater
                    : inWater && water.Position.y + WaterlineOffset < water.Height.Value;
                if (_mode != PlayerMovementMode.Swim) _swimmingUnderwater = false;
                _water = water;
                _lastResolvedTime = now;
                _state = new(_mode, underwater, _swimmingUnderwater, previous != _mode,
                    serverGrounded, serverJumping, water.Position, Vector3.zero,
                    water.Height, water.SurfaceVelocity, 0, Time.frameCount, now);
                state = _state;
                detail = "Server water observation selected " + _mode + ".";
                return true;
            }
            catch (Exception error)
            { return Stop("Movement-mode resolution failed (" + error.GetType().Name + ").", out detail); }
        }

        private void LeaveSwimming()
        {
            _mode = PlayerMovementMode.Ground;
            _waterBlocked = false;
            _wasInWater = false;
            _swimmingUnderwater = false;
            _underwater = false;
            _exitStarted = double.NaN;
        }

        private bool Current()
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed || !PlayerRoles.IsAvailable)
                return false;
            try
            {
                return _bridge.IsServer && _bridge.TryReadCurrentTarget(out var entity)
                    && Alive(_entity) && Alive(_root) && Same(_entity, entity)
                    && _entity.GetInstanceID() == _entityId && _root.GetInstanceID() == _rootId
                    && PlayerRoles.IsCurrentCarrier(entity)
                    && Same(entity.transform, _root) && _root.parent == null
                    && entity.gameObject.scene.handle == _scene && entity.gameObject.activeInHierarchy
                    && UnitScale(_root.lossyScale) && Upright(_root.up)
                    && PhysicsSceneExtensions.GetPhysicsScene(entity.gameObject.scene) == Physics.defaultPhysicsScene
                    && Physics.autoSimulation;
            }
            catch { return false; }
        }

        private bool Stop(string reason, out string detail)
        {
            detail = StopReason = reason;
            _disposed = true;
            _water = null;
            _swimBinding = null;
            _stepHelperCollider = null;
            _bridge.Retired -= Dispose;
            return false;
        }

        /// <summary>Releases this observer only. The adapter must retire its own active collision motor.</summary>
        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed) return;
            Stop("Movement-mode binding retired.", out _);
        }

        private static bool Valid(PlayerMovementModeSettings s)
            => s.WaterLayerMask != 0 && s.RequiredWaterTag is not { Length: 0 }
                && (s.OceanMode is OceanWaterMode.Include or OceanWaterMode.Ignore
                    or OceanWaterMode.IgnoreBelowTerrain)
                && float.IsFinite(s.MinimumOceanSpatialLength) && s.MinimumOceanSpatialLength >= 0
                && float.IsFinite(s.WaterlineOffset) && MathF.Abs(s.WaterlineOffset) <= 10
                && float.IsFinite(s.SwimEnterOffset) && MathF.Abs(s.SwimEnterOffset) <= 10
                && float.IsFinite(s.SwimExitOffset) && MathF.Abs(s.SwimExitOffset) <= 10
                && s.SwimEnterOffset >= s.SwimExitOffset
                && float.IsFinite(s.ForceDisableSwimmingAfter) && s.ForceDisableSwimmingAfter >= 0
                && s.ForceDisableSwimmingAfter <= 60;

        private static bool Alive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        private static bool Same(UnityEngine.Object a, UnityEngine.Object b)
            => Alive(a) && Alive(b) && a.Pointer == b.Pointer && a.GetInstanceID() == b.GetInstanceID();
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool UnitScale(Vector3 v)
            => Finite(v) && MathF.Abs(v.x - 1) < 0.0001f && MathF.Abs(v.y - 1) < 0.0001f && MathF.Abs(v.z - 1) < 0.0001f;
        private static bool Upright(Vector3 v) => Finite(v) && Vector3.Dot(v, Vector3.up) > 0.9999f;
    }
}
