// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Policy before the native server handles VailHitEvent requests against AI actors.</summary>
    public static partial class ServerVailHits
    {
        public const string ServiceKey = "openacai.sons.serverVailHits";

        private static ServerVailHitState _state = new(
            VailHitHookStatus.NotAttempted, "The Vail-hit hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before the server's VailHitEvent callback and its actor lookup.
        /// Rejection skips this callback's impact handling, not every source of AI damage.
        /// </summary>
        public static event Action<VailHitRequest> Receiving;

        public static ServerVailHitState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(VailHitHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server Vail-hit policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(VailHitHookStatus.Installing, "Registering the Vail-hit hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { typeof(VailHitEvent) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(VailHitEvent)");

                var prefix = new HarmonyMethod(typeof(ServerVailHits), nameof(BeforeHit))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var postfix = new HarmonyMethod(typeof(ServerVailHits), nameof(AfterHit)) { priority = Priority.First };
                var finalizer = new HarmonyMethod(typeof(ServerVailHits), nameof(FinishHit)) { priority = Priority.First };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-vail-hits");
                harmony.Patch(target, prefix: prefix, postfix: postfix, finalizer: finalizer);
            }
            catch (Exception error)
            {
                PublishState(VailHitHookStatus.Failed,
                    "Vail-hit hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server Vail-hit policy hook failed: " + error);
                return;
            }

            PublishState(VailHitHookStatus.Installed,
                "Vail-hit hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeHit(VailHitEvent __0, bool __runOriginal, out VailHitOutcomeScope __state)
        {
            __state = null;
            var handlers = Receiving;
            if (handlers == null || !__runOriginal)
            {
                // An earlier prefix owns a skipped call; only the outcome observer still sees it.
                if (Processed != null)
                    __state = BeginOutcome(__0, null);
                return __runOriginal;
            }
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The Vail-hit callback ran outside the loader's game thread.");
                return false;
            }

            VailHitRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                request = CreateRequest(__0);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                    {
                        if (Processed != null)
                            __state = BeginOutcome(__0, request);
                        return false;
                    }
                }

                if (Processed != null)
                    __state = BeginOutcome(__0, request);
                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading Vail-hit context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                if (request != null && Processed != null)
                    __state = BeginOutcome(__0, request);
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static VailHitRequest CreateRequest(VailHitEvent hit)
        {
            var connection = hit.RaisedBy;
            uint? connectionId = connection == null ? null : connection.ConnectionId;
            ulong? registeredSteamId = null;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId))
            {
                if (steamId.m_SteamID != 0)
                    registeredSteamId = steamId.m_SteamID;
            }

            return new VailHitRequest(hit, connectionId, registeredSteamId,
                ServerSessions.ResolveSource(connection));
        }

        private static void PublishState(VailHitHookStatus status, string detail)
        {
            var state = new ServerVailHitState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server Vail-hit policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum VailHitHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerVailHitState
    {
        internal ServerVailHitState(VailHitHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public VailHitHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of an unvalidated AI-hit request, with no retained native objects.</summary>
    public sealed class VailHitRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal VailHitRequest(VailHitEvent source, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RequestedDamage = source.Damage;
            RequestedDamageNodeId = source.DamageNodeId;
            RequestedWeight = source.Weight;
            RequestedDirection = source.Direction;
            RequestedImpactType = source.ImpactType;
            RequestedSubImpactType = source.SubImpactType;
            var velocity = source.Velocity;
            RequestedVelocity = new Vector3(velocity.x, velocity.y, velocity.z);
            var direction = source.SenderDir;
            ClaimedSenderDirection = new Vector3(direction.x, direction.y, direction.z);
            ClaimedHitBehind = source.HitBehind;
            ClaimedComboEnder = source.ComboEnder;
            ClaimedKnockdown = source.Knockdown;
            ClaimedPowerAttack = source.PowerAttack;
            RequestedStatusType = source.StatusType;
            RequestedStatusDuration = source.StatusDuration;
            ClaimedBlocked = source.Blocked;
            ClaimedBlockedByArmor = source.BlockedByArmor;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Sender-selected target, not proof of a valid AI actor, ownership or proximity.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Raw damage field, not the resulting health change.</summary>
        public float RequestedDamage { get; }
        /// <summary>Target damage-node selection, not an authenticated weapon or item ID.</summary>
        public int RequestedDamageNodeId { get; }
        public float RequestedWeight { get; }
        public int RequestedDirection { get; }
        public int RequestedImpactType { get; }
        public int RequestedSubImpactType { get; }
        public Vector3 RequestedVelocity { get; }
        /// <summary>Sender-supplied direction, not an observed transform.</summary>
        public Vector3 ClaimedSenderDirection { get; }
        public bool ClaimedHitBehind { get; }
        public bool ClaimedComboEnder { get; }
        public bool ClaimedKnockdown { get; }
        public bool ClaimedPowerAttack { get; }
        public int RequestedStatusType { get; }
        public float RequestedStatusDuration { get; }
        public bool ClaimedBlocked { get; }
        public bool ClaimedBlockedByArmor { get; }
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

        /// <summary>
        /// Reads the requested target's damage node for RequestedDamageNodeId, as ServerWorld.TryReadNpcDamageNode
        /// does. Valid only during synchronous Receiving dispatch: WrongThread on another thread and Unavailable
        /// afterwards. The SDK's target lookup may differ from the native actor lookup, and a resolved node does
        /// not prove the sender hit it.
        /// </summary>
        public bool TryReadTargetDamageNode(out NpcDamageNodeSnapshot snapshot, out NpcStateReadStatus status)
        {
            snapshot = null;
            if (Environment.CurrentManagedThreadId != _dispatchThreadId)
            {
                status = NpcStateReadStatus.WrongThread;
                return false;
            }
            if (!_acceptingDecisions)
            {
                status = NpcStateReadStatus.Unavailable;
                return false;
            }
            return ServerWorld.TryReadNpcDamageNode(RequestedTargetNetworkId, RequestedDamageNodeId,
                out snapshot, out status);
        }

        internal void Close()
        {
            _acceptingDecisions = false;
        }
    }
}
