// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server policy before the native item-drop callback changes world objects.</summary>
    public static partial class ServerItemDrops
    {
        public const string ServiceKey = "openacai.sons.serverItemDrops";

        private static ServerItemDropState _state = new(
            ServerItemDropHookStatus.NotAttempted, "The item-drop hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before CoopServerCallbacks.OnEvent(DropItem).
        /// Rejection or a handler exception skips that body, without refunding client inventory.
        /// </summary>
        public static event Action<ItemDropRequest> Receiving;

        public static ServerItemDropState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(ServerItemDropHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server item-drop policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(ServerItemDropHookStatus.Installing, "Registering the server item-drop hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), new[] { typeof(DropItem) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(DropItem)");

                var prefix = new HarmonyMethod(typeof(ServerItemDrops), nameof(BeforeDrop))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-item-drops");
                harmony.Patch(target, prefix: prefix,
                    postfix: new HarmonyMethod(typeof(ServerItemDrops), nameof(AfterDrop)) { priority = Priority.Last },
                    finalizer: new HarmonyMethod(typeof(ServerItemDrops), nameof(FinishDrop)) { priority = Priority.Last });
            }
            catch (Exception error)
            {
                PublishState(ServerItemDropHookStatus.Failed, "Item-drop hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server item-drop policy hook failed: " + error);
                return;
            }

            PublishState(ServerItemDropHookStatus.Installed,
                "Item-drop hook registration succeeded. This is not evidence of a received or blocked request.");
            InstallObservationHook(addStartupWarning);
        }

        private static bool BeforeDrop(DropItem __0, bool __runOriginal, out DropScope __state)
        {
            // A call skipped by an earlier prefix still opens its scope so nesting and FinishDrop stay balanced.
            BeginDropObservation(out __state);
            ItemDropRequest request = null;
            var allowed = !__runOriginal || ApplyDropPolicy(__0, out request);
            if (__state != null)
            {
                CaptureDropSource(__0, request, __state);
                __state.PolicyAllowed = allowed;
                __state.Collecting = __runOriginal && allowed;
            }
            return __runOriginal && allowed;
        }

        private static bool ApplyDropPolicy(DropItem source, out ItemDropRequest request)
        {
            request = null;
            var handlers = Receiving;
            if (handlers == null)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The item-drop callback ran outside the loader's game thread.");
                return false;
            }

            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                request = CaptureRequest(source);
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
                ReportDispatchFailure("Reading item-drop context or running a policy handler failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static ItemDropRequest CaptureRequest(DropItem source)
        {
            var connection = source.RaisedBy;
            uint? connectionId = connection == null ? null : connection.ConnectionId;
            ulong? registeredSteamId = null;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                registeredSteamId = steamId.m_SteamID;
            return new ItemDropRequest(source, connectionId, registeredSteamId,
                ServerSessions.ResolveSource(connection));
        }

        private static void PublishState(ServerItemDropHookStatus status, string detail)
        {
            var previous = State;
            var state = new ServerItemDropState(status, detail,
                previous.ObservationStatus, previous.ObservationDetail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            // Log a broken policy once rather than once per player request.
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning(
                    "Server item-drop policy rejected a request: " + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ServerItemDropHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerItemDropState
    {
        internal ServerItemDropState(ServerItemDropHookStatus status, string detail,
            ServerItemDropHookStatus observationStatus = ServerItemDropHookStatus.NotAttempted,
            string observationDetail = "Item-drop result observation has not been installed.")
        {
            Status = status;
            Detail = detail;
            ObservationStatus = observationStatus;
            ObservationDetail = observationDetail;
        }

        public ServerItemDropHookStatus Status { get; }
        public string Detail { get; }
        public ServerItemDropHookStatus ObservationStatus { get; }
        public string ObservationDetail { get; }
    }

    /// <summary>
    /// Managed copies of a drop request, with no retained native objects. Requested values are
    /// untrusted payload; they do not establish item ownership, quantity or valid world placement.
    /// </summary>
    public sealed class ItemDropRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal ItemDropRequest(DropItem source, uint? sourceConnectionId, ulong? registeredSteamId,
            ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedPrefabId = source.PrefabId.Value;
            RequestedItemCount = source.ItemCount;
            var position = source.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            var velocity = source.Velocity;
            RequestedVelocity = new Vector3(velocity.x, velocity.y, velocity.z);
            var torque = source.Torque;
            RequestedTorque = new Vector3(torque.x, torque.y, torque.z);
            var preSpawned = source.PreSpawned;
            RequestedPreSpawnedNetworkId = preSpawned == null ? null : preSpawned.networkId.PackedValue;
            RequestedRawPreSpawnedNetworkId = source.GetPreSpawnedNetworkIdValue().PackedValue;
            RequestedAvoidImpacts = source.AvoidImpacts;
            RequestedShouldDespawn = source.ShouldDespawn;
            RequestedIsKinematic = source.IsKinematic;

            object itemInstance = null;
            var itemInstanceReadSucceeded = false;
            try
            {
                itemInstance = source.ItemInstance;
                itemInstanceReadSucceeded = true;
            }
            catch (Exception) { }

            if (itemInstance is Bolt.IProtocolToken token)
            {
                HasItemInstanceToken = true;
                try
                {
                    var instance = token.TryCast<ItemInstanceBoltDataToken>()?.ItemInstance;
                    if (instance != null)
                    {
                        RequestedInstanceItemId = instance._itemID;
                        var modules = instance._modules;
                        RequestedInstanceModuleCount = modules == null || modules.WasCollected ? null : modules.Count;
                    }
                }
                catch (Exception) { }
            }

            var itemDescription = CaptureDescription(itemInstance, itemInstanceReadSucceeded);
            RequestedItemInstanceTypeName = itemDescription.TypeName;
            RequestedItemInstanceTypeNameReadSucceeded = itemDescription.TypeNameReadSucceeded;
            RequestedItemInstanceText = itemDescription.Text;
            RequestedItemInstanceTextReadSucceeded = itemDescription.TextReadSucceeded;

            var preSpawnedDescription = CaptureDescription(preSpawned, true);
            RequestedPreSpawnedTypeName = preSpawnedDescription.TypeName;
            RequestedPreSpawnedTypeNameReadSucceeded = preSpawnedDescription.TypeNameReadSucceeded;
            RequestedPreSpawnedText = preSpawnedDescription.Text;
            RequestedPreSpawnedTextReadSucceeded = preSpawnedDescription.TextReadSucceeded;
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

        /// <summary>Transport ID, or null for a server-local event. IDs can be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }

        /// <summary>
        /// Connection-lifetime handle, or null for local events, unavailable tracking or a departed source.
        /// Its live metadata may change; this request's RegisteredSteamId is an event-time copy.
        /// </summary>
        public ServerSession SourceSession { get; }

        /// <summary>
        /// Steam ID registered by the game for the source connection, when available.
        /// This is not a separate authentication or permission verdict.
        /// </summary>
        public ulong? RegisteredSteamId { get; }

        /// <summary>Bolt prefab ID requested by the sender, not an inventory item ID.</summary>
        public int RequestedPrefabId { get; }

        /// <summary>Requested bundle count, not a verified inventory quantity.</summary>
        public int RequestedItemCount { get; }
        public Vector3 RequestedPosition { get; }
        public Quaternion RequestedRotation { get; }
        public Vector3 RequestedVelocity { get; }
        public Vector3 RequestedTorque { get; }

        /// <summary>Sender-selected existing entity, if resolved. This does not establish control over it.</summary>
        public ulong? RequestedPreSpawnedNetworkId { get; }

        /// <summary>Unresolved wire value, including zero. A nonzero value need not resolve to an existing entity.</summary>
        public ulong RequestedRawPreSpawnedNetworkId { get; }

        /// <summary>Copied managed token wrapper type name, not an item identity or ownership verdict.</summary>
        public string RequestedItemInstanceTypeName { get; }
        /// <summary>True for a successfully read null token or successful managed type-name capture.</summary>
        public bool RequestedItemInstanceTypeNameReadSucceeded { get; }
        /// <summary>Unmodified token ToString result. Null can mean a null token, null result or failed read.</summary>
        public string RequestedItemInstanceText { get; }
        /// <summary>True for a successfully read null token or successful ToString call, even if it returned null.</summary>
        public bool RequestedItemInstanceTextReadSucceeded { get; }

        /// <summary>True when the event carried any item-instance token, including one of an unrelated type.</summary>
        public bool HasItemInstanceToken { get; }

        /// <summary>
        /// Client-chosen item ID from an ItemInstanceBoltDataToken. Null for no token, another token type, a null
        /// instance or a failed read, so null never means "no instance". The native callback stores this instance on
        /// the spawned pickup without comparing it to the prefab; a match only shows the request is self-consistent,
        /// not that the sender owned or removed the item.
        /// </summary>
        public int? RequestedInstanceItemId { get; }

        /// <summary>Client-chosen module count on the same instance. Module contents are not validated.</summary>
        public int? RequestedInstanceModuleCount { get; }

        /// <summary>Copied managed entity wrapper type name. This does not establish live presence or control.</summary>
        public string RequestedPreSpawnedTypeName { get; }
        /// <summary>True for a managed-null entity wrapper or successful managed type-name capture.</summary>
        public bool RequestedPreSpawnedTypeNameReadSucceeded { get; }
        /// <summary>Unmodified entity ToString result, not a network-ID substitute or permission to remove it.</summary>
        public string RequestedPreSpawnedText { get; }
        /// <summary>True for a managed-null entity wrapper or successful ToString call, even if it returned null.</summary>
        public bool RequestedPreSpawnedTextReadSucceeded { get; }

        public bool RequestedAvoidImpacts { get; }
        public bool RequestedShouldDespawn { get; }
        public bool RequestedIsKinematic { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this callback's world changes. Does not restore client inventory or remove existing objects.
        /// Returns false outside synchronous dispatch or on another thread. Rejection cannot be undone.
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
