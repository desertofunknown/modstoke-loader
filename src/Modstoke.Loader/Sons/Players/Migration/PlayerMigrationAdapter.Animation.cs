// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using RedLoader;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private PlayerPreservation.AnimationRuntimeBinding _runtimeAnimation;
        private bool _animationPreparationAttempted, _animationActivationAttempted, _animationDispatchBound;
        private bool _animationWritersPrepared, _animationRetirementDeferred;
        private float _animationResumeSpeed;
        private int _animationFrame = -1;
        private IDisposable _observerEventRoute;
        private const int MaximumRetainedAnimationTransitions = 4096;
        private Queue<RetainedAnimationTransition> _retainedAnimationTransitions;
        private bool _drainingAnimationTransitions;
        private readonly record struct RetainedAnimationTransition(ulong Target, int Layer,
            int Hash, bool AnyState, float NormalizedTime);

        // The native callback inlines ApplyHashToRemote and writes layer hashes before
        // ApplyTransitionRemote. Intercept it before either write, not only the helper.
        private static bool BeforeReplicaAnimationEvent(updateMecanimRemoteState __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (__0 == null || ExpectedReplicas.Count == 0) return true;
            PlayerMigrationAdapter owner = null;
            try
            {
                ulong id = __0.GetTargetNetworkIdValue().PackedValue;
                if (!ExpectedReplicas.TryGetValue(id, out owner)) return true;
                if (Environment.CurrentManagedThreadId != _replicaThread
                    || owner.Role == BoltMigrationRole.Destination) return false;
                var sender = __0.RaisedBy;
                if (sender == null || sender.WasCollected || !owner._peer.IsCurrent
                    || sender.Pointer != owner._peer.ConnectionPointer
                    || sender.ConnectionId != owner._peer.ConnectionId) return false;
                if (!owner.AnimationLifetimeCurrent() || !owner.ReplacementCurrent(__0.Target))
                {
                    owner.RetainUnconfirmed("An animation event reached a replacement outside its exact repaired lifetime.");
                    return false;
                }
                // Server activation may precede this peer's first water sample. Retain
                // immutable fields, never the pooled native event or a replacement ACK.
                owner.RetainAnimationTransition(new(id, __0.layer, __0.hash, __0.anyState, __0.normalizedTime));
                if (owner.AnimationGameplayActive() && owner._runtimeAnimation?.IsActive == true
                    && !owner._drainingAnimationTransitions
                    && !owner.TryDrainAnimationTransitions(out var detail)) owner.RetainUnconfirmed(detail);
                // The enclosing Bolt dispatcher, not this listener, releases event storage.
                return false;
            }
            catch (Exception error)
            {
                if (owner != null) owner.RetainUnconfirmed(Failure("Replacement animation receive unconfirmed", error));
                return false;
            }
        }

        private void RetainAnimationTransition(RetainedAnimationTransition transition)
        {
            if (transition.Target != _replacementId || transition.Hash == 0 || transition.Layer < 0
                || !float.IsFinite(transition.NormalizedTime))
                throw new InvalidOperationException("The retained animation transition is malformed.");
            _retainedAnimationTransitions ??= new();
            if (_retainedAnimationTransitions.Count == MaximumRetainedAnimationTransitions)
                throw new InvalidOperationException("Animation activation backlog exceeded its bound; runtime continuation is unconfirmed.");
            _retainedAnimationTransitions.Enqueue(transition);
        }

        private bool TryDrainAnimationTransitions(out string detail)
        {
            detail = "Animation transitions require the exact active replica owner.";
            if (_drainingAnimationTransitions || !AnimationGameplayActive() || _runtimeAnimation?.IsActive != true)
                return false;
            _drainingAnimationTransitions = true;
            try
            {
                int remaining = _retainedAnimationTransitions?.Count ?? 0;
                while (remaining-- > 0)
                {
                    // Transfer once before the native call. An unconfirmed native write
                    // retires the owner, never retries a potentially applied transition.
                    var transition = _retainedAnimationTransitions.Dequeue();
                    var result = _runtimeAnimation.TryApplyReplicaTransition(transition.Target,
                        transition.Layer, transition.Hash, transition.AnyState, transition.NormalizedTime,
                        false, out detail);
                    if (!Successful(result)) return false;
                }
                detail = "Accepted replica animation transitions drained in arrival order.";
                return true;
            }
            finally { _drainingAnimationTransitions = false; }
        }

        private bool TryPrepareRuntimeAnimation(out string detail)
        {
            detail = "Animation requires repaired references and the exact held replacement.";
            if (_runtimeAnimation != null) return _runtimeAnimation.TryValidate(out detail);
            if (_animationPreparationAttempted || !AnimationLifetimeCurrent()
                || _replacementReplica?.Held() != true
                || !PlayerPreservation.TryReadAnimationResumeSpeed(_snapshot.AnimationState,
                    _plan.OldNetworkId, out _animationResumeSpeed, out detail)) return false;
            _animationPreparationAttempted = true;
            try
            {
                _replacementReplica.PrepareRuntimeAnimationWriters();
                _animationWritersPrepared = true;
                bool prepared = Role == BoltMigrationRole.Destination
                    ? PlayerPreservation.TryPrepareServerAnimation(_bridge,
                        AnimationWritersHeld, out _runtimeAnimation, out detail)
                    : PlayerPreservation.TryPrepareRuntimeAnimation(_peer, _replacement,
                        Role == BoltMigrationRole.Source
                            ? PlayerPreservation.AnimationRuntimeRole.SourceReplica
                            : PlayerPreservation.AnimationRuntimeRole.ObserverReplica,
                        AnimationLifetimeCurrent, AnimationGameplayActive, AnimationWritersHeld,
                        out _runtimeAnimation, out detail);
                if (!prepared) RetainUnconfirmed(detail);
                return prepared;
            }
            catch (Exception error)
            {
                detail = Failure("Replacement animation preparation unconfirmed", error);
                RetainUnconfirmed(detail);
                return false;
            }
        }

        private bool AnimationLifetimeCurrent()
            => Environment.CurrentManagedThreadId == _thread && !_cancelled && !_unconfirmed
                && !_factory.IsDisposed && _peer.IsCurrent && _referencesRepaired
                && ReplacementCurrent(_replacement) && ReadExactSession() != null;

        private bool AnimationWritersHeld()
            => AnimationLifetimeCurrent() && _replacementReplica?.RuntimeAnimationWritersHeld() == true;

        private bool AnimationGameplayActive()
            => AnimationLifetimeCurrent()
                && ReadExactSession()?.Status == BoltMigrationStatus.Completed
                && (Role == BoltMigrationRole.Observer || _bridge?.IsGameplayActive == true);

        private bool TryActivateRuntimeAnimation(out string detail)
        {
            detail = "Prepared animation cannot precede complete aggregate activation.";
            if (_runtimeAnimation?.IsActive == true) return _runtimeAnimation.TryValidate(out detail);
            if (_animationActivationAttempted || _runtimeAnimation == null || !AnimationGameplayActive()) return false;
            _animationActivationAttempted = true;
            try
            {
                if (Role == BoltMigrationRole.Observer
                    && !PlayerEventRouting.TryBindObserver(_peer, ReadExactSession(),
                        out _observerEventRoute, out detail))
                { RetainUnconfirmed(detail); return false; }
                _replacementReplica.ResumeRuntimeAnimationClock(_animationResumeSpeed);
                _animatorHeld = false;
                if (!_runtimeAnimation.TryActivate(out detail))
                { RetainUnconfirmed(detail); return false; }
                if (Role != BoltMigrationRole.Destination
                    && !TryDrainAnimationTransitions(out detail))
                { RetainUnconfirmed(detail); return false; }
                GlobalEvents.OnLateUpdate.Subscribe(PumpRuntimeAnimation);
                _animationDispatchBound = true;
                detail = "Replacement animation activated with exclusive native publication and application.";
                return true;
            }
            catch (Exception error)
            {
                detail = Failure("Replacement animation activation unconfirmed", error);
                RetainUnconfirmed(detail);
                return false;
            }
        }

        private void PumpRuntimeAnimation()
        {
            try
            {
                if (_animationFrame == UnityEngine.Time.frameCount) return;
                _animationFrame = UnityEngine.Time.frameCount;
                if (!AnimationGameplayActive())
                { RetainUnconfirmed("Animation lost its exact completed player lifetime."); return; }
                if (Role != BoltMigrationRole.Destination
                    && !TryDrainAnimationTransitions(out var transitionDetail))
                { RetainUnconfirmed(transitionDetail); return; }
                if (Role != BoltMigrationRole.Observer
                    && (_armourRoute == null || !_armourRoute.TryPump(out _)))
                { RetainUnconfirmed(_armourRoute?.FailureReason ?? "The prepared armour route is unavailable."); return; }
                bool publishStats = Role != BoltMigrationRole.Observer;
                if (Role == BoltMigrationRole.Destination)
                {
                    if (_serverLife == null || _waterPresentation == null)
                    { RetainUnconfirmed("The complete server life/water owners are unavailable."); return; }
                    var boundary = _serverLife.TryPumpServerBoundary(out var lifeDetail);
                    if (boundary == PlayerLifeBoundaryResult.Faulted)
                    { RetainUnconfirmed(lifeDetail); return; }
                    publishStats = boundary == PlayerLifeBoundaryResult.Settled;
                    if (!_waterPresentation.TryPump(out var waterDetail))
                    { RetainUnconfirmed(waterDetail); return; }
                }
                else if (Role == BoltMigrationRole.Source
                    && (_lifeRoute == null || !_lifeRoute.TryPump(out _)))
                { RetainUnconfirmed(_lifeRoute?.FailureReason ?? "The source life route is unavailable."); return; }
                if (publishStats
                    && (_statProjection == null || !_statProjection.TryPump(out var statDetail)))
                {
                    RetainUnconfirmed(_statProjection?.FailureReason ?? "The prepared stat stream is unavailable.");
                    return;
                }
                if (Role == BoltMigrationRole.Destination)
                {
                    TryPumpServerCartPassengerExit(out var exitDetail);
                    if (_unconfirmed) { RetainUnconfirmed(exitDetail); return; }
                }
                var result = Role == BoltMigrationRole.Destination
                    ? _runtimeAnimation.TryPublishEvaluatedState(out var detail)
                    : _runtimeAnimation.TryApplyReplicaState(out detail);
                if (!Successful(result)) RetainUnconfirmed(detail);
            }
            catch (Exception error)
            { RetainUnconfirmed(Failure("Replacement animation update unconfirmed", error)); }
        }

        private void RetireRuntimeAnimation()
        {
            _observerEventRoute?.Dispose();
            _observerEventRoute = null;
            if (_animationDispatchBound)
            {
                GlobalEvents.OnLateUpdate.Unsubscribe(PumpRuntimeAnimation);
                _animationDispatchBound = false;
            }
            // Native finalizers still own their call scopes. Retain all gates and retire
            // the endpoint on the game loop after those scopes have unwound.
            if (HasActiveReplicaCall || PlayerPreservation.HasActiveNativeAnimationCall
                || _runtimeAnimation?.CanDispose == false)
            {
                if (!_animationRetirementDeferred)
                {
                    GlobalEvents.OnLateUpdate.Subscribe(RetireRuntimeAnimation);
                    _animationRetirementDeferred = true;
                }
                return;
            }
            if (_animationRetirementDeferred)
            {
                GlobalEvents.OnLateUpdate.Unsubscribe(RetireRuntimeAnimation);
                _animationRetirementDeferred = false;
            }
            try
            {
                if (_animationWritersPrepared) _replacementReplica?.QuarantineRuntimeAnimation();
            }
            catch (Exception error)
            { Detail += " Animation clock quarantine unconfirmed (" + error.GetType().Name + ")."; }
            finally
            {
                try
                {
                    _runtimeAnimation?.Dispose();
                    _runtimeAnimation = null;
                }
                catch (Exception error)
                { Detail += " Animation endpoint disposal unconfirmed (" + error.GetType().Name + ")."; }
            }
        }
    }
}
