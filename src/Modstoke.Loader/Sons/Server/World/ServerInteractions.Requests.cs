// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Copied interaction context and a synchronous decision window, without retained native objects.</summary>
    public abstract class InteractionRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal InteractionRequest(Bolt.Event source)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
            ReceivedAt = DateTimeOffset.UtcNow;
            ReceivedUnityFrame = UnityEngine.Time.frameCount;
            ReceivedBoltFrame = BoltNetwork.frame;
        }

        /// <summary>Immediate transport source, or null for local events. IDs may be reused between sessions.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Exact source lifetime when available. Its live metadata may change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Unity frame at dispatch, for checking that a read such as TryReadPlayerPose ran in the same frame.</summary>
        public int ReceivedUnityFrame { get; }
        /// <summary>Host BoltNetwork.frame at dispatch: a fixed-step count, not the sender's frame or a Unity frame.</summary>
        public int ReceivedBoltFrame { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        /// <summary>
        /// Skips this receiver only, without undoing earlier actions or refunding resources.
        /// Returns false after dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (Environment.CurrentManagedThreadId != _dispatchThreadId || !_acceptingDecisions)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal void Close() => _acceptingDecisions = false;
    }

    /// <summary>Item-plating activation copies, not proof of a live structure or successful activation.</summary>
    public sealed class ItemPlatingActivationRequest : InteractionRequest
    {
        internal ItemPlatingActivationRequest(ItemPlatingStructureActivateEvent source) : base(source)
        {
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            try { TargetDescription = InteractionDescription.Capture(source.Target); }
            catch (Exception) { }
        }

        /// <summary>Raw serialized Target ID, including zero. No entity or component is required to exist.</summary>
        public ulong RequestedTargetNetworkId { get; }
        public InteractionDescription TargetDescription { get; } = InteractionDescription.Unread;
    }

    /// <summary>Artifact activation copies; native component and ownership handling remain downstream.</summary>
    public sealed class ArtifactActivationRequest : InteractionRequest
    {
        internal ArtifactActivationRequest(ArtifactStructureActivateEvent source) : base(source)
        {
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            try { TargetDescription = InteractionDescription.Capture(source.Target); }
            catch (Exception) { }

            // Native handling reads Activate only after finding a live ArtifactStructure.
            try { RequestedActivate = source.Activate; }
            catch (Exception) { }
        }

        /// <summary>Raw serialized Target ID, including zero. No entity or component is required to exist.</summary>
        public ulong RequestedTargetNetworkId { get; }
        public InteractionDescription TargetDescription { get; } = InteractionDescription.Unread;
        /// <summary>Requested activation state, or null on read failure; not observed structure state.</summary>
        public bool? RequestedActivate { get; }
    }

    /// <summary>Native debug-command payload copies. These values do not establish authorization or execution.</summary>
    public sealed class DebugCommandRequest : InteractionRequest
    {
        internal DebugCommandRequest(debugCommand source) : base(source)
        {
            // An absent native DebugConsole consumes none of these fields.
            try
            {
                Input = source.input;
                InputReadSucceeded = true;
            }
            catch (Exception) { }
            try
            {
                Input2 = source.input2;
                Input2ReadSucceeded = true;
            }
            catch (Exception) { }
            try { RequestedTargetNetworkId = source.GettargetNetworkIdValue().PackedValue; }
            catch (Exception) { }
        }

        /// <summary>Unmodified input payload. Null may be a successful read.</summary>
        public string Input { get; }
        public bool InputReadSucceeded { get; }
        /// <summary>Unmodified input2 payload. Null may be a successful read.</summary>
        public string Input2 { get; }
        public bool Input2ReadSucceeded { get; }
        /// <summary>Raw serialized lowercase target ID, including zero; null on read failure.</summary>
        public ulong? RequestedTargetNetworkId { get; }
    }

    /// <summary>Independent copied getter, type-name and text outcomes. Contains no retained native value.</summary>
    public sealed class InteractionDescription
    {
        internal static InteractionDescription Unread { get; } = new(false, false, null, false, null, false);

        private InteractionDescription(bool valueReadSucceeded, bool isNull, string typeName,
            bool typeNameReadSucceeded, string text, bool textReadSucceeded)
        {
            ValueReadSucceeded = valueReadSucceeded;
            IsNull = isNull;
            TypeName = typeName;
            TypeNameReadSucceeded = typeNameReadSucceeded;
            Text = text;
            TextReadSucceeded = textReadSucceeded;
        }

        internal static InteractionDescription Capture(object value)
        {
            if (value is null)
                return new InteractionDescription(true, true, null, false, null, false);

            string typeName = null;
            var typeNameReadSucceeded = false;
            try
            {
                var type = value.GetType();
                typeName = type.FullName ?? type.Name;
                typeNameReadSucceeded = true;
            }
            catch (Exception) { }

            string text = null;
            var textReadSucceeded = false;
            try
            {
                text = value.ToString();
                textReadSucceeded = true;
            }
            catch (Exception) { }
            return new InteractionDescription(true, false, typeName, typeNameReadSucceeded, text, textReadSucceeded);
        }

        public bool ValueReadSucceeded { get; }
        /// <summary>Whether the successfully read value was managed null. Meaningful only when ValueReadSucceeded.</summary>
        public bool IsNull { get; }
        /// <summary>Managed FullName, falling back to Name, or null on read failure.</summary>
        public string TypeName { get; }
        public bool TypeNameReadSucceeded { get; }
        /// <summary>Unmodified ToString result; null may be a successful result. Not an identity or ownership claim.</summary>
        public string Text { get; }
        public bool TextReadSucceeded { get; }
    }
}
