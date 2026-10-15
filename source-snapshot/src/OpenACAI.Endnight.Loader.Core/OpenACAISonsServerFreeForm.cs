// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Construction.Multiplayer;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    /// <summary>Free-form placement policy and authority checks for construction replication messages.</summary>
    public static class OpenACAISonsServerFreeForm
    {
        public const string ServiceKey = "openacai.sons.serverFreeForm";

        private static OpenACAISonsServerFreeFormState _state = new(
            OpenACAISonsFreeFormHookStatus.NotAttempted, "The free-form request hook has not been installed.",
            OpenACAISonsFreeFormHookStatus.NotAttempted, "The construction authority guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;
        private static int _reportedFeedbackFailure;
        private static int _reportedAuthorityGuardFailure;
        private static int _reportedSnapshotRequestFailure;

        /// <summary>
        /// Runs before the server's FreeFormPlaceElement callback, including native validation.
        /// Animation and placement messages are separate requests and are not deduplicated.
        /// </summary>
        public static event Action<OpenACAISonsFreeFormRequest> Receiving;

        /// <summary>
        /// Runs before the server serializes a construction snapshot for a FreeFormRequestAllData callback.
        /// Rejecting sends no denial notice and may leave a joining client waiting for its snapshot.
        /// </summary>
        public static event Action<OpenACAISonsFreeFormSnapshotRequest> SnapshotRequested;

        public static OpenACAISonsServerFreeFormState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsFreeFormHookStatus.Disabled, detail);
                PublishAuthorityGuardState(OpenACAISonsFreeFormHookStatus.Disabled, detail);
                PublishSnapshotRequestState(OpenACAISonsFreeFormHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server free-form policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallAuthorityGuard(addStartupWarning);
            InstallSnapshotRequestHook(addStartupWarning);
            PublishState(OpenACAISonsFreeFormHookStatus.Installing, "Registering the free-form request hook.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyBase),
                    nameof(FreeFormSystemNetworkProxyBase.OnEvent), new[] { typeof(FreeFormPlaceElement) });
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyBase).FullName, "OnEvent(FreeFormPlaceElement)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerFreeForm), nameof(BeforeRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-free-form");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsFreeFormHookStatus.Failed,
                    "Free-form hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server free-form policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsFreeFormHookStatus.Installed,
                "Free-form hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static void InstallAuthorityGuard(Action<string> addStartupWarning)
        {
            PublishAuthorityGuardState(OpenACAISonsFreeFormHookStatus.Installing,
                "Registering the construction authority guard.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerFreeForm), nameof(BeforeAuthorityDispatch))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-free-form-authority");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishAuthorityGuardState(OpenACAISonsFreeFormHookStatus.Failed,
                    "Construction authority guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Free-form construction authority guard failed: " + error);
                return;
            }

            PublishAuthorityGuardState(OpenACAISonsFreeFormHookStatus.Installed,
                "Construction authority guard registration succeeded. Multiplayer enforcement remains unverified.");
        }

        private static void InstallSnapshotRequestHook(Action<string> addStartupWarning)
        {
            PublishSnapshotRequestState(OpenACAISonsFreeFormHookStatus.Installing,
                "Registering the construction snapshot-request hook.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyServer),
                    nameof(FreeFormSystemNetworkProxyServer.OnEvent), new[] { typeof(FreeFormRequestAllData) });
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyServer).FullName,
                        "OnEvent(FreeFormRequestAllData)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerFreeForm), nameof(BeforeSnapshotRequest))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-snapshot-requests");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishSnapshotRequestState(OpenACAISonsFreeFormHookStatus.Failed,
                    "Snapshot-request hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Construction snapshot-request hook failed: " + error);
                return;
            }

            PublishSnapshotRequestState(OpenACAISonsFreeFormHookStatus.Installed,
                "Snapshot-request hook registration succeeded. Live request handling remains unverified.");
        }

        private static bool BeforeSnapshotRequest(FreeFormRequestAllData __0)
        {
            var handlers = SnapshotRequested;
            if (handlers == null)
                return true;

            OpenACAISonsFreeFormSnapshotRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportSnapshotRequestFailure("A snapshot request ran outside the loader's game thread.");
                    return false;
                }

                request = new OpenACAISonsFreeFormSnapshotRequest(__0);
                foreach (Action<OpenACAISonsFreeFormSnapshotRequest> handler in handlers.GetInvocationList())
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

        private static bool BeforeAuthorityDispatch(Bolt.Event __0)
        {
            if (__0 == null)
                return true;

            var isServerMessage = __0.TryCast<FreeFormStructureRejected>() != null
                || __0.TryCast<FreeFormStructureCreationValidated>() != null
                || __0.TryCast<FreeFormStructureCreated>() != null
                || __0.TryCast<FreeFormStructureDestroyed>() != null
                || __0.TryCast<FreeFormStructureEditedTransform>() != null
                || __0.TryCast<FreeFormStructureEditedElements>() != null
                || __0.TryCast<FreeFormStructureInsertedElement>() != null
                || __0.TryCast<FreeFormStructureEditedLinks>() != null
                || __0.TryCast<FreeFormStructureCascadeRepositionSupported>() != null
                || __0.TryCast<FreeFormStructureDamagedStatus>() != null
                || __0.TryCast<FreeFormFreeLinkChange>() != null
                || __0.TryCast<FreeFormSupportingScrewLinkChange>() != null
                || __0.TryCast<FreeFormSupportedByScrewLinkChange>() != null
                || __0.TryCast<FreeWireConnectorChange>() != null
                || __0.TryCast<FreeFormLargeBatchBegin>() != null
                || __0.TryCast<FreeFormLargeBatchFinished>() != null;
            if (!isServerMessage)
                return true;

            var forward = false;
            try
            {
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportAuthorityGuardFailure("A construction replication event ran outside the loader's game thread.");
                    return false;
                }

                var source = __0.RaisedBy;
                if (source == null)
                {
                    forward = true;
                    return true;
                }

                // These messages are server-authored state, not requests a client may relay to other peers.
                if (BoltNetwork.isServer)
                    return false;
                if (!BoltNetwork.isClient)
                    return false;

                var server = BoltNetwork.server;
                forward = server != null && source.Pointer == server.Pointer
                    && source.ConnectionId == server.ConnectionId;
                return forward;
            }
            catch (Exception error)
            {
                ReportAuthorityGuardFailure("Reading construction authority failed (" + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                if (!forward)
                {
                    // Dispatch was skipped, so no native callback or outgoing channel owns this storage.
                    try { __0.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportAuthorityGuardFailure("Releasing rejected event storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static bool BeforeRequest(FreeFormSystemNetworkProxyBase __instance, FreeFormPlaceElement __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("A free-form callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsFreeFormRequest request = null;
            try
            {
                // The native base callback is shared with clients; policy applies only on a server.
                if (!BoltNetwork.isServer)
                    return true;

                request = new OpenACAISonsFreeFormRequest(__0, __instance);
                foreach (Action<OpenACAISonsFreeFormRequest> handler in handlers.GetInvocationList())
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
                if (request?.NativeFeedbackStatus == OpenACAISonsFreeFormFeedbackStatus.Pending)
                    SendNativeFeedback(request);
            }
        }

        private static void SendNativeFeedback(OpenACAISonsFreeFormRequest request)
        {
            var sendAttempted = false;
            try
            {
                // Resolve the captured lifetime again after handlers, never a replacement sharing its transport ID.
                var connection = OpenACAISonsServerSessions.ResolveCurrent(request.SourceSession);
                if (connection == null)
                {
                    request.SetNativeFeedbackStatus(OpenACAISonsFreeFormFeedbackStatus.Unavailable);
                    return;
                }

                var reply = FreeFormStructureRejected.Create(connection, Bolt.ReliabilityModes.ReliableOrdered);
                reply.PlaceActionNum = request.RequestedPlaceActionNumber;
                sendAttempted = true;
                reply.Send();
                request.SetNativeFeedbackStatus(OpenACAISonsFreeFormFeedbackStatus.Queued);
            }
            catch (Exception error)
            {
                request.SetNativeFeedbackStatus(sendAttempted
                    ? OpenACAISonsFreeFormFeedbackStatus.SendUncertain
                    : OpenACAISonsFreeFormFeedbackStatus.Failed);
                if (Interlocked.Exchange(ref _reportedFeedbackFailure, 1) != 0)
                    return;
                try
                {
                    OpenACAIEndnightRuntime.LogSource?.LogWarning("Free-form rejection feedback failed ("
                        + error.GetType().FullName + "). Further feedback failures will not be logged.");
                }
                catch (Exception) { }
            }
        }

        private static void PublishState(OpenACAISonsFreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new OpenACAISonsServerFreeFormState(status, detail,
                current.AuthorityGuardStatus, current.AuthorityGuardDetail,
                current.SnapshotRequestStatus, current.SnapshotRequestDetail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void PublishAuthorityGuardState(OpenACAISonsFreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new OpenACAISonsServerFreeFormState(current.Status, current.Detail, status, detail,
                current.SnapshotRequestStatus, current.SnapshotRequestDetail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void PublishSnapshotRequestState(OpenACAISonsFreeFormHookStatus status, string detail)
        {
            var current = State;
            var state = new OpenACAISonsServerFreeFormState(current.Status, current.Detail,
                current.AuthorityGuardStatus, current.AuthorityGuardDetail, status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportSnapshotRequestFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedSnapshotRequestFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Construction snapshot request rejected: "
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Free-form construction authority guard: "
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server free-form policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsFreeFormHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public enum OpenACAISonsFreeFormFeedbackStatus
    {
        NotRequested,
        Pending,
        Unavailable,
        Queued,
        SendUncertain,
        Failed
    }

    public sealed class OpenACAISonsServerFreeFormState
    {
        internal OpenACAISonsServerFreeFormState(OpenACAISonsFreeFormHookStatus status, string detail,
            OpenACAISonsFreeFormHookStatus authorityGuardStatus, string authorityGuardDetail)
            : this(status, detail, authorityGuardStatus, authorityGuardDetail,
                OpenACAISonsFreeFormHookStatus.NotAttempted, "The construction snapshot-request hook has not been installed.")
        {
        }

        internal OpenACAISonsServerFreeFormState(OpenACAISonsFreeFormHookStatus status, string detail,
            OpenACAISonsFreeFormHookStatus authorityGuardStatus, string authorityGuardDetail,
            OpenACAISonsFreeFormHookStatus snapshotRequestStatus, string snapshotRequestDetail)
        {
            Status = status;
            Detail = detail;
            AuthorityGuardStatus = authorityGuardStatus;
            AuthorityGuardDetail = authorityGuardDetail;
            SnapshotRequestStatus = snapshotRequestStatus;
            SnapshotRequestDetail = snapshotRequestDetail;
        }

        public OpenACAISonsFreeFormHookStatus Status { get; }
        public string Detail { get; }
        /// <summary>Registration status for construction replication authority checks before routing.</summary>
        public OpenACAISonsFreeFormHookStatus AuthorityGuardStatus { get; }
        public string AuthorityGuardDetail { get; }
        /// <summary>The shared authority-hook status, retained for callers using the original rejection guard API.</summary>
        public OpenACAISonsFreeFormHookStatus RejectionGuardStatus => AuthorityGuardStatus;
        public string RejectionGuardDetail => AuthorityGuardDetail;
        /// <summary>Registration status for the full construction snapshot-request policy callback.</summary>
        public OpenACAISonsFreeFormHookStatus SnapshotRequestStatus { get; }
        public string SnapshotRequestDetail { get; }
    }

    /// <summary>Managed source context for a full construction snapshot request; no payload or native objects are retained.</summary>
    public sealed class OpenACAISonsFreeFormSnapshotRequest : OpenACAISonsConstructionRequest
    {
        internal OpenACAISonsFreeFormSnapshotRequest(FreeFormRequestAllData source) : base(source)
        {
        }
    }

    /// <summary>Raw construction or prediction selection, not a Bolt network ID or validated reference.</summary>
    public readonly struct OpenACAISonsFreeFormStructureSelection
    {
        internal OpenACAISonsFreeFormStructureSelection(int typeId, int instanceId, int direction)
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
    public sealed class OpenACAISonsFreeFormRequest : OpenACAISonsConstructionRequest
    {
        private int _nativeFeedbackStatus;
        private FreeFormSystemNetworkProxyBase _dispatchProxy;

        internal OpenACAISonsFreeFormRequest(FreeFormPlaceElement source, FreeFormSystemNetworkProxyBase dispatchProxy) : base(source)
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
        public OpenACAISonsFreeFormStructureSelection RequestedTarget { get; }
        public OpenACAISonsFreeFormStructureSelection RequestedEditing { get; }
        public OpenACAISonsFreeFormStructureSelection RequestedSupport1 { get; }
        public OpenACAISonsFreeFormStructureSelection RequestedSupport2 { get; }
        public OpenACAISonsFreeFormStructureSelection RequestedSupport3 { get; }
        public OpenACAISonsFreeFormStructureSelection RequestedSupport4 { get; }
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

        /// <summary>Local feedback-send outcome, not confirmation of receipt, prediction cleanup or inventory restoration.</summary>
        public OpenACAISonsFreeFormFeedbackStatus NativeFeedbackStatus =>
            (OpenACAISonsFreeFormFeedbackStatus)Volatile.Read(ref _nativeFeedbackStatus);

        /// <summary>
        /// Rejects and requests one native rejection notice to this exact source session after dispatch.
        /// Does not run native fallback pickups, cancel server animations, or restore inventory.
        /// </summary>
        public bool RejectWithNativeFeedback()
        {
            if (!Reject())
                return false;
            SetNativeFeedbackStatus(OpenACAISonsFreeFormFeedbackStatus.Pending);
            return true;
        }

        internal void SetNativeFeedbackStatus(OpenACAISonsFreeFormFeedbackStatus status)
        {
            Volatile.Write(ref _nativeFeedbackStatus, (int)status);
        }
    }
}
