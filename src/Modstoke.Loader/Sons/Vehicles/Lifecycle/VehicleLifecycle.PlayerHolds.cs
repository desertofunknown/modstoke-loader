// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Gameplay;
using global::Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartOccupantWriterHold
        {
            private CartPlayerRigHold _playerRig;
            private readonly List<CartOccupants.RemotePlayerRig> _remotePlayerRigs = new();

            // Called during read-only capture, before publishing Pointers. The existing action,
            // ghost and IK gates remain the owner of those writers, including their conflicts.
            private void CapturePlayerRigWriters(CartOccupants occupants, HashSet<IntPtr> pointers)
            {
                foreach (var seat in occupants.RetainedOccupants())
                {
                    if (seat.LocalPlayerAction is null)
                    {
                        if (seat.PlayerNetworkId != 0)
                            _remotePlayerRigs.Add(occupants.CaptureRemotePlayerRig(seat.Slot, pointers));
                        continue;
                    }
                    Require(_playerRig == null, "Only one exact local seated player may be retained.");
                    Require(CartOccupantWriterHooks.PlayerRigWritersQualified,
                        "Original seated player frame writers have not been qualified.");
                    _playerRig = new CartPlayerRigHold(occupants, seat, pointers);
                }
            }

            private void PausePlayerRig()
            {
                foreach (var rig in _remotePlayerRigs) rig.Pause();
                _playerRig?.Pause();
            }
            private void RequirePlayerRig()
            {
                _playerRig?.RequireCurrent(false);
                foreach (var rig in _remotePlayerRigs) rig.RequireCurrent(false);
            }
            private void RequirePlayerRigHeld()
            {
                _playerRig?.RequireCurrent(true);
                foreach (var rig in _remotePlayerRigs) rig.RequireCurrent(true);
            }
            private void RestorePlayerRig()
            {
                try
                {
                    foreach (var rig in _remotePlayerRigs) rig.RequireCurrent(true);
                    _playerRig?.Restore();
                    foreach (var rig in _remotePlayerRigs) rig.Restore();
                }
                catch
                {
                    try { _playerRig?.Rehold(); }
                    catch (Exception) { TerminalConflict ??= "Original seated player restoration could not be kept held."; }
                    foreach (var rig in _remotePlayerRigs)
                    {
                        try { rig.Rehold(); }
                        catch (Exception) { TerminalConflict ??= "Original remote seat restoration could not be kept held."; }
                    }
                    // The caller restores actor clocks first. A later player failure must not
                    // leave those actors running behind the shared, still-acquired writer gates.
                    foreach (var animation in _animations)
                    {
                        try { animation.Pause(); }
                        catch (Exception) { TerminalConflict ??= "Occupied animation restoration could not be kept held."; }
                    }
                    throw;
                }
            }
            private void ReholdPlayerRig()
            {
                _playerRig?.Rehold();
                foreach (var rig in _remotePlayerRigs) rig.Rehold();
            }

            internal bool OwnsRetainedPlayerWriter(Component component)
            {
                if (!_held || component is null) return false;
                if (_playerRig?.Owns(component) == true) return true;
                foreach (var rig in _remotePlayerRigs) if (rig.Owns(component)) return true;
                return false;
            }
        }

        private sealed partial class CartOccupants
        {
            internal RemotePlayerRig CaptureRemotePlayerRig(int slot, HashSet<IntPtr> pointers)
            {
                var endpoint = slot == 0 ? GhostDriver : _seats[slot].Endpoint;
                Require(endpoint != null && _seats[slot].Player == null && _seats[slot].Actor == null,
                    "The remote seat must resolve to its exact player endpoint, not its driver ghost.");
                return new RemotePlayerRig(this, slot, pointers);
            }

            internal sealed class RemotePlayerRig
            {
                private readonly CartOccupants _occupants;
                private readonly Endpoint _endpoint;
                private readonly int _slot;
                private readonly Transform _root, _parent, _projection;
                private readonly BoltPlayerSetup _setup;
                private readonly CoopMecanimReplicator _replicator;
                private readonly CoopPlayerRemoteSetup _remote;
                private readonly Animator _animator;
                private readonly RuntimeAnimatorController _controller;
                private readonly Rigidbody[] _bodies;
                private readonly Vector3 _position, _scale;
                private readonly Quaternion _rotation;
                private readonly int _setupInstance, _replicatorInstance;
                private readonly bool _setupEnabled, _replicatorEnabled, _remoteEnabled, _animatorEnabled;
                private readonly bool _rootMotion;
                private readonly float _speed;
                private bool _paused;

                internal RemotePlayerRig(CartOccupants occupants, int slot, HashSet<IntPtr> pointers)
                {
                    var endpoint = slot == 0 ? occupants.GhostDriver : occupants._seats[slot].Endpoint;
                    _occupants = occupants; _endpoint = endpoint; _slot = slot;
                    var entity = endpoint.Original;
                    Require(entity.TryFindState<IPlayerState>(out _) && !SameEntity(entity, LocalPlayer.Entity),
                        "A remote occupied rig must be an exact player, never the host player or driver ghost.");
                    _root = entity.transform; _parent = _root.parent;
                    _projection = occupants.Source.Cart._externalEntityTransforms[slot];
                    Require(Live(_projection) && (_projection.Pointer == _root.Pointer || _projection.IsChildOf(_root))
                        && !_root.IsChildOf(occupants.Source.Entity.transform)
                        && (endpoint.OldId == endpoint.NewId || endpoint.Replacement.transform.Pointer == _root.Pointer),
                        "Remote cart occupancy requires the original retained player hierarchy.");
                    var setups = entity.GetComponentsInChildren<BoltPlayerSetup>(true);
                    var replicators = entity.GetComponentsInChildren<CoopMecanimReplicator>(true);
                    var remotes = entity.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                    Require(setups.Length == 1 && replicators.Length == 1 && remotes.Length == 1
                        && entity.GetComponentsInChildren<FirstPersonCharacter>(true).Length == 0
                        && entity.GetComponentsInChildren<PlayerAnimatorControl>(true).Length == 0,
                        "The exact remote seated player writer topology is unavailable.");
                    _setup = setups[0]; _replicator = replicators[0]; _remote = remotes[0];
                    Require(CartOccupantWriterHooks.PlayerRigWritersQualified
                        && !PlayerMigrationAdapter.IsReplicaWriterHeld(_setup)
                        && !PlayerMigrationAdapter.IsReplicaWriterHeld(_replicator),
                        "Remote seat writers must be qualified and cannot be owned by a separate player migration.");
                    _setupInstance = _setup.GetInstanceID(); _replicatorInstance = _replicator.GetInstanceID();
                    _setupEnabled = _setup.enabled; _replicatorEnabled = _replicator.enabled; _remoteEnabled = _remote.enabled;
                    _animator = _replicator.GetTargetAnimator();
                    Require(Live(_animator) && _remote._playerAnimator?.Pointer == _animator.Pointer
                        && (_animator.transform.Pointer == _root.Pointer || _animator.transform.IsChildOf(_root)),
                        "Remote seated animation must use its original player animator.");
                    _controller = _animator.runtimeAnimatorController; _animatorEnabled = _animator.enabled;
                    _rootMotion = _animator.applyRootMotion; _speed = _animator.speed;
                    Require(float.IsFinite(_speed) && _speed >= 0 && _animator.isInitialized && Live(_controller)
                        && !_animator.hasBoundPlayables && _animator.recorderMode == AnimatorRecorderMode.Offline
                        && !_animator.isMatchingTarget && _animator.layerCount > 0 && _animator.layerCount <= 32,
                        "The remote seat animator is not at a supported native boundary.");
                    for (int i = 0; i < _animator.layerCount; i++)
                        Require(!_animator.IsInTransition(i), "Wait for the remote seat animation transition to settle.");
                    var bodies = entity.GetComponentsInChildren<Rigidbody>(true);
                    _bodies = new Rigidbody[bodies.Length];
                    for (int i = 0; i < _bodies.Length; i++) _bodies[i] = bodies[i];
                    _position = _root.position; _rotation = _root.rotation; _scale = _root.localScale;
                    RequireCurrent(false);
                    pointers.Add(_setup.Pointer); pointers.Add(_replicator.Pointer);
                    pointers.Add(endpoint.Original._entity.Pointer); pointers.Add(endpoint.Replacement._entity.Pointer);
                }

                internal bool Owns(Component component) => component is not null
                    && ((component.Pointer == _setup.Pointer && component.GetInstanceID() == _setupInstance)
                        || (component.Pointer == _replicator.Pointer && component.GetInstanceID() == _replicatorInstance));

                internal void Pause()
                {
                    RequireCurrent(false);
                    _paused = true;
                    _animator.speed = 0;
                    RequireCurrent(true);
                }

                internal void Restore()
                {
                    RequireCurrent(true);
                    _animator.speed = _speed;
                    _paused = false;
                    RequireCurrent(false);
                }

                internal void Rehold()
                {
                    Require(Live(_animator) && Ptr(_animator.runtimeAnimatorController) == Ptr(_controller),
                        "Cannot rehold a replaced remote seat animator.");
                    _paused = true;
                    _animator.speed = 0;
                }

                internal void RequireCurrent(bool held)
                {
                    _endpoint.RequireCurrent(_occupants._moved || _occupants._parked);
                    foreach (var body in _bodies)
                        Require(Live(body) && body.isKinematic
                            && (body.transform.Pointer == _root.Pointer || body.transform.IsChildOf(_root)),
                            "A remote seated player has an independent dynamic body or changed hierarchy.");
                    Require(Live(_root) && Ptr(_root.parent) == Ptr(_parent)
                        && _root.position == _position && _root.rotation == _rotation && _root.localScale == _scale
                        && Live(_projection) && (_projection.Pointer == _root.Pointer || _projection.IsChildOf(_root))
                        && Live(_setup) && _setup.GetInstanceID() == _setupInstance && _setup.enabled == _setupEnabled
                        && Live(_replicator) && _replicator.GetInstanceID() == _replicatorInstance && _replicator.enabled == _replicatorEnabled
                        && Live(_remote) && _remote.enabled == _remoteEnabled
                        && Live(_animator) && _animator.enabled == _animatorEnabled
                        && _animator.speed == (_paused ? 0 : _speed) && (!held || _paused)
                        && _animator.applyRootMotion == _rootMotion
                        && Ptr(_animator.runtimeAnimatorController) == Ptr(_controller)
                        && Ptr(_replicator.GetTargetAnimator()) == Ptr(_animator) && Ptr(_remote._playerAnimator) == Ptr(_animator)
                        && !PlayerMigrationAdapter.IsReplicaWriterHeld(_setup)
                        && !PlayerMigrationAdapter.IsReplicaWriterHeld(_replicator),
                        "Original remote seat rig changed or a separate player hold claimed its clocks.");
                    var entity = _occupants._moved
                        || (_occupants._parked && (!Live(_endpoint.Original) || !_endpoint.Original.isAttached))
                        ? _endpoint.Replacement : _endpoint.Original;
                    Require(SameEntity(_setup.entity, entity) && SameEntity(_replicator.entity, entity)
                        && SameEntity(_remote.entity, entity), "Remote seat writers were not rebound to the exact retained player.");
                    Require(PlayerCarrier.ReadNativeInitialization(entity, out var initialization)
                        == BoltMigrationAdmission.Ready, initialization);
                    if (!_occupants._parked)
                    {
                        var cart = _occupants._moved ? _occupants.Target.Cart : _occupants.Source.Cart;
                        Require(Ptr(cart._externalEntityTransforms[_slot]) == Ptr(_projection),
                            "Remote seat projection no longer targets its original player rig.");
                    }
                    Require(PlayerLifecycle.ReadReplicaActionBoundary(_setup, _replicator, out var detail)
                        == BoltMigrationAdmission.Ready, detail);
                }
            }
        }

        // This is the original seated local rig, not a substitute controller or a server's
        // host LocalPlayer. Inventory, committed-event routing and pending native continuations
        // still require their owning lifecycle admission before the aggregate can report Ready.
        private sealed class CartPlayerRigHold
        {
            private readonly PlayerGolfCartAction _action;
            private readonly GolfCartPlayerState _state;
            private readonly Transform _root, _parent, _camera;
            private readonly FirstPersonCharacter _character;
            private readonly PlayerAnimatorControl _control;
            private readonly PlayerStats _stats;
            private readonly Rigidbody _body;
            private readonly Animator _animator;
            private readonly SimpleMouseRotator _mainRotator, _camRotator;
            private readonly RuntimeAnimatorController _animatorController;
            private readonly bool _controlEnabled, _mainRotatorEnabled, _camRotatorEnabled, _animatorEnabled;
            private readonly bool _rootMotion, _driver;
            private readonly RigidbodyConstraints _constraints;
            private readonly RigidbodyInterpolation _interpolation;
            private readonly CollisionDetectionMode _collisionMode;
            private readonly List<Action> _subscriptions = new();
            private Vector3 _position, _scale;
            private Quaternion _rotation;
            private float _speed, _pausedAt, _initializedTime, _driverStartTime;
            private float _resumedInitializedTime, _resumedDriverStartTime;
            private bool _attempted, _paused, _restoring;
            private readonly int _actionInstance, _characterInstance, _controlInstance;

            internal CartPlayerRigHold(CartOccupants occupants, VehicleRetainedOccupant seat,
                HashSet<IntPtr> pointers)
            {
                _action = seat.LocalPlayerAction; _state = _action._state;
                _driver = _action.TryCast<PlayerGolfCartDriverAction>() != null;
                var entity = LocalPlayer.Entity;
                _root = LocalPlayer.Transform; _camera = LocalPlayer.MainCamTr;
                _character = LocalPlayer.FpCharacter; _control = LocalPlayer.AnimControl;
                _stats = LocalPlayer.Stats; _body = LocalPlayer.Rigidbody; _animator = LocalPlayer.Animator;
                _mainRotator = LocalPlayer.MainRotator; _camRotator = LocalPlayer.CamRotator;
                Require(seat.PlayerNetworkId != 0 && Live(entity) && entity.isAttached
                    && entity.networkId.PackedValue == seat.PlayerNetworkId
                    && entity.TryFindState<IPlayerState>(out var playerState)
                    && Live(_root) && _action.transform.IsChildOf(_root)
                    && !_root.IsChildOf(occupants.Source.Entity.transform)
                    && Live(playerState.Transform?.Simulate),
                    "A seated player hold requires its exact player ID, never the physical cart's ghost ID.");
                Require(Live(_character) && Live(_control) && Live(_stats) && Live(_camera)
                    && Live(_body) && Live(_animator) && Live(_mainRotator) && Live(_camRotator),
                    "The original seated controls, camera, body and animator are unavailable.");
                RequireExactSaveType<FirstPersonCharacter>(_character);
                RequireExactSaveType<PlayerAnimatorControl>(_control);
                _actionInstance = _action.GetInstanceID(); _characterInstance = _character.GetInstanceID();
                _controlInstance = _control.GetInstanceID();
                _parent = _root.parent;
                _controlEnabled = _control.enabled; _animatorEnabled = _animator.enabled;
                _mainRotatorEnabled = _mainRotator.enabled; _camRotatorEnabled = _camRotator.enabled;
                _animatorController = _animator.runtimeAnimatorController; _rootMotion = _animator.applyRootMotion;
                _constraints = _body.constraints; _interpolation = _body.interpolation;
                _collisionMode = _body.collisionDetectionMode;
                Require(_animator.isInitialized && Live(_animatorController) && !_animator.hasBoundPlayables
                    && _animator.recorderMode == AnimatorRecorderMode.Offline && !_animator.isMatchingTarget
                    && _animator.layerCount > 0 && _animator.layerCount <= 32,
                    "The original seated animator has an unsupported playable or controller state.");
                for (int i = 0; i < _animator.layerCount; i++)
                    Require(!_animator.IsInTransition(i), "Wait for the original seat animation transition to settle.");
                CaptureSubscriptions();
                pointers.Add(_character.Pointer); pointers.Add(_control.Pointer);
                RequireCurrent(false);
            }

            private void CaptureSubscriptions()
            {
                var death = _stats._onDeathCallback;
                Require(death != null, "The original seated death subscription is absent.");
                int matches = 0;
                foreach (var entry in death.GetInvocationList())
                    if (Same(entry.Target, _action) && entry.Method?.Name == "TriggerDisconnectOnDeath") matches++;
                Require(matches == 1, "The original death callback must name the exact seated action once.");
                _subscriptions.Add(() => Require(Same(_stats._onDeathCallback, death),
                    "The original seated death subscription was replaced."));
                var knockdown = _control._knockDownReceivers;
                var cutscene = _control._cutsceneDisconnectReceivers;
                CaptureReceivers(() => _control._knockDownReceivers, knockdown,
                    () => knockdown.Count, i => knockdown[i]);
                CaptureReceivers(() => _control._cutsceneDisconnectReceivers, cutscene,
                    () => cutscene.Count, i => cutscene[i]);
                var activators = _action._cachedFullBodyActivators;
                Require(activators != null && activators.Length <= 32,
                    "The original seated full-body activators are unavailable.");
                var entries = new IntPtr[activators.Length];
                for (int i = 0; i < entries.Length; i++) entries[i] = activators[i]?.Pointer ?? IntPtr.Zero;
                _subscriptions.Add(() =>
                {
                    Require(Same(_action._cachedFullBodyActivators, activators), "Seated full-body activators changed.");
                    for (int i = 0; i < entries.Length; i++)
                        Require((activators[i]?.Pointer ?? IntPtr.Zero) == entries[i], "A seated full-body activator was replaced.");
                });
            }

            private void CaptureReceivers(Func<Il2CppObjectBase> read, Il2CppObjectBase list,
                Func<int> count, Func<int, Il2CppObjectBase> item)
            {
                Require(list != null && !list.WasCollected && count() <= 32, "Seated action receiver list is unavailable.");
                var entries = new IntPtr[count()];
                int matches = 0;
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = item(i)?.Pointer ?? IntPtr.Zero;
                    if (entries[i] == _action.Pointer) matches++;
                }
                Require(matches == 1, "A seated action receiver must occur exactly once.");
                _subscriptions.Add(() =>
                {
                    Require(Same(read(), list) && count() == entries.Length, "Seated action receiver topology changed.");
                    for (int i = 0; i < entries.Length; i++)
                        Require((item(i)?.Pointer ?? IntPtr.Zero) == entries[i], "An original seated action receiver was replaced.");
                });
            }

            internal void RequireCurrent(bool held)
            {
                Require(Live(_action) && _action.GetInstanceID() == _actionInstance
                    && Same(_action._state, _state) && _state.IsConnected
                    && Same(LocalPlayer.Transform, _root) && Same(_root.parent, _parent)
                    && Same(LocalPlayer.FpCharacter, _character) && Same(LocalPlayer.AnimControl, _control)
                    && Same(LocalPlayer.Stats, _stats) && Same(LocalPlayer.MainCamTr, _camera)
                    && Same(LocalPlayer.Rigidbody, _body) && Same(LocalPlayer.Animator, _animator)
                    && Same(LocalPlayer.MainRotator, _mainRotator) && Same(LocalPlayer.CamRotator, _camRotator)
                    && Live(_camera) && Live(_root) && Live(_character) && Live(_control) && Live(_body) && Live(_animator)
                    && Live(_mainRotator) && Live(_camRotator) && Live(_stats),
                    "Original seated rig, camera or action identity changed.");
                // Native driver/passenger PostInitialize disables FPC and uses this same
                // kinematic body without gravity. Never convert an active ground motor here.
                Require(!_character.enabled && _character._isInMidAction
                    && Same(_character._rigidbody, _body) && _body.isKinematic && !_body.useGravity
                    && _body.constraints == _constraints && _body.interpolation == _interpolation
                    && _body.collisionDetectionMode == _collisionMode
                    && _control.enabled == _controlEnabled && _animator.enabled == _animatorEnabled
                    && _mainRotator.enabled == _mainRotatorEnabled && _camRotator.enabled == _camRotatorEnabled
                    && Same(_animator.runtimeAnimatorController, _animatorController) && _animator.applyRootMotion == _rootMotion
                    && !_character.IsInvoking() && !_control.IsInvoking() && !_action.IsInvoking(),
                    "The original native seated motor/animation policy changed or has a pending invoke.");
                foreach (var check in _subscriptions) check();
                if (_attempted)
                {
                    Require((_animator.speed == 0 || _animator.speed == _speed)
                        && (_state.InitializedTime == _initializedTime
                            || (_restoring && _state.InitializedTime == _resumedInitializedTime))
                        && (_state.DriverStartTime == _driverStartTime
                            || (_restoring && _state.DriverStartTime == _resumedDriverStartTime)),
                        "The original seated animation or action clocks no longer belong to this hold.");
                }
                if (!held) return;
                Require(_attempted && _paused && !_restoring && _animator.speed == 0
                    && _root.position == _position && _root.rotation == _rotation && _root.localScale == _scale
                    && _state.InitializedTime == _initializedTime && _state.DriverStartTime == _driverStartTime,
                    "The held original seated pose, animation clock or action clock changed.");
            }

            internal bool Owns(Component component) => component is not null
                && ((component.Pointer == _action.Pointer && component.GetInstanceID() == _actionInstance)
                    || (component.Pointer == _character.Pointer && component.GetInstanceID() == _characterInstance)
                    || (component.Pointer == _control.Pointer && component.GetInstanceID() == _controlInstance));

            internal void Pause()
            {
                Require(!_attempted, "Original seated rig hold is single-use.");
                RequireCurrent(false);
                _speed = Finite(_animator.speed); _pausedAt = Finite(Time.time);
                _initializedTime = Finite(_state.InitializedTime); _driverStartTime = Finite(_state.DriverStartTime);
                _position = _root.position; _rotation = _root.rotation; _scale = _root.localScale;
                _attempted = true;
                try
                {
                    _animator.speed = 0;
                    _paused = true;
                    RequireCurrent(true);
                }
                catch
                {
                    Rehold();
                    throw;
                }
            }

            internal void Restore()
            {
                if (!_attempted) return;
                RequireCurrent(false);
                Require(_animator.speed == 0 || _animator.speed == _speed,
                    "Original seated animation speed no longer belongs to this hold.");
                if (!_restoring)
                {
                    RequireCurrent(true);
                    float elapsed = Finite(Time.time - _pausedAt);
                    Require(elapsed >= 0, "The seated action clock moved backwards.");
                    _resumedInitializedTime = Finite(_initializedTime + elapsed);
                    _resumedDriverStartTime = _driver ? Finite(_driverStartTime + elapsed) : _driverStartTime;
                    _restoring = true;
                }
                // Own each value before setting it; a failed native write may have committed.
                Require(_state.InitializedTime == _initializedTime || _state.InitializedTime == _resumedInitializedTime,
                    "The retained seat initialization clock changed during release.");
                Require(_state.DriverStartTime == _driverStartTime || _state.DriverStartTime == _resumedDriverStartTime,
                    "The retained driver clock changed during release.");
                _state.InitializedTime = _resumedInitializedTime;
                _state.DriverStartTime = _resumedDriverStartTime;
                Require(_state.InitializedTime == _resumedInitializedTime && _state.DriverStartTime == _resumedDriverStartTime,
                    "Original seated action clocks did not resume.");
                _animator.speed = _speed;
                Require(_animator.speed == _speed, "Original seated animator did not resume.");
                _paused = false;
            }

            // Called if any occupant's restoration fails before the shared writer gates release.
            // Reuse the original snapshots; never adopt a partially restored clock or speed.
            internal void Rehold()
            {
                if (!_attempted) return;
                RequireCurrent(false);
                Require(_animator.speed == 0 || _animator.speed == _speed,
                    "The original seated animator is no longer owned by this hold.");
                Require(_state.InitializedTime == _initializedTime
                    || (_restoring && _state.InitializedTime == _resumedInitializedTime),
                    "The retained initialization clock cannot be held again.");
                Require(_state.DriverStartTime == _driverStartTime
                    || (_restoring && _state.DriverStartTime == _resumedDriverStartTime),
                    "The retained driver clock cannot be held again.");
                _animator.speed = 0;
                _state.InitializedTime = _initializedTime;
                _state.DriverStartTime = _driverStartTime;
                _paused = true; _restoring = false;
                RequireCurrent(true);
            }

            private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
                => right is null ? left is null : left is not null && !left.WasCollected
                    && !right.WasCollected && left.Pointer == right.Pointer;
        }
    }
}
