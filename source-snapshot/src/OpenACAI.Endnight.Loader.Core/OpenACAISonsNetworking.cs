// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons networking planning surface. This exposes multiplayer
// readiness and message intent validation without depending on RedLoader/SonsSdk
// networking globals or sending packets before the Bolt path is verified.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsNetworking
    {
        public const string ServiceKey = "openacai.sons.networking";
        public const int MaxChannelCount = 256;
        public const int MaxChannelIdLength = 96;
        public const int MaxPlannedPayloadBytes = 65535;

        private static readonly bool RuntimeTransportEnabledValue = false;
        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAISonsNetworkChannelSnapshot> Channels =
            new Dictionary<string, OpenACAISonsNetworkChannelSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] SupportedTargetNames =
        {
            "only-self",
            "only-server",
            "all-clients",
            "everyone",
            "others",
            "connection"
        };

        private static OpenACAISonsNetworkingSnapshot _state = OpenACAISonsNetworkingSnapshot.CreateUnknown();
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

        public static OpenACAISonsNetworkingSnapshot State
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

        public static IReadOnlyList<OpenACAISonsNetworkChannelSnapshot> GetChannels()
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
            out OpenACAISonsNetworkChannelSnapshot channel)
        {
            return TryRegisterMessageChannel(modId, channelId, "", out channel);
        }

        public static bool TryRegisterMessageChannel(
            string modId,
            string channelId,
            string description,
            out OpenACAISonsNetworkChannelSnapshot channel)
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

                channel = new OpenACAISonsNetworkChannelSnapshot(
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

        public static OpenACAISonsNetworkMessagePlan PlanMessage(
            string modId,
            string channelId,
            OpenACAISonsNetworkTarget target,
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
                detail = "Unsupported OpenACAI network target.";
            }
            else if (normalizedPayloadBytes > MaxPlannedPayloadBytes)
            {
                reason = "payload-too-large";
                detail = "Planned payload exceeds OpenACAI's current bounded payload size.";
            }
            else if (target == OpenACAISonsNetworkTarget.Connection && string.IsNullOrWhiteSpace(targetConnectionId))
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
                    detail = "OpenACAI accepted the message plan, but Bolt networking was not visible to the runtime probe.";
                }
                else if (!snapshot.BoltNetworkRunning && RequiresActiveNetwork(target))
                {
                    status = "DEFERRED";
                    reason = "network-not-running";
                    detail = "OpenACAI accepted the message plan, but the current Sons session is not running multiplayer networking.";
                }
                else if (requiresServerAuthority && !snapshot.IsServer)
                {
                    status = "DEFERRED";
                    reason = "server-authority-required";
                    detail = "OpenACAI accepted the message plan, but this runtime is not the multiplayer server.";
                }
                else if (!RuntimeTransportEnabled)
                {
                    status = "DEFERRED";
                    reason = "transport-disabled";
                    detail = "OpenACAI accepted the message plan; runtime packet transport remains disabled until the Bolt path is verified.";
                }
                else
                {
                    canSendNow = true;
                    status = "READY";
                    reason = "ready";
                    detail = "OpenACAI networking gate accepted the plan for transport.";
                }
            }

            var plan = new OpenACAISonsNetworkMessagePlan(
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

        public static OpenACAISonsNetworkingSnapshot Refresh()
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
            OpenACAISons.SceneChanged += _ => Refresh();
            OpenACAISons.WorldChanged += _ => Refresh();
            OpenACAISons.Lifecycle += _ => Refresh();
            Publish(OpenACAISonsNetworkingSnapshot.CreateUnknown());
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK networking provider could not be registered.");
            }
        }

        private static void Publish(OpenACAISonsNetworkingSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                _state = snapshot ?? OpenACAISonsNetworkingSnapshot.CreateUnknown();
                OpenACAIModding.SetService(ServiceKey, _state);
            }
        }

        private static void RecordPlan(OpenACAISonsNetworkMessagePlan plan)
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

        private static OpenACAISonsNetworkingSnapshot CreateSnapshot()
        {
            var sonsState = OpenACAISons.State;
            var environment = OpenACAISons.Environment;
            var probe = ProbeNetwork();

            IReadOnlyList<OpenACAISonsNetworkChannelSnapshot> channels;
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
            return new OpenACAISonsNetworkingSnapshot(
                ServiceKey,
                DateTimeOffset.UtcNow.ToString("O"),
                true,
                "READY",
                "OpenACAI networking planning is available; runtime packet transport is gated until verified.",
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
                "OpenACAI has not installed packet/chat hooks yet; this slice validates message intent only.",
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

        private static string ChannelKey(string modId, string channelId)
        {
            return NormalizeId(modId) + ":" + NormalizeId(channelId);
        }

        private static string ChannelReasonDetail(string reason)
        {
            switch (reason)
            {
                case "missing-mod-id":
                    return "A mod id is required for OpenACAI network channel ownership.";
                case "missing-channel-id":
                    return "A network channel id is required.";
                case "channel-id-too-long":
                    return "Network channel ids are bounded to " + MaxChannelIdLength + " characters.";
                default:
                    return "Network channel identity is invalid.";
            }
        }

        private static bool IsSupportedTarget(OpenACAISonsNetworkTarget target)
        {
            return target == OpenACAISonsNetworkTarget.OnlySelf
                || target == OpenACAISonsNetworkTarget.OnlyServer
                || target == OpenACAISonsNetworkTarget.AllClients
                || target == OpenACAISonsNetworkTarget.Everyone
                || target == OpenACAISonsNetworkTarget.Others
                || target == OpenACAISonsNetworkTarget.Connection;
        }

        private static bool RequiresActiveNetwork(OpenACAISonsNetworkTarget target)
        {
            return target != OpenACAISonsNetworkTarget.OnlySelf;
        }

        private static string TargetName(OpenACAISonsNetworkTarget target)
        {
            switch (target)
            {
                case OpenACAISonsNetworkTarget.OnlySelf:
                    return "only-self";
                case OpenACAISonsNetworkTarget.OnlyServer:
                    return "only-server";
                case OpenACAISonsNetworkTarget.AllClients:
                    return "all-clients";
                case OpenACAISonsNetworkTarget.Everyone:
                    return "everyone";
                case OpenACAISonsNetworkTarget.Others:
                    return "others";
                case OpenACAISonsNetworkTarget.Connection:
                    return "connection";
                default:
                    return "unknown";
            }
        }

        private static OpenACAISonsNetworkProbe ProbeNetwork()
        {
            Type boltNetworkType;
            try
            {
                boltNetworkType = ResolveType("Bolt.BoltNetwork");
            }
            catch (Exception ex)
            {
                return new OpenACAISonsNetworkProbe(false, "CHECK", "Bolt probe failed: " + ex.Message, false, false, false);
            }

            if (boltNetworkType == null)
            {
                return new OpenACAISonsNetworkProbe(false, "PENDING", "BoltNetwork type is not currently visible.", false, false, false);
            }

            string runningDetail;
            var running = ReadStaticBool(boltNetworkType, "isRunning", out runningDetail);
            string serverDetail;
            var isServer = ReadStaticBool(boltNetworkType, "isServer", out serverDetail);
            string clientDetail;
            var isClient = ReadStaticBool(boltNetworkType, "isClient", out clientDetail);
            var status = running || isServer || isClient ? "READY" : "INFO";
            var detail = "BoltNetwork visible; running "
                + running
                + ", server "
                + isServer
                + ", client "
                + isClient
                + ".";
            if (!string.IsNullOrWhiteSpace(runningDetail) || !string.IsNullOrWhiteSpace(serverDetail) || !string.IsNullOrWhiteSpace(clientDetail))
            {
                detail += " Probe notes: " + string.Join(" ", new[] { runningDetail, serverDetail, clientDetail }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            return new OpenACAISonsNetworkProbe(true, status, detail, running, isServer, isClient);
        }

        private static Type ResolveType(string fullName)
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch
                {
                    continue;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static bool ReadStaticBool(Type type, string memberName, out string detail)
        {
            detail = "";
            if (type == null)
            {
                detail = memberName + " type unavailable.";
                return false;
            }

            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var property = type.GetProperty(memberName, flags);
                if (property != null)
                {
                    return Convert.ToBoolean(property.GetValue(null, null));
                }

                var field = type.GetField(memberName, flags);
                if (field != null)
                {
                    return Convert.ToBoolean(field.GetValue(null));
                }
            }
            catch (Exception ex)
            {
                detail = memberName + " read failed: " + ex.Message;
                return false;
            }

            detail = memberName + " member unavailable.";
            return false;
        }

        private static string ClassifySession(OpenACAISonsEnvironmentSnapshot environment, OpenACAISonsNetworkProbe probe)
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

            return OpenACAISons.IsInWorld ? "SinglePlayerOrOffline" : "NoActiveNetwork";
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = Refresh();
            return new OpenACAIMenuContentSnapshot(
                "Sons Networking",
                "OpenACAI-owned multiplayer readiness and network message planning gate.",
                new[]
                {
                    CreateGateSection(snapshot),
                    CreateChannelSection(snapshot),
                    CreateActivitySection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateGateSection(OpenACAISonsNetworkingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Networking Gate",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, snapshot.Status, "OpenACAI-owned networking planning snapshot."),
                    Row("Planning", snapshot.PlanningEnabled ? "Enabled" : "Disabled", snapshot.PlanningEnabled ? "READY" : "CHECK", "Mods can register channels and validate network message intent."),
                    Row("Runtime Transport", snapshot.RuntimeTransportEnabled ? "Enabled" : "Disabled", snapshot.RuntimeTransportEnabled ? "READY" : "INFO", "Packet send/receive remains gated until the Bolt path is verified."),
                    Row("Hook Status", snapshot.TransportHookStatus, "INFO", snapshot.TransportHookDetail),
                    Row("Bolt", snapshot.BoltAvailable ? "Visible" : "Unavailable", snapshot.BoltProbeStatus, snapshot.BoltProbeDetail),
                    Row("Session", snapshot.SessionMode, snapshot.BoltNetworkRunning ? "READY" : "INFO", "Server " + snapshot.IsServer + ", client " + snapshot.IsClient + "."),
                    Row("Dedicated Server", snapshot.IsDedicatedServer ? "Yes" : "No", snapshot.IsDedicatedServer ? "INFO" : "READY", "Executable/runtime classification."),
                    Row("Scene", string.IsNullOrWhiteSpace(snapshot.SceneName) ? snapshot.SceneKind : snapshot.SceneName, snapshot.IsInWorld ? "READY" : "INFO", "In-world " + snapshot.IsInWorld + "."),
                    Row("Targets", string.Join(", ", snapshot.SupportedTargets), "INFO", "Current OpenACAI-owned target names.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateChannelSection(OpenACAISonsNetworkingSnapshot snapshot)
        {
            var rows = new List<OpenACAIMenuRowSnapshot>
            {
                Row("Channels", snapshot.RegisteredChannelCount + "/" + snapshot.MaxChannelCount, "INFO", "Registered mod-owned OpenACAI network channels."),
                Row("Max Payload", snapshot.MaxPlannedPayloadBytes + " bytes", "INFO", "Bounded planned payload size before transport implementation is verified.")
            };

            foreach (var channel in snapshot.RegisteredChannels.Take(12))
            {
                rows.Add(Row(
                    channel.ModId + ":" + channel.ChannelId,
                    "Registered",
                    "READY",
                    string.IsNullOrWhiteSpace(channel.Description) ? "Mod-owned OpenACAI network channel." : channel.Description));
            }

            if (snapshot.RegisteredChannels.Count > 12)
            {
                rows.Add(Row("More Channels", (snapshot.RegisteredChannels.Count - 12).ToString(), "INFO", "Additional channels are present in runtime state."));
            }

            return new OpenACAIMenuSectionSnapshot("Message Channels", rows);
        }

        private static OpenACAIMenuSectionSnapshot CreateActivitySection(OpenACAISonsNetworkingSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
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

        private static string LastChannel(OpenACAISonsNetworkingSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.LastPlanModId) || string.IsNullOrWhiteSpace(snapshot.LastPlanChannelId))
            {
                return "none";
            }

            return snapshot.LastPlanModId + ":" + snapshot.LastPlanChannelId;
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsNetworking;
            public string DisplayName => "Sons Networking";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 724;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons networking readiness and message planning gate.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsNetworking.CreateContent();
            }
        }

        private sealed class OpenACAISonsNetworkProbe
        {
            internal OpenACAISonsNetworkProbe(
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

    public enum OpenACAISonsNetworkTarget
    {
        OnlySelf = 0,
        OnlyServer = 1,
        AllClients = 2,
        Everyone = 3,
        Others = 4,
        Connection = 5
    }

    public sealed class OpenACAISonsNetworkChannelSnapshot
    {
        internal OpenACAISonsNetworkChannelSnapshot(
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

    public sealed class OpenACAISonsNetworkMessagePlan
    {
        internal OpenACAISonsNetworkMessagePlan(
            string modId,
            string channelId,
            OpenACAISonsNetworkTarget target,
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
        public OpenACAISonsNetworkTarget Target { get; }
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

    public sealed class OpenACAISonsNetworkingSnapshot
    {
        internal OpenACAISonsNetworkingSnapshot(
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
            IReadOnlyList<OpenACAISonsNetworkChannelSnapshot> registeredChannels,
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
            ServiceKey = serviceKey ?? OpenACAISonsNetworking.ServiceKey;
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
            SupportedTargets = supportedTargets ?? Array.Empty<string>();
            RegisteredChannels = registeredChannels ?? Array.Empty<OpenACAISonsNetworkChannelSnapshot>();
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
        public IReadOnlyList<OpenACAISonsNetworkChannelSnapshot> RegisteredChannels { get; }
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

        internal static OpenACAISonsNetworkingSnapshot CreateUnknown()
        {
            return new OpenACAISonsNetworkingSnapshot(
                OpenACAISonsNetworking.ServiceKey,
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
                OpenACAISonsNetworking.RuntimeTransportEnabled,
                "not-installed",
                "OpenACAI networking packet/chat hooks have not been installed.",
                OpenACAISonsNetworking.SupportedTargets,
                Array.Empty<OpenACAISonsNetworkChannelSnapshot>(),
                OpenACAISonsNetworking.MaxChannelCount,
                OpenACAISonsNetworking.MaxChannelIdLength,
                OpenACAISonsNetworking.MaxPlannedPayloadBytes,
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
