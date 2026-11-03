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
    /// <summary>Policy before the native server handles StructureHitEvent requests.</summary>
    public static class ServerStructureHits
    {
        public const string ServiceKey = "openacai.sons.serverStructureHits";

        private static ServerStructureHitState _state = new(
            StructureHitHookStatus.NotAttempted, "The structure-hit hook has not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs synchronously before CoopServerCallbacks.OnEvent(StructureHitEvent), including its validation.
        /// Rejection skips that callback's impact processing, not every source of structure damage.
        /// </summary>
        public static event Action<StructureHitRequest> Receiving;

        public static ServerStructureHitState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;

            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(StructureHitHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server structure-hit policy hook was not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            PublishState(StructureHitHookStatus.Installing, "Registering the structure-hit hook.");
            try
            {
                var target = AccessTools.Method(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent),
                    new[] { typeof(StructureHitEvent) });
                if (target == null)
                    throw new MissingMethodException(typeof(CoopServerCallbacks).FullName, "OnEvent(StructureHitEvent)");

                var prefix = new HarmonyMethod(typeof(ServerStructureHits), nameof(BeforeHit))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-structure-hits");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(StructureHitHookStatus.Failed,
                    "Structure-hit hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server structure-hit policy hook failed: " + error);
                return;
            }

            PublishState(StructureHitHookStatus.Installed,
                "Structure-hit hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeHit(StructureHitEvent __0, bool __runOriginal)
        {
            if (!__runOriginal)
                return false;
            var handlers = Receiving;
            if (handlers == null)
                return true;

            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("The structure-hit callback ran outside the loader's game thread.");
                return false;
            }

            StructureHitRequest request = null;
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

                request = new StructureHitRequest(__0, connectionId, registeredSteamId,
                    ServerSessions.ResolveSource(connection));
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
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
                ReportDispatchFailure("Reading structure-hit context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(StructureHitHookStatus status, string detail)
        {
            var state = new ServerStructureHitState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server structure-hit policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum StructureHitHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerStructureHitState
    {
        internal ServerStructureHitState(StructureHitHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        public StructureHitHookStatus Status { get; }
        public string Detail { get; }
    }

    /// <summary>Managed copies of an unvalidated hit request, with no retained native objects.</summary>
    public sealed class StructureHitRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal StructureHitRequest(StructureHitEvent source, uint? sourceConnectionId,
            ulong? registeredSteamId, ServerSession sourceSession)
        {
            SourceConnectionId = sourceConnectionId;
            SourceSession = sourceSession;
            RegisteredSteamId = registeredSteamId;
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RequestedDamage = source.Damage;
            RequestedImpactType = source.ImpactType;
            var center = source.Center;
            RequestedCenter = new Vector3(center.x, center.y, center.z);
            RequestedRadius = source.Radius;
            ReceivedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Transport connection ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not an independent authentication or permission verdict.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Sender-selected network ID, not proof of target validity, proximity or ownership.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Raw damage field. The native structure handler chooses the actual health change.</summary>
        public float RequestedDamage { get; }
        /// <summary>Raw impact-type value, not a validated enum or verified weapon type.</summary>
        public int RequestedImpactType { get; }
        public Vector3 RequestedCenter { get; }
        public float RequestedRadius { get; }
        public DateTimeOffset ReceivedAt { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this server callback without undoing client effects or sending a denial message.
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
