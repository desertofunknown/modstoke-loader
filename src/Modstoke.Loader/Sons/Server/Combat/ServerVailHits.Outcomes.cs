// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ServerVailHits
    {
        private static int _reportedOutcomeFailure;

        /// <summary>
        /// Raised on the game thread after the server's VailHitEvent callback returns or throws, for callbacks
        /// that began while this event had subscribers. Observation only: handlers cannot change the outcome,
        /// and their exceptions are isolated and logged once. Live only while State reports Installed.
        /// The listen host's own hits, damage over time and dismember kills never raise this event.
        /// </summary>
        public static event Action<VailHitOutcome> Processed;

        private static VailHitOutcomeScope BeginOutcome(VailHitEvent hit, VailHitRequest request)
        {
            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId || !BoltNetwork.isServer)
                    return null;
                if (request == null)
                {
                    request = CreateRequest(hit);
                    request.Close();
                }
                var scope = new VailHitOutcomeScope(request);
                if (!request.IsRejected)
                    scope.CaptureBefore();
                return scope;
            }
            catch (Exception error)
            {
                ReportOutcomeFailure(error);
                return null;
            }
        }

        private static void AfterHit(bool __runOriginal, VailHitOutcomeScope __state)
        {
            if (__state != null)
                __state.RanOriginal = __runOriginal;
        }

        private static Exception FinishHit(Exception __exception, VailHitOutcomeScope __state)
        {
            if (__state != null)
                RaiseOutcome(__state, __exception != null);
            return __exception;
        }

        private static void RaiseOutcome(VailHitOutcomeScope scope, bool faulted)
        {
            var handlers = Processed;
            if (handlers == null || Environment.CurrentManagedThreadId != _ownerThreadId)
                return;
            VailHitOutcome outcome;
            try
            {
                outcome = scope.Complete(faulted);
            }
            catch (Exception error)
            {
                ReportOutcomeFailure(error);
                return;
            }
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(outcome);
                }
                catch (Exception error)
                {
                    ReportOutcomeFailure(error);
                }
            }
        }

        private static void ReportOutcomeFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedOutcomeFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server Vail-hit outcome observation failed ("
                    + error.GetType().FullName + "). Further outcome failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class VailHitOutcomeScope
        {
            private ServerWorld.NpcActorProbe _target;
            private float? _healthBefore;
            private bool? _deadBefore;

            internal VailHitOutcomeScope(VailHitRequest request)
            {
                Request = request;
            }

            internal VailHitRequest Request { get; }
            internal bool? RanOriginal { get; set; }

            internal void CaptureBefore()
            {
                if (ServerWorld.NpcActorProbe.TryBind(Request.RequestedTargetNetworkId, out var probe)
                        != NpcStateReadStatus.Success
                    || probe.TryRead(out var health, out _, out var dead) != NpcStateReadStatus.Success)
                    return;
                _target = probe;
                _healthBefore = health;
                _deadBefore = dead;
            }

            internal VailHitOutcome Complete(bool faulted)
            {
                float? healthAfter = null;
                bool? deadAfter = null;
                var changed = false;
                if (_target != null && !Request.IsRejected && (faulted || RanOriginal == true))
                {
                    if (!_target.IsCurrent())
                        changed = true;
                    else if (_target.TryRead(out var health, out _, out var dead) == NpcStateReadStatus.Success)
                    {
                        healthAfter = health;
                        deadAfter = dead;
                    }
                    else if (!_target.IsCurrent())
                        changed = true;
                }

                var status = Request.IsRejected ? VailHitOutcomeStatus.Rejected
                    : faulted ? VailHitOutcomeStatus.NativeException
                    : RanOriginal != true ? VailHitOutcomeStatus.NotObserved
                    : changed ? VailHitOutcomeStatus.ActorChanged
                    : !_healthBefore.HasValue || !healthAfter.HasValue ? VailHitOutcomeStatus.ActorUnavailable
                    : healthAfter.Value < _healthBefore.Value ? VailHitOutcomeStatus.Applied
                    : VailHitOutcomeStatus.NoHealthChange;
                return new VailHitOutcome(Request, status, _healthBefore, healthAfter,
                    _deadBefore.HasValue && deadAfter.HasValue ? !_deadBefore.Value && deadAfter.Value : null,
                    UnityEngine.Time.frameCount);
            }
        }
    }

    public enum VailHitOutcomeStatus
    {
        /// <summary>The target's health stat was lower after the callback than before it.</summary>
        Applied,
        /// <summary>The callback ran and the health stat did not decrease. Not proof the hit was invalid.</summary>
        NoHealthChange,
        /// <summary>A Receiving policy rejected the request, so the native callback was skipped.</summary>
        Rejected,
        /// <summary>No single actor health stat could be read for the requested target, before or after.</summary>
        ActorUnavailable,
        /// <summary>The resolved actor, its entity or its network ID changed during the callback.</summary>
        ActorChanged,
        /// <summary>An exception escaped the callback or a later patch; health after is read when possible.</summary>
        NativeException,
        /// <summary>Another patch skipped the native callback; nothing was measured.</summary>
        NotObserved
    }

    /// <summary>
    /// Managed copies describing one server VailHitEvent callback, with no retained native objects.
    /// Health values are the target's health stat measured around this one synchronous callback and are
    /// attributed only to the event's transport source. They are not damage dealt and are no verdict:
    /// overkill, prevented death, armour, blocking and later status effects all shape or hide the change.
    /// </summary>
    public sealed class VailHitOutcome
    {
        internal VailHitOutcome(VailHitRequest request, VailHitOutcomeStatus status, float? healthBefore,
            float? healthAfter, bool? diedDuringCallback, int frame)
        {
            SourceSession = request.SourceSession;
            SourceConnectionId = request.SourceConnectionId;
            RegisteredSteamId = request.RegisteredSteamId;
            TargetNetworkId = request.RequestedTargetNetworkId;
            RequestedDamage = request.RequestedDamage;
            RequestedDamageNodeId = request.RequestedDamageNodeId;
            WasRejected = request.IsRejected;
            Status = status;
            HealthBefore = healthBefore;
            HealthAfter = healthAfter;
            if (healthBefore.HasValue && healthAfter.HasValue)
                HealthRemoved = Math.Max(0f, healthBefore.Value - healthAfter.Value);
            DiedDuringCallback = diedDuringCallback;
            Frame = frame;
        }

        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Sender-selected target ID, as in VailHitRequest.RequestedTargetNetworkId.</summary>
        public ulong TargetNetworkId { get; }
        /// <summary>Sender-written damage field, not the resulting health change.</summary>
        public float RequestedDamage { get; }
        /// <summary>Sender-written damage-node selection, not proof the hit reached that node.</summary>
        public int RequestedDamageNodeId { get; }
        public bool WasRejected { get; }
        public VailHitOutcomeStatus Status { get; }
        /// <summary>Raw health stat before the callback, or null when not captured.</summary>
        public float? HealthBefore { get; }
        /// <summary>Raw health stat after the callback, or null when unavailable or the actor changed.</summary>
        public float? HealthAfter { get; }
        /// <summary>max(0, before - after) across this one synchronous callback. Not "damage dealt".</summary>
        public float? HealthRemoved { get; }
        /// <summary>True when the raw _isDead flag changed from false to true during the callback; null when unread.</summary>
        public bool? DiedDuringCallback { get; }
        /// <summary>Unity frame when the callback finished, not a Bolt or simulation frame.</summary>
        public int Frame { get; }
    }
}
