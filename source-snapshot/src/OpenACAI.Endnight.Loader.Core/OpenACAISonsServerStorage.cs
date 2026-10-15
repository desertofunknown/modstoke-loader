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
    /// <summary>Policy before the native server handles structure-storage add and remove requests.</summary>
    public static class OpenACAISonsServerStorage
    {
        public const string ServiceKey = "openacai.sons.serverStorage";

        private static OpenACAISonsServerStorageState _state = new(
            OpenACAISonsStorageHookStatus.NotAttempted, "The add hook has not been installed.",
            OpenACAISonsStorageHookStatus.NotAttempted, "The remove hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the matching CoopServerCallbacks.OnEvent body.
        /// Rejection skips storage changes and that body's replies or fallback pickups, without a refund.
        /// </summary>
        public static event Action<OpenACAISonsStorageRequest> Receiving;

        public static OpenACAISonsServerStorageState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsStorageOperation.Add, OpenACAISonsStorageHookStatus.Disabled, detail);
                PublishState(OpenACAISonsStorageOperation.Remove, OpenACAISonsStorageHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server storage policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(OpenACAISonsStorageOperation.Add, typeof(ItemStorageAddItemEvent), nameof(BeforeAdd), addStartupWarning);
            InstallHook(OpenACAISonsStorageOperation.Remove, typeof(ItemStorageRemoveItemEvent), nameof(BeforeRemove), addStartupWarning);
        }

        private static void InstallHook(OpenACAISonsStorageOperation operation, Type eventType, string prefixName,
            Action<string> addStartupWarning)
        {
            PublishState(operation, OpenACAISonsStorageHookStatus.Installing, "Registering the storage hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerStorage), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-storage");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(operation, OpenACAISonsStorageHookStatus.Failed,
                    "Storage hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server storage " + operation + " policy hook failed: " + error);
                return;
            }

            PublishState(operation, OpenACAISonsStorageHookStatus.Installed,
                "Storage hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeAdd(ItemStorageAddItemEvent __0) => BeforeRequest(__0);
        private static bool BeforeRemove(ItemStorageRemoveItemEvent __0) => BeforeRequest(__0);

        private static bool BeforeRequest(Bolt.Event source)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The storage callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsStorageRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = ReadRequest(source, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsStorageRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading storage context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static OpenACAISonsStorageRequest ReadRequest(Bolt.Event source, uint? connectionId, ulong? registeredSteamId,
            OpenACAISonsServerSession sourceSession)
        {
            if (source is ItemStorageAddItemEvent add)
                return new OpenACAISonsStorageRequest(OpenACAISonsStorageOperation.Add, connectionId, registeredSteamId,
                    add.GetTargetNetworkIdValue().PackedValue, add.ItemId, ReadInstanceItemId(add.ItemInstance),
                    add.Count, add.StorageSlotIndex, sourceSession);

            var remove = (ItemStorageRemoveItemEvent)source;
            return new OpenACAISonsStorageRequest(OpenACAISonsStorageOperation.Remove, connectionId, registeredSteamId,
                remove.GetTargetNetworkIdValue().PackedValue, remove.ItemId, ReadInstanceItemId(remove.ItemInstance),
                remove.Count, remove.StorageSlotIndex, sourceSession);
        }

        private static int? ReadInstanceItemId(Bolt.IProtocolToken token)
        {
            // The native callback consumes this token, not the event's separate ItemId field.
            return token?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance?._itemID;
        }

        private static void PublishState(OpenACAISonsStorageOperation operation, OpenACAISonsStorageHookStatus status, string detail)
        {
            var previous = State;
            var state = operation == OpenACAISonsStorageOperation.Add
                ? new OpenACAISonsServerStorageState(status, detail, previous.RemoveStatus, previous.RemoveDetail)
                : new OpenACAISonsServerStorageState(previous.AddStatus, previous.AddDetail, status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // A broken policy must not produce one log entry per incoming request.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning(
                    "Server storage policy rejected a request: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsStorageOperation
    {
        Add,
        Remove
    }

    public enum OpenACAISonsStorageHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerStorageState
    {
        internal OpenACAISonsServerStorageState(OpenACAISonsStorageHookStatus addStatus, string addDetail,
            OpenACAISonsStorageHookStatus removeStatus, string removeDetail)
        {
            AddStatus = addStatus;
            AddDetail = addDetail;
            RemoveStatus = removeStatus;
            RemoveDetail = removeDetail;
        }

        public OpenACAISonsStorageHookStatus AddStatus { get; }
        public string AddDetail { get; }
        public OpenACAISonsStorageHookStatus RemoveStatus { get; }
        public string RemoveDetail { get; }
    }

    /// <summary>A managed request snapshot. Requested values are untrusted and retain no native objects.</summary>
    public sealed class OpenACAISonsStorageRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsStorageRequest(OpenACAISonsStorageOperation operation, uint? sourceConnectionId,
            ulong? registeredSteamId, ulong requestedTargetNetworkId, int requestedItemId, int? requestedInstanceItemId,
            int requestedCount, int requestedStorageSlotIndex, OpenACAISonsServerSession sourceSession)
        {
            Operation = operation;
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedTargetNetworkId = requestedTargetNetworkId;
            RequestedItemId = requestedItemId;
            RequestedInstanceItemId = requestedInstanceItemId;
            RequestedCount = requestedCount;
            RequestedStorageSlotIndex = requestedStorageSlotIndex;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Native request type, not a guarantee of the effect of a malformed count.</summary>
        public OpenACAISonsStorageOperation Operation { get; }

        /// <summary>Transport ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public OpenACAISonsServerSession SourceSession { get; }

        /// <summary>Game-registered Steam ID when available, not a separate authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }

        /// <summary>Raw requested target ID, even if unresolved. It does not prove access to that storage.</summary>
        public ulong RequestedTargetNetworkId { get; }

        /// <summary>Separate event field; the native storage callback instead consumes the item-instance token.</summary>
        public int RequestedItemId { get; }

        /// <summary>
        /// Item ID carried by the instance token, or null if the expected token or instance is absent.
        /// This does not validate the item's definition, modules or inventory ownership.
        /// </summary>
        public int? RequestedInstanceItemId { get; }

        /// <summary>Unmodified signed request count, not a verified quantity or a normalized delta.</summary>
        public int RequestedCount { get; }
        public int RequestedStorageSlotIndex { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this callback's changes, replies and fallback pickups, without restoring client inventory.
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
