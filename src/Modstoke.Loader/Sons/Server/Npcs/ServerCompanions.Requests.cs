// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons
{
    public enum CompanionHookStatus { NotAttempted, Disabled, Installing, Installed, Failed }

    public sealed class ServerCompanionState
    {
        internal ServerCompanionState(CompanionHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }
        public CompanionHookStatus Status { get; }
        public string Detail { get; }
    }

    public enum CompanionValidationStatus
    {
        Valid,
        SessionUnavailable,
        PlayerUnavailable,
        PlayerMismatch,
        TargetUnavailable,
        TargetNotServerOwned,
        InvalidRouting,
        InvalidEvent,
        InvalidOrder,
        InvalidSubtype,
        OrderDataUnavailable,
        ContextChanged,
        Failed,
        DecisionClosed
    }

    /// <summary>
    /// Copied fields from a remote RobbyOrderEvent before server routing. IDs and order fields are
    /// claims until checked. This is an interaction attempt, not an accepted or completed job.
    /// </summary>
    public sealed class CompanionRequest
    {
        private readonly int _dispatchThread = Environment.CurrentManagedThreadId;
        private Func<CompanionValidationStatus> _validate;
        private bool _acceptingDecisions = true;
        private bool _rejected;
        private bool _requireValid;
        private bool _requireNotInvalid;

        internal CompanionRequest(uint sourceConnectionId, ServerSession session,
            ulong targetId, ulong playerId, int eventType, int orderType, int subtype1, int subtype2,
            int targets, Func<CompanionValidationStatus> validate)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = session;
            RequestedTargetNetworkId = targetId;
            RequestedPlayerNetworkId = playerId;
            RequestedEventType = eventType;
            RequestedOrderType = orderType;
            RequestedSubType1 = subtype1;
            RequestedSubType2 = subtype2;
            RoutingTargets = targets;
            ReceivedAt = DateTimeOffset.UtcNow;
            _validate = validate;
        }

        public uint SourceConnectionId { get; }
        /// <summary>Exact live connection handle when available; not proof of a Steam account or permissions.</summary>
        public ServerSession SourceSession { get; }
        public ulong RequestedTargetNetworkId { get; }
        public ulong RequestedPlayerNetworkId { get; }
        public int RequestedEventType { get; }
        public int RequestedOrderType { get; }
        public int RequestedSubType1 { get; }
        public int RequestedSubType2 { get; }
        public int RoutingTargets { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);
        internal bool ValidationRequired => _requireValid || _requireNotInvalid;

        /// <summary>
        /// True for statuses that judge the request itself. Unavailable, changed or failed reads say
        /// nothing about the request and are never verdicts.
        /// </summary>
        public static bool IsInvalidVerdict(CompanionValidationStatus status) =>
            status is CompanionValidationStatus.PlayerMismatch or CompanionValidationStatus.TargetNotServerOwned
                or CompanionValidationStatus.InvalidRouting or CompanionValidationStatus.InvalidEvent
                or CompanionValidationStatus.InvalidOrder or CompanionValidationStatus.InvalidSubtype;

        /// <summary>
        /// Requires a current source/player binding, server-owned Kelvin target, server-only routing
        /// and valid native order indices. Failure rejects this dispatch. Successful checks repeat
        /// after subscribers return, before native forwarding or mutation. This does not authorize
        /// proximity, interaction ownership, resources, injury recovery or job completion.
        /// </summary>
        public CompanionValidationStatus RequireValidNativeRequest()
        {
            if (!CanDecide()) return CompanionValidationStatus.DecisionClosed;
            _requireValid = true;
            var status = _validate();
            if (status != CompanionValidationStatus.Valid) Reject();
            return status;
        }

        /// <summary>
        /// Runs the same checks but rejects only an invalid verdict (see <see cref="IsInvalidVerdict"/>).
        /// A read that cannot be completed leaves this dispatch to native handling. The check repeats
        /// after subscribers return; RequireValidNativeRequest, if also called, still applies.
        /// </summary>
        public CompanionValidationStatus RejectInvalidNativeRequest()
        {
            if (!CanDecide()) return CompanionValidationStatus.DecisionClosed;
            _requireNotInvalid = true;
            var status = _validate();
            if (IsInvalidVerdict(status)) Reject();
            return status;
        }

        /// <summary>Runs the same checks without deciding or requiring anything.</summary>
        public CompanionValidationStatus CheckNativeRequest() =>
            CanDecide() ? _validate() : CompanionValidationStatus.DecisionClosed;

        // The post-subscriber recheck applies the strictest requirement any subscriber set.
        internal bool PassesRequiredValidation() => _requireValid
            ? RequireValidNativeRequest() == CompanionValidationStatus.Valid
            : !IsInvalidVerdict(RejectInvalidNativeRequest());

        /// <summary>Rejects this delivery only. Rejection is permanent and does not undo client prediction.</summary>
        public bool Reject()
        {
            if (!CanDecide()) return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        private bool CanDecide() => Environment.CurrentManagedThreadId == _dispatchThread && _acceptingDecisions;

        internal void Close()
        {
            _acceptingDecisions = false;
            _validate = null;
        }
    }
}
