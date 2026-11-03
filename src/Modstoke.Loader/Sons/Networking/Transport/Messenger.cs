// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Private, server-routed messages. This transport does not select recipients or display native chat.</summary>
    public static class Messenger
    {
        public const int MaxTextBytes = 256;
        public const int MaxPayloadBytes = 512;
        public const int MaxPendingRequests = 8;
        private static Func<MessengerRequest, MessengerResult> _handler;
        private static int _reportedPolicyFailure;
        private static int _reportedReceiveFailure;

        /// <summary>Current transport availability, not a permission grant or a delivery guarantee.</summary>
        public static bool IsAvailable => ServerNotices.MessengerAvailable;
        public static event Action<MessengerMessage> Received;
        public static event Action<MessengerResult> Completed;

        /// <summary>
        /// Gets the current Messenger lifetime's managed identity on the game thread.
        /// Independent of command sessions; changes when Messenger retires its connection or host player/world lifetime.
        /// Returns false with an empty identity when unavailable or off-thread. This cache key grants no permission.
        /// </summary>
        public static bool TryGetCurrentSession(out Guid sessionId) =>
            ServerNotices.TryGetCurrentMessengerSession(out sessionId);

        /// <summary>Registers the sole server policy. Disposing the returned registration removes that policy.</summary>
        public static IDisposable RegisterHandler(Func<MessengerRequest, MessengerResult> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);
            if (Interlocked.CompareExchange(ref _handler, handler, null) != null)
                throw new InvalidOperationException("A messenger handler is already registered.");
            return new Registration(handler);
        }

        /// <summary>Submits once and tracks its outcome, including an uncertain native send. Completed reports handling, never delivery.</summary>
        public static bool TrySubmit(string channel, string target, string text, out Guid id, out string detail) =>
            ServerNotices.SubmitMessenger(channel, target, text, out id, out detail);

        public static NoticeSendResult SendToSession(ServerSession recipient,
            MessengerMessage message) => ServerNotices.SendMessengerToSession(recipient, message);

        public static NoticeSendResult SendToHost(HostPlayer recipient,
            WorldRecordContext world, MessengerMessage message) =>
            ServerNotices.SendMessengerToHost(recipient, world, message);

        internal static bool HasHandler => Volatile.Read(ref _handler) != null;

        internal static MessengerResult Handle(MessengerRequest request)
        {
            var handler = Volatile.Read(ref _handler);
            if (handler == null)
                return new MessengerResult(request.Id, false, "Messenger policy is unavailable.");
            try
            {
                var result = handler(request);
                if (result != null && result.Id == request.Id && MessengerWire.ValidDetail(result.Detail))
                    return result;
            }
            catch (Exception error) { ReportFailure(ref _reportedPolicyFailure, "policy", error); }
            return new MessengerResult(request.Id, false, "Messenger handling failed.");
        }

        internal static void DispatchReceived(MessengerMessage message, Guid scope)
        {
            var handlers = Received;
            if (handlers == null) return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                // A preceding subscriber can retire the connection or world synchronously.
                if (!ServerNotices.MessengerScopeCurrent(scope)) break;
                try { handler(message); }
                catch (Exception error) { ReportFailure(ref _reportedReceiveFailure, "Received subscriber", error); }
            }
        }

        internal static void DispatchCompleted(MessengerResult result) =>
            Modding.Dispatch("Messenger.Completed", Completed, result);

        // Logs the exception type only; message text never reaches the log.
        private static void ReportFailure(ref int reported, string area, Exception error)
        {
            if (Interlocked.Exchange(ref reported, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Messenger " + area + " failed ("
                    + error.GetType().FullName + "). Further failures will not be logged.");
            }
            catch (Exception) { }
        }

        private sealed class Registration(Func<MessengerRequest, MessengerResult> handler) : IDisposable
        {
            private Func<MessengerRequest, MessengerResult> _registered = handler;
            public void Dispose()
            {
                var registered = Interlocked.Exchange(ref _registered, null);
                if (registered != null) Interlocked.CompareExchange(ref _handler, null, registered);
            }
        }
    }

    /// <summary>Server-captured caller and world. Exactly one of Session and HostPlayer is present.</summary>
    public sealed class MessengerRequest
    {
        internal MessengerRequest(Guid id, ServerSession session,
            global::Modstoke.Sons.HostPlayer hostPlayer, WorldRecordContext world, string channel, string target, string text)
        {
            Id = id;
            Session = session;
            HostPlayer = hostPlayer;
            World = world;
            Channel = channel;
            Target = target;
            Text = text;
        }
        public Guid Id { get; }
        public ServerSession Session { get; }
        public global::Modstoke.Sons.HostPlayer HostPlayer { get; }
        public WorldRecordContext World { get; }
        public string Channel { get; }
        public string Target { get; }
        public string Text { get; }
    }

    /// <summary>Server handling outcome. Accepted does not assert delivery, display, or recipient acknowledgement.</summary>
    public sealed class MessengerResult(Guid id, bool accepted, string detail)
    {
        public Guid Id { get; } = id;
        public bool Accepted { get; } = accepted;
        public string Detail { get; } = detail;
    }

    /// <summary>A server-authored message with stable identity supplied by the registered policy.</summary>
    public sealed class MessengerMessage(Guid id, string channel, string conversationId,
        string senderId, string senderName, string text)
    {
        public Guid Id { get; } = id;
        public string Channel { get; } = channel;
        public string ConversationId { get; } = conversationId;
        public string SenderId { get; } = senderId;
        public string SenderName { get; } = senderName;
        public string Text { get; } = text;
    }

    internal static class MessengerWire
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly JsonSerializerOptions Options = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 2
        };

        internal static bool ValidChannel(string channel) => channel is "expedition" or "tribe" or "direct";
        internal static bool ValidTarget(string channel, string target) => channel == "direct"
            ? target == "host" || (Guid.TryParseExact(target, "D", out var id) && id != Guid.Empty)
            : string.IsNullOrEmpty(target);
        internal static bool ValidDetail(string value) => ValidText(value, 128, allowEmpty: true);

        internal static bool ValidText(string value, int bytes, bool allowEmpty = false)
        {
            if (value == null) return allowEmpty;
            if (value.Length > bytes || (!allowEmpty && string.IsNullOrWhiteSpace(value))) return false;
            foreach (var character in value)
                if (char.IsControl(character) || character is '<' or '>' or '\u2028' or '\u2029') return false;
            try { return Utf8.GetByteCount(value) <= bytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool TryEncode(string kind, Payload payload, out string json)
        {
            json = null;
            if (!Valid(kind, payload)) return false;
            var candidate = JsonSerializer.Serialize(payload, Options);
            if (Utf8.GetByteCount(candidate) > Messenger.MaxPayloadBytes) return false;
            json = candidate;
            return true;
        }

        internal static bool TryDecode(string kind, string json, out Payload payload)
        {
            payload = null;
            if (json == null || json.Length > Messenger.MaxPayloadBytes) return false;
            try
            {
                if (Utf8.GetByteCount(json) > Messenger.MaxPayloadBytes) return false;
                payload = JsonSerializer.Deserialize<Payload>(json, Options);
                return Valid(kind, payload);
            }
            catch (Exception error) when (error is JsonException or EncoderFallbackException) { return false; }
        }

        private static bool Valid(string kind, Payload value)
        {
            if (value == null || value.Id == Guid.Empty) return false;
            return kind switch
            {
                "messenger-request" => ValidChannel(value.Channel) && ValidTarget(value.Channel, value.Target)
                    && ValidText(value.Text, Messenger.MaxTextBytes) && value.Conversation == null
                    && value.Sender == null && value.Name == null && !value.Accepted && value.Detail == null,
                "messenger-message" => ValidChannel(value.Channel) && value.Target == null
                    && ValidText(value.Text, Messenger.MaxTextBytes) && ValidText(value.Conversation, 96)
                    && ValidText(value.Sender, 64) && ValidText(value.Name, 64) && !value.Accepted && value.Detail == null,
                "messenger-result" => value.Channel == null && value.Target == null && value.Text == null
                    && value.Conversation == null && value.Sender == null && value.Name == null && ValidDetail(value.Detail),
                _ => false
            };
        }

        internal static Payload FromMessage(MessengerMessage value) => value == null ? null : new Payload
        {
            Id = value.Id, Channel = value.Channel, Conversation = value.ConversationId,
            Sender = value.SenderId, Name = value.SenderName, Text = value.Text
        };

        internal sealed class Payload
        {
            public Payload() { }
            [JsonPropertyName("i")] public Guid Id { get; set; }
            [JsonPropertyName("c")] public string Channel { get; set; }
            [JsonPropertyName("t")] public string Target { get; set; }
            [JsonPropertyName("x")] public string Text { get; set; }
            [JsonPropertyName("v")] public string Conversation { get; set; }
            [JsonPropertyName("s")] public string Sender { get; set; }
            [JsonPropertyName("n")] public string Name { get; set; }
            [JsonPropertyName("a")] public bool Accepted { get; set; }
            [JsonPropertyName("d")] public string Detail { get; set; }
        }
    }
}
