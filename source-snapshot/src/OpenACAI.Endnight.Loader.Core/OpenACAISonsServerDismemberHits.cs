// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Policy before the native server handles DismemberHitEvent requests.</summary>
    public static class OpenACAISonsServerDismemberHits
    {
        public const string ServiceKey = "openacai.sons.serverDismemberHits";

        private static OpenACAISonsServerDismemberHitState _state = new(
            OpenACAISonsDismemberHitHookStatus.NotAttempted, "The dismember-hit hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server's DismemberHitEvent callback and its actor lookup.
        /// Rejection affects this request only, not other AI-hit or direct dismemberment paths.
        /// </summary>
        public static event Action<OpenACAISonsDismemberHitRequest> Receiving;

        public static OpenACAISonsServerDismemberHitState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsDismemberHitHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server dismember-hit policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsDismemberHitHookStatus.Installing, "Registering the dismember-hit hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { typeof(DismemberHitEvent) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(DismemberHitEvent)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerDismemberHits), nameof(BeforeHit))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-dismember-hits");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsDismemberHitHookStatus.Failed,
                    "Dismember-hit hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server dismember-hit policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsDismemberHitHookStatus.Installed,
                "Dismember-hit hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeHit(DismemberHitEvent __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The dismember-hit callback ran outside the loader's game thread.");
                return false;
            }

            OpenACAISonsDismemberHitRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                var connection = __0.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = new OpenACAISonsDismemberHitRequest(__0, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsDismemberHitRequest> handler in handlers.GetInvocationList())
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
                ReportDispatchFailure("Reading dismember-hit context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(OpenACAISonsDismemberHitHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerDismemberHitState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server dismember-hit policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsDismemberHitHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerDismemberHitState
    {
        internal OpenACAISonsServerDismemberHitState(OpenACAISonsDismemberHitHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsDismemberHitHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of an unvalidated dismemberment request, with no retained native objects.</summary>
    public sealed class OpenACAISonsDismemberHitRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsDismemberHitRequest(DismemberHitEvent source, uint? sourceConnectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RequestedDamage = source.Damage;
            RequestedPartId = source.PartId;
            var position = source.PartPosition;
            RequestedPartPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.PartRotation;
            RequestedPartRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Sender-selected target, not proof of a valid AI actor, ownership or proximity.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Raw contribution to dismemberment damage, not an actor-health delta.</summary>
        public float RequestedDamage { get; }
        /// <summary>Raw part selection, distinct from VailHitEvent's damage-node ID and from item IDs.</summary>
        public int RequestedPartId { get; }
        /// <summary>Sender-supplied part pose that native handling may use for spawning, not an observed transform.</summary>
        public Vector3 RequestedPartPosition { get; }
        /// <summary>Raw quaternion components, without normalization or validity checks.</summary>
        public Quaternion RequestedPartRotation { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this server callback without undoing prior effects or sending a denial message.
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
