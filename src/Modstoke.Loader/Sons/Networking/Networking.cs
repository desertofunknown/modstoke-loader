// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Sons networking planning surface. This reports multiplayer readiness and validates
// mod-owned channel and message intent; it never sends. Traffic goes through Packets,
// Datagrams, ControlTransport and Messenger, which own the Bolt hooks.

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static class Networking
    {
        public const string ServiceKey = "openacai.sons.networking";
        public const int MaxChannelCount = 256;
        public const int MaxChannelIdLength = 96;
        public const int MaxPlannedPayloadBytes = 65535;

        private static readonly bool RuntimeTransportEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<(string ModId, string ChannelId), NetworkChannelSnapshot> Channels =
            new(EqualityComparer<(string ModId, string ChannelId)>.Create(
                (left, right) => StringComparer.OrdinalIgnoreCase.Equals(left.ModId, right.ModId)
                    && StringComparer.OrdinalIgnoreCase.Equals(left.ChannelId, right.ChannelId),
                key => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.ModId),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(key.ChannelId))));
        private static readonly IReadOnlyList<string> SupportedTargetNames = Array.AsReadOnly(new[]
        {
            "only-self",
            "only-server",
            "all-clients",
            "everyone",
            "others",
            "connection"
        });

        private static NetworkingSnapshot _state = NetworkingSnapshot.CreateUnknown();
        private static bool _initialized;
        private static int _planRequestCount;
        private static int _acceptedPlanCount;
        private static int _rejectedPlanCount;
        private static string _lastPlanStatus = "INFO";
        private static string _lastPlanDetail = "No network message plan has been validated.";
        private static string _lastPlanReason = "none";
        private static string _lastPlanModId = "";
        private static string _lastPlanChannelId = "";
        private static string _lastPlanTarget = "none";
        private static int _lastPlanPayloadBytes;

        public static NetworkingSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        public static bool Ready => State.Ready;
        public static bool PlanningEnabled => true;
        public static bool RuntimeTransportEnabled => RuntimeTransportEnabledValue;
        public static IReadOnlyList<string> SupportedTargets => SupportedTargetNames;

        public static IReadOnlyList<NetworkChannelSnapshot> GetChannels()
        {
            lock (SyncRoot)
            {
                return Channels.Values
                    .OrderBy(channel => channel.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(channel => channel.ChannelId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }

        public static bool TryRegisterMessageChannel(
            string modId,
            string channelId,
            out NetworkChannelSnapshot channel)
        {
            return TryRegisterMessageChannel(modId, channelId, "", out channel);
        }

        public static bool TryRegisterMessageChannel(
            string modId,
            string channelId,
            string description,
            out NetworkChannelSnapshot channel)
        {
            channel = null;
            string normalizedModId;
            string normalizedChannelId;
            string reason;
            if (!TryNormalizeChannel(modId, channelId, out normalizedModId, out normalizedChannelId, out reason))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (Channels.Count >= MaxChannelCount && !Channels.ContainsKey(ChannelKey(normalizedModId, normalizedChannelId)))
                {
                    return false;
                }

                channel = new NetworkChannelSnapshot(
                    normalizedModId,
                    normalizedChannelId,
                    description ?? "",
                    DateTimeOffset.UtcNow.ToString("O"));
                Channels[ChannelKey(normalizedModId, normalizedChannelId)] = channel;
            }

            Refresh();
            return true;
        }

        public static bool TryUnregisterMessageChannel(string modId, string channelId)
        {
            string normalizedModId;
            string normalizedChannelId;
            string reason;
            if (!TryNormalizeChannel(modId, channelId, out normalizedModId, out normalizedChannelId, out reason))
            {
                return false;
            }

            bool removed;
            lock (SyncRoot)
            {
                removed = Channels.Remove(ChannelKey(normalizedModId, normalizedChannelId));
            }

            if (removed)
            {
                Refresh();
            }

            return removed;
        }

        public static NetworkMessagePlan PlanMessage(
            string modId,
            string channelId,
            NetworkTarget target,
            int payloadBytes = 0,
            string targetConnectionId = null,
            bool requiresServerAuthority = false)
        {
            var snapshot = State;
            var targetName = TargetName(target);
            var accepted = false;
            var canSendNow = false;
            var status = "CHECK";
            var reason = "invalid";
            var detail = "Network message plan is invalid.";
            var normalizedPayloadBytes = Math.Max(0, payloadBytes);
            string normalizedModId;
            string normalizedChannelId;

            if (!TryNormalizeChannel(modId, channelId, out normalizedModId, out normalizedChannelId, out reason))
            {
                detail = ChannelReasonDetail(reason);
            }
            else if (!IsSupportedTarget(target))
            {
                reason = "unsupported-target";
                detail = "Unsupported network target.";
            }
            else if (payloadBytes < 0)
            {
                reason = "invalid-payload-size";
                detail = "Planned payload size cannot be negative.";
            }
            else if (normalizedPayloadBytes > MaxPlannedPayloadBytes)
            {
                reason = "payload-too-large";
                detail = "Planned payload exceeds the planning payload bound.";
            }
            else if (target == NetworkTarget.Connection && string.IsNullOrWhiteSpace(targetConnectionId))
            {
                reason = "missing-target-connection";
                detail = "A connection target requires a target connection id.";
            }
            else if (!HasChannel(normalizedModId, normalizedChannelId))
            {
                reason = "channel-not-registered";
                detail = "Register the mod-owned network channel before planning messages on it.";
            }
            else
            {
                accepted = true;
                if (!snapshot.BoltAvailable)
                {
                    status = "DEFERRED";
                    reason = "bolt-unavailable";
                    detail = "Message plan accepted, but Bolt networking was not visible to the runtime probe.";
                }
                else if (!snapshot.BoltNetworkRunning && RequiresActiveNetwork(target))
                {
                    status = "DEFERRED";
                    reason = "network-not-running";
                    detail = "Message plan accepted, but the current Sons session is not running multiplayer networking.";
                }
                else if (requiresServerAuthority && !snapshot.IsServer)
                {
                    status = "DEFERRED";
                    reason = "server-authority-required";
                    detail = "Message plan accepted, but this runtime is not the multiplayer server.";
                }
                else if (!RuntimeTransportEnabled)
                {
                    status = "DEFERRED";
                    reason = "transport-disabled";
                    detail = "Message plan accepted. Planning never sends; use Packets, Datagrams or Messenger for traffic.";
                }
                else
                {
                    canSendNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "Networking gate accepted the plan for transport.";
                }
            }

            var plan = new NetworkMessagePlan(
                normalizedModId ?? "",
                normalizedChannelId ?? "",
                target,
                targetName,
                targetConnectionId ?? "",
                normalizedPayloadBytes,
                requiresServerAuthority,
                accepted,
                canSendNow,
                RuntimeTransportEnabled,
                status,
                reason,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
            RecordPlan(plan);
            Refresh();
            return plan;
        }

        public static NetworkingSnapshot Refresh()
        {
            var snapshot = CreateSnapshot();
            Publish(snapshot);
            return snapshot;
        }

        internal static void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            GameRuntime.SceneChanged += _ => Refresh();
            GameRuntime.WorldChanged += _ => Refresh();
            GameRuntime.Lifecycle += _ => Refresh();
            Publish(NetworkingSnapshot.CreateUnknown());
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK networking provider could not be registered.");
            }
        }

        private static void Publish(NetworkingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? NetworkingSnapshot.CreateUnknown();
                Modding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(NetworkMessagePlan plan)
        {
            lock (SyncRoot)
            {
                _planRequestCount++;
                if (plan != null && plan.Accepted)
                {
                    _acceptedPlanCount++;
                }
                else
                {
                    _rejectedPlanCount++;
                }

                _lastPlanStatus = plan == null ? "CHECK" : plan.Status;
                _lastPlanDetail = plan == null ? "Network message plan was null." : plan.Detail;
                _lastPlanReason = plan == null ? "null-plan" : plan.ReasonCode;
                _lastPlanModId = plan == null ? "" : plan.ModId;
                _lastPlanChannelId = plan == null ? "" : plan.ChannelId;
                _lastPlanTarget = plan == null ? "unknown" : plan.TargetName;
                _lastPlanPayloadBytes = plan == null ? 0 : plan.PayloadBytes;
            }
        }

        private static NetworkingSnapshot CreateSnapshot()
        {
            var sonsState = GameRuntime.State;
            var environment = GameRuntime.Environment;
            var probe = ProbeNetwork();

            IReadOnlyList<NetworkChannelSnapshot> channels;
            int planRequestCount;
            int acceptedPlanCount;
            int rejectedPlanCount;
            string lastPlanStatus;
            string lastPlanDetail;
            string lastPlanReason;
            string lastPlanModId;
            string lastPlanChannelId;
            string lastPlanTarget;
            int lastPlanPayloadBytes;

            lock (SyncRoot)
            {
                channels = Channels.Values
                    .OrderBy(channel => channel.ModId, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(channel => channel.ChannelId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                planRequestCount = _planRequestCount;
                acceptedPlanCount = _acceptedPlanCount;
                rejectedPlanCount = _rejectedPlanCount;
                lastPlanStatus = _lastPlanStatus ?? "";
                lastPlanDetail = _lastPlanDetail ?? "";
                lastPlanReason = _lastPlanReason ?? "";
                lastPlanModId = _lastPlanModId ?? "";
                lastPlanChannelId = _lastPlanChannelId ?? "";
                lastPlanTarget = _lastPlanTarget ?? "";
                lastPlanPayloadBytes = _lastPlanPayloadBytes;
            }

            var sessionMode = ClassifySession(environment, probe);
            return new NetworkingSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                true,
                "READY",
                "Networking planning is available. It validates message intent only; Packets, Datagrams, ControlTransport and Messenger carry traffic.",
                sonsState == null ? "" : sonsState.SceneName,
                sonsState == null ? "" : sonsState.SceneKind,
                sonsState != null && sonsState.IsInWorld,
                environment != null && environment.IsDedicatedServer,
                probe.BoltAvailable,
                probe.BoltProbeStatus,
                probe.BoltProbeDetail,
                probe.BoltNetworkRunning,
                probe.IsServer,
                probe.IsClient,
                sessionMode,
                PlanningEnabled,
                RuntimeTransportEnabled,
                "not-installed",
                "Planning installs no hooks; Packets, Datagrams, ControlTransport and Messenger own the Bolt transport hooks.",
                SupportedTargetNames,
                channels,
                MaxChannelCount,
                MaxChannelIdLength,
                MaxPlannedPayloadBytes,
                planRequestCount,
                acceptedPlanCount,
                rejectedPlanCount,
                lastPlanStatus,
                lastPlanDetail,
                lastPlanReason,
                lastPlanModId,
                lastPlanChannelId,
                lastPlanTarget,
                lastPlanPayloadBytes);
        }

        private static bool HasChannel(string modId, string channelId)
        {
            lock (SyncRoot)
            {
                return Channels.ContainsKey(ChannelKey(modId, channelId));
            }
        }

        private static bool TryNormalizeChannel(
            string modId,
            string channelId,
            out string normalizedModId,
            out string normalizedChannelId,
            out string reason)
        {
            normalizedModId = NormalizeId(modId);
            normalizedChannelId = NormalizeId(channelId);
            if (string.IsNullOrWhiteSpace(normalizedModId))
            {
                reason = "missing-mod-id";
                return false;
            }

            if (string.IsNullOrWhiteSpace(normalizedChannelId))
            {
                reason = "missing-channel-id";
                return false;
            }

            if (normalizedChannelId.Length > MaxChannelIdLength)
            {
                reason = "channel-id-too-long";
                return false;
            }

            reason = "validated";
            return true;
        }

        private static string NormalizeId(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToLowerInvariant();
        }

        private static (string ModId, string ChannelId) ChannelKey(string modId, string channelId)
        {
            return (NormalizeId(modId), NormalizeId(channelId));
        }

        private static string ChannelReasonDetail(string reason)
        {
            switch (reason)
            {
                case "missing-mod-id":
                    return "A mod id is required for network channel ownership.";
                case "missing-channel-id":
                    return "A network channel id is required.";
                case "channel-id-too-long":
                    return "Network channel ids are bounded to " + MaxChannelIdLength + " characters.";
                default:
                    return "Network channel identity is invalid.";
            }
        }

        private static bool IsSupportedTarget(NetworkTarget target)
        {
            return target == NetworkTarget.OnlySelf
                || target == NetworkTarget.OnlyServer
                || target == NetworkTarget.AllClients
                || target == NetworkTarget.Everyone
                || target == NetworkTarget.Others
                || target == NetworkTarget.Connection;
        }

        private static bool RequiresActiveNetwork(NetworkTarget target)
        {
            return target != NetworkTarget.OnlySelf;
        }

        private static string TargetName(NetworkTarget target)
        {
            switch (target)
            {
                case NetworkTarget.OnlySelf:
                    return "only-self";
                case NetworkTarget.OnlyServer:
                    return "only-server";
                case NetworkTarget.AllClients:
                    return "all-clients";
                case NetworkTarget.Everyone:
                    return "everyone";
                case NetworkTarget.Others:
                    return "others";
                case NetworkTarget.Connection:
                    return "connection";
                default:
                    return "unknown";
            }
        }

        private static NetworkProbe ProbeNetwork()
        {
            bool running;
            bool isServer;
            bool isClient;
            try
            {
                running = BoltNetwork.isRunning;
                isServer = BoltNetwork.isServer;
                isClient = BoltNetwork.isClient;
            }
            catch (Exception ex)
            {
                return new NetworkProbe(false, "CHECK", "Bolt probe failed: " + ex.Message, false, false, false);
            }

            var status = running || isServer || isClient ? "READY" : "INFO";
            var detail = "BoltNetwork visible; running "
                + running
                + ", server "
                + isServer
                + ", client "
                + isClient
                + ".";
            return new NetworkProbe(true, status, detail, running, isServer, isClient);
        }

        private static string ClassifySession(EnvironmentSnapshot environment, NetworkProbe probe)
        {
            if (environment != null && environment.IsDedicatedServer)
            {
                return "DedicatedServer";
            }

            if (probe.BoltNetworkRunning && probe.IsServer)
            {
                return "MultiplayerServer";
            }

            if (probe.BoltNetworkRunning && probe.IsClient)
            {
                return "MultiplayerClient";
            }

            if (probe.BoltNetworkRunning)
            {
                return "Multiplayer";
            }

            return GameRuntime.IsInWorld ? "SinglePlayerOrOffline" : "NoActiveNetwork";
        }

        private static MenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new MenuContentSnapshot(
                "Sons Networking",
                "Multiplayer readiness and network message planning.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateChannelSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateGateSection(NetworkingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Networking Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "Networking planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can register channels and validate network message intent."),
                    Row("Runtime Transport", snapshot.RuntimeTransportEnabled ? "Enabled" : "Disabled", snapshot.RuntimeTransportEnabled ? "READY" : "INFO", "Planning never sends; Packets, Datagrams and Messenger carry traffic."),
                    Row("Hook Status", snapshot.TransportHookStatus, "INFO", snapshot.TransportHookDetail),
                    Row("Bolt", snapshot.BoltAvailable ? "Visible" : "Unavailable", snapshot.BoltProbeStatus, snapshot.BoltProbeDetail),
                    Row("Session", snapshot.SessionMode, snapshot.BoltNetworkRunning ? "READY" : "INFO", "Server " + snapshot.IsServer + ", client " + snapshot.IsClient + "."),
                    Row("Dedicated Server", snapshot.IsDedicatedServer ? "Yes" : "No", snapshot.IsDedicatedServer ? "INFO" : "READY", "Executable/runtime classification."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? snapshot.SceneKind : snapshot.SceneName, snapshot.IsInWorld ? "READY" : "INFO", "In-world " + snapshot.IsInWorld + "."),
                    Row("Targets", string.Join(", ", snapshot.SupportedTargets), "INFO", "Supported planning target names.")
                });
        }

        private static MenuSectionSnapshot CreateChannelSection(NetworkingSnapshot snapshot)
        {
            var rows = new List<MenuRowSnapshot>
            {
                Row("Channels", snapshot.RegisteredChannelCount + "/" + snapshot.MaxChannelCount, "INFO", "Registered mod-owned planning channels."),
                Row("Max Payload", snapshot.MaxPlannedPayloadBytes + " bytes", "INFO", "Upper bound for a planned payload.")
            };

            foreach (var channel in snapshot.RegisteredChannels.Take(12))
            {
                rows.Add(Row(
                    channel.ModId + ":" + channel.ChannelId,
                    "Registered",
                    "READY",
                    string.IsNullOrWhiteSpace(channel.Description) ? "Mod-owned planning channel." : channel.Description));
            }

            if (snapshot.RegisteredChannels.Count > 12)
            {
                rows.Add(Row("More Channels", (snapshot.RegisteredChannels.Count - 12).ToString(), "INFO", "Additional channels are present in runtime state."));
            }

            return new MenuSectionSnapshot("Message Channels", rows);
        }

        private static MenuSectionSnapshot CreateActivitySection(NetworkingSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Plan Activity",
                new[]
                {
                    Row("Plans", snapshot.PlanRequestCount + " requested", "INFO", snapshot.AcceptedPlanCount + " accepted, " + snapshot.RejectedPlanCount + " rejected."),
                    Row("Last Plan", snapshot.LastPlanTarget + " / " + snapshot.LastPlanStatus, snapshot.LastPlanStatus, snapshot.LastPlanDetail),
                    Row("Last Channel", LastChannel(snapshot), "INFO", "Last planned mod/channel pair."),
                    Row("Last Payload", snapshot.LastPlanPayloadBytes + " bytes", "INFO", "Last planned payload size."),
                    Row("Last Reason", snapshot.LastPlanReason, snapshot.LastPlanStatus, "Last network plan reason code.")
                });
        }

        private static string LastChannel(NetworkingSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.LastPlanModId) || string.IsNullOrWhiteSpace(snapshot.LastPlanChannelId))
            {
                return "none";
            }

            return snapshot.LastPlanModId + ":" + snapshot.LastPlanChannelId;
        }

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsNetworking;
            public string DisplayName => "Sons Networking";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 724;
            public string Source => "Modstoke Loader";
            public string Description => "Show Sons networking readiness and message planning.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return Networking.CreateContent();
            }
        }

        private sealed class NetworkProbe
        {
            internal NetworkProbe(
                bool boltAvailable,
                string boltProbeStatus,
                string boltProbeDetail,
                bool boltNetworkRunning,
                bool isServer,
                bool isClient)
            {
                BoltAvailable = boltAvailable;
                BoltProbeStatus = boltProbeStatus ?? "";
                BoltProbeDetail = boltProbeDetail ?? "";
                BoltNetworkRunning = boltNetworkRunning;
                IsServer = isServer;
                IsClient = isClient;
            }

            internal bool BoltAvailable { get; }
            internal string BoltProbeStatus { get; }
            internal string BoltProbeDetail { get; }
            internal bool BoltNetworkRunning { get; }
            internal bool IsServer { get; }
            internal bool IsClient { get; }
        }
    }

    public enum NetworkTarget
    {
        OnlySelf = 0,
        OnlyServer = 1,
        AllClients = 2,
        Everyone = 3,
        Others = 4,
        Connection = 5
    }

    public sealed class NetworkChannelSnapshot
    {
        internal NetworkChannelSnapshot(
            string modId,
            string channelId,
            string description,
            string registeredUtc)
        {
            ModId = modId ?? "";
            ChannelId = channelId ?? "";
            Description = description ?? "";
            RegisteredUtc = registeredUtc ?? "";
        }

        public string ModId { get; }
        public string ChannelId { get; }
        public string Description { get; }
        public string RegisteredUtc { get; }
    }

    public sealed class NetworkMessagePlan
    {
        internal NetworkMessagePlan(
            string modId,
            string channelId,
            NetworkTarget target,
            string targetName,
            string targetConnectionId,
            int payloadBytes,
            bool requiresServerAuthority,
            bool accepted,
            bool canSendNow,
            bool runtimeTransportEnabled,
            string status,
            string reasonCode,
            string detail,
            string generatedUtc)
        {
            ModId = modId ?? "";
            ChannelId = channelId ?? "";
            Target = target;
            TargetName = targetName ?? "";
            TargetConnectionId = targetConnectionId ?? "";
            PayloadBytes = Math.Max(0, payloadBytes);
            RequiresServerAuthority = requiresServerAuthority;
            Accepted = accepted;
            CanSendNow = canSendNow;
            RuntimeTransportEnabled = runtimeTransportEnabled;
            Status = status ?? "";
            ReasonCode = reasonCode ?? "";
            Detail = detail ?? "";
            GeneratedUtc = generatedUtc ?? "";
        }

        public string ModId { get; }
        public string ChannelId { get; }
        public NetworkTarget Target { get; }
        public string TargetName { get; }
        public string TargetConnectionId { get; }
        public int PayloadBytes { get; }
        public bool RequiresServerAuthority { get; }
        public bool Accepted { get; }
        public bool CanSendNow { get; }
        public bool RuntimeTransportEnabled { get; }
        public string Status { get; }
        public string ReasonCode { get; }
        public string Detail { get; }
        public string GeneratedUtc { get; }
    }

    public sealed class NetworkingSnapshot
    {
        internal NetworkingSnapshot(
            string serviceKey,
            string generatedUtc,
            bool ready,
            string status,
            string detail,
            string sceneName,
            string sceneKind,
            bool isInWorld,
            bool isDedicatedServer,
            bool boltAvailable,
            string boltProbeStatus,
            string boltProbeDetail,
            bool boltNetworkRunning,
            bool isServer,
            bool isClient,
            string sessionMode,
            bool planningEnabled,
            bool runtimeTransportEnabled,
            string transportHookStatus,
            string transportHookDetail,
            IReadOnlyList<string> supportedTargets,
            IReadOnlyList<NetworkChannelSnapshot> registeredChannels,
            int maxChannelCount,
            int maxChannelIdLength,
            int maxPlannedPayloadBytes,
            int planRequestCount,
            int acceptedPlanCount,
            int rejectedPlanCount,
            string lastPlanStatus,
            string lastPlanDetail,
            string lastPlanReason,
            string lastPlanModId,
            string lastPlanChannelId,
            string lastPlanTarget,
            int lastPlanPayloadBytes)
        {
            ServiceKey = serviceKey ?? Networking.ServiceKey;
            GeneratedUtc = generatedUtc ?? "";
            Ready = ready;
            Status = status ?? "";
            Detail = detail ?? "";
            SceneName = sceneName ?? "";
            SceneKind = sceneKind ?? "";
            IsInWorld = isInWorld;
            IsDedicatedServer = isDedicatedServer;
            BoltAvailable = boltAvailable;
            BoltProbeStatus = boltProbeStatus ?? "";
            BoltProbeDetail = boltProbeDetail ?? "";
            BoltNetworkRunning = boltNetworkRunning;
            IsServer = isServer;
            IsClient = isClient;
            SessionMode = sessionMode ?? "";
            PlanningEnabled = planningEnabled;
            RuntimeTransportEnabled = runtimeTransportEnabled;
            TransportHookStatus = transportHookStatus ?? "";
            TransportHookDetail = transportHookDetail ?? "";
            SupportedTargets = Array.AsReadOnly((supportedTargets ?? Array.Empty<string>()).ToArray());
            RegisteredChannels = Array.AsReadOnly((registeredChannels ?? Array.Empty<NetworkChannelSnapshot>()).ToArray());
            RegisteredChannelCount = RegisteredChannels.Count;
            MaxChannelCount = Math.Max(0, maxChannelCount);
            MaxChannelIdLength = Math.Max(0, maxChannelIdLength);
            MaxPlannedPayloadBytes = Math.Max(0, maxPlannedPayloadBytes);
            PlanRequestCount = Math.Max(0, planRequestCount);
            AcceptedPlanCount = Math.Max(0, acceptedPlanCount);
            RejectedPlanCount = Math.Max(0, rejectedPlanCount);
            LastPlanStatus = lastPlanStatus ?? "";
            LastPlanDetail = lastPlanDetail ?? "";
            LastPlanReason = lastPlanReason ?? "";
            LastPlanModId = lastPlanModId ?? "";
            LastPlanChannelId = lastPlanChannelId ?? "";
            LastPlanTarget = lastPlanTarget ?? "";
            LastPlanPayloadBytes = Math.Max(0, lastPlanPayloadBytes);
        }

        public int Schema => 1;
        public string ServiceKey { get; }
        public string GeneratedUtc { get; }
        public bool Ready { get; }
        public string Status { get; }
        public string Detail { get; }
        public string SceneName { get; }
        public string SceneKind { get; }
        public bool IsInWorld { get; }
        public bool IsDedicatedServer { get; }
        public bool BoltAvailable { get; }
        public string BoltProbeStatus { get; }
        public string BoltProbeDetail { get; }
        public bool BoltNetworkRunning { get; }
        public bool IsServer { get; }
        public bool IsClient { get; }
        public string SessionMode { get; }
        public bool PlanningEnabled { get; }
        public bool RuntimeTransportEnabled { get; }
        public string TransportHookStatus { get; }
        public string TransportHookDetail { get; }
        public IReadOnlyList<string> SupportedTargets { get; }
        public IReadOnlyList<NetworkChannelSnapshot> RegisteredChannels { get; }
        public int RegisteredChannelCount { get; }
        public int MaxChannelCount { get; }
        public int MaxChannelIdLength { get; }
        public int MaxPlannedPayloadBytes { get; }
        public int PlanRequestCount { get; }
        public int AcceptedPlanCount { get; }
        public int RejectedPlanCount { get; }
        public string LastPlanStatus { get; }
        public string LastPlanDetail { get; }
        public string LastPlanReason { get; }
        public string LastPlanModId { get; }
        public string LastPlanChannelId { get; }
        public string LastPlanTarget { get; }
        public int LastPlanPayloadBytes { get; }

        internal static NetworkingSnapshot CreateUnknown()
        {
            return new NetworkingSnapshot(
                Networking.ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                false,
                "PENDING",
                "Sons networking planning has not refreshed yet.",
                "",
                "",
                false,
                false,
                false,
                "PENDING",
                "BoltNetwork has not been probed yet.",
                false,
                false,
                false,
                "Unknown",
                true,
                Networking.RuntimeTransportEnabled,
                "not-installed",
                "Planning installs no hooks; Packets, Datagrams, ControlTransport and Messenger own the Bolt transport hooks.",
                Networking.SupportedTargets,
                Array.Empty<NetworkChannelSnapshot>(),
                Networking.MaxChannelCount,
                Networking.MaxChannelIdLength,
                Networking.MaxPlannedPayloadBytes,
                0,
                0,
                0,
                "INFO",
                "No network message plan has been validated.",
                "none",
                "",
                "",
                "none",
                0);
        }
    }
}
