// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server inventory requests and opt-in routing policy using the game's existing client protocol.</summary>
    public static partial class ServerInventory
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
        public static ItemGrantResult QueueItemGrant(ServerSession recipient,
            int itemId, int amount = 1)
        {
            var queuedCount = 0;
            ItemGrantResult Result(ItemGrantStatus status) => new(status, amount, queuedCount);

            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0 || !ServerSessions.HooksInstalled)
                return Result(ItemGrantStatus.Unavailable);
            if (Environment.CurrentManagedThreadId != ownerThread)
                return Result(ItemGrantStatus.WrongThread);
            if (itemId <= 0 || itemId > MaxItemId)
                return Result(ItemGrantStatus.InvalidItemId);
            if (amount <= 0 || amount > MaxGrantAmount)
                return Result(ItemGrantStatus.InvalidAmount);

            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return Result(ItemGrantStatus.NotServer);
                if (recipient == null)
                    return Result(ItemGrantStatus.SessionUnavailable);

                // Read the current database, not a previously published metadata snapshot.
                var data = ItemDatabaseManager.ItemById(itemId);
                if (data is null || data.WasCollected || !data || data.Id != itemId)
                    return Result(ItemGrantStatus.ItemUnavailable);

                // Vanilla's non-bundled overflow branch can drop the original bulk count after partial acceptance.
                // Separate one-item events avoid that count discrepancy without changing the client.
                while (queuedCount < amount)
                {
                    var status = QueueOne(recipient, itemId);
                    if (status != ItemGrantStatus.Queued)
                        return Result(status);
                    queuedCount++;
                }
                return Result(ItemGrantStatus.Queued);
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return Result(ItemGrantStatus.Failed);
            }
        }

        /// <summary>
        /// Requests removal of one item by ID from an exact remote session, including vanilla clients.
        /// The native receiver chooses the matching item and applies its normal inventory and equipment rules.
        /// Queued does not confirm removal. This cannot select an item instance, verify possession or enforce
        /// confiscation against a modified client. The caller owns authorization; no automatic retry is made.
        /// </summary>
        public static ItemRemovalStatus QueueItemRemoval(ServerSession recipient, int itemId)
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0 || !ServerSessions.HooksInstalled)
                return ItemRemovalStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return ItemRemovalStatus.WrongThread;
            if (itemId <= 0)
                return ItemRemovalStatus.InvalidItemId;

            ItemRemoveFromPlayer request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return ItemRemovalStatus.NotServer;
                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return ServerSessions.HooksInstalled
                        ? ItemRemovalStatus.SessionUnavailable : ItemRemovalStatus.Unavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return ItemRemovalStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return ItemRemovalStatus.PlayerUnavailable;

                // This event carries a full int32 ID, unlike the clamped PlayerAddItem field.
                var data = ItemDatabaseManager.ItemById(itemId);
                if (data is null || data.WasCollected || !data || data.Id != itemId)
                    return ItemRemovalStatus.ItemUnavailable;

                request = ItemRemoveFromPlayer.Create(connection, ReliabilityModes.ReliableOrdered);
                request.ItemId = itemId;

                var current = ServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return ServerSessions.HooksInstalled
                        ? ItemRemovalStatus.SessionUnavailable : ItemRemovalStatus.Unavailable;
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (currentPlayer is null || currentPlayer.WasCollected || !currentPlayer || !currentPlayer.isAttached
                    || currentPlayer.Pointer != player.Pointer)
                    return ItemRemovalStatus.PlayerUnavailable;
                var currentSource = currentPlayer.source;
                if (currentSource == null || currentSource.Pointer != current.Pointer
                    || currentSource.ConnectionId != current.ConnectionId)
                    return ItemRemovalStatus.PlayerUnavailable;

                // There is no player-entity field: delivery operates on the recipient's then-current local inventory.
                sendAttempted = true;
                request.Send();
                return ItemRemovalStatus.Queued;
            }
            catch (Exception error)
            {
                ReportRemovalFailure(error);
                return sendAttempted ? ItemRemovalStatus.SendUncertain : ItemRemovalStatus.Failed;
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

        private static ItemGrantStatus QueueOne(ServerSession recipient, int itemId)
        {
            PlayerAddItem request = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return ItemGrantStatus.NotServer;
                var instance = new ItemInstance(itemId);
                var token = instance.GetBoltDataToken();
                if (token is null || token.WasCollected)
                    return ItemGrantStatus.ItemUnavailable;

                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return ServerSessions.HooksInstalled
                        ? ItemGrantStatus.SessionUnavailable : ItemGrantStatus.Unavailable;
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (player is null || player.WasCollected || !player || !player.isAttached)
                    return ItemGrantStatus.PlayerUnavailable;
                var source = player.source;
                if (source == null || source.Pointer != connection.Pointer || source.ConnectionId != connection.ConnectionId)
                    return ItemGrantStatus.PlayerUnavailable;

                request = PlayerAddItem.Create(connection, ReliabilityModes.ReliableOrdered);
                request.Player = player;
                request.ItemId = itemId;
                request.Amount = 1;
                request.ItemInstance = token.Cast<IProtocolToken>();

                // Once Send starts, an exception cannot establish that the event was not queued.
                sendAttempted = true;
                request.Send();
                return ItemGrantStatus.Queued;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return sendAttempted ? ItemGrantStatus.SendUncertain : ItemGrantStatus.Failed;
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
                RuntimeEntryPoint.LogSource?.LogWarning("Server item removal failed ("
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
                RuntimeEntryPoint.LogSource?.LogWarning("Server item grant failed ("
                    + error.GetType().FullName + "). Further grant failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ItemRemovalStatus
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
    public sealed class ItemGrantResult
    {
        internal ItemGrantResult(ItemGrantStatus status, int requestedAmount, int queuedCount)
        {
            Status = status;
            RequestedAmount = requestedAmount;
            QueuedCount = queuedCount;
        }

        public ItemGrantStatus Status { get; }
        /// <summary>The caller's original amount, including invalid input.</summary>
        public int RequestedAmount { get; }
        /// <summary>Number of one-item Send calls that returned. Earlier sends are not rolled back.</summary>
        public int QueuedCount { get; }
        /// <summary>One additional Send threw and might have queued its item.</summary>
        public bool HasUncertainSend => Status == ItemGrantStatus.SendUncertain;
    }

    public enum ItemGrantStatus
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
