// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using System.Collections.Generic;
using TheForest.Items.Inventory;
using TheForest.Player.Actions;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public enum PlayerClimbAction
    {
        RopeEnterBottom, RopeEnterTop, RopeExitBottom, RopeExitTop, RopeReset,
        WallEnterBottom, WallEnterTop, WallExitGround, WallExitTop, WallReset,
        CraneEnter, CraneExit, CraneReset
    }

    /// <summary>
    /// Native observations, not a movement acknowledgement or proof that deferred callbacks drained.
    /// Target references are process-local and must never be serialized as network identities.
    /// </summary>
    public readonly record struct PlayerClimbReadback(
        bool RopeActive, bool WallActive, bool CraneActive, bool OnRope,
        bool RootMotion, bool GravityLocked, bool Kinematic, bool DetectCollisions,
        bool PrimaryColliderEnabled, bool PrimaryColliderIsTrigger, bool AnimatorInTransition,
        Transform RopeRoot, Transform RopeBottom, Transform CraneTrigger,
        Vector3 Position, Quaternion Rotation, Vector3 Velocity, uint LastIssuedSequence,
        bool NativeCallUncertain);

    /// <summary>
    /// Executes already-authorized climb transitions on the ORIGINAL local rig only. Native
    /// input, camera, animator root motion, collision coroutines and action methods remain intact.
    /// This is not a server climb motor or an input router, and grants no migration capability.
    /// </summary>
    public sealed class PlayerClimb : IDisposable
    {
        private static readonly Dictionary<IntPtr, PlayerClimb> BoundRigs = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerRigBindings _bindings;
        private readonly GameObject _root, _actions;
        private readonly IntPtr _rootPointer;
        private readonly FirstPersonCharacter _character;
        private readonly PlayerAnimatorControl _animation;
        private readonly Animator _animator;
        private readonly PlayerInventory _inventory;
        private readonly Rigidbody _body;
        private readonly Collider _primary;
        private readonly Camera _camera;
        private readonly Transform _cameraTransform;
        private readonly PlayerClimbRopeAction _rope;
        private readonly PlayerClimbWallAction _wall;
        private readonly playerCraneClimbAction _crane;
        private readonly int _scene;
        private PlayerBridge _bridge;
        private uint _lastIssuedSequence;
        private bool _busy, _uncertain, _disposed;

        public bool IsDisposed => _disposed;
        public bool NativeCallUncertain => _uncertain;
        public string StopReason { get; private set; }

        private PlayerClimb(PlayerRigBindings bindings)
        {
            _bindings = bindings;
            _root = bindings.Root;
            _rootPointer = _root.Pointer;
            _actions = LocalPlayer.SpecialActions;
            _character = LocalPlayer.FpCharacter;
            _animation = LocalPlayer.AnimControl;
            _animator = LocalPlayer.Animator;
            _inventory = LocalPlayer.Inventory;
            _body = LocalPlayer.Rigidbody;
            _primary = _character._primaryCollider;
            _camera = LocalPlayer.MainCam;
            _cameraTransform = LocalPlayer.MainCamTr;
            _scene = _root.scene.handle;
            _rope = _actions.GetComponent<PlayerClimbRopeAction>();
            _wall = _actions.GetComponent<PlayerClimbWallAction>();
            _crane = _actions.GetComponent<playerCraneClimbAction>();
        }

        /// <summary>
        /// Capture with the ordinary local rig, before detach. Active climbs are refused: the
        /// migration snapshot does not carry their coroutine/animation/attachment state.
        /// The caller still owns inventory admission and native action draining.
        /// </summary>
        public static bool TryCapture(PlayerRigBindings bindings,
            out PlayerClimb climb, out string detail)
        {
            climb = null;
            detail = "Capture requires the attached original local rig at an idle climb boundary.";
            if (!GameBuild.IsRecognized || !PlayerRoles.IsAvailable || bindings == null)
                return false;
            try
            {
                var original = bindings.OriginalEntity;
                if (!Same(original, LocalPlayer.Entity) || !original.isAttached || !original.isOwner
                    || original.networkId.PackedValue != bindings.OriginalNetworkId
                    || !Same(bindings.Root, LocalPlayer.GameObject) || !Alive(LocalPlayer.SpecialActions)
                    || !Alive(LocalPlayer.FpCharacter)) return false;
                var candidate = new PlayerClimb(bindings);
                if (!candidate.SameRig() || !candidate.NativeIdle()) return false;
                climb = candidate;
                detail = "Original climb actions, input rig and camera identities retained without mutation.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb capture failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>Bind once, after the exact rig caches have been repaired to a completed client bridge.</summary>
        public bool TryBind(PlayerBridge bridge, out string detail)
        {
            detail = "An idle retained rig and its exact completed client migration are required.";
            if (!OnThread() || _disposed || _busy || _bridge != null || bridge == null || bridge.IsServer
                || bridge.Plan.OldNetworkId != _bindings.OriginalNetworkId) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var replacement) || !SameRig() || !NativeIdle()
                    || BoundRigs.ContainsKey(_rootPointer)
                    || !Same(LocalPlayer.Entity, replacement) || Same(replacement, _bindings.OriginalEntity)
                    || _bindings.OriginalEntity.isAttached || _bindings.OriginalEntity._entity != null
                    || BoltNetwork.FindEntity(new Bolt.NetworkId(_bindings.OriginalNetworkId)) != null
                    || BoltCore.FindEntity(new Bolt.NetworkId(_bindings.OriginalNetworkId)) != null) return false;
                _bridge = bridge;
                BoundRigs.Add(_rootPointer, this);
                bridge.Retired += Dispose;
                detail = "Climb transitions bound to this retained player; no server movement coverage is implied.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// For the trusted authenticated SERVER-COMMAND dispatcher, never a client intent handler.
        /// The dispatcher must first validate actor, target, interaction, inventory and server motor
        /// admission, including delayed native stash/reequip callbacks. Resolve target to this
        /// peer's exact native trigger. A successful return means
        /// issued once, not completed. Do not replay uncertain native calls or acknowledge simulation.
        /// Native player input must not also issue the same transition.
        /// </summary>
        public bool TryExecuteAuthorized(Guid migrationId, ulong generation, ulong networkId,
            uint sequence, PlayerClimbAction action, Transform target, out string detail)
        {
            detail = "A current retained binding and the next authenticated climb command are required.";
            if (!OnThread() || _disposed || _busy || _uncertain || _bridge == null
                || migrationId != _bridge.Plan.Id || generation != _bridge.Plan.Generation
                || networkId != _bridge.NetworkId || sequence == 0 || _lastIssuedSequence == uint.MaxValue
                || sequence != _lastIssuedSequence + 1) return false;
            _busy = true;
            try
            {
                if (!Current() || !Preflight(action, target, out detail) || !Current()) return false;
                // Native calls can start coroutines or change inventory before returning/throwing.
                _lastIssuedSequence = sequence;
                _uncertain = true;
                bool issued = InvokeNative(action, target);
                if (!Current())
                { detail = "The rig changed during the native climb call; do not replay it."; return false; }
                if (!issued)
                { detail = "The native climb exit refused after invocation; its side effects are not rolled back."; return false; }
                _uncertain = false;
                detail = "Native climb transition issued once; poll readback and drain native actions before migration.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb transition failed (" + error.GetType().Name + "); do not replay an invoked command."; return false; }
            finally { _busy = false; }
        }

        public bool TryRead(out PlayerClimbReadback state, out string detail)
        {
            state = default;
            detail = "The exact retained player binding must still be current.";
            if (!OnThread() || _disposed || _busy) return false;
            try
            {
                if (!Current()) return false;
                state = new(Alive(_rope) && _rope._isClimbing, Alive(_wall) && _wall.doingClimb,
                    Alive(_crane) && _crane._onCrane, _animation.onRope, _animation.useRootMotion,
                    _animation.lockGravity, _body.isKinematic, _body.detectCollisions,
                    _primary.enabled, _primary.isTrigger, AnimatorInTransition(),
                    Alive(_rope) ? _rope._currentRopeRoot : null,
                    Alive(_rope) ? _rope._bottomTriggerTr : null,
                    Alive(_crane) ? _crane._triggerTr : null,
                    _root.transform.position, _root.transform.rotation, _body.velocity,
                    _lastIssuedSequence, _uncertain);
                detail = "Native climb/physics state read; this is not authoritative movement or deferred-cleanup acknowledgement.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb readback failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// Release only after the enclosing lifecycle has drained native callbacks. This additionally
        /// checks observable idle/physics state; it does not replace that drain. It never rewinds a rig.
        /// </summary>
        public bool TryRelease(out string detail)
        {
            detail = "Finish the native exit/reset and drain action callbacks before releasing the climb binding.";
            if (!OnThread() || _disposed || _busy || _uncertain) return false;
            try
            {
                if (!Current() || !NativeIdle()) return false;
                Dispose();
                detail = "Idle climb binding released without changing the original rig.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb release failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool Current() => !_disposed && OnThread() && _bridge != null && !_bridge.IsDisposed && !_bridge.IsServer
            && _bridge.TryReadCurrentTarget(out var entity) && Same(LocalPlayer.Entity, entity) && SameRig()
            && BoundRigs.TryGetValue(_rootPointer, out var current) && ReferenceEquals(current, this);

        private bool SameRig()
        {
            if (!OnThread() || !PlayerRoles.IsAvailable || !Alive(_root)
                || !_root.activeInHierarchy || _root.scene.handle != _scene
                || !Same(_root, LocalPlayer.GameObject) || !Same(_actions, LocalPlayer.SpecialActions)
                || !_actions.activeInHierarchy
                || !Same(_character, LocalPlayer.FpCharacter) || !Same(_animation, LocalPlayer.AnimControl)
                || !Same(_animator, LocalPlayer.Animator) || !Same(_inventory, LocalPlayer.Inventory)
                || !Same(_body, LocalPlayer.Rigidbody) || !Same(_primary, _character._primaryCollider)
                || !Same(_body, _character._rigidbody) || !Same(_body, _primary.attachedRigidbody)
                || !Same(_camera, LocalPlayer.MainCam) || !Same(_cameraTransform, LocalPlayer.MainCamTr)
                || !Same(_animator, _animation._animator) || !Same(_body, _animation.rb)
                || !Same(_root.transform, _animation.rootTr) || !Same(_inventory, _animation.player)
                || !Owned(_actions.transform) || !Owned(_character.transform)
                || !Owned(_animation.transform) || !Same(_body.gameObject, _root)) return false;
            return ActionMatches(_rope) && ActionMatches(_wall) && ActionMatches(_crane)
                && (Alive(_rope) || Alive(_wall) || Alive(_crane))
                && (!Alive(_rope) || _rope._player == null || Same(_rope._player, _inventory))
                && (!Alive(_wall) || _wall._player == null || Same(_wall._player, _inventory));
        }

        private bool ActionMatches(Component action) => action is null
            || (Alive(action) && Same(action.gameObject, _actions));
        private bool Owned(Transform transform) => Alive(transform)
            && (Same(transform, _root.transform) || transform.IsChildOf(_root.transform));
        private bool NativeIdle() => !(Alive(_rope) && _rope._isClimbing)
            && !(Alive(_wall) && _wall.doingClimb) && !(Alive(_crane) && _crane._onCrane)
            && !_animation.onRope && !_animation.cliffClimb && !_animation.useRootMotion
            && !_animation.lockGravity && !_character._isInMidAction && !_body.isKinematic
            && _body.detectCollisions && _primary.enabled && !_primary.isTrigger && !AnimatorInTransition();
        private bool AnimatorInTransition()
        {
            for (int layer = 0; layer < _animator.layerCount; layer++)
                if (_animator.IsInTransition(layer)) return true;
            return false;
        }
        private bool OnThread() => Environment.CurrentManagedThreadId == _thread;
        private static bool Alive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer
                && left.GetInstanceID() == right.GetInstanceID();

        /// <summary>
        /// Retires managed authorization only. Never calls global-player cleanup after identity loss,
        /// resets active gameplay, stops unrelated coroutines, or restores stale collision/camera data.
        /// Use an authorized native exit/reset and TryRelease before a planned retirement.
        /// </summary>
        public void Dispose()
        {
            if (!OnThread() || _disposed) return;
            _disposed = true;
            if (_bridge != null) _bridge.Retired -= Dispose;
            if (BoundRigs.TryGetValue(_rootPointer, out var current) && ReferenceEquals(current, this))
                BoundRigs.Remove(_rootPointer);
            StopReason = "Climb command binding retired; native gameplay and any pending cleanup remain with the original rig.";
        }

        private bool Preflight(PlayerClimbAction action, Transform target, out string detail)
        {
            detail = "The exact native action and an appropriate live trigger are required.";
            bool entry = action is PlayerClimbAction.RopeEnterBottom or PlayerClimbAction.RopeEnterTop
                or PlayerClimbAction.WallEnterBottom or PlayerClimbAction.WallEnterTop
                or PlayerClimbAction.CraneEnter;
            bool needsTarget = entry || action is PlayerClimbAction.RopeExitTop or PlayerClimbAction.WallExitTop;
            if (needsTarget && (!Alive(target) || !target.gameObject.activeInHierarchy
                || target.gameObject.scene.handle != _scene || Owned(target))) return false;
            if (!needsTarget && target is not null) return false;
            if (entry && (!NativeIdle() || !_character.enabled || !_animation.enabled || !_animator.enabled)) return false;
            switch (action)
            {
                case PlayerClimbAction.RopeEnterBottom:
                    var bottom = target.GetComponent<activateClimb>();
                    return Alive(_rope) && _rope.isActiveAndEnabled && Alive(bottom) && bottom.isActiveAndEnabled
                        && !activateClimb.PlayerIsBusy() && bottom.IsRopeLongEnough() && bottom.PlayerIsAligned();
                case PlayerClimbAction.RopeEnterTop:
                    var top = target.GetComponent<activateClimbTop>();
                    return Alive(_rope) && _rope.isActiveAndEnabled && Alive(top) && top.isActiveAndEnabled
                        && !activateClimbTop.PlayerIsBusy() && top.IsRopeLongEnough() && top.PlayerCanReachRope();
                case PlayerClimbAction.RopeExitTop:
                    // Native ExitClimbRopeAtTop compares trn.parent to _currentRopeRoot;
                    // _exitTopTrigger is a temporarily disabled collider, not the exit identity.
                    return Alive(_rope) && _rope._isClimbing && Same(target.parent, _rope._currentRopeRoot)
                        && !_rope.CheckForCeiling(true);
                case PlayerClimbAction.RopeExitBottom:
                case PlayerClimbAction.RopeReset:
                    return Alive(_rope) && _rope._isClimbing && !(Alive(_wall) && _wall.doingClimb)
                        && !(Alive(_crane) && _crane._onCrane);
                case PlayerClimbAction.WallEnterBottom:
                case PlayerClimbAction.WallEnterTop:
                    return Alive(_wall) && _wall.isActiveAndEnabled;
                case PlayerClimbAction.WallExitGround:
                case PlayerClimbAction.WallExitTop:
                case PlayerClimbAction.WallReset:
                    return Alive(_wall) && _wall.doingClimb && !(Alive(_rope) && _rope._isClimbing)
                        && !(Alive(_crane) && _crane._onCrane);
                case PlayerClimbAction.CraneEnter:
                    return Alive(_crane) && _crane.isActiveAndEnabled;
                case PlayerClimbAction.CraneExit:
                case PlayerClimbAction.CraneReset:
                    return Alive(_crane) && _crane._onCrane && !(Alive(_rope) && _rope._isClimbing)
                        && !(Alive(_wall) && _wall.doingClimb);
                default: return false;
            }
        }

        private bool InvokeNative(PlayerClimbAction action, Transform target)
        {
            switch (action)
            {
                case PlayerClimbAction.RopeEnterBottom: _rope.EnterClimbRopeFromBottom(target.GetComponent<activateClimb>()); break;
                case PlayerClimbAction.RopeEnterTop: _rope.EnterClimbRopeFromTop(target.GetComponent<activateClimbTop>()); break;
                case PlayerClimbAction.RopeExitBottom: return _rope.ExitClimbRopeAtBottom(false);
                case PlayerClimbAction.RopeExitTop: _rope.ExitClimbRopeAtTop(target); break;
                case PlayerClimbAction.RopeReset: _rope.resetClimbRope(); break;
                case PlayerClimbAction.WallEnterBottom: _wall.enterClimbWall(target); break;
                case PlayerClimbAction.WallEnterTop: _wall.enterClimbWallTop(target); break;
                case PlayerClimbAction.WallExitGround: _wall.exitClimbWallGround(); break;
                case PlayerClimbAction.WallExitTop: _wall.exitClimbWallTop(target); break;
                case PlayerClimbAction.WallReset: _wall.resetClimbWall(); break;
                case PlayerClimbAction.CraneEnter: _crane.EnterCraneClimb(target); break;
                case PlayerClimbAction.CraneExit: _crane.ExitCraneClimb(); break;
                case PlayerClimbAction.CraneReset: _crane.ForceCraneReset(); break;
                default: throw new InvalidOperationException("Unknown climb action.");
            }
            return true;
        }
    }
}
