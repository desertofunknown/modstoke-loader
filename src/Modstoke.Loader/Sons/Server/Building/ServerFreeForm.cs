// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Construction.Multiplayer;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Free-form placement policy and authority checks for construction replication messages.</summary>
    public static class ServerFreeForm
    {
        public const string ServiceKey = "openacai.sons.serverFreeForm";

        private static ServerFreeFormState _state = new(
            FreeFormHookStatus.NotAttempted, "The free-form request hook has not been installed.",
            FreeFormHookStatus.NotAttempted, "The construction authority guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;
        private static int _reportedFeedbackFailure;
        private static int _reportedAuthorityGuardFailure;
        private static int _reportedSnapshotRequestFailure;
        private static readonly HashSet<IntPtr> ServerMessageClasses = new();

        /// <summary>
        /// Runs before the server's FreeFormPlaceElement callback, including native validation.
        /// Animation and placement messages are separate requests and are not deduplicated.
        /// </summary>
        public static event Action<FreeFormRequest> Receiving;

        /// <summary>
        /// Runs before the server serializes a construction snapshot for a FreeFormRequestAllData callback.
        /// Rejecting sends no denial notice and may leave a joining client waiting for its snapshot.
        /// </summary>
        public static event Action<FreeFormSnapshotRequest> SnapshotRequested;

        public static ServerFreeFormState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(FreeFormHookStatus.Disabled, detail);
                PublishAuthorityGuardState(FreeFormHookStatus.Disabled, detail);
                PublishSnapshotRequestState(FreeFormHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server free-form policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallAuthorityGuard(addStartupWarning);
            InstallSnapshotRequestHook(addStartupWarning);
            PublishState(FreeFormHookStatus.Installing, "Registering the free-form request hook.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyBase),
                    nameof(FreeFormSystemNetworkProxyBase.OnEvent), new[] { typeof(FreeFormPlaceElement) });
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyBase).FullName, "OnEvent(FreeFormPlaceElement)");

                var prefix = new HarmonyMethod(typeof(ServerFreeForm), nameof(BeforeRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-free-form");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(FreeFormHookStatus.Failed,
                    "Free-form hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server free-form policy hook failed: " + error);
                return;
            }

            PublishState(FreeFormHookStatus.Installed,
                "Free-form hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static void InstallAuthorityGuard(Action<string> addStartupWarning)
        {
            PublishAuthorityGuardState(FreeFormHookStatus.Installing,
                "Registering the construction authority guard.");
            try
            {
                RegisterServerMessageClasses();
                ServerDispatchGate.Register(DispatchRoute.FreeFormAuthority);
            }
            catch (Exception error)
            {
                PublishAuthorityGuardState(FreeFormHookStatus.Failed,
                    "Construction authority guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Free-form construction authority guard failed: " + error);
                return;
            }

            PublishAuthorityGuardState(FreeFormHookStatus.Installed,
                "Construction authority guard registration succeeded. Multiplayer enforcement remains unverified.");
        }

        private static void RegisterServerMessageClasses()
        {
            ServerMessageClasses.Clear();
            AddServerMessageClass<FreeFormStructureRejected>();
            AddServerMessageClass<FreeFormStructureCreationValidated>();
            AddServerMessageClass<FreeFormStructureCreated>();
            AddServerMessageClass<FreeFormStructureDestroyed>();
            AddServerMessageClass<FreeFormStructureEditedTransform>();
            AddServerMessageClass<FreeFormStructureEditedElements>();
            AddServerMessageClass<FreeFormStructureInsertedElement>();
            AddServerMessageClass<FreeFormStructureEditedLinks>();
            AddServerMessageClass<FreeFormStructureCascadeRepositionSupported>();
            AddServerMessageClass<FreeFormStructureDamagedStatus>();
            AddServerMessageClass<FreeFormFreeLinkChange>();
            AddServerMessageClass<FreeFormSupportingScrewLinkChange>();
            AddServerMessageClass<FreeFormSupportedByScrewLinkChange>();
            AddServerMessageClass<FreeWireConnectorChange>();
            AddServerMessageClass<FreeFormLargeBatchBegin>();
            AddServerMessageClass<FreeFormLargeBatchFinished>();
        }

        private static void AddServerMessageClass<T>()
        {
            var nativeClass = Il2CppClassPointerStore<T>.NativeClassPtr;
            if (nativeClass == IntPtr.Zero)
                throw new InvalidOperationException("The native " + typeof(T).Name + " class is unavailable.");
            ServerMessageClasses.Add(nativeClass);
        }

        private static void InstallSnapshotRequestHook(Action<string> addStartupWarning)
        {
            PublishSnapshotRequestState(FreeFormHookStatus.Installing,
                "Registering the construction snapshot-request hook.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyServer),
                    nameof(FreeFormSystemNetworkProxyServer.OnEvent), new[] { typeof(FreeFormRequestAllData) });
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyServer).FullName,
                        "OnEvent(FreeFormRequestAllData)");

                var prefix = new HarmonyMethod(typeof(ServerFreeForm), nameof(BeforeSnapshotRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-snapshot-requests");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishSnapshotRequestState(FreeFormHookStatus.Failed,
                    "Snapshot-request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Construction snapshot-request hook failed: " + error);
                return;
            }

            PublishSnapshotRequestState(FreeFormHookStatus.Installed,
                "Snapshot-request hook registration succeeded. Live request handling remains unverified.");
        }

        private static bool BeforeSnapshotRequest(FreeFormRequestAllData __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            var handlers = SnapshotRequested;
            if (handlers == null)
                return true;

            FreeFormSnapshotRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportSnapshotRequestFailure("A snapshot request ran outside the loader's game thread.");
                    return false;
                }

                request = new FreeFormSnapshotRequest(__0);
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
                ReportSnapshotRequestFailure("Reading snapshot-request context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        // Generated Bolt event classes are sealed, so an exact class match covers every instance.
        internal static bool IsServerMessageClass(IntPtr nativeClass) => ServerMessageClasses.Contains(nativeClass);

        internal static DispatchVerdict RouteAuthorityDispatch(Bolt.Event message)
        {
            if (!IsServerMessageClass(IL2CPP.il2cpp_object_get_class(message.Pointer)))
                return DispatchVerdict.Forward;

            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportAuthorityGuardFailure("A construction replication event ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var source = message.RaisedBy;
                if (source == null)
                    return DispatchVerdict.Forward;

                // These messages are server-authored state, not requests a client may relay to other peers.
                if (BoltNetwork.isServer)
                    return DispatchVerdict.Reject;
                if (!BoltNetwork.isClient)
                    return DispatchVerdict.Reject;

                var server = BoltNetwork.server;
                return server != null && source.Pointer == server.Pointer
                    && source.ConnectionId == server.ConnectionId ? DispatchVerdict.Forward : DispatchVerdict.Reject;
            }
            catch (Exception error)
            {
                ReportAuthorityGuardFailure("Reading construction authority failed (" + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportAuthorityGuardFailure("Releasing rejected event storage failed (" + error.GetType().FullName + ").");

        private static bool BeforeRequest(FreeFormSystemNetworkProxyBase __instance, FreeFormPlaceElement __0,
            bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("A free-form callback ran outside the loader's game thread.");
                return false;
            }

            FreeFormRequest request = null;
            try
            {
                // The native base callback is shared with clients; policy applies only on a server.
                if (!BoltNetwork.isServer)
                    return true;

                request = new FreeFormRequest(__0, __instance);
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
                ReportDispatchFailure("Reading free-form context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (request?.NativeFeedbackStatus == FreeFormFeedbackStatus.Pending)
                    SendNativeFeedback(request);
            }
        }

        private static void SendNativeFeedback(FreeFormRequest request)
        {
            var sendAttempted = false;
            try
            {
                // Resolve the captured lifetime again after handlers, never a replacement sharing its transport ID.
                var connection = ServerSessions.ResolveCurrent(request.SourceSession);
                if (connection == null)
                {
                    request.SetNativeFeedbackStatus(FreeFormFeedbackStatus.Unavailable);
                    return;
                }

                var reply = FreeFormStructureRejected.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                reply.PlaceActionNum = request.RequestedPlaceActionNumber;
                sendAttempted = true;
                reply.Send();
                request.SetNativeFeedbackStatus(FreeFormFeedbackStatus.Queued);
            }
            catch (Exception error)
            {
                request.SetNativeFeedbackStatus(sendAttempted
                    ? FreeFormFeedbackStatus.SendUncertain
                    : FreeFormFeedbackStatus.Failed);
                if (Interlocked.Exchange(ref _reportedFeedbackFailure, 1) != 0)
                    return;
                try
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Free-form rejection feedback failed ("
                        + error.GetType().FullName + "). Further feedback failures will not be logged.");
                }
                catch (Exception) { }
            }
        }

        private static void PublishState(FreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new ServerFreeFormState(status, detail,
                current.AuthorityGuardStatus, current.AuthorityGuardDetail,
                current.SnapshotRequestStatus, current.SnapshotRequestDetail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void PublishAuthorityGuardState(FreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new ServerFreeFormState(current.Status, current.Detail, status, detail,
                current.SnapshotRequestStatus, current.SnapshotRequestDetail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void PublishSnapshotRequestState(FreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new ServerFreeFormState(current.Status, current.Detail,
                current.AuthorityGuardStatus, current.AuthorityGuardDetail, status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportSnapshotRequestFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedSnapshotRequestFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Construction snapshot request rejected: "
                    + detail + " Further snapshot-request failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportAuthorityGuardFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedAuthorityGuardFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Free-form construction authority guard: "
                    + detail + " Further guard failures will not be logged.");
            }
            catch (Exception) { }
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server free-form policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum FreeFormHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public enum FreeFormFeedbackStatus
    {
        NotRequested,
        Pending,
        Unavailable,
        Queued,
        SendUncertain,
        Failed
    }

    public sealed class ServerFreeFormState
    {
        internal ServerFreeFormState(FreeFormHookStatus status, string detail,
            FreeFormHookStatus authorityGuardStatus, string authorityGuardDetail)
            : this(status, detail, authorityGuardStatus, authorityGuardDetail,
                FreeFormHookStatus.NotAttempted, "The construction snapshot-request hook has not been installed.")
        {
        }

        internal ServerFreeFormState(FreeFormHookStatus status, string detail,
            FreeFormHookStatus authorityGuardStatus, string authorityGuardDetail,
            FreeFormHookStatus snapshotRequestStatus, string snapshotRequestDetail)
        {
            Status = status;
            Detail = detail;
            AuthorityGuardStatus = authorityGuardStatus;
            AuthorityGuardDetail = authorityGuardDetail;
            SnapshotRequestStatus = snapshotRequestStatus;
            SnapshotRequestDetail = snapshotRequestDetail;
        }

        public FreeFormHookStatus Status { get; }
        public string Detail { get; }
        /// <summary>Registration status for construction replication authority checks before routing.</summary>
        public FreeFormHookStatus AuthorityGuardStatus { get; }
        public string AuthorityGuardDetail { get; }
        /// <summary>The shared authority-hook status, retained for callers using the original rejection guard API.</summary>
        public FreeFormHookStatus RejectionGuardStatus => AuthorityGuardStatus;
        public string RejectionGuardDetail => AuthorityGuardDetail;
        /// <summary>Registration status for the full construction snapshot-request policy callback.</summary>
        public FreeFormHookStatus SnapshotRequestStatus { get; }
        public string SnapshotRequestDetail { get; }
    }

    /// <summary>Managed source context for a full construction snapshot request; no payload or native objects are retained.</summary>
    public sealed class FreeFormSnapshotRequest : ConstructionRequest
    {
        internal FreeFormSnapshotRequest(FreeFormRequestAllData source) : base(source)
        {
        }
    }

    /// <summary>Raw construction or prediction selection, not a Bolt network ID or validated reference.</summary>
    public readonly struct FreeFormStructureSelection
    {
        internal FreeFormStructureSelection(int typeId, int instanceId, int direction)
        {
            TypeId = typeId;
            InstanceId = instanceId;
            Direction = direction;
        }

        public int TypeId { get; }
        /// <summary>May name a creating action rather than a database instance when its anticipation flag is set.</summary>
        public int InstanceId { get; }
        public int Direction { get; }
    }

    /// <summary>Managed copies of an unvalidated FreeFormPlaceElement payload.</summary>
    public sealed class FreeFormRequest : ConstructionRequest
    {
        private int _nativeFeedbackStatus;
        private FreeFormSystemNetworkProxyBase _dispatchProxy;

        internal FreeFormRequest(FreeFormPlaceElement source, FreeFormSystemNetworkProxyBase dispatchProxy) : base(source)
        {
            RequestedDatabaseVersion = source.DBVersion;
            RequestedModuleId = source.ModuleID;
            RequestedItemId = source.ItemID;
            RequestedTargetElementIndex = source.TargetElementTransformIndex;
            RequestedPickupNetworkId = source.GetPickupEntityNetworkIdValue().PackedValue;
            RequestedPlacementMode = source.PlacementMode;
            var position = source.PlacePosition;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var axis = source.PlaceAxis;
            RequestedAxis = new Vector3(axis.x, axis.y, axis.z);
            var normal = source.HitNormal;
            RequestedHitNormal = new Vector3(normal.x, normal.y, normal.z);
            var lookAt = source.LookAtPos;
            RequestedLookAtPosition = new Vector3(lookAt.x, lookAt.y, lookAt.z);
            RequestedTarget = new(source.TargetTypeID, source.TargetInstanceID, source.TargetDir);
            RequestedEditing = new(source.EditingTypeID, source.EditingInstanceID, source.EditingDir);
            RequestedSupport1 = new(source.Support1TypeID, source.Support1InstanceID, source.Support1Dir);
            RequestedSupport2 = new(source.Support2TypeID, source.Support2InstanceID, source.Support2Dir);
            RequestedSupport3 = new(source.Support3TypeID, source.Support3InstanceID, source.Support3Dir);
            RequestedSupport4 = new(source.Support4TypeID, source.Support4InstanceID, source.Support4Dir);
            ClaimedInvalid = source.IsInvalid;
            RequestedPlaceActionNumber = source.PlaceActionNum;
            RequestedIsPlacing = source.IsPlacing;
            ClaimedPlayerNetworkId = source.GetSendingPlayerEntityNetworkIdValue().PackedValue;
            RequestedAnticipatedStructuresFlags = source.AnticipatedStructuresFlags;
            RequestedTreeId = Guid.Parse(source.TreeID.ToString());
            RequestedHeldOnlyItemCount = source.HeldOnlyItemCount;
            _dispatchProxy = dispatchProxy;

            try
            {
                if (dispatchProxy is not null && !dispatchProxy.WasCollected && dispatchProxy)
                {
                    var server = dispatchProxy.TryCast<FreeFormSystemNetworkProxyServer>();
                    if (server is null)
                        ReceiverReadSucceeded = true;
                    else if (!server.WasCollected && server)
                    {
                        IsServerReceiver = true;
                        ReceiverReadSucceeded = true;
                    }
                }
            }
            catch (Exception) { }

            object pickup = null;
            var pickupReadSucceeded = false;
            try
            {
                pickup = source.PickupEntity;
                pickupReadSucceeded = true;
            }
            catch (Exception) { }
            var pickupDescription = CaptureDescription(pickup, pickupReadSucceeded);
            RequestedPickupEntityTypeName = pickupDescription.TypeName;
            RequestedPickupEntityTypeNameReadSucceeded = pickupDescription.TypeNameReadSucceeded;
            RequestedPickupEntityText = pickupDescription.Text;
            RequestedPickupEntityTextReadSucceeded = pickupDescription.TextReadSucceeded;

            object player = null;
            var playerReadSucceeded = false;
            try
            {
                player = source.SendingPlayerEntity;
                playerReadSucceeded = true;
            }
            catch (Exception) { }
            var playerDescription = CaptureDescription(player, playerReadSucceeded);
            ClaimedPlayerEntityTypeName = playerDescription.TypeName;
            ClaimedPlayerEntityTypeNameReadSucceeded = playerDescription.TypeNameReadSucceeded;
            ClaimedPlayerEntityText = playerDescription.Text;
            ClaimedPlayerEntityTextReadSucceeded = playerDescription.TextReadSucceeded;
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

        internal FreeFormSystemNetworkProxyBase DispatchProxy => IsDispatchOpen ? _dispatchProxy : null;

        internal override void Close()
        {
            base.Close();
            _dispatchProxy = null;
        }

        public int RequestedDatabaseVersion { get; }
        public int RequestedModuleId { get; }
        /// <summary>Claimed held item ID, not proof of inventory ownership.</summary>
        public int RequestedItemId { get; }
        public int RequestedTargetElementIndex { get; }
        public ulong RequestedPickupNetworkId { get; }
        public int RequestedPlacementMode { get; }
        /// <summary>Raw payload pose and geometry, without validation, normalization or world lookup.</summary>
        public Vector3 RequestedPosition { get; }
        public Vector3 RequestedAxis { get; }
        public Vector3 RequestedHitNormal { get; }
        public Vector3 RequestedLookAtPosition { get; }
        public FreeFormStructureSelection RequestedTarget { get; }
        public FreeFormStructureSelection RequestedEditing { get; }
        public FreeFormStructureSelection RequestedSupport1 { get; }
        public FreeFormStructureSelection RequestedSupport2 { get; }
        public FreeFormStructureSelection RequestedSupport3 { get; }
        public FreeFormStructureSelection RequestedSupport4 { get; }
        /// <summary>Sender-provided flag, not the result of server validation.</summary>
        public bool ClaimedInvalid { get; }
        /// <summary>Sender-supplied prediction correlation number, not a globally unique operation ID.</summary>
        public int RequestedPlaceActionNumber { get; }
        /// <summary>True requests placement processing; false requests the animation path. Both remain unvalidated.</summary>
        public bool RequestedIsPlacing { get; }
        /// <summary>Sender-selected player field, separate from the actual source session.</summary>
        public ulong ClaimedPlayerNetworkId { get; }
        public int RequestedAnticipatedStructuresFlags { get; }
        public Guid RequestedTreeId { get; }
        public int RequestedHeldOnlyItemCount { get; }

        /// <summary>True when the actual callback receiver was a live native server proxy at capture time.</summary>
        public bool IsServerReceiver { get; }
        /// <summary>False means receiver classification was unavailable, not that the receiver was a client.</summary>
        public bool ReceiverReadSucceeded { get; }

        /// <summary>Copied managed pickup wrapper type name, not item identity or ownership evidence.</summary>
        public string RequestedPickupEntityTypeName { get; }
        /// <summary>True for a successfully read managed-null pickup or successful managed type-name capture.</summary>
        public bool RequestedPickupEntityTypeNameReadSucceeded { get; }
        /// <summary>Unmodified pickup ToString result, not a network-ID substitute or permission to remove it.</summary>
        public string RequestedPickupEntityText { get; }
        /// <summary>True for a successfully read managed-null pickup or successful ToString call, including null results.</summary>
        public bool RequestedPickupEntityTextReadSucceeded { get; }

        /// <summary>Copied managed wrapper type name for the sender-selected entity, not the authenticated player.</summary>
        public string ClaimedPlayerEntityTypeName { get; }
        /// <summary>True for a successfully read managed-null player field or successful managed type-name capture.</summary>
        public bool ClaimedPlayerEntityTypeNameReadSucceeded { get; }
        /// <summary>Unmodified sender-selected entity ToString result, not proof of the actual source identity.</summary>
        public string ClaimedPlayerEntityText { get; }
        /// <summary>True for a successfully read managed-null player field or successful ToString call, including null results.</summary>
        public bool ClaimedPlayerEntityTextReadSucceeded { get; }

        /// <summary>Local feedback-send outcome, not confirmation of receipt, prediction cleanup or inventory restoration.</summary>
        public FreeFormFeedbackStatus NativeFeedbackStatus =>
            (FreeFormFeedbackStatus)Volatile.Read(ref _nativeFeedbackStatus);

        /// <summary>
        /// Rejects and requests one native rejection notice to this exact source session after dispatch.
        /// Does not run native fallback pickups, cancel server animations, or restore inventory.
        /// </summary>
        public bool RejectWithNativeFeedback()
        {
            if (!Reject())
                return false;
            SetNativeFeedbackStatus(FreeFormFeedbackStatus.Pending);
            return true;
        }

        internal void SetNativeFeedbackStatus(FreeFormFeedbackStatus status)
        {
            Volatile.Write(ref _nativeFeedbackStatus, (int)status);
        }
    }
}
