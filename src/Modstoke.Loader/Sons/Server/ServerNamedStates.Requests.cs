// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;

namespace Modstoke.Sons
{
    public enum NamedStateHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerNamedStateState
    {
        internal ServerNamedStateState(NamedStateHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public NamedStateHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>The observed route. Server-local dispatch is not authenticated host input.</summary>
    public enum NamedStateOrigin
    {
        RemoteDispatch,
        ServerLocalDispatch
    }

    /// <summary>
    /// One native named-state change attempt with a synchronous decision window.
    /// A registered name is not proof of a live component, door, keycard requirement, valid state value,
    /// or permission. Numeric indices are transient registry positions, never durable identifiers.
    /// </summary>
    public sealed class NamedStateRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal NamedStateRequest(int requestedIndex, int requestedValue, string registeredName,
            NamedStateOrigin origin, ServerSession sourceSession,
            int routingTargets, DateTimeOffset receivedAt)
        {
            RequestedIndex = requestedIndex;
            RequestedValue = requestedValue;
            RegisteredName = registeredName;
            Origin = origin;
            SourceSession = sourceSession;
            RoutingTargets = routingTargets;
            ReceivedAt = receivedAt;
        }

        public int RequestedIndex { get; }
        public int RequestedValue { get; }
        /// <summary>Copied registry name, or null if unresolved; not typed object or state-validity proof.</summary>
        public string RegisteredName { get; }
        public NamedStateOrigin Origin { get; }
        public ServerSession SourceSession { get; }
        public int RoutingTargets { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this dispatch; does not undo sender-local state. Returns false after the callback
        /// window or on another thread. Rejection cannot be undone and is not a completion receipt.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;
    }

    public enum NamedStateReadStatus
    {
        Success,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidRegistry
    }

    /// <summary>A copied registry entry. The index is transient and the name does not identify a proven live component.</summary>
    public sealed class NamedStateDescriptor
    {
        internal NamedStateDescriptor(int index, string name)
        {
            Index = index;
            Name = name;
        }

        public int Index { get; }
        public string Name { get; }
    }

    /// <summary>
    /// Immutable registry metadata observed on the game thread, not current state values or proof
    /// of live components, doors, keycard requirements, or valid state transitions.
    /// </summary>
    public sealed class NamedStateRegistrySnapshot
    {
        internal NamedStateRegistrySnapshot(NamedStateDescriptor[] entries,
            DateTimeOffset observedAt)
        {
            Entries = Array.AsReadOnly((NamedStateDescriptor[])entries.Clone());
            ObservedAt = observedAt;
        }

        public IReadOnlyList<NamedStateDescriptor> Entries { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
