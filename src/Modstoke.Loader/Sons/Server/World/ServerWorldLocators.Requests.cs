// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using Modstoke.Loader;
using global::Sons.Save;
using global::Sons.TerrainDetail;

namespace Modstoke.Sons
{
    public static partial class ServerWorldLocators
    {
        private static int _reportedRequestFailure;

        /// <summary>
        /// Queues a native locator-state event for this server and its peers. The caller owns authorization.
        /// Queued means the native call returned, not policy acceptance, application, delivery or persistence.
        /// </summary>
        public static WorldLocatorChangeStatus RequestState(Guid locatorId, WorldLocatorState newState)
        {
            if (State.Status != WorldLocatorHookStatus.Installed)
                return WorldLocatorChangeStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return WorldLocatorChangeStatus.WrongThread;
            if (!Enum.IsDefined(newState))
                return WorldLocatorChangeStatus.InvalidState;

            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return WorldLocatorChangeStatus.NotServer;
                if (!SaveGameManager.TryGetInstance(out var saveManager)
                    || saveManager is null || saveManager.WasCollected || !saveManager || saveManager._loadingInProgress)
                    return WorldLocatorChangeStatus.NotReady;
                if (!WorldLocatorReads.TryRead(locatorId, out var observation, out _, out var id))
                    return WorldLocatorChangeStatus.ReadUnavailable;
                if (!observation.HasAnyObservation)
                    return WorldLocatorChangeStatus.LocatorNotObserved;

                sendAttempted = true;
                // Everyone includes native local handling after the routing policy. Do not mutate a holder first.
                WorldObjectLocator.SetStateNetwork((WorldObjectState)newState, id, GlobalTargets.Everyone);
                return WorldLocatorChangeStatus.Queued;
            }
            catch (Exception error)
            {
                ReportRequestFailure(error);
                return sendAttempted
                    ? WorldLocatorChangeStatus.SendUncertain
                    : WorldLocatorChangeStatus.Failed;
            }
        }

        private static void ReportRequestFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRequestFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("World-locator state request failed ("
                    + error.GetType().FullName + "). Further request failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum WorldLocatorChangeStatus
    {
        /// <summary>The native network-only request returned; subsequent routing policy may still reject it.</summary>
        Queued,
        Unavailable,
        WrongThread,
        InvalidState,
        NotServer,
        /// <summary>The save manager is unavailable or a save load is in progress.</summary>
        NotReady,
        /// <summary>The existing local locator observation API could not read its required sources.</summary>
        ReadUnavailable,
        /// <summary>No matching registered, recorded or unapplied entry was observed. No request was sent.</summary>
        LocatorNotObserved,
        /// <summary>Failure before invoking the native network request.</summary>
        Failed,
        /// <summary>The native call may have queued the request. No retry or rollback is attempted.</summary>
        SendUncertain
    }
}
