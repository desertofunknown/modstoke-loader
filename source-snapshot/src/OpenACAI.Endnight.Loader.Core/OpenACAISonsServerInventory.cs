// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using OpenACAI.Endnight.Loader;
using Sons.Inventory;
using Sons.Items.Core;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Server inventory requests and opt-in routing policy using the game's existing client protocol.</summary>
    public static partial class OpenACAISonsServerInventory
    {
        /// <summary>Largest item ID preserved by the native PlayerAddItem setter.</summary>
        public const int MaxItemId = 2047;
        /// <summary>Per-call SDK limit, not a wire-format or client inventory capacity.</summary>
        public const int MaxGrantAmount = 1024;

        private static int _ownerThreadId;
        private static int _reportedFailure;
        private static int _reportedRemovalFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Queues reliable one-item requests to an exact remote session, including vanilla clients.
        /// Queued counts do not confirm receipt, accepted quantity or inventory state. No automatic retry is made.
        /// The caller owns authorization; this does not debit storage or restore a removed item instance.
        /// </summary>
        public static OpenACAISonsItemGrantResult QueueItemGrant(OpenACAISonsServerSession recipient,
            int itemId, int amount = 1)
        {
            var queuedCount = 0;
            OpenACAISonsItemGrantResult Result(OpenACAISonsItemGrantStatus status) => new(status, amount, queuedCount);

            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0 || !OpenACAISonsServerSessions.HooksInstalled)
                return Result(OpenACAISonsItemGrantStatus.Unavailable);
            if (Environment.CurrentManagedThreadId != ownerThread)
                return Result(OpenACAISonsItemGrantStatus.WrongThread);
            if (itemId <= 0 || itemId > MaxItemId)
                return Result(OpenACAISonsItemGrantStatus.InvalidItemId);
            if (amount <= 0 || amount > MaxGrantAmount)
                return Result(OpenACAISonsItemGrantStatus.InvalidAmount);

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return Result(OpenACAISonsItemGrantStatus.NotServer);
                if (recipient == null)
                    return Result(OpenACAISonsItemGrantStatus.SessionUnavailable);

                // Read the current database, not a previously published metadata snapshot.
                var data = ItemDatabaseManager.ItemById(itemId);
                if (data is null || data.WasCollected || !data || data.Id != itemId)
                    return Result(OpenACAISonsItemGrantStatus.ItemUnavailable);

                // Vanilla's non-bundled overflow branch can drop the original bulk count after partial acceptance.
                // Separate one-item events avoid that count discrepancy without changing the client.
                while (queuedCount < amount)
                {
                    var status = QueueOne(recipient, itemId);
                    if (status != OpenACAISonsItemGrantStatus.Queued)
                        return Result(status);
                    queuedCount++;
                }
                return Result(OpenACAISonsItemGrantStatus.Queued);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(OpenACAISonsItemGrantStatus.Failed);
            }
        }

        /// <summary>
        /// Requests removal of one item by ID from an exact remote session, including vanilla clients.
        /// The native receiver chooses the matching item and applies its normal inventory and equipment rules.
        /// Queued does not confirm removal. This cannot select an item instance, verify possession or enforce
        /// confiscation against a modified client. The caller owns authorization; no automatic retry is made.
        /// </summary>
        public static OpenACAISonsItemRemovalStatus QueueItemRemoval(OpenACAISonsServerSession recipient, int itemId)
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0 || !OpenACAISonsServerSessions.HooksInstalled)
                return OpenACAISonsItemRemovalStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsItemRemovalStatus.WrongThread;
            if (itemId <= 0)
                return OpenACAISonsItemRemovalStatus.InvalidItemId;

            ItemRemoveFromPlayer request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsItemRemovalStatus.NotServer;
                var connection = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsItemRemovalStatus.SessionUnavailable : OpenACAISonsItemRemovalStatus.Unavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return OpenACAISonsItemRemovalStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsItemRemovalStatus.PlayerUnavailable;

                // This event carries a full int32 ID, unlike the clamped PlayerAddItem field.
                var data = ItemDatabaseManager.ItemById(itemId);
                if (data is null || data.WasCollected || !data || data.Id != itemId)
                    return OpenACAISonsItemRemovalStatus.ItemUnavailable;

                request = ItemRemoveFromPlayer.Create(connection, ReliabilityModes.ReliableOrdered);
                request.ItemId = itemId;

                var current = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsItemRemovalStatus.SessionUnavailable : OpenACAISonsItemRemovalStatus.Unavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return OpenACAISonsItemRemovalStatus.PlayerUnavailable;
                var currentSource = currentPlayer.source;
                if (currentSource == null || currentSource.Pointer != current.Pointer
                    || currentSource.ConnectionId != current.ConnectionId)
                    return OpenACAISonsItemRemovalStatus.PlayerUnavailable;

                // There is no player-entity field: delivery operates on the recipient's then-current local inventory.
                sendAttempted = true;
                request.Send();
                return OpenACAISonsItemRemovalStatus.Queued;
            }
            catch (Exception error)
            {
                ReportRemovalFailure(error);
                return sendAttempted ? OpenACAISonsItemRemovalStatus.SendUncertain : OpenACAISonsItemRemovalStatus.Failed;
            }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception error) { ReportRemovalFailure(error); }
                }
            }
        }

        private static OpenACAISonsItemGrantStatus QueueOne(OpenACAISonsServerSession recipient, int itemId)
        {
            PlayerAddItem request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsItemGrantStatus.NotServer;
                var instance = new ItemInstance(itemId);
                var token = instance.GetBoltDataToken();
                if (token is null || token.WasCollected)
                    return OpenACAISonsItemGrantStatus.ItemUnavailable;

                var connection = OpenACAISonsServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsItemGrantStatus.SessionUnavailable : OpenACAISonsItemGrantStatus.Unavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return OpenACAISonsItemGrantStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return OpenACAISonsItemGrantStatus.PlayerUnavailable;

                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = player;
                request.ItemId = itemId;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();

                // Once Send starts, an exception cannot establish that the event was not queued.
                sendAttempted = true;
                request.Send();
                return OpenACAISonsItemGrantStatus.Queued;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return sendAttempted ? OpenACAISonsItemGrantStatus.SendUncertain : OpenACAISonsItemGrantStatus.Failed;
            }
            finally
            {
                if (request != null && !sendAttempted)
                {
                    try { request.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
        }

        private static void ReportRemovalFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRemovalFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server item removal failed ("
                    + error.GetType().FullName + "). Further removal failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server item grant failed ("
                    + error.GetType().FullName + "). Further grant failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsItemRemovalStatus
    {
        /// <summary>The native send call returned, not proof of receipt or removal.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidItemId,
        SessionUnavailable,
        PlayerUnavailable,
        ItemUnavailable,
        /// <summary>Preparation failed before Send was attempted.</summary>
        Failed,
        /// <summary>Send threw and may have queued the request. Retrying can remove another item.</summary>
        SendUncertain
    }

    /// <summary>Managed send progress, not a client inventory receipt. Non-Queued results can have queued items.</summary>
    public sealed class OpenACAISonsItemGrantResult
    {
        internal OpenACAISonsItemGrantResult(OpenACAISonsItemGrantStatus status, int requestedAmount, int queuedCount)
        {
            Status = status;
            RequestedAmount = requestedAmount;
            QueuedCount = queuedCount;
        }

        public OpenACAISonsItemGrantStatus Status { get; }
        /// <summary>The caller's original amount, including invalid input.</summary>
        public int RequestedAmount { get; }
        /// <summary>Number of one-item Send calls that returned. Earlier sends are not rolled back.</summary>
        public int QueuedCount { get; }
        /// <summary>One additional Send threw and might have queued its item.</summary>
        public bool HasUncertainSend => Status == OpenACAISonsItemGrantStatus.SendUncertain;
    }

    public enum OpenACAISonsItemGrantStatus
    {
        /// <summary>All requested one-item sends returned, not proof of client receipt or inventory acceptance.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        InvalidItemId,
        InvalidAmount,
        SessionUnavailable,
        PlayerUnavailable,
        ItemUnavailable,
        /// <summary>Preparation failed for the next item. Earlier queued items remain queued.</summary>
        Failed,
        /// <summary>Send threw; it may have queued the request. Retrying can duplicate a grant.</summary>
        SendUncertain
    }
}
