// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using OpenACAI.Endnight.Loader;
using Sons.Save;
using Sons.TerrainDetail;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorldLocators
    {
        private static int _reportedRequestFailure;

        /// <summary>
        /// Queues a native locator-state event for this server and its peers. The caller owns authorization.
        /// Queued means the native call returned, not policy acceptance, application, delivery or persistence.
        /// </summary>
        public static OpenACAISonsWorldLocatorChangeStatus RequestState(Guid locatorId, OpenACAISonsWorldLocatorState newState)
        {
            if (State.Status != OpenACAISonsWorldLocatorHookStatus.Installed)
                return OpenACAISonsWorldLocatorChangeStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return OpenACAISonsWorldLocatorChangeStatus.WrongThread;
            if (!Enum.IsDefined(newState))
                return OpenACAISonsWorldLocatorChangeStatus.InvalidState;

            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return OpenACAISonsWorldLocatorChangeStatus.NotServer;
                if (!SaveGameManager.TryGetInstance(out var saveManager)
                    || saveManager is null || saveManager.WasCollected || !saveManager || saveManager._loadingInProgress)
                    return OpenACAISonsWorldLocatorChangeStatus.NotReady;
                if (!OpenACAISonsWorldLocatorReads.TryRead(locatorId, out var observation, out _))
                    return OpenACAISonsWorldLocatorChangeStatus.ReadUnavailable;
                if (!observation.HasAnyObservation)
                    return OpenACAISonsWorldLocatorChangeStatus.LocatorNotObserved;

                var id = OpenACAISonsWorldLocatorReads.CreateLookupId(observation.LocatorId, out _);
                sendAttempted = true;
                // Everyone includes native local handling after the routing policy. Do not mutate a holder first.
                WorldObjectLocator.SetStateNetwork((WorldObjectState)newState, id, GlobalTargets.Everyone);
                return OpenACAISonsWorldLocatorChangeStatus.Queued;
            }
            catch (Exception error)
            {
                ReportRequestFailure(error);
                return sendAttempted
                    ? OpenACAISonsWorldLocatorChangeStatus.SendUncertain
                    : OpenACAISonsWorldLocatorChangeStatus.Failed;
            }
        }

        private static void ReportRequestFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedRequestFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("World-locator state request failed ("
                    + error.GetType().FullName + "). Further request failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsWorldLocatorChangeStatus
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
