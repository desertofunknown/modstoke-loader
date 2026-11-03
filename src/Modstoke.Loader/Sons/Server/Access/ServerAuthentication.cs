// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;
using Steamworks;

namespace Modstoke.Sons
{
    /// <summary>Read-only observations from the game's Steam game-server validation callback.</summary>
    public static class ServerAuthentication
    {
        /// <summary>Accounts TryReadLatest retains. The oldest observation is evicted first.</summary>
        public const int MaximumRetainedObservations = 1024;

        private static readonly object LatestLock = new();
        private static readonly Dictionary<ulong, AuthenticationObservation> Latest = new();
        private static long _observationCount;
        private static int _installationAttempted;
        private static int _reportedFailure;
        private static bool _hooksInstalled;

        /// <summary>Hook registration succeeded, not proof that a ticket or player has been authenticated.</summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>
        /// Runs synchronously on the native callback's thread after normal handling returns.
        /// Observations cannot cancel validation and do not identify a Bolt session or a specific ticket.
        /// </summary>
        public static event Action<AuthenticationObservation> Observed;

        /// <summary>Observations recorded since load, with or without subscribers. Not a count of joins.</summary>
        public static long ObservationCount => Interlocked.Read(ref _observationCount);

        /// <summary>
        /// Returns the latest observation recorded for this account since load; callable from any thread.
        /// Account-only: it can predate the current session or come from an earlier connection, and must not
        /// be carried over to a reconnect. A missing entry proves nothing, because rejected joins, listen-host
        /// joins and undelivered callbacks never produce one.
        /// </summary>
        public static bool TryReadLatest(ulong steamId, out AuthenticationObservation latest)
        {
            lock (LatestLock)
                return Latest.TryGetValue(steamId, out latest);
        }

        /// <summary>
        /// Resolves the session behind VanillaTargetConnectionId. Valid only during the Observed callback, and
        /// only when that callback runs on the game thread; otherwise it reports WrongThread. This is the game's
        /// own SteamID-to-connection table, not an authenticated binding, and a failed response may already
        /// have queued that session's disconnect.
        /// </summary>
        public static bool TryGetVanillaTarget(AuthenticationObservation observation, out ServerSession session,
            out ConnectionReadStatus status)
        {
            ArgumentNullException.ThrowIfNull(observation);
            session = null;
            if (!HooksInstalled || !ServerSessions.HooksInstalled)
            {
                status = ConnectionReadStatus.Unavailable;
                return false;
            }
            if (!ServerSessions.IsOwnerThread || !observation.IsCallbackThread)
            {
                status = ConnectionReadStatus.WrongThread;
                return false;
            }

            try
            {
                if (!observation.TryGetOpenTarget(out var connection))
                {
                    status = ConnectionReadStatus.Unavailable;
                    return false;
                }
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = ConnectionReadStatus.NotServer;
                    return false;
                }
                if (connection == null || connection.WasCollected
                    || connection.ConnectionId != observation.VanillaTargetConnectionId)
                {
                    status = ConnectionReadStatus.SessionUnavailable;
                    return false;
                }
                var resolved = ServerSessions.ResolveSource(connection);
                if (resolved == null || !resolved.IsCurrent)
                {
                    status = ServerSessions.HooksInstalled ? ConnectionReadStatus.SessionUnavailable
                        : ConnectionReadStatus.Unavailable;
                    return false;
                }
                session = resolved;
                status = ConnectionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = ConnectionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                Warn(warning, "Server authentication observations were not installed: Harmony support is unavailable.");
                return;
            }

            try
            {
                var target = AccessTools.Method(typeof(SonsServerSteam), nameof(SonsServerSteam.ValidateAuthTicketResponse),
                    new[] { typeof(ValidateAuthTicketResponse_t) });
                if (target == null || !target.IsStatic || target.ReturnType != typeof(void))
                    throw new MissingMethodException(typeof(SonsServerSteam).FullName, "ValidateAuthTicketResponse");

                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-authentication");
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(ServerAuthentication), nameof(BeforeValidation)),
                    postfix: new HarmonyMethod(typeof(ServerAuthentication), nameof(AfterValidation)));
                Volatile.Write(ref _hooksInstalled, true);
            }
            catch (Exception error)
            {
                Warn(warning, "Server authentication observation hook failed (" + error.GetType().FullName
                    + "); observations are disabled.");
            }
        }

        private static void BeforeValidation(ValidateAuthTicketResponse_t __0, out ValidationTarget __state)
        {
            __state = null;
            if (!HooksInstalled)
                return;
            try
            {
                // The native handler makes this lookup for a failed response, then disconnects what it finds.
                var found = GameServerManager.GetRegisteredClientBoltConnectionId(__0.m_SteamID, out var connection);
                __state = new ValidationTarget(found && connection != null && !connection.WasCollected
                    ? connection : null);
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
        }

        private static void AfterValidation(ValidateAuthTicketResponse_t __0, bool __runOriginal,
            ValidationTarget __state)
        {
            if (!HooksInstalled || !__runOriginal)
                return;

            AuthenticationObservation observation = null;
            try
            {
                observation = new AuthenticationObservation(__0.m_SteamID.m_SteamID,
                    __0.m_OwnerSteamID.m_SteamID, (int)__0.m_eAuthSessionResponse, __state);
                Record(observation);
                var handlers = Observed;
                if (handlers == null)
                    return;
                foreach (Action<AuthenticationObservation> handler in handlers.GetInvocationList())
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error)
            {
                ReportFailure(error);
            }
            finally
            {
                observation?.Close();
            }
        }

        private static void Record(AuthenticationObservation observation)
        {
            Interlocked.Increment(ref _observationCount);
            lock (LatestLock)
            {
                if (Latest.Count >= MaximumRetainedObservations && !Latest.ContainsKey(observation.SteamId))
                {
                    var oldest = default(KeyValuePair<ulong, AuthenticationObservation>);
                    foreach (var pair in Latest)
                    {
                        if (oldest.Value == null || pair.Value.ObservedAt < oldest.Value.ObservedAt)
                            oldest = pair;
                    }
                    Latest.Remove(oldest.Key);
                }
                Latest[observation.SteamId] = observation;
            }
        }

        private static void Warn(Action<string> warning, string message)
        {
            try { warning?.Invoke(message); }
            catch (Exception) { }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server authentication observation failed ("
                    + error.GetType().FullName + "). Further observation failures will not be logged.");
            }
            catch (Exception) { }
        }

        internal sealed class ValidationTarget
        {
            internal ValidationTarget(BoltConnection connection)
            {
                Connection = connection;
                ConnectionId = connection == null ? null : connection.ConnectionId;
            }

            internal BoltConnection Connection { get; }
            internal uint? ConnectionId { get; }
        }
    }

    /// <summary>Known Steam validation response values. These are not roles or permission levels.</summary>
    public enum AuthSessionResponse
    {
        Ok = 0,
        UserNotConnectedToSteam = 1,
        NoLicenseOrExpired = 2,
        VacBanned = 3,
        LoggedInElsewhere = 4,
        VacCheckTimedOut = 5,
        AuthTicketCanceled = 6,
        AuthTicketInvalidAlreadyUsed = 7,
        AuthTicketInvalid = 8,
        PublisherIssuedBan = 9,
        AuthTicketNetworkIdentityFailure = 10
    }

    /// <summary>Immutable callback values, not a retained authentication state or proof for a current connection.</summary>
    public sealed class AuthenticationObservation
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private BoltConnection _target;
        private bool _open = true;

        internal AuthenticationObservation(ulong steamId, ulong ownerSteamId, int rawResponse,
            ServerAuthentication.ValidationTarget target)
        {
            SteamId = steamId;
            OwnerSteamId = ownerSteamId;
            RawResponse = rawResponse;
            var response = (AuthSessionResponse)rawResponse;
            Response = Enum.IsDefined(response) ? response : null;
            VanillaTargetChecked = target != null;
            VanillaTargetConnectionId = target?.ConnectionId;
            _target = target?.Connection;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Reported ticket subject, including zero if supplied. Not a Bolt session identifier.</summary>
        public ulong SteamId { get; }
        /// <summary>Reported game owner. It can differ from SteamId and does not confer server permissions.</summary>
        public ulong OwnerSteamId { get; }
        public int RawResponse { get; }
        /// <summary>Known interpretation of RawResponse, or null for an unrecognized value.</summary>
        public AuthSessionResponse? Response { get; }
        /// <summary>Local observation time, not the time Steam validated a ticket.</summary>
        public DateTimeOffset ObservedAt { get; }

        /// <summary>
        /// True when the SDK took the game's SteamID-to-connection lookup before the native handler ran.
        /// When false, a null VanillaTargetConnectionId says nothing about what vanilla did.
        /// </summary>
        public bool VanillaTargetChecked { get; }
        /// <summary>
        /// The game's own SteamID-to-connection lookup, taken just before the native handler runs. Not an
        /// authenticated binding: it names whichever connection the game registered for this account, which
        /// may not be the one that presented the ticket. For a failed response, null with VanillaTargetChecked
        /// means vanilla only logs. For Ok responses vanilla makes no lookup; the value is read anyway.
        /// </summary>
        public uint? VanillaTargetConnectionId { get; }
        /// <summary>
        /// Whether the native failure branch disconnects a connection, from static reading of that handler.
        /// It does not confirm the disconnect completed or that the client left.
        /// </summary>
        public bool VanillaWillDisconnect => RawResponse != 0 && VanillaTargetConnectionId.HasValue;

        internal bool IsCallbackThread => Environment.CurrentManagedThreadId == _thread;

        internal bool TryGetOpenTarget(out BoltConnection target)
        {
            target = _target;
            return _open;
        }

        internal void Close()
        {
            _open = false;
            _target = null;
        }
    }
}
