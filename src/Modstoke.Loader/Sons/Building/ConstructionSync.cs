// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Construction;
using Construction.Multiplayer;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Coordinates optional-client snapshot requests and native construction queue loading.</summary>
    public static class ConstructionSync
    {
        public const string ServiceKey = "openacai.sons.constructionSync";

        private static ConstructionSyncState _state = new(
            ConstructionSyncHookStatus.NotAttempted, "The construction sync guard has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedUpdateFailure;
        private static int _reportedRequestFailure;

        public static ConstructionSyncState State => Volatile.Read(ref _state);

        /// <summary>
        /// Requests a full construction snapshot from the current server on the game thread.
        /// The client must have finished joining and have no pending construction predictions or incomplete batch.
        /// Requested means the native request call returned, not that a snapshot arrived or rebuilt successfully.
        /// No automatic retry or timeout is provided; denial or transfer failure can leave the native queue waiting.
        /// </summary>
        public static ConstructionSnapshotRequestStatus RequestSnapshot()
        {
            if (State.Status != ConstructionSyncHookStatus.Installed)
                return ConstructionSnapshotRequestStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return ConstructionSnapshotRequestStatus.WrongThread;

            var requestStarted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer || !BoltNetwork.isConnected)
                    return ConstructionSnapshotRequestStatus.NotClient;
                var server = BoltNetwork.server;
                if (server is null || server.WasCollected)
                    return ConstructionSnapshotRequestStatus.Unavailable;
                var serverPointer = server.Pointer;
                var serverId = server.ConnectionId;

                if (BlobStuffClient.IsWaitingForBlob || RuntimeStructureDatabase.IsLoadingSave
                    || RuntimeStructureDatabase.IsLoadingPrefab)
                    return ConstructionSnapshotRequestStatus.Busy;

                // Awake installs both components on the same object. Do not create a new blob helper:
                // its constructor replaces the construction channel's serializer registration.
                var predictions = FreeFormSystemNetworkProxyClient.LocallyAnticipatedStructures;
                if (predictions is null || predictions.WasCollected || !predictions
                    || !predictions.isActiveAndEnabled || !predictions._initialized)
                    return ConstructionSnapshotRequestStatus.Unavailable;
                var client = predictions.GetComponent<FreeFormSystemNetworkProxyClient>();
                if (client is null || client.WasCollected || !client || !client.isActiveAndEnabled)
                    return ConstructionSnapshotRequestStatus.Unavailable;
                var blob = client._blobStuff;
                var batch = client._commandBatch;
                var commands = client._commands;
                var created = predictions._anticipatedStructures;
                var dismantled = predictions._anticipatedDismantledStructures;
                if (blob is null || blob.WasCollected || batch is null || batch.WasCollected || !batch
                    || commands is null || created is null || dismantled is null)
                    return ConstructionSnapshotRequestStatus.Unavailable;

                if (!RuntimeStructureDatabase.TryGetInstance(out var database)
                    || database is null || database.WasCollected || !database)
                    return ConstructionSnapshotRequestStatus.Unavailable;
                var manager = client.GetOrFetchConstructionManager();
                if (manager is null || manager.WasCollected || !manager || !manager.isActiveAndEnabled)
                    return ConstructionSnapshotRequestStatus.Unavailable;
                if (database.IsLocked || manager.RunningBlockingRoutine || created.Count != 0 || dismantled.Count != 0
                    || batch.IsWaitingForFullBatch(commands))
                    return ConstructionSnapshotRequestStatus.Busy;

                var transport = BlobSync.Transport;
                var serializers = BlobSync.ChannelSerializer;
                if (transport is null || transport.WasCollected || !transport.IsConnectClient
                    || serializers is null || !serializers.TryGetValue(1, out var serializer) || serializer is null)
                    return ConstructionSnapshotRequestStatus.Unavailable;

                var currentServer = BoltNetwork.server;
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer || !BoltNetwork.isConnected
                    || currentServer is null || currentServer.WasCollected
                    || currentServer.Pointer != serverPointer || currentServer.ConnectionId != serverId)
                    return ConstructionSnapshotRequestStatus.Unavailable;

                // From this point an exception can leave the wait set or the request queued.
                // Native completion owns the reset and version cleanup; do not retry or force it here.
                requestStarted = true;
                blob.BeginWaitingForBlob();
                FreeFormSystemNetworkProxyClient.RequestAllStructuresData();
                return ConstructionSnapshotRequestStatus.Requested;
            }
            catch (Exception error)
            {
                ReportRequestFailure(error);
                return requestStarted
                    ? ConstructionSnapshotRequestStatus.RequestUncertain
                    : ConstructionSnapshotRequestStatus.Failed;
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(ConstructionSyncHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Construction sync guard was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(ConstructionSyncHookStatus.Installing, "Registering the construction sync guard.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyClient),
                    nameof(FreeFormSystemNetworkProxyClient.LateUpdate), Type.EmptyTypes);
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyClient).FullName, "LateUpdate()");

                var prefix = new HarmonyMethod(typeof(ConstructionSync), nameof(BeforeClientUpdate))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-construction-sync");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(ConstructionSyncHookStatus.Failed,
                    "Construction sync guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Construction sync guard failed: " + error);
                return;
            }

            PublishState(ConstructionSyncHookStatus.Installed,
                "Construction sync guard registration succeeded. Live snapshot application remains unverified.");
        }

        private static bool BeforeClientUpdate()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportUpdateFailure("A construction client update ran outside the loader's game thread.");
                return false;
            }

            try
            {
                // Native blob application sets this flag while rebuilding, including coroutine waits.
                // Keep queued commands for the native completion/version cleanup instead of applying them mid-load.
                return !RuntimeStructureDatabase.IsLoadingSave;
            }
            catch (Exception error)
            {
                ReportUpdateFailure("Reading the construction loading state failed.", error);
                return false;
            }
        }

        private static void PublishState(ConstructionSyncHookStatus status, string detail)
        {
            var state = new ConstructionSyncState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportUpdateFailure(string detail, Exception error = null)
        {
            if (Interlocked.Exchange(ref _reportedUpdateFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Construction sync update deferred: "
                    + detail + " Further update failures will not be logged."
                    + (error == null ? string.Empty : " " + error));
            }
            catch (Exception) { }
        }

        private static void ReportRequestFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRequestFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Construction snapshot request failed. Further request failures will not be logged. "
                    + error);
            }
            catch (Exception) { }
        }
    }

    public enum ConstructionSnapshotRequestStatus
    {
        /// <summary>The native request call returned. This is not a server response or snapshot receipt.</summary>
        Requested,
        /// <summary>The sync guard or current client construction/blob components are unavailable.</summary>
        Unavailable,
        WrongThread,
        NotClient,
        /// <summary>Native loading, waiting, construction work or prediction reconciliation is still in progress.</summary>
        Busy,
        /// <summary>Preparation failed before attempting to set the wait flag or issue the request.</summary>
        Failed,
        /// <summary>The wait/request step threw. Waiting may be set and the request may be queued; do not blindly retry.</summary>
        RequestUncertain
    }

    public enum ConstructionSyncHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ConstructionSyncState
    {
        internal ConstructionSyncState(ConstructionSyncHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Hook registration status, not current loading state or confirmation of a completed snapshot.</summary>
        public ConstructionSyncHookStatus Status { get; }
        public string Detail { get; }
    }
}
