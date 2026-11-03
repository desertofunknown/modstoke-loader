// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Animation;
using global::Sons.Gui;
using TheForest.Items.Inventory;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatLocalLife
    {
        private void BeginDowned()
        {
            _equipmentStash.ReserveCleanup();
            Change(() => _animation.StopCoroutine("smoothEnableLayerNew"));
            Change(() => _animation.StopCoroutine("smoothDisableLayerNew"));
            if (_layersRegistered)
            {
                Change(() => _animation.UnregisterLayerBehaviourActivators(_reviveLayers));
                _layersRegistered = false;
            }
            Change(() => _rigidbody.velocity = Vector3.zero);
            var interrupted = _animation.OnActionInterrupted;
            if (interrupted != null) Change(() => interrupted.Invoke());
            Change(() => _inventory.Close(true));
            Change(() => _inventory.enabled = false);
            _equipmentStash.Execute();
            Change(() => _movement.SetIsInMidAction(true));
            Change(() => _movement.SetAnimatorJumpBool(false));
            Change(() => _movement.StopCoroutine("startJumpTimer"));
            Change(() => _movement._jumpTimerIsRunning = false);
            Change(() => _movement._isJumping = false);
            Change(() => _movement.fallShakeBlock = false);
            if (!_movement._stepHelperEnabled)
            {
                Change(() => _movement._stepHelperEnabled = true);
                Change(() => _movement.UpdateStepHelperEnabled());
            }
            Change(() => _movement.CancelInvoke("EnableAnimatorJumpBoolAfterDelay"));
            if (!_movement.jumpCoolDown)
            {
                Change(() => _animator.SetInteger(AnimationHashes.jumpTypeHash, 0));
                Change(() => _movement.SetAnimatorJumpBool(false));
            }
            Change(() => _movement.ClearFallDamageFlag());
            Change(() => _movement._landingVelocityY = 0f);
            Change(() => _cameraRotation._originalRotation = Quaternion.identity);
            Change(() =>
            {
                var angles = _cameraRotation._targetAngles;
                angles.x = angles.y = 0f;
                _cameraRotation._targetAngles = angles;
            });
            DisableControls();
            Change(() => _movement.resetPhysicMaterial());
            Change(() => UiManager.SetShowTutorialsUi(false));
            Change(() => _animator.SetBool(AnimationHashes.stickBlockHash, false));
            Change(() => _animator.SetFloat(AnimationHashes.TwoArmBlockHash, 0f));
            Change(() => _animator.SetFloat(AnimationHashes.singleArmBlockHash, 0f));
            Change(() => _stats._playerIsDowned = true);
            Change(() => _stats._healing = false);
            Change(() => _barrier.Vitals._hasDied = true);
            Change(() => _animator.SetBool(AnimationHashes.injuredBoolHash, true));
            Change(() => _animator.SetBool(AnimationHashes.deathBoolHash, true));
            Change(() => _animator.SetBool(AnimationHashes.firstDeathHash, true));
            Change(() => _camera.SetFollowAnim(true));
            Change(() => UiManager.SetShowHud(false));
            Change(() => UiManager.SetShowInWorldUi(false));
            Change(() => _stats.StopDyingMpAudio());
            Change(() => _stats._dyingState = PlayerStats.DyingState.Dying);
            Change(() => _networkState.Dying = 1);
            Change(() => _stimuli.SetAlive(false));
            Change(() => _stimuli.SetDowned(true));
        }

        private void BeginGetUp()
        {
            Change(() => _stats._dyingState = PlayerStats.DyingState.Alive);
            Change(() => _networkState.Dying = 0);
            Change(() => _stimuli.SetDowned(false));
            Change(() => _stimuli.SetAlive(true));
            Change(() => _movement.SetIsInMidAction(false));
            Change(() => _stats._playerDeathCount = 0);
            Change(() => _barrier.Vitals._hasDied = false);
            Change(() => _camera.SetFollowAnim(true));
            Change(() => _animator.SetBool(AnimationHashes.injuredBoolHash, false));
            Change(() => _animator.SetBool(AnimationHashes.deathBoolHash, false));
            Change(() => _animation.doingFullBodyAction = true);
            Change(() => _animation.RegisterLayerBehaviourActivators(_reviveLayers));
            _layersRegistered = true;
            Change(() => _stats.StopDyingMpAudio());
            // The captured native respawn timer stays inactive throughout this controller.
            Change(() => _stats._playerIsDowned = false);
            Change(() => _inventory.SetCurrentView(PlayerInventory.PlayerViews.World));
            Change(() => UiManager.SetShowHud(true));
            Change(() => UiManager.SetShowInWorldUi(true));
            Change(() => _stats._healing = true);
        }

        private void FinishGetUp()
        {
            if (_layersRegistered)
            {
                Change(() => _animation.UnregisterLayerBehaviourActivators(_reviveLayers));
                _layersRegistered = false;
            }
            Change(() => _inventory.enabled = true);
            Change(() => _bodyRotation.enabled = true);
            Change(() => _cameraRotation.enabled = true);
            Change(() => _movement.enabled = true);
            Change(() => _camera.InstantUnlockInPlace(false));
            Change(() => _movement.DisableMovementSlow());
            Change(() => _animation._spineBendEnabled = true);
            Change(() => _animation.StopCoroutine("smoothDisableLayerNew"));
            Change(() => _animation.doingFullBodyAction = false);
            Change(() => _movement.SetIsInMidAction(false));
            Change(() => _animation.SetPlayerLocked(false));
            Change(() => _movement.UnLockView());
            Change(() => UiManager.SetShowTutorialsUi(true));
            Change(() => _stats._healing = false);
            if (_inventory._currentView == PlayerInventory.PlayerViews.World)
                Change(() => _movement._movementLocked = false);
            // The strict ledger remains unequipped; an authorized equip flow owns restoration.
        }

        private void DisableControls()
        {
            Change(() => _movement.enabled = false);
            Change(() => _bodyRotation.enabled = false);
            Change(() => _cameraRotation.enabled = false);
        }
    }
}
