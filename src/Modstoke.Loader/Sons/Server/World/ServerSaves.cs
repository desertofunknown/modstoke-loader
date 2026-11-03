// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Gameplay;
using global::Sons.Multiplayer;
using global::Sons.Save;

namespace Modstoke.Sons
{
    /// <summary>Explicit save requests through the game's dedicated-server autosave controller.</summary>
    public static class ServerSaves
    {
        private static int _ownerThreadId;
        private static int _reportedFailure;

        internal static void Initialize()
        {
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);
        }

        /// <summary>
        /// Queues an autosave using the dedicated server's native configuration and readiness rules.
        /// Queued is not evidence of serialization, backup creation or durable storage.
        /// The caller owns authorization. No request is made automatically.
        /// </summary>
        public static ServerSaveRequestStatus RequestConfiguredAutosave()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return ServerSaveRequestStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return ServerSaveRequestStatus.WrongThread;

            var requestAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer
                    || !GameServerManager.HasDedicatedState || !GameServerManager.IsDedicatedServer)
                    return ServerSaveRequestStatus.NotDedicatedServer;

                if (!SaveGameManager.TryGetInstance(out var manager)
                    || manager is null || manager.WasCollected || !manager)
                    return ServerSaveRequestStatus.SaveManagerUnavailable;
                if (manager._loadingInProgress || !SaveGameManager._setupSavePaths
                    || SaveGameManager._activeSaveGameType != SaveGameType.Multiplayer)
                    return ServerSaveRequestStatus.NotReady;

                if (!DedicatedServerAutosave.TryGetInstance(out var autosave)
                    || autosave is null || autosave.WasCollected || !autosave || !autosave.isActiveAndEnabled)
                    return ServerSaveRequestStatus.AutosaveUnavailable;
                var timer = autosave._timer;
                if (timer is null || timer.WasCollected)
                    return ServerSaveRequestStatus.AutosaveUnavailable;

                // QueueAutosave also sets the initial-save flag; do not use it to bypass native readiness.
                if (!autosave.IsReadyToSave())
                    return ServerSaveRequestStatus.NotReady;
                if (autosave._autosaveQueued)
                    return ServerSaveRequestStatus.AlreadyQueued;

                requestAttempted = true;
                DedicatedServerAutosave.QueueAutosave();

                if (!DedicatedServerAutosave.TryGetInstance(out var current)
                    || current is null || current.WasCollected || !current || !current.isActiveAndEnabled
                    || current.Pointer != autosave.Pointer)
                    return ServerSaveRequestStatus.RequestUncertain;

                return current._autosaveQueued
                    ? ServerSaveRequestStatus.Queued
                    : ServerSaveRequestStatus.RequestUncertain;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return requestAttempted
                    ? ServerSaveRequestStatus.RequestUncertain
                    : ServerSaveRequestStatus.Failed;
            }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Dedicated-server save request failed ("
                    + error.GetType().FullName + "). Further request failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ServerSaveRequestStatus
    {
        /// <summary>The same live controller reported a queued request after the native call returned.</summary>
        Queued,
        /// <summary>The controller already had a queued request; no additional request was made.</summary>
        AlreadyQueued,
        Unavailable,
        WrongThread,
        NotDedicatedServer,
        SaveManagerUnavailable,
        AutosaveUnavailable,
        NotReady,
        /// <summary>Failure before invoking QueueAutosave. Native readiness may have refreshed its cached flag.</summary>
        Failed,
        /// <summary>The native request may have changed state. No retry or rollback is attempted.</summary>
        RequestUncertain
    }
}
