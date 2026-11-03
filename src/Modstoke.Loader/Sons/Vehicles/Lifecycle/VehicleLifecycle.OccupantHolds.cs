// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Animation;
using global::Sons.Characters;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed partial class CartOccupantWriterHold
        {
            private readonly List<Action> _independentChecks = new();
            private readonly List<ActorAnimationHold> _animations = new();

            private void CaptureIndependentWriters(CartOccupants occupants, HashSet<IntPtr> pointers)
            {
                var cart = occupants.Source.Cart;
                Require(cart._driverConnectIkCoroutine == null && cart._pendingDriverIkTargetsController == null,
                    "A pending driver IK coroutine cannot be suspended by a settled occupant hold.");
                foreach (var seat in occupants.RetainedOccupants())
                {
                    if (seat.LocalActor is not null) CaptureActor(seat.LocalActor, seat.SeatNetworkId, true);
                    var external = cart._externalVailActors[seat.Slot];
                    if (external is not null)
                    {
                        Require(seat.LocalActor is null || seat.LocalActor.Pointer == external.Pointer,
                            "Local and external actor caches name different occupants.");
                        CaptureActor(external, seat.SeatNetworkId, false);
                    }
                    var driver = seat.LocalPlayerAction?.TryCast<global::Sons.Gameplay.PlayerGolfCartDriverAction>();
                    if (driver is not null)
                    {
                        Require(driver._driverConnectIkCoroutine == null, "The original driver has a pending IK coroutine.");
                        _independentChecks.Add(() => Require(Live(driver) && driver._driverConnectIkCoroutine == null,
                            "The original driver started an IK coroutine while held."));
                    }
                }
                CaptureIk(cart._currentDriverIkTargetsController);
                CaptureIk(cart._externalDriverIkTargetsController);

                void CaptureActor(VailActor actor, ulong seatId, bool local)
                {
                    Require(Live(actor) && !actor.transform.IsChildOf(occupants.Source.Entity.transform)
                        && !actor.IsInvoking(), "The seated actor must survive the cart without pending invokes.");
                    RequireExactSaveType<VailActor>(actor);
                    var network = actor._actorNetworked;
                    Require(Live(network), "The seated actor has no retained network writer.");
                    var entity = network.entity;
                    Require(Live(entity) && entity.isAttached && entity.networkId.PackedValue == seatId,
                        "The actor writer does not own the exact occupied seat identity.");
                    if (pointers.Contains(actor.Pointer)) return;
                    var backing = entity._entity;
                    var serializer = backing?.Serializer;
                    Require(backing != null && serializer != null, "The actor attachment is unavailable.");
                    int uniqueId = actor.UniqueId;
                    bool owner = entity.isOwner, enabled = actor.enabled, active = actor.gameObject.activeInHierarchy;
                    bool networkEnabled = network.enabled;
                    var actionAsset = actor._activeActionAsset;
                    var actionInstance = actor._activeActionInstance;
                    var thought = actor._activeThought;
                    var stimuli = actor._activeStimuli;
                    Require(uniqueId > 0, "A retained actor must have its original persistent identity.");
                    pointers.Add(actor.Pointer); pointers.Add(network.Pointer); pointers.Add(backing.Pointer);
                    _independentChecks.Add(() =>
                    {
                        Require(Live(actor) && actor.UniqueId == uniqueId && actor.enabled == enabled
                            && actor.gameObject.activeInHierarchy == active && !actor.IsInvoking()
                            && Same(actor._actorNetworked, network) && Live(network) && network.enabled == networkEnabled
                            && Live(entity) && entity.isAttached && entity.networkId.PackedValue == seatId
                            && entity.isOwner == owner && Same(entity._entity, backing)
                            && Same(entity._entity.Serializer, serializer) && SameEntity(network.entity, entity),
                            "The retained actor's exact lifetime or authority changed.");
                        // Do not Set/ClearActiveAction: those pool the old MaeSequence. The
                        // shared _nearStimuliList is intentionally neither held nor snapshotted.
                        Require(Same(actor._activeActionAsset, actionAsset) && Same(actor._activeActionInstance, actionInstance)
                            && Same(actor._activeThought, thought) && Same(actor._activeStimuli, stimuli),
                            "The retained Vail action or active-stimulus object was replaced.");
                    });
                    if (local)
                    {
                        var action = actor.GetComponentInChildren<VailActorGolfCartAction>(true);
                        Require(Live(action) && !action.IsInvoking() && action._golfCartController?.Pointer == cart.Pointer,
                            "The original actor cart action is unavailable.");
                        RequireExactSaveType<VailActorGolfCartAction>(action);
                        bool actionEnabled = action.enabled;
                        int slot = action._slotIndex;
                        pointers.Add(action.Pointer);
                        _independentChecks.Add(() => Require(Live(action) && action.enabled == actionEnabled
                            && action._slotIndex == slot && !action.IsInvoking()
                            && actor.GetComponentInChildren<VailActorGolfCartAction>(true)?.Pointer == action.Pointer,
                            "The retained Vail cart action changed."));
                    }
                    var animator = actor._animator;
                    Require(Live(animator), "The seated actor's original animator is unavailable.");
                    foreach (var animation in _animations)
                        Require(animation.Pointer != animator.Pointer, "Two retained actors share an animator.");
                    _animations.Add(new ActorAnimationHold(animator));
                    _independentChecks.Add(() => Require(Same(actor._animator, animator), "The original Vail animator was replaced."));
                    // AI hands can be driven independently of the physical cart's driver cache.
                    foreach (var ik in actor.GetComponentsInChildren<IkTargetsController>(true)) CaptureIk(ik);
                }

                void CaptureIk(IkTargetsController controller)
                {
                    if (controller is null) return;
                    Require(Live(controller) && !controller.transform.IsChildOf(occupants.Source.Entity.transform)
                        && !controller.IsInvoking() && !controller.IsSmoothTransitioning(),
                        "Only retained settled IK controllers can be held.");
                    RequireExactSaveType<IkTargetsController>(controller);
                    if (!pointers.Add(controller.Pointer)) return;
                    bool enabled = controller.enabled;
                    var operations = controller._activeOperations;
                    Require(operations != null && operations.Count <= 16, "The retained IK operation list is absent or unbounded.");
                    var retained = new Il2CppObjectBase[operations.Count];
                    for (int i = 0; i < retained.Length; i++)
                    {
                        var operation = operations[i];
                        Require(operation != null && !operation.WasCollected, "An IK operation is unavailable.");
                        retained[i] = operation;
                        var solver = ReadNative(operation, "_solver", false);
                        if (solver is not null) pointers.Add(solver.Pointer);
                        _independentChecks.Add(() => Require(Same(ReadNative(operation, "_solver", false), solver),
                            "An active IK operation changed its solver."));
                    }
                    _independentChecks.Add(() =>
                    {
                        Require(Live(controller) && controller.enabled == enabled && !controller.IsInvoking()
                            && !controller.IsSmoothTransitioning() && Same(controller._activeOperations, operations)
                            && operations.Count == retained.Length, "Retained IK controller or operation topology changed.");
                        for (int i = 0; i < retained.Length; i++)
                            Require(Same(operations[i], retained[i]), "A retained IK operation was replaced.");
                    });
                    CaptureLimb(controller, "_leftHandLimb");
                    CaptureLimb(controller, "_rightHandLimb");
                    var biped = ReadNative(controller, "_bipped", false);
                    if (biped is not null)
                    {
                        CaptureSolverComponent(biped);
                        var solvers = ReadNative(biped, "solvers");
                        foreach (string name in new[] { "leftFoot", "rightFoot", "leftHand", "rightHand", "spine", "lookAt", "aim" })
                        {
                            var solver = ReadNative(solvers, name);
                            pointers.Add(solver.Pointer);
                            _independentChecks.Add(() => Require(Same(ReadNative(solvers, name), solver), "A retained biped solver was replaced."));
                        }
                        _independentChecks.Add(() => Require(Same(ReadNative(biped, "solvers"), solvers), "The retained biped solver set changed."));
                    }
                    _independentChecks.Add(() => Require(Same(ReadNative(controller, "_bipped", false), biped), "The retained biped component changed."));
                }

                void CaptureLimb(IkTargetsController controller, string name)
                {
                    var limb = ReadNative(controller, name);
                    CaptureSolverComponent(limb);
                    var solver = ReadNative(limb, "solver");
                    pointers.Add(solver.Pointer);
                    _independentChecks.Add(() => Require(Same(ReadNative(controller, name), limb)
                        && Same(ReadNative(limb, "solver"), solver), "An original hand solver was replaced."));
                }

                void CaptureSolverComponent(Il2CppObjectBase value)
                {
                    var component = value.TryCast<Behaviour>();
                    Require(Live(component) && !component.transform.IsChildOf(occupants.Source.Entity.transform),
                        "A retained IK solver component would be destroyed with the cart.");
                    if (!pointers.Add(component.Pointer)) return;
                    bool enabled = component.enabled, active = component.gameObject.activeInHierarchy;
                    _independentChecks.Add(() => Require(Live(component) && component.enabled == enabled
                        && component.gameObject.activeInHierarchy == active, "A retained IK solver component changed activation."));
                }
            }

            private static Il2CppObjectBase ReadNative(Il2CppObjectBase instance, string name, bool required = true)
            {
                Require(instance != null && !instance.WasCollected, "A retained IK object is unavailable.");
                var property = instance.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
                Require(property != null && property.CanRead && typeof(Il2CppObjectBase).IsAssignableFrom(property.PropertyType),
                    "The qualified native IK property is unavailable: " + name);
                var value = property.GetValue(instance) as Il2CppObjectBase;
                Require(value is null ? !required : !value.WasCollected, "The native IK property is unavailable: " + name);
                return value;
            }

            private static bool Same(Il2CppObjectBase current, Il2CppObjectBase original)
                => original is null ? current is null : current is not null && !original.WasCollected
                    && !current.WasCollected && current.Pointer == original.Pointer;

            private void RequireIndependentWriters()
            {
                foreach (var check in _independentChecks) check();
                foreach (var animation in _animations) animation.RequireCurrent();
            }

            private void PauseActorAnimation()
            {
                foreach (var animation in _animations) animation.Pause();
            }

            private void RestoreActorAnimation()
            {
                try
                {
                    foreach (var animation in _animations) animation.Restore();
                }
                catch
                {
                    // Do not leave earlier actors running after a later restoration fails.
                    // Reuse the original snapshots, never recapture a partially restored speed.
                    foreach (var animation in _animations)
                    {
                        try { animation.Pause(); }
                        catch (Exception) { TerminalConflict ??= "Actor animation restoration could not be kept held."; }
                    }
                    throw;
                }
            }

            private sealed class ActorAnimationHold
            {
                private readonly Animator _animator;
                private readonly float _speed;
                private readonly bool _enabled;
                private bool _attempted, _paused;
                internal IntPtr Pointer => _animator.Pointer;

                internal ActorAnimationHold(Animator animator)
                {
                    _animator = animator; _speed = Finite(animator.speed); _enabled = animator.enabled;
                }

                internal void RequireCurrent()
                {
                    Require(Live(_animator) && _animator.enabled == _enabled
                        && (_attempted && !_paused ? _animator.speed == 0f || _animator.speed == _speed
                            : _animator.speed == (_paused ? 0f : _speed)), "The retained actor animator policy changed.");
                }

                internal void Pause()
                {
                    RequireCurrent();
                    _attempted = true;
                    _animator.speed = 0f;
                    Require(_animator.speed == 0f, "The retained actor animator did not pause.");
                    _paused = true;
                }

                internal void RequirePaused()
                {
                    Require(_attempted && _paused, "Actor animation suspension is not confirmed.");
                    RequireCurrent();
                }

                internal void Restore()
                {
                    RequireCurrent();
                    if (!_attempted) return;
                    // Clear the confirmed flag before restoring so a throwing native setter
                    // can be read back and cancelled again without losing the original speed.
                    _paused = false;
                    _animator.speed = _speed;
                    Require(_animator.speed == _speed, "The retained actor animator did not resume.");
                    _attempted = false;
                }
            }
        }
    }
}
