// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerInventory
    {
        public const string ServiceKey = "openacai.sons.serverInventory";

        private static ServerInventoryState _state = new(
            InventoryHookStatus.NotAttempted, "The inventory routing hook has not been installed.");
        private static int _installationAttempted;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs before server dispatch of PlayerAddItem and ItemRemoveFromPlayer, including local sends.
        /// Rejection stops this event only; it does not restore earlier storage or inventory changes.
        /// </summary>
        public static event Action<InventoryRequest> Receiving;

        public static ServerInventoryState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(InventoryHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server inventory policy hook was not installed: " + detail);
                return;
            }

            Initialize();
            PublishState(InventoryHookStatus.Installing, "Registering the inventory routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.Inventory);
            }
            catch (Exception error)
            {
                PublishState(InventoryHookStatus.Failed,
                    "Inventory hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server inventory policy hook failed: " + error);
                return;
            }

            PublishState(InventoryHookStatus.Installed,
                "Inventory hook registration succeeded. Live transfer and policy handling remain unverified.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return DispatchVerdict.Forward;
            var grant = source.TryCast<PlayerAddItem>();
            var removal = grant == null ? source.TryCast<ItemRemoveFromPlayer>() : null;
            if (grant == null && removal == null)
                return DispatchVerdict.Forward;

            InventoryRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The inventory callback ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new InventoryRequest(source, grant, removal, connectionId,
                    registeredSteamId, ServerSessions.ResolveSource(connection));
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return DispatchVerdict.Reject;
                }

                return DispatchVerdict.Forward;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading inventory context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected inventory storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(InventoryHookStatus status, string detail)
        {
            var state = new ServerInventoryState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server inventory policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum InventoryOperation
    {
        Add,
        Remove
    }

    public enum InventoryHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerInventoryState
    {
        internal ServerInventoryState(InventoryHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of receipt, rejection or an inventory change.</summary>
        public InventoryHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Unvalidated routing and payload copies. No native event, entity or item instance is retained.</summary>
    public sealed class InventoryRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal InventoryRequest(Bolt.Event source, PlayerAddItem grant, ItemRemoveFromPlayer removal,
            uint? sourceConnectionId, ulong? registeredSteamId, ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RoutingTargets = source.Targets;
            var entity = source.TargetEntity;
            RoutedEntityNetworkId = entity == null ? null : entity.NetworkId.PackedValue;
            var connection = source.TargetConnection;
            RoutedConnectionId = connection == null ? null : connection.ConnectionId;

            if (grant != null)
            {
                Operation = InventoryOperation.Add;
                RequestedPlayerNetworkId = grant.GetPlayerNetworkIdValue().PackedValue;
                RequestedItemId = grant.ItemId;
                RequestedAmount = grant.Amount;
                var token = grant.ItemInstance;
                HasItemInstanceToken = token != null;
                RequestedInstanceItemId = token?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance?._itemID;

                var itemDescription = CaptureDescription(token, true);
                RequestedItemInstanceTypeName = itemDescription.TypeName;
                RequestedItemInstanceTypeNameReadSucceeded = itemDescription.TypeNameReadSucceeded;
                RequestedItemInstanceText = itemDescription.Text;
                RequestedItemInstanceTextReadSucceeded = itemDescription.TextReadSucceeded;

                object player = null;
                var playerReadSucceeded = false;
                try
                {
                    player = grant.Player;
                    playerReadSucceeded = true;
                }
                catch (Exception) { }
                var playerDescription = CaptureDescription(player, playerReadSucceeded);
                RequestedPlayerTypeName = playerDescription.TypeName;
                RequestedPlayerTypeNameReadSucceeded = playerDescription.TypeNameReadSucceeded;
                RequestedPlayerText = playerDescription.Text;
                RequestedPlayerTextReadSucceeded = playerDescription.TextReadSucceeded;
            }
            else
            {
                Operation = InventoryOperation.Remove;
                RequestedItemId = removal.ItemId;
            }
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        private static (string TypeName, bool TypeNameReadSucceeded, string Text, bool TextReadSucceeded)
            CaptureDescription(object value, bool sourceReadSucceeded)
        {
            if (!sourceReadSucceeded)
                return (null, false, null, false);
            if (value is null)
                return (null, true, null, true);

            string typeName = null;
            var typeNameReadSucceeded = false;
            try
            {
                var type = value.GetType();
                typeName = type.FullName ?? type.Name;
                typeNameReadSucceeded = true;
            }
            catch (Exception) { }

            string text = null;
            var textReadSucceeded = false;
            try
            {
                text = value.ToString();
                textReadSucceeded = true;
            }
            catch (Exception) { }
            return (typeName, typeNameReadSucceeded, text, textReadSucceeded);
        }

        /// <summary>Event type, not a verified inventory delta.</summary>
        public InventoryOperation Operation { get; }
        /// <summary>Immediate transport source, or null for server-local events. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available, not a permission verdict.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw routing mode; payload player and routing destination are separate fields.</summary>
        public int RoutingTargets { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
        /// <summary>Raw add-event player ID, including zero or unresolved IDs. Null for removal.</summary>
        public ulong? RequestedPlayerNetworkId { get; }
        /// <summary>Event item ID, separate from the add-event instance token's item ID.</summary>
        public int RequestedItemId { get; }
        /// <summary>Raw add-event amount. Null for removal, which has no quantity field.</summary>
        public int? RequestedAmount { get; }
        /// <summary>True when the add event carries a token, even if it has no readable item instance. False for removal.</summary>
        public bool HasItemInstanceToken { get; }
        /// <summary>Token item ID, or null if absent or incompatible. Does not validate modules or ownership.</summary>
        public int? RequestedInstanceItemId { get; }
        /// <summary>Managed type name of the add-event token, not its nested item instance. Null for removal.</summary>
        public string RequestedItemInstanceTypeName { get; }
        /// <summary>True for a captured type name or a successfully read null token. False for removal.</summary>
        public bool RequestedItemInstanceTypeNameReadSucceeded { get; }
        /// <summary>Unmodified add-event token text, not item identity or ownership evidence. Null for removal.</summary>
        public string RequestedItemInstanceText { get; }
        /// <summary>True when token text was captured, including a successful null. False for removal.</summary>
        public bool RequestedItemInstanceTextReadSucceeded { get; }
        /// <summary>Managed type name of the add-event player wrapper. Null for removal.</summary>
        public string RequestedPlayerTypeName { get; }
        /// <summary>True for a captured type name or a successfully read null player. False for removal.</summary>
        public bool RequestedPlayerTypeNameReadSucceeded { get; }
        /// <summary>Unmodified add-event player text, not authenticated identity. Null for removal.</summary>
        public string RequestedPlayerText { get; }
        /// <summary>True when player text was captured, including a successful null. False for removal.</summary>
        public bool RequestedPlayerTextReadSucceeded { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this dispatch without a denial reply, refund or rollback of prior effects.
        /// Returns false after dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close()
        {
            _acceptingDecisions = false;
        }
    }
}
