// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Policy before the native server handles structure-storage add and remove requests.</summary>
    public static partial class ServerStorage
    {
        public const string ServiceKey = "openacai.sons.serverStorage";

        private static ServerStorageState _state = new(
            StorageHookStatus.NotAttempted, "The add hook has not been installed.",
            StorageHookStatus.NotAttempted, "The remove hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the matching CoopServerCallbacks.OnEvent body.
        /// Rejection skips storage changes and that body's replies or fallback pickups, without a refund.
        /// </summary>
        public static event Action<StorageRequest> Receiving;

        public static ServerStorageState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(StorageOperation.Add, StorageHookStatus.Disabled, detail);
                PublishState(StorageOperation.Remove, StorageHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server storage policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(StorageOperation.Add, typeof(ItemStorageAddItemEvent), nameof(BeforeAdd), addStartupWarning);
            InstallHook(StorageOperation.Remove, typeof(ItemStorageRemoveItemEvent), nameof(BeforeRemove), addStartupWarning);
        }

        private static void InstallHook(StorageOperation operation, Type eventType, string prefixName,
            Action<string> addStartupWarning)
        {
            PublishState(operation, StorageHookStatus.Installing, "Registering the storage hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { eventType });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(" + eventType.Name + ")");

                var prefix = new HarmonyMethod(typeof(ServerStorage), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-storage");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(operation, StorageHookStatus.Failed,
                    "Storage hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server storage " + operation + " policy hook failed: " + error);
                return;
            }

            PublishState(operation, StorageHookStatus.Installed,
                "Storage hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeAdd(ItemStorageAddItemEvent __0, bool __runOriginal) =>
            __runOriginal && BeforeRequest(__0);
        private static bool BeforeRemove(ItemStorageRemoveItemEvent __0, bool __runOriginal) =>
            __runOriginal && BeforeRequest(__0);

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

            StorageRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                var sourceSession = ServerSessions.ResolveSource(connection);
                // ResolveSource refreshes registration from the native table for a live tracked session.
                ulong? registeredSteamId = sourceSession?.RegisteredSteamId;
                if (sourceSession == null && connection != null
                    && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = ReadRequest(source, connectionId, registeredSteamId, sourceSession);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
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

        private static StorageRequest ReadRequest(Bolt.Event source, uint? connectionId, ulong? registeredSteamId,
            ServerSession sourceSession)
        {
            if (source is ItemStorageAddItemEvent add)
                return new StorageRequest(StorageOperation.Add, connectionId, registeredSteamId,
                    add.GetTargetNetworkIdValue().PackedValue, add.ItemId, ReadInstanceItemId(add.ItemInstance),
                    add.Count, add.StorageSlotIndex, sourceSession);

            var remove = (ItemStorageRemoveItemEvent)source;
            return new StorageRequest(StorageOperation.Remove, connectionId, registeredSteamId,
                remove.GetTargetNetworkIdValue().PackedValue, remove.ItemId, ReadInstanceItemId(remove.ItemInstance),
                remove.Count, remove.StorageSlotIndex, sourceSession);
        }

        private static int? ReadInstanceItemId(Bolt.IProtocolToken token)
        {
            // The native callback consumes this token, not the event's separate ItemId field.
            return token?.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance?._itemID;
        }

        internal static StorageReadStatus InspectTargetSlot(StorageRequest request,
            out StorageSlotSnapshot snapshot)
        {
            snapshot = null;
            try
            {
                var networkId = request.RequestedTargetNetworkId;
                var status = ResolveStorageTarget(networkId, out var target);
                if (status != StorageReadStatus.Success)
                    return status;
                var entity = target.Entity;
                var component = target.Component;
                var slots = target.Slots;
                var slotCount = target.SlotCount;
                var replicatedSlotCount = target.ReplicatedSlotCount;
                var slotIndex = request.RequestedStorageSlotIndex;
                if (slotIndex < 0 || slotIndex >= slotCount || slotIndex >= replicatedSlotCount)
                    return StorageReadStatus.SlotOutOfRange;
                var slot = slots[slotIndex];
                if (!IsLiveStorageObject(slot))
                    return StorageReadStatus.SlotUnavailable;
                if (!MatchesStorageEntity(slot._boltEntity, entity))
                    return StorageReadStatus.StorageOwnerMismatch;

                var itemId = request.RequestedInstanceItemId;
                bool? hasItemEntry = null;
                int? itemCount = null, maximumCount = null, uniqueInstanceCount = null;
                if (itemId.HasValue)
                {
                    var manager = slot._itemInstanceManager;
                    if (!IsLiveStorageObject(manager))
                        return StorageReadStatus.ItemStateUnavailable;
                    var items = manager._itemsMap;
                    if (items is null || items.WasCollected)
                        return StorageReadStatus.ItemStateUnavailable;
                    hasItemEntry = items.TryGetValue(itemId.Value, out var entry);
                    if (hasItemEntry.Value)
                    {
                        if (entry is null || entry.WasCollected)
                            return StorageReadStatus.ItemStateUnavailable;
                        itemCount = entry.Count;
                        maximumCount = entry.MaxCount;
                        var uniqueInstances = entry.UniqueInstances;
                        if (uniqueInstances is not null)
                        {
                            if (uniqueInstances.WasCollected)
                                return StorageReadStatus.ItemStateUnavailable;
                            var count = uniqueInstances.Count;
                            if (count < 0)
                                return StorageReadStatus.ItemStateUnavailable;
                            uniqueInstanceCount = count;
                        }
                    }
                }

                var copy = new StorageSlotSnapshot(networkId, entity.prefabId.Value, entity.isOwner,
                    target.Kind, target.IsBusy, slotIndex, slot._StorageSlotIndex_k__BackingField, slotCount, replicatedSlotCount,
                    itemId, hasItemEntry, itemCount, maximumCount, uniqueInstanceCount);
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return StorageReadStatus.NotServer;
                var current = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (!MatchesStorageEntity(current, entity) || !IsLiveStorageObject(component)
                    || !IsLiveStorageObject(slot) || !MatchesStorageEntity(slot._boltEntity, current))
                    return StorageReadStatus.TargetUnavailable;

                snapshot = copy;
                return StorageReadStatus.Success;
            }
            catch (Exception)
            {
                return StorageReadStatus.Failed;
            }
        }

        private static bool IsLiveStorageObject(UnityEngine.Object value) =>
            value is not null && !value.WasCollected && value;

        private static bool MatchesStorageEntity(BoltEntity candidate, BoltEntity expected) =>
            IsLiveStorageObject(candidate) && IsLiveStorageObject(expected)
            && candidate.isAttached && expected.isAttached && candidate.Pointer == expected.Pointer
            && candidate.networkId.PackedValue == expected.networkId.PackedValue;

        private static void PublishState(StorageOperation operation, StorageHookStatus status, string detail)
        {
            var previous = State;
            var state = operation == StorageOperation.Add
                ? new ServerStorageState(status, detail, previous.RemoveStatus, previous.RemoveDetail)
                : new ServerStorageState(previous.AddStatus, previous.AddDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // A broken policy must not produce one log entry per incoming request.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Server storage policy rejected a request: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum StorageOperation
    {
        Add,
        Remove
    }

    public enum StorageHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerStorageState
    {
        internal ServerStorageState(StorageHookStatus addStatus, string addDetail,
            StorageHookStatus removeStatus, string removeDetail)
        {
            AddStatus = addStatus;
            AddDetail = addDetail;
            RemoveStatus = removeStatus;
            RemoveDetail = removeDetail;
        }

        public StorageHookStatus AddStatus { get; }
        public string AddDetail { get; }
        public StorageHookStatus RemoveStatus { get; }
        public string RemoveDetail { get; }
    }

    /// <summary>A managed request snapshot. Requested values are untrusted and retain no native objects.</summary>
    public sealed class StorageRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal StorageRequest(StorageOperation operation, uint? sourceConnectionId,
            ulong? registeredSteamId, ulong requestedTargetNetworkId, int requestedItemId, int? requestedInstanceItemId,
            int requestedCount, int requestedStorageSlotIndex, ServerSession sourceSession)
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
        public StorageOperation Operation { get; }

        /// <summary>Transport ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public ServerSession SourceSession { get; }

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
        /// Reads only this request's target slot and token-item count entry during Receiving on its dispatch thread.
        /// The snapshot contains managed copies at inspection time, not a reservation, permission verdict or
        /// prediction of the native result. No placement validator, mutation or rejection is performed.
        /// A non-success status leaves snapshot null. Unknown storage implementations are not inspected.
        /// </summary>
        public StorageReadStatus InspectTargetSlot(out StorageSlotSnapshot snapshot)
        {
            snapshot = null;
            if (Environment.CurrentManagedThreadId != _dispatchThreadId)
                return StorageReadStatus.WrongThread;
            if (!_acceptingDecisions)
                return StorageReadStatus.RequestClosed;
            return ServerStorage.InspectTargetSlot(this, out snapshot);
        }

        /// <summary>
        /// Copies a bounded prefix of this request's target slots and their item-map records during Receiving.
        /// The requested slot and token item do not filter this read. Each slot reports its own read status;
        /// Success can contain unavailable or truncated slots, so inspect snapshot.IsComplete.
        /// Limits must be positive and within MaximumContentsSlots and MaximumContentsItemsPerSlot.
        /// Records follow native dictionary order, which is not stable across calls. Large backing maps
        /// report ScanLimitExceeded without enumeration. No item instances are created and no placement
        /// callbacks or transfers are invoked.
        /// A non-success return leaves snapshot null. Inspection does not accept or reject the request.
        /// </summary>
        public StorageReadStatus InspectTargetContents(out StorageContentsSnapshot snapshot,
            int maximumSlots = 16, int maximumItemsPerSlot = 64)
        {
            snapshot = null;
            if (Environment.CurrentManagedThreadId != _dispatchThreadId)
                return StorageReadStatus.WrongThread;
            if (!_acceptingDecisions)
                return StorageReadStatus.RequestClosed;
            return ServerStorage.InspectTargetContents(this, maximumSlots, maximumItemsPerSlot, out snapshot);
        }

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

    public enum StorageReadStatus
    {
        Success,
        WrongThread,
        RequestClosed,
        NotServer,
        TargetUnavailable,
        StorageUnavailable,
        UnsupportedStorage,
        StorageOwnerMismatch,
        StorageStateUnavailable,
        SlotOutOfRange,
        SlotUnavailable,
        ItemStateUnavailable,
        Failed,
        InvalidLimit,
        BindingChanged,
        ScanLimitExceeded
    }

    public enum StorageKind
    {
        Stationary,
        Mobile
    }

    /// <summary>Native storage fields copied during a request; retains no native objects or inventory instances.</summary>
    public sealed class StorageSlotSnapshot
    {
        internal StorageSlotSnapshot(ulong targetNetworkId, int prefabId, bool isServerOwned,
            StorageKind kind, bool isBusy, int requestedSlotIndex, int nativeSlotIndex,
            int slotCount, int replicatedSlotCount, int? itemId, bool? hasItemEntry, int? itemCount,
            int? maximumCount, int? uniqueInstanceCount)
        {
            TargetNetworkId = targetNetworkId;
            PrefabId = prefabId;
            IsServerOwned = isServerOwned;
            Kind = kind;
            IsBusy = isBusy;
            RequestedSlotIndex = requestedSlotIndex;
            NativeSlotIndex = nativeSlotIndex;
            SlotCount = slotCount;
            ReplicatedSlotCount = replicatedSlotCount;
            ItemId = itemId;
            HasItemEntry = hasItemEntry;
            NativeItemCount = itemCount;
            NativeMaximumCount = maximumCount;
            NativeUniqueInstanceCount = uniqueInstanceCount;
            CapturedAt = DateTimeOffset.UtcNow;
        }

        public ulong TargetNetworkId { get; }
        public int PrefabId { get; }
        /// <summary>Native entity ownership, not permission for the requesting player.</summary>
        public bool IsServerOwned { get; }
        public StorageKind Kind { get; }
        /// <summary>The storage component's busy field, not a guarantee that the native callback will refuse it.</summary>
        public bool IsBusy { get; }
        public int RequestedSlotIndex { get; }
        /// <summary>The selected StructureStorage's own slot-index field, which is not assumed to match the request.</summary>
        public int NativeSlotIndex { get; }
        public int SlotCount { get; }
        public int ReplicatedSlotCount { get; }
        /// <summary>Copied token item ID, not the event's separate ItemId or a validated inventory identity.</summary>
        public int? ItemId { get; }
        /// <summary>Null without a token item ID; false means no entry exists for that ID in the slot's item map.</summary>
        public bool? HasItemEntry { get; }
        /// <summary>Raw Items.Count, or null without an entry; returned separately from the unique-instance list length.</summary>
        public int? NativeItemCount { get; }
        /// <summary>Raw Items.MaxCount, or null without an entry; not a placement verdict or computed free capacity.</summary>
        public int? NativeMaximumCount { get; }
        /// <summary>UniqueInstances list length, or null without that list; no instances or modules are traversed.</summary>
        public int? NativeUniqueInstanceCount { get; }
        public DateTimeOffset CapturedAt { get; }
    }
}
