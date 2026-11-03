// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons
{
    public enum PrinterHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerPrinterState
    {
        internal ServerPrinterState(PrinterHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public PrinterHookStatus Status { get; }
        public string Detail { get; }
    }

    public enum PrinterOperation
    {
        RecipeSelection,
        PrintStart,
        LocalRecipeCycle
    }

    /// <summary>
    /// Identifies the observed route. Server-local dispatch or interaction is not authenticated host input.
    /// </summary>
    public enum PrinterOrigin
    {
        RemoteDispatch,
        ServerLocalDispatch,
        ServerLocalInteraction,
        /// <summary>An SDK device command attributed to one exact current player.</summary>
        DeviceCommand
    }

    /// <summary>
    /// One printer operation attempt, with a synchronous decision window.
    /// Target, recipe, and routing values are raw claims, not proof of authority or validated printer state.
    /// This request is not a production, completion, or refund receipt.
    /// </summary>
    public sealed class PrinterRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal PrinterRequest(PrinterOperation operation,
            PrinterOrigin origin, ServerSession sourceSession, ulong targetNetworkId,
            int? requestedRecipeId, int? routingTargets, DateTimeOffset receivedAt,
            HostPlayer sourceHostPlayer = null)
        {
            Operation = operation;
            Origin = origin;
            SourceSession = sourceSession;
            SourceHostPlayer = sourceHostPlayer;
            RequestedTargetNetworkId = targetNetworkId;
            RequestedRecipeId = requestedRecipeId;
            RoutingTargets = routingTargets;
            ReceivedAt = receivedAt;
        }

        public PrinterOperation Operation { get; }
        public PrinterOrigin Origin { get; }
        public ServerSession SourceSession { get; }
        /// <summary>Only populated for an explicitly attributed DeviceCommand; null never implies host.</summary>
        public HostPlayer SourceHostPlayer { get; }
        public ulong RequestedTargetNetworkId { get; }
        public int? RequestedRecipeId { get; }
        public int? RoutingTargets { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips only this delivery or local operation; it does not roll back client prediction,
        /// refund resources, or establish production completion.
        /// Returns false after synchronous dispatch or on another thread. Rejection cannot be undone.
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
}
