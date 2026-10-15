// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Bolt;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    /// <summary>Policy before a server routes native HitPlayer events to their recipients.</summary>
    public static class OpenACAISonsServerPlayerHits
    {
        public const string ServiceKey = "openacai.sons.serverPlayerHits";

        private static OpenACAISonsServerPlayerHitState _state = new(
            OpenACAISonsPlayerHitHookStatus.NotAttempted, "The player-hit hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs on the server before routing received or locally created HitPlayer events.
        /// Rejection stops this event's routing, not every source of player damage.
        /// </summary>
        public static event Action<OpenACAISonsPlayerHitRequest> Receiving;

        public static OpenACAISonsServerPlayerHitState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsPlayerHitHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server player-hit policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(OpenACAISonsPlayerHitHookStatus.Installing, "Registering the player-hit routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerPlayerHits), nameof(BeforeDispatch))
                {
                    priority = HarmonyLib.Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-server-player-hits");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsPlayerHitHookStatus.Failed,
                    "Player-hit hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server player-hit policy hook failed: " + error);
                return;
            }

            PublishState(OpenACAISonsPlayerHitHookStatus.Installed,
                "Player-hit hook registration succeeded. This is not evidence of a received or blocked hit.");
        }

        private static bool BeforeDispatch(Bolt.Event __0)
        {
            var handlers = Receiving;
            if (handlers == null)
                return true;

            var hit = __0?.TryCast<HitPlayer>();
            if (hit == null)
                return true;

            var forward = false;
            OpenACAISonsPlayerHitRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The player-hit callback ran outside the loader's game thread.");
                    return false;
                }

                var connection = hit.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = new OpenACAISonsPlayerHitRequest(hit, connectionId, registeredSteamId,
                    OpenACAISonsServerSessions.ResolveSource(connection));
                foreach (Action<OpenACAISonsPlayerHitRequest> handler in handlers.GetInvocationList())
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                forward = true;
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading player-hit context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    // Skipping Dispatch also skips its storage release or transfer to an outgoing channel.
                    try { hit.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected player-hit storage failed (" + error.GetType().FullName + ").");
                    }
                }
            }
        }

        private static void PublishState(OpenACAISonsPlayerHitHookStatus status, string detail)
        {
            var state = new OpenACAISonsServerPlayerHitState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Server player-hit policy rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum OpenACAISonsPlayerHitHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsServerPlayerHitState
    {
        internal OpenACAISonsServerPlayerHitState(OpenACAISonsPlayerHitHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public OpenACAISonsPlayerHitHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of routing and hit fields. No native event, entity or connection is retained.</summary>
    public sealed class OpenACAISonsPlayerHitRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal OpenACAISonsPlayerHitRequest(HitPlayer source, uint? sourceConnectionId,
            ulong? registeredSteamId, OpenACAISonsServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RoutingTargets = source.Targets;
            var routedEntity = source.TargetEntity;
            RoutedEntityNetworkId = routedEntity == null ? null : routedEntity.NetworkId.PackedValue;
            var routedConnection = source.TargetConnection;
            RoutedConnectionId = routedConnection == null ? null : routedConnection.ConnectionId;
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RequestedDamage = source.Damage;
            RequestedImpactType = source.ImpactType;
            RequestedWeight = source.Weight;
            RequestedBurnTime = source.BurnTime;
            var position = source.AttackerPos;
            ClaimedAttackerPosition = new Vector3(position.x, position.y, position.z);
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable, not proof of a player-controlled attack.</summary>
        public OpenACAISonsServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Raw native routing mode. OnlyOwner is 9; no mode or recipient validity is inferred.</summary>
        public int RoutingTargets { get; }
        /// <summary>Routing-envelope entity ID, distinct from the payload's Target. Null when absent.</summary>
        public ulong? RoutedEntityNetworkId { get; }
        /// <summary>Explicit routing-envelope connection, if any. Not the resolved entity owner's connection.</summary>
        public uint? RoutedConnectionId { get; }
        /// <summary>Raw payload Target ID. The native player receiver instead belongs to the routed entity.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Raw requested damage. The victim's native hit handling chooses the actual health change.</summary>
        public int RequestedDamage { get; }
        public int RequestedImpactType { get; }
        public float RequestedWeight { get; }
        public float RequestedBurnTime { get; }
        /// <summary>Sender-supplied position, not an observed attacker transform or identity.</summary>
        public Vector3 ClaimedAttackerPosition { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Stops this server dispatch without undoing prior effects or sending a denial message.
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
