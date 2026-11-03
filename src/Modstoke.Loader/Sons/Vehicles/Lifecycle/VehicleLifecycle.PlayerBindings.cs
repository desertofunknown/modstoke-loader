// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Gameplay;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartOccupants
        {
            private sealed class CartPlayerBinding
            {
                private readonly PlayerGolfCartAction _action;
                private readonly GolfCartPlayerState _state;
                private readonly GameObject _inputHolder;
                private readonly int _slot;
                internal readonly GolfCartController SourceCart;
                private GolfCartController _target;
                private readonly Transform _proxy;
                private readonly Il2CppObjectBase _activators;
                private readonly Il2CppSystem.Action<InputAction.CallbackContext> _zoom;
                private Il2CppSystem.Action<InputAction.CallbackContext> _targetZoom;
                private readonly PositionConstraint _position;
                private readonly RotationConstraint _rotation;
                private readonly int _positionCount, _rotationCount;
                private readonly bool _positionActive, _rotationActive;
                private bool _parked;
                private readonly List<TransformReference> _sources = new();
                private readonly List<Action> _constraintChecks = new();

                internal CartPlayerBinding(PlayerGolfCartAction action, Binding source, Binding target)
                {
                    _action = action; SourceCart = source.Cart;
                    Require(Live(action) && !action.transform.IsChildOf(source.Entity.transform),
                        "The original player action must survive source cart retirement.");
                    _state = action._state; _inputHolder = action._inputStateHolder;
                    Require(_state != null && _state.IsConnected && action._hasSetupInputs && Live(_inputHolder)
                        && _state.ConnectedGolfCartController?.Pointer == source.Cart.Pointer,
                        "The exact connected seat and original native input subscription must already be initialized.");
                    _slot = _state.ConnectedSlotIndex;
                    _proxy = action._networkProxyConnectedTransform;
                    _activators = action._cachedFullBodyActivators;
                    var driver = action.TryCast<PlayerGolfCartDriverAction>();
                    var passenger = action.TryCast<PlayerGolfCartPassengerAction>();
                    Require(driver != null ^ passenger != null, "Only native driver/passenger player actions are supported.");
                    Require(driver != null ? _slot == 0 : _slot >= 1 && _slot <= 3,
                        "The original action type does not match its exact native seat.");
                    if (driver != null) RequireExactSaveType<PlayerGolfCartDriverAction>(driver);
                    else RequireExactSaveType<PlayerGolfCartPassengerAction>(passenger);
                    _zoom = driver != null ? driver._zoomMapAction : passenger._zoomMapAction;
                    Require(_zoom != null && !_zoom.WasCollected && _zoom.GetInvocationList().Length == 1
                        && Ptr(_zoom.Target) == source.Cart.Pointer
                        && _zoom.Method?.Name == nameof(GolfCartController.OnZoomMapAction),
                        "The native map callback must target the exact source cart.");
                    if (target != null) BindTarget(target);
                    if (_proxy is null) return;
                    Require(Live(_proxy) && !_proxy.IsChildOf(source.Entity.transform),
                        "The player's retained proxy transform would be destroyed with the source cart.");
                    _position = _proxy.GetComponent<PositionConstraint>();
                    _rotation = _proxy.GetComponent<RotationConstraint>();
                    _positionCount = _position == null ? 0 : _position.sourceCount;
                    _rotationCount = _rotation == null ? 0 : _rotation.sourceCount;
                    _positionActive = _position != null && _position.constraintActive;
                    _rotationActive = _rotation != null && _rotation.constraintActive;
                    Require(_positionCount <= 16 && _rotationCount <= 16, "Player seat constraints exceed the supported bound.");
                    for (int i = 0; i < _positionCount; i++)
                    {
                        int index = i;
                        var original = _position.GetSource(index);
                        _constraintChecks.Add(() => Require(_position.GetSource(index).weight == original.weight,
                            "Player position-constraint source weight changed."));
                        _sources.Add(new TransformReference(() => _position.GetSource(index).sourceTransform,
                            value => { var entry = _position.GetSource(index); Require(entry.weight == original.weight,
                                "Player position-constraint weight changed."); entry.sourceTransform = value; _position.SetSource(index, entry); }, source, target));
                    }
                    for (int i = 0; i < _rotationCount; i++)
                    {
                        int index = i;
                        var original = _rotation.GetSource(index);
                        _constraintChecks.Add(() => Require(_rotation.GetSource(index).weight == original.weight,
                            "Player rotation-constraint source weight changed."));
                        _sources.Add(new TransformReference(() => _rotation.GetSource(index).sourceTransform,
                            value => { var entry = _rotation.GetSource(index); Require(entry.weight == original.weight,
                                "Player rotation-constraint weight changed."); entry.sourceTransform = value; _rotation.SetSource(index, entry); }, source, target));
                    }
                }

                internal void Move(GolfCartController cart)
                {
                    RequireOriginalInputs();
                    bool forward = cart.Pointer == _target?.Pointer;
                    Require(forward || cart.Pointer == SourceCart.Pointer, "Unknown player cart reference destination.");
                    foreach (var reference in _sources) reference.Write(forward);
                    var callback = forward ? _targetZoom : _zoom;
                    Require(callback != null, "The exact destination map callback has not been bound.");
                    var driver = _action.TryCast<PlayerGolfCartDriverAction>();
                    if (driver != null) driver._zoomMapAction = callback;
                    else _action.Cast<PlayerGolfCartPassengerAction>()._zoomMapAction = callback;
                    if (_position is not null) _position.constraintActive = _positionActive;
                    if (_rotation is not null) _rotation.constraintActive = _rotationActive;
                    _parked = false;
                }

                internal void Park()
                {
                    RequireCurrent(SourceCart);
                    _parked = true;
                    if (_position is not null) _position.constraintActive = false;
                    if (_rotation is not null) _rotation.constraintActive = false;
                    foreach (var reference in _sources) reference.Park();
                    RequireCurrent(SourceCart, true);
                }

                internal void BindTarget(Binding target)
                {
                    _target = target.Cart;
                    // DoZoomMapAction remains the original input subscriber. Only its native
                    // forwarding delegate changes receiver; no SetupInputs/enter replay.
                    _targetZoom = Il2CppSystem.Delegate.CreateDelegate(_zoom.GetIl2CppType(),
                        target.Cart, _zoom.Method).Cast<Il2CppSystem.Action<InputAction.CallbackContext>>();
                    Require(Ptr(_targetZoom.Target) == target.Cart.Pointer
                        && _targetZoom.Method?.Name == _zoom.Method.Name,
                        "The native map callback did not bind the replacement cart.");
                    foreach (var reference in _sources) reference.BindTarget(target);
                }

                internal void RequireCurrent(GolfCartController cart, bool parked = false)
                {
                    RequireOriginalInputs();
                    Require(_state.IsConnected && _state.ConnectedSlotIndex == _slot
                        && _state.ConnectedGolfCartController?.Pointer == cart.Pointer,
                        "The original player seat exited or changed during replacement.");
                    Require(Live(_action) && Ptr(_action._networkProxyConnectedTransform) == Ptr(_proxy)
                        && Ptr(_action._cachedFullBodyActivators) == Ptr(_activators),
                        "Original player proxy/body activation binding changed.");
                    var driver = _action.TryCast<PlayerGolfCartDriverAction>();
                    var zoom = driver != null ? driver._zoomMapAction : _action.Cast<PlayerGolfCartPassengerAction>()._zoomMapAction;
                    Require(Ptr(zoom) == Ptr(cart.Pointer == _target?.Pointer ? _targetZoom : _zoom),
                        "The retained map input callback has the wrong cart receiver.");
                    if (_proxy is not null)
                        Require(Live(_proxy) && Ptr(_proxy.GetComponent<PositionConstraint>()) == Ptr(_position)
                            && Ptr(_proxy.GetComponent<RotationConstraint>()) == Ptr(_rotation)
                            && (_position is null || (Live(_position) && _position.sourceCount == _positionCount
                                && _position.constraintActive == (!_parked && _positionActive)))
                            && (_rotation is null || (Live(_rotation) && _rotation.sourceCount == _rotationCount
                                && _rotation.constraintActive == (!_parked && _rotationActive))),
                            "The player's seat constraints changed.");
                    foreach (var reference in _sources) reference.RequireCurrent(cart.Pointer == _target?.Pointer, parked);
                    foreach (var check in _constraintChecks) check();
                }

                private void RequireOriginalInputs()
                    => Require(Live(_action) && Ptr(_action._state) == Ptr(_state)
                        && _action._hasSetupInputs && Live(_inputHolder) && Ptr(_action._inputStateHolder) == Ptr(_inputHolder),
                        "The original native seat input subscription was cleared or replaced.");
            }
        }
    }
}
