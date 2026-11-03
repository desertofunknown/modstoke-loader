// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Policy before a server routes native HitPlayer events to their recipients.</summary>
    public static class ServerPlayerHits
    {
        public const string ServiceKey = "openacai.sons.serverPlayerHits";

        private static ServerPlayerHitState _state = new(
            PlayerHitHookStatus.NotAttempted, "The player-hit hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs on the server before routing received or locally created HitPlayer events.
        /// Rejection stops this event's routing, not every source of player damage.
        /// </summary>
        public static event Action<PlayerHitRequest> Receiving;

        public static ServerPlayerHitState State => Volatile.Read(ref _state);
        internal static bool IsOwnerThread => Environment.CurrentManagedThreadId == _ownerThreadId;

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(PlayerHitHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server player-hit policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(PlayerHitHookStatus.Installing, "Registering the player-hit routing hook.");
            try
            {
                ServerDispatchGate.Register(DispatchRoute.PlayerHits);
            }
            catch (Exception error)
            {
                PublishState(PlayerHitHookStatus.Failed,
                    "Player-hit hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server player-hit policy hook failed: " + error);
                return;
            }

            PublishState(PlayerHitHookStatus.Installed,
                "Player-hit hook registration succeeded. This is not evidence of a received or blocked hit.");
        }

        internal static DispatchVerdict RouteDispatch(Bolt.Event source)
        {
            // An accepted native combat hit now belongs to the ingress queue, which released its storage.
            if (PlayerCombatIngress.TryDispatch(source)) return DispatchVerdict.Retain;
            var handlers = Receiving;
            var hasCombatOwners = Combat.StrictCombatServerHits.HasOwners;
            if (handlers == null && !hasCombatOwners)
                return DispatchVerdict.Forward;

            var hit = source.TryCast<HitPlayer>();
            // NPC damage and dismemberment are independent legacy claims. Reject owned
            // sources at dispatch, before any native callback or forwarded recipient.
            var actorHit = hit == null && hasCombatOwners
                && (source.TryCast<VailHitEvent>() != null || source.TryCast<DismemberHitEvent>() != null);
            if (hit == null && !actorHit)
                return DispatchVerdict.Forward;

            PlayerHitRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;
                if (Environment.CurrentManagedThreadId != _ownerThreadId)
                {
                    ReportDispatchFailure("The combat-event callback ran outside the loader's game thread.");
                    return DispatchVerdict.Reject;
                }

                var connection = source.RaisedBy;
                uint? connectionId = connection == null ? null : connection.ConnectionId;
                var sourceSession = ServerSessions.ResolveSource(connection);
                // Combat ownership is retained by the SDK even after the policy mod stops.
                // This is a transport-level exclusion, not a claim about the attack's cause.
                if (Combat.StrictCombatServerHits.Blocks(connection, connectionId, sourceSession))
                    return DispatchVerdict.Reject;
                if (hit == null || handlers == null)
                    return DispatchVerdict.Forward;
                ulong? registeredSteamId = null;
                if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
                {
                    if (steamId.m_SteamID != 0)
                        registeredSteamId = steamId.m_SteamID;
                }

                request = new PlayerHitRequest(hit, connectionId, registeredSteamId,
                    sourceSession);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return DispatchVerdict.Reject;
                }

                return DispatchVerdict.Forward;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading combat-event context or running a player-hit policy handler failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
            finally
            {
                request?.Close();
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected combat-event storage failed (" + error.GetType().FullName + ").");

        private static void PublishState(PlayerHitHookStatus status, string detail)
        {
            var state = new ServerPlayerHitState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server combat-event routing rejected an event: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum PlayerHitHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerPlayerHitState
    {
        internal ServerPlayerHitState(PlayerHitHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public PlayerHitHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of routing and hit fields. No native event, entity or connection is retained.</summary>
    public sealed class PlayerHitRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal PlayerHitRequest(HitPlayer source, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
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
        public ServerSession SourceSession { get; }
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
