// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;
using Steamworks;

namespace OpenACAI.Sons
{
    /// <summary>Read-only observations from the game's Steam game-server validation callback.</summary>
    public static class OpenACAISonsServerAuthentication
    {
        private static int _installationAttempted;
        private static int _reportedFailure;
        private static bool _hooksInstalled;

        /// <summary>Hook registration succeeded, not proof that a ticket or player has been authenticated.</summary>
        public static bool HooksInstalled => Volatile.Read(ref _hooksInstalled);

        /// <summary>
        /// Runs synchronously on the native callback's thread after normal handling returns.
        /// Observations cannot cancel validation and do not identify a Bolt session or a specific ticket.
        /// </summary>
        public static event Action<OpenACAISonsAuthenticationObservation> Observed;

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> warning)
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

                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-authentication");
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(OpenACAISonsServerAuthentication), nameof(AfterValidation)));
                Volatile.Write(ref _hooksInstalled, true);
            }
            catch (Exception error)
            {
                Warn(warning, "Server authentication observation hook failed (" + error.GetType().FullName
                    + "); observations are disabled.");
            }
        }

        private static void AfterValidation(ValidateAuthTicketResponse_t __0, bool __runOriginal)
        {
            var handlers = Observed;
            if (!HooksInstalled || !__runOriginal || handlers == null)
                return;

            try
            {
                var observation = new OpenACAISonsAuthenticationObservation(__0.m_SteamID.m_SteamID,
                    __0.m_OwnerSteamID.m_SteamID, (int)__0.m_eAuthSessionResponse);
                foreach (Action<OpenACAISonsAuthenticationObservation> handler in handlers.GetInvocationList())
                {
                    try { handler(observation); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error)
            {
                ReportFailure(error);
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
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server authentication observation failed ("
                    + error.GetType().FullName + "). Further observation failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    /// <summary>Known Steam validation response values. These are not roles or permission levels.</summary>
    public enum OpenACAISonsAuthSessionResponse
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
    public sealed class OpenACAISonsAuthenticationObservation
    {
        internal OpenACAISonsAuthenticationObservation(ulong steamId, ulong ownerSteamId, int rawResponse)
        {
            SteamId = steamId;
            OwnerSteamId = ownerSteamId;
            RawResponse = rawResponse;
            var response = (OpenACAISonsAuthSessionResponse)rawResponse;
            Response = Enum.IsDefined(response) ? response : null;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Reported ticket subject, including zero if supplied. Not a Bolt session identifier.</summary>
        public ulong SteamId { get; }
        /// <summary>Reported game owner. It can differ from SteamId and does not confer server permissions.</summary>
        public ulong OwnerSteamId { get; }
        public int RawResponse { get; }
        /// <summary>Known interpretation of RawResponse, or null for an unrecognized value.</summary>
        public OpenACAISonsAuthSessionResponse? Response { get; }
        /// <summary>Local observation time, not the time Steam validated a ticket.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
