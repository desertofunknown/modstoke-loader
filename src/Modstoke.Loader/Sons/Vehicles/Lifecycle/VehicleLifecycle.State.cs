// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Utilities.Timer;
using FMOD;
using FMOD.Studio;
using global::Sons.Environment;
using global::Sons.Gameplay;
using global::Sons.Gameplay.Swimming;
using global::Sons.Physics;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed record CartTimerState(bool Stopped, float Start, float Interval, bool RunFinished, float Elapsed)
        {
            internal static CartTimerState Capture(AutoTimer timer)
            {
                RequireExactSaveType<AutoTimer>(timer);
                Require(timer.OnFinished == null && !float.IsNaN(timer._interval) && timer._interval >= 0,
                    "Timer callbacks or invalid intervals require a separate lifetime contract.");
                return new(timer._stopped, Finite(timer._start), timer._interval, timer._runFinishedEvent,
                    Finite(Time.time - timer._start));
            }

            internal void Restore(AutoTimer timer, bool resume)
            {
                RequireExactSaveType<AutoTimer>(timer);
                Require(timer.OnFinished == null, "Replacement timer acquired a callback.");
                float start = resume && !Stopped ? Finite(Time.time - Elapsed) : Start;
                timer._start = start;
                timer._interval = Interval;
                timer._runFinishedEvent = RunFinished;
                timer._stopped = !resume || Stopped;
                Require(timer._start == start && timer._interval == Interval && timer._runFinishedEvent == RunFinished
                    && timer._stopped == (!resume || Stopped), "Cart timer readback failed.");
            }

            internal void RequirePaused(AutoTimer timer)
            {
                RequireExactSaveType<AutoTimer>(timer);
                Require(timer.OnFinished == null && timer._stopped && timer._start == Start
                    && timer._interval == Interval && timer._runFinishedEvent == RunFinished,
                    "A suspended cart timer changed.");
            }
        }

        // Dry caches only. Ocean query helpers, colliders and callbacks are NOT transplanted.
        private sealed record CartWaterState(float Height, float Depth, float Volume,
            System.Numerics.Vector3 Normal, System.Numerics.Vector3 Velocity, System.Numerics.Vector3 CheckPosition,
            bool InCave, int CacheFrame, int CaveFrame, int CapturedFrame, float TriggerOffset)
        {
            internal static CartWaterState Capture(WaterLevelSensor sensor)
            {
                RequireExactSaveType<WaterLevelSensor>(sensor);
                Require(!sensor._isInWater && !sensor._isInOcean && sensor._lastWaterCollider is null
                    && sensor._overrideCheckInCaveAction == null
                    && sensor._trackedColliders != null && sensor._trackedColliders.Count == 0
                    && sensor._trackedOceanColliders != null && sensor._trackedOceanColliders.Count == 0,
                    "Wet, tracked-collider or callback-driven water sensors cannot be migrated.");
                Require(sensor._insideColliderResult != null, "Water sensor scratch references are unavailable.");
                foreach (var collider in sensor._insideColliderResult)
                    Require(collider is null, "Water sensor retains an external collider reference.");
                return new(Finite(sensor._cachedWaterHeight), Finite(sensor._cachedWaterDepth),
                    Finite(sensor._waterVolumeLevelCache),
                    V(sensor._cachedWaterNormal), V(sensor._cachedWaterSurfaceVelocity), V(sensor._lastManualCheckPosition),
                    sensor._inCave, sensor._cacheFrame, sensor._lastInCaveCheckFrame, Time.frameCount,
                    Finite(sensor._triggerStayCheckIntervalOffset));
            }

            internal void Restore(WaterLevelSensor sensor, bool resume)
            {
                _ = Capture(sensor);
                int shift = resume ? checked(Time.frameCount - CapturedFrame) : 0;
                sensor._cachedWaterHeight = Height; sensor._cachedWaterDepth = Depth;
                sensor._waterVolumeLevelCache = Volume;
                sensor._cachedWaterNormal = U(Normal); sensor._cachedWaterSurfaceVelocity = U(Velocity);
                sensor._lastManualCheckPosition = U(CheckPosition); sensor._inCave = InCave;
                sensor._cacheFrame = CacheFrame < 0 ? CacheFrame : checked(CacheFrame + shift);
                sensor._lastInCaveCheckFrame = CaveFrame < 0 ? CaveFrame : checked(CaveFrame + shift);
                sensor._triggerStayCheckIntervalOffset = TriggerOffset;
                var read = Capture(sensor);
                Require((read with { CapturedFrame = CapturedFrame, CacheFrame = CacheFrame, CaveFrame = CaveFrame }) == this
                    && read.CacheFrame == (CacheFrame < 0 ? CacheFrame : checked(CacheFrame + shift))
                    && read.CaveFrame == (CaveFrame < 0 ? CaveFrame : checked(CaveFrame + shift)),
                    "Dry water-cache readback failed.");
            }
        }

        private sealed record CartFloatState(float WaterLevel, float WaterDepth, float PressureTimer,
            float LastPressureGoal, float PressureGoal, float DefaultDrag, float DefaultAngularDrag,
            float TotalForce, float TotalWaterForce, float WaterLogged, int RandomOffset)
        {
            internal static CartFloatState Capture(FloatController floating)
            {
                RequireExactSaveType<FloatController>(floating);
                Require(!floating._isInWater && !floating._appliedRigidbodyDrag
                    && floating._waterForces != null && floating._waterForces.Count == 0
                    && floating._gizmoForces != null && floating._gizmoForces.Count == 0,
                    "Active buoyancy, applied drag or force history cannot be migrated.");
                return new(Finite(floating._waterLevel), Finite(floating._waterDepth),
                    Finite(floating._pressureFluctuationGoalTimer), Finite(floating._lastPressureFluctuationGoal),
                    Finite(floating._pressureFluctuationGoal), Finite(floating._defaultDrag), Finite(floating._defaultAngularDrag),
                    Finite(floating._totalForceMag), Finite(floating._totalWaterForceFactor),
                    Finite(floating._waterLoggedFactor), floating._frameRandomOffset);
            }

            internal void Restore(FloatController floating)
            {
                _ = Capture(floating);
                floating._waterLevel = WaterLevel; floating._waterDepth = WaterDepth;
                floating._pressureFluctuationGoalTimer = PressureTimer; floating._lastPressureFluctuationGoal = LastPressureGoal;
                floating._pressureFluctuationGoal = PressureGoal; floating._defaultDrag = DefaultDrag;
                floating._defaultAngularDrag = DefaultAngularDrag; floating._totalForceMag = TotalForce;
                floating._totalWaterForceFactor = TotalWaterForce; floating._waterLoggedFactor = WaterLogged;
                floating._frameRandomOffset = RandomOffset;
                Require(Capture(floating) == this, "Dry buoyancy-state readback failed.");
            }
        }

        private sealed partial class CartLocalState
        {
            private CartTimerState[] _timers;
            private readonly CartWaterState[] _water;
            private readonly CartFloatState[] _floating;
            private readonly string[] _paths;
            private readonly bool[] _enabled;
            private readonly float _cloud;
            private readonly TimeOfDay _time;
            private readonly bool _lights;
            private readonly bool _distanceActive;
            private readonly bool _kinematic;
            private readonly bool _shove;
            private readonly int _zoom;
            private readonly int _terrainAudio;
            private readonly System.Numerics.Quaternion _waterRotation;

            private CartLocalState(Binding binding, Component[] components)
            {
                var cart = binding.Cart;
                RequireQuiet(binding);
                _timers = new[] { CartTimerState.Capture(cart._idleBrakeTimer),
                    CartTimerState.Capture(cart._shoveCooldownTimer), CartTimerState.Capture(cart._reverseBeepTimer) };
                _water = new CartWaterState[components.Length];
                _floating = new CartFloatState[components.Length];
                _paths = new string[components.Length];
                _enabled = new bool[components.Length];
                for (int i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    _paths[i] = Path(binding.Entity.transform, component.transform);
                    _enabled[i] = ((Behaviour)component).enabled;
                    if (component is WaterLevelSensor sensor)
                    {
                        Require(sensor._rigidBody?.Pointer == binding.Body.Pointer,
                            "A water sensor is bound to a different physics body.");
                        _water[i] = CartWaterState.Capture(sensor);
                    }
                    else if (component is FloatController floating)
                    {
                        Require(floating._rigidbody?.Pointer == binding.Body.Pointer,
                            "Buoyancy is bound to a different physics body.");
                        _floating[i] = CartFloatState.Capture(floating);
                    }
                }
                _cloud = Finite(cart._cloudFactor); _time = cart._currentTimeOfDay;
                _lights = cart._lightsOn; _distanceActive = cart._distanceActivated;
                _kinematic = cart._setToKinematic; _zoom = cart._currentZoomLevel;
                _shove = cart._registeredShoveInteractions;
                _terrainAudio = (int)cart._terrainAudioType;
                _waterRotation = Q(cart._activeWaterLevelSensorOffsetRotation);
            }

            internal static CartLocalState Capture(Binding binding, Component[] components) => new(binding, components);

            internal void RequireQuiet(Binding binding)
            {
                var cart = binding.Cart;
                Require(!cart._isInDeepWater && cart._lightSetCoroutine == null && cart._driverConnectIkCoroutine == null
                    && !cart.IsInvoking(), "Pending invokes, light/IK coroutines or deep-water state cannot be preserved.");
                var occupants = OccupantsFor(cart);
                if (occupants != null) occupants.RequireCallbacks(cart);
                else Require(cart.OnLocalDriverConnected == null && cart.OnLocalDriverDisconnected == null
                    && cart.OnNetworkDriverConnected == null && cart.OnNetworkDriverDisconnected == null,
                    "Cart event subscribers require explicit reference repair; they cannot be discarded.");
                RequireSaveCallback(binding.Saver._onSerializeCallback, cart, "OnSerialize");
                RequireSaveCallback(binding.Saver._onDeserializeCallback, cart, "OnDeserialize");
                Quiet(cart._engineAudioEventInstance); Quiet(cart._beepAudioEventInstance); Quiet(cart._hornEventInstance);
                Require(cart._wheelEventInstances != null && cart._wheelEventInstances.Count <= 4,
                    "Cart wheel-audio ownership is unavailable.");
                foreach (var instance in cart._wheelEventInstances) Quiet(instance);
            }

            private static void Quiet(EventInstance instance)
            {
                if (!instance.hasHandle()) return;
                Require(instance.isValid() && instance.getPlaybackState(out var playback) == RESULT.OK
                    && playback == PLAYBACK_STATE.STOPPED,
                    "Active, stopping or invalid audio cannot be moved to a new cart lifetime.");
            }

            private static void RequireSaveCallback(Il2CppSystem.Delegate callback, GolfCartController cart, string method)
            {
                Require(callback != null && !callback.WasCollected, "Native cart save callback is unavailable.");
                var callbacks = callback.GetInvocationList();
                Require(callbacks != null && callbacks.Length == 1 && callbacks[0].Target?.Pointer == cart.Pointer
                    && callbacks[0].Method?.Name == method,
                    "The save callback does not belong exclusively to this cart lifetime.");
            }

            internal void RequireCompatible(CartLocalState target)
            {
                Require(Equal(_paths, target._paths) && Equal(_enabled, target._enabled),
                    "Replacement water/buoyancy topology or component policy differs.");
                for (int i = 0; i < _paths.Length; i++)
                    Require((_water[i] == null) == (target._water[i] == null)
                        && (_floating[i] == null) == (target._floating[i] == null), "Replacement native writer kinds differ.");
            }

            internal void CopyTimerExpectations(CartLocalState source) => _timers = (CartTimerState[])source._timers.Clone();

            internal void RequireUnchanged(Binding binding, Component[] components)
            {
                RequireQuiet(binding);
                RequireCartValues(binding.Cart, "Suspended cart local state changed.");
                for (int i = 0; i < components.Length; i++)
                {
                    Require(_paths[i] == Path(binding.Entity.transform, components[i].transform)
                        && ((Behaviour)components[i]).enabled == _enabled[i], "Held component topology changed.");
                    if (_water[i] != null)
                        Require((CartWaterState.Capture((WaterLevelSensor)components[i]) with
                            { CapturedFrame = _water[i].CapturedFrame }) == _water[i], "Suspended water cache changed.");
                    if (_floating[i] != null)
                        Require(CartFloatState.Capture((FloatController)components[i]) == _floating[i], "Suspended buoyancy state changed.");
                }
            }

            internal void PauseTimers(GolfCartController cart)
            {
                cart._idleBrakeTimer._stopped = true;
                cart._shoveCooldownTimer._stopped = true;
                cart._reverseBeepTimer._stopped = true;
                RequirePausedTimers(cart);
            }

            internal void RequirePausedTimers(GolfCartController cart)
            {
                _timers[0].RequirePaused(cart._idleBrakeTimer);
                _timers[1].RequirePaused(cart._shoveCooldownTimer);
                _timers[2].RequirePaused(cart._reverseBeepTimer);
            }

            internal void Restore(Binding binding, Component[] components, bool resume)
            {
                RequireQuiet(binding);
                var cart = binding.Cart;
                _timers[0].Restore(cart._idleBrakeTimer, resume);
                _timers[1].Restore(cart._shoveCooldownTimer, resume);
                _timers[2].Restore(cart._reverseBeepTimer, resume);
                cart._cloudFactor = _cloud; cart._currentTimeOfDay = _time;
                cart._lightsOn = _lights; cart._distanceActivated = _distanceActive;
                cart._setToKinematic = _kinematic; cart._currentZoomLevel = _zoom;
                cart._terrainAudioType = (TheForest.Utils.TerrainUtils.AudioTextureType)_terrainAudio;
                cart._activeWaterLevelSensorOffsetRotation = U(_waterRotation);
                if (resume)
                {
                    if (_shove) cart.EnableShoveInteractions();
                    else cart.DisableShoveInteractions();
                    Require(cart._registeredShoveInteractions == _shove, "Shove-interaction registration readback failed.");
                }
                if (Live(cart._headlightRenderer))
                    cart._headlightRenderer.sharedMaterials = _lights ? cart._headlightOnMaterials : cart._headlightOffMaterials;
                for (int i = 0; i < components.Length; i++)
                {
                    Require(_paths[i] == Path(binding.Entity.transform, components[i].transform)
                        && ((Behaviour)components[i]).enabled == _enabled[i], "Held component topology changed.");
                    if (_water[i] != null) _water[i].Restore((WaterLevelSensor)components[i], resume);
                    if (_floating[i] != null) _floating[i].Restore((FloatController)components[i]);
                }
                RequireCartValues(cart, "Cart local-state readback failed.");
            }

            private void RequireCartValues(GolfCartController cart, string detail)
            {
                Require(cart._cloudFactor == _cloud && cart._currentTimeOfDay.Equals(_time)
                    && cart._lightsOn == _lights && cart._distanceActivated == _distanceActive
                    && cart._setToKinematic == _kinematic && cart._currentZoomLevel == _zoom
                    && (int)cart._terrainAudioType == _terrainAudio
                    && Q(cart._activeWaterLevelSensorOffsetRotation) == _waterRotation,
                    detail);
            }

            private static string Path(Transform root, Transform node)
            {
                var indices = new List<int>();
                while (node.Pointer != root.Pointer)
                {
                    Require(Live(node.parent) && indices.Count < 64, "Writer is outside the cart hierarchy.");
                    indices.Add(node.GetSiblingIndex()); node = node.parent;
                }
                return string.Join("/", indices);
            }
        }
    }
}
