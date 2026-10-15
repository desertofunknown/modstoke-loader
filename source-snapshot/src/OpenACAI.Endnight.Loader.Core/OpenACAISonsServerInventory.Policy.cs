// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Inventory;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerInventory
    {
        public const string ServiceKey = "openacai.sons.serverInventory";

        private static OpenACAISonsServerInventoryState _state = new(
            OpenACAISonsInventoryHookStatus.NotAttempted, "The inventory routing hook has not been installed.");
        private static int _installationAttempted;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs before server dispatch of PlayerAddItem and ItemRemoveFromPlayer, including local sends.
        /// Rejection stops this event only; it does not restore earlier storage or inventory changes.
        /// </summary>
        public static event Action<OpenACAISonsInventoryRequest> Receiving;

        public static OpenACAISonsServerInventoryState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsInventoryHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server inventory policy hook was not installed: " + detail);
                return;
            }

            Initialize();
            PublishState(OpenACAISonsInventoryHookStatus.Installing, "Registering the inventory routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerInventory), nameof(BeforeDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-inventory");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsInventoryHookStatus.Failed,
                    "Inventory hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server inventory policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsInventoryHookStatus.Installed,
                "Inventory hook registration succeeded. Live transfer and policy handling remain unverified.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            var grant = __0?.TryCast<PlayerAddItem>();
            var removal = grant == null ? __0?.TryCast<ItemRemoveFromPlayer>() : null;
            if (grant == null && removal == null)
                return true;

            var forward = false;
            OpenACAISonsInventoryRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The inventory callback ran outside the loader's game thread.");
                    return false;
                }

                var connection = __0.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                    && steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;

                request = new OpenACAISonsInventoryRequest(__0, grant, removal, connectionId,
                    registeredSteamId, OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsInventoryRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                forward = true;
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading inventory context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Rejected dispatch transfers no storage to native listeners or outgoing channels.
                    try { __0.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected inventory storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static void PublishState(OpenACAISonsInventoryHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerInventoryState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server inventory policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsInventoryOperation
    {
        Add,
        Remove
    }

    public enum OpenACAISonsInventoryHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerInventoryState
    {
        internal OpenACAISonsServerInventoryState(OpenACAISonsInventoryHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration, not evidence of receipt, rejection or an inventory change.</summary>
        public OpenACAISonsInventoryHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Unvalidated routing and payload copies. No native event, entity or item instance is retained.</summary>
    public sealed class OpenACAISonsInventoryRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsInventoryRequest(Bolt.Event source, PlayerAddItem grant, ItemRemoveFromPlayer removal,
            uint? sourceConnectionId, ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
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
                Operation = OpenACAISonsInventoryOperation.Add;
                RequestedPlayerNetworkId = grant.GetPlayerNetworkIdValue().PackedValue;
                RequestedItemId = grant.ItemId;
                RequestedAmount = grant.Amount;
                var token = grant.ItemInstance;
                HasItemInstanceToken = token != null;
                RequestedInstanceItemId = token?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance?._itemID;
            }
            else
            {
                Operation = OpenACAISonsInventoryOperation.Remove;
                RequestedItemId = removal.ItemId;
            }
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Event type, not a verified inventory delta.</summary>
        public OpenACAISonsInventoryOperation Operation { get; }
        /// <summary>Immediate transport source, or null for server-local events. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available, not a permission verdict.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
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
