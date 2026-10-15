// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using OpenACAI.Endnight.Loader;
using Sons.Gameplay;
using Sons.Multiplayer;
using Sons.Save;

namespace OpenACAI.Sons
{
    /// <summary>Explicit save requests through the game's dedicated-server autosave controller.</summary>
    public static class OpenACAISonsServerSaves
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
        public static OpenACAISonsServerSaveRequestStatus RequestConfiguredAutosave()
        {
            var ownerThread = Volatile.Read(ref _ownerThreadId);
            if (ownerThread == 0)
                return OpenACAISonsServerSaveRequestStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != ownerThread)
                return OpenACAISonsServerSaveRequestStatus.WrongThread;

            var requestAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer || !GameServerManager.IsDedicatedServer)
                    return OpenACAISonsServerSaveRequestStatus.NotDedicatedServer;

                if (!SaveGameManager.TryGetInstance(out var manager)
                    || manager is null || manager.WasCollected || !manager)
                    return OpenACAISonsServerSaveRequestStatus.SaveManagerUnavailable;
                if (manager._loadingInProgress || !SaveGameManager._setupSavePaths
                    || SaveGameManager._activeSaveGameType != SaveGameType.Multiplayer)
                    return OpenACAISonsServerSaveRequestStatus.NotReady;

                if (!DedicatedServerAutosave.TryGetInstance(out var autosave)
                    || autosave is null || autosave.WasCollected || !autosave || !autosave.isActiveAndEnabled)
                    return OpenACAISonsServerSaveRequestStatus.AutosaveUnavailable;
                var timer = autosave._timer;
                if (timer is null || timer.WasCollected)
                    return OpenACAISonsServerSaveRequestStatus.AutosaveUnavailable;

                // QueueAutosave also sets the initial-save flag; do not use it to bypass native readiness.
                if (!autosave.IsReadyToSave())
                    return OpenACAISonsServerSaveRequestStatus.NotReady;
                if (autosave._autosaveQueued)
                    return OpenACAISonsServerSaveRequestStatus.AlreadyQueued;

                requestAttempted = true;
                DedicatedServerAutosave.QueueAutosave();

                if (!DedicatedServerAutosave.TryGetInstance(out var current)
                    || current is null || current.WasCollected || !current || !current.isActiveAndEnabled
                    || current.Pointer != autosave.Pointer)
                    return OpenACAISonsServerSaveRequestStatus.RequestUncertain;

                return current._autosaveQueued
                    ? OpenACAISonsServerSaveRequestStatus.Queued
                    : OpenACAISonsServerSaveRequestStatus.RequestUncertain;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return requestAttempted
                    ? OpenACAISonsServerSaveRequestStatus.RequestUncertain
                    : OpenACAISonsServerSaveRequestStatus.Failed;
            }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Dedicated-server save request failed ("
                    + error.GetType().FullName + "). Further request failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsServerSaveRequestStatus
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
