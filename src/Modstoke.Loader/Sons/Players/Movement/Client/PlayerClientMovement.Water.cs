// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        private PlayerWaterPresentation _water;
        private SimpleMouseRotator _waterCameraRotator, _waterMainRotator;
        private int _waterCameraRotatorId, _waterMainRotatorId;

        internal bool CanDispatchSwimmingEntry => Environment.CurrentManagedThreadId == _thread
            && !_disposed && _activated && !_starting && !Time.inFixedTimeStep
            && !_insideFixed && !_awaitingPhysics && !_insideCoroutine && !_jumpPending
            && !_sendingNative && _transition == null && _nativeMode == null
            && _zipline == null && _deferredNativeExit == null && !CartMigrationHeld
            && !LifeMovementBlocked && _life?.NativeCallbackActive != true
            && _sourceHold?.IsAcknowledged == true && RetainedTargetCurrent(true);

        internal bool TryBindSwimmingTransitions(PlayerBridge bridge,
            PlayerLifeRoute route, out string detail)
        {
            detail = "Bind swimming after the same retained source life and water owners, before source-hold activation.";
            if (!ReferenceEquals(bridge, _bridge) || _water == null || route == null
                || !ReferenceEquals(_life, route.Source) || _sourceHold != null) return false;
            return _water.TryBindSourceSwimming(bridge, this, route, out detail);
        }

        internal bool TryBindWater(PlayerBridge bridge, PlayerWaterPresentation owner,
            out string detail)
        {
            detail = "Bind the exact source water endpoint while movement and its bridge are prepared and inactive.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || _activated || _starting
                || Time.inFixedTimeStep || _insideFixed || _awaitingPhysics
                || !ReferenceEquals(_prepared, this) || !ReferenceEquals(bridge, _bridge)
                || _bridge.IsDisposed || _bridge.IsServer || _bridge.IsGameplayActive || _water != null
                || owner == null || owner.IsDisposed || owner.FailureReason != null || owner.IsActive) return false;
            try
            {
                // nativeMode=true validates the original objects/topology while allowing
                // the lifecycle's disabled FPC and kinematic body. It does not release them.
                if (!_bridge.TryReadCurrentTarget(out var entity) || !_rig.Matches(entity, true, true)) return false;
                var camera = LocalPlayer.CamRotator;
                var main = LocalPlayer.MainRotator;
                if (!Alive(camera) || !Alive(main)) return false;
                int cameraId = camera.GetInstanceID(), mainId = main.GetInstanceID();
                if (!owner.TryBindSourceMovement(bridge, this, _rig.Character, _rig.Animation,
                    _rig.Body, _rig.Root, out detail)) return false;
                _waterCameraRotator = camera; _waterCameraRotatorId = cameraId;
                _waterMainRotator = main; _waterMainRotatorId = mainId;
                _water = owner;
                return true;
            }
            catch (Exception error)
            { detail = "Movement water binding refused (" + error.Message + ")."; return false; }
        }

        internal bool OwnsWaterViewConsumer(Component component)
            => !_disposed && ((Same(component, _waterCameraRotator) && Alive(_waterCameraRotator)
                    && _waterCameraRotator.GetInstanceID() == _waterCameraRotatorId)
                || (Same(component, _waterMainRotator) && Alive(_waterMainRotator)
                    && _waterMainRotator.GetInstanceID() == _waterMainRotatorId));

        internal bool WaterBindingCurrent(PlayerWaterPresentation owner)
            => !_disposed && ReferenceEquals(_water, owner)
                && OwnsWaterViewConsumer(LocalPlayer.CamRotator)
                && OwnsWaterViewConsumer(LocalPlayer.MainRotator)
                && Same(LocalPlayer.CamRotator, _waterCameraRotator)
                && Same(LocalPlayer.MainRotator, _waterMainRotator);

        private bool WaterReadyForActivation(out string detail)
        {
            detail = "The bound source water endpoint must apply its first fresh sample before releasing the retained rig.";
            // Unbound legacy clients are unchanged. Aggregate migration must explicitly
            // bind this endpoint; successful movement preparation is not water readiness.
            if (_water == null) return true;
            if (!_water.SwimmingTransitionsPrepared)
            { detail = "The exact swimming receipt/commit owner and entry-only native executor are not prepared."; return false; }
            if (_sourceHold?.IsAcknowledged != true)
            { detail = SourceInputHoldFailure ?? "Arm the source input/body owner and acknowledge the confirmed lifecycle release before activation."; return false; }
            if (!_water.IsActive) { detail = _water.FailureReason ?? detail; return false; }
            return PumpSourceInputHold(out detail) == BoltMigrationAdmission.Ready;
        }
    }
}
