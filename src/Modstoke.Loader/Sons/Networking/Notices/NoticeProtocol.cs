// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modstoke.Sons
{
    internal static class NoticeProtocol
    {
        internal const string Prefix = "\u001eOpenACAI.Notice:";
        internal const int MaxMessageBytes = 256;
        internal const int MaxCommandBytes = 256;
        internal const int MaxReplyBytes = 512;
        // Includes JSON escaping and the capability token, not just the displayed UTF-8 text.
        private const int MaxFrameBytes = 1024;
        private const int MaxClaimNameLength = 32;
        private const int MaxClaimVersionLength = 64;
        private const int MaxClaimCount = 4096;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 4
        };

        internal static bool IsMessageValid(string message)
        {
            if (message == null || message.Length > MaxMessageBytes || string.IsNullOrWhiteSpace(message))
                return false;

            foreach (var character in message)
            {
                if (char.IsControl(character) || character is '<' or '>' or '\u2028' or '\u2029')
                    return false;
            }

            try { return Utf8.GetByteCount(message) <= MaxMessageBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static bool IsCommandValid(string message)
        {
            if (message == null || message.Length > MaxCommandBytes || !Commands.IsCommandTextValid(message))
                return false;
            try { return Utf8.GetByteCount(message) <= MaxCommandBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        private static bool IsReplyValid(string message)
        {
            if (message == null)
                return true;
            if (message.Length > MaxReplyBytes)
                return false;
            try { return Utf8.GetByteCount(message) <= MaxReplyBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static string LimitCommandReply(string reply, out bool truncated)
        {
            truncated = false;
            return string.IsNullOrEmpty(reply) ? null : LimitReply(reply, MaxReplyBytes, out _, out truncated);
        }

        private static string LimitReply(string reply, int byteBudget, out int byteCount, out bool truncated)
        {
            byteCount = 0;
            var length = 0;
            // Inspect only the bounded prefix. A malformed surrogate ends the valid text prefix.
            while (length < reply.Length)
            {
                var character = reply[length];
                var characterCount = 1;
                int bytes;
                if (char.IsHighSurrogate(character))
                {
                    if (length + 1 >= reply.Length || !char.IsLowSurrogate(reply[length + 1]))
                        break;
                    characterCount = 2;
                    bytes = 4;
                }
                else if (char.IsLowSurrogate(character))
                    break;
                else
                    bytes = character <= 0x7f ? 1 : character <= 0x7ff ? 2 : 3;
                if (byteCount + bytes > byteBudget)
                    break;
                byteCount += bytes;
                length += characterCount;
            }
            truncated = length != reply.Length;
            return truncated ? reply.Substring(0, length) : reply;
        }

        internal static string Encode(string kind, byte[] token, string message = null,
            int receiptVersion = 0, Guid? noticeId = null, string outcome = null,
            int commandVersion = 0, long commandSequence = 0, int commandReplyVersion = 0,
            int messengerVersion = 0, long messengerSequence = 0)
        {
            var replyBytes = 0;
            var replyTruncated = false;
            if (kind == "command-result")
                message = string.IsNullOrEmpty(message) ? null
                    : LimitReply(message, MaxReplyBytes, out replyBytes, out replyTruncated);
            var frame = new Frame
            {
                Version = 1,
                Kind = kind,
                Token = Convert.ToBase64String(token),
                Message = message,
                ReceiptVersion = receiptVersion,
                NoticeId = noticeId,
                Outcome = outcome,
                CommandVersion = commandVersion,
                CommandSequence = commandSequence,
                CommandReplyVersion = commandReplyVersion,
                MessengerVersion = messengerVersion,
                MessengerSequence = messengerSequence,
                ReplyTruncated = replyTruncated
            };
            return Serialize(frame, replyBytes);
        }

        // Recent hosts before this version consumed their own answers but still reserved the capability, so they
        // must not recognize this hello; they reject any version other than 1. Hosts accept both versions.
        private const int HelloVersion = 2;

        internal static string EncodeHello(byte[] token, string loader, string loaderVersion,
            int? nativeMods, int? legacyMods)
        {
            if (!IsClaimName(loader) || !IsClaimVersion(loaderVersion) || !IsClaimCount(nativeMods)
                || !IsClaimCount(legacyMods))
                throw new ArgumentException("The client claims are not valid notice handshake values.");
            return Serialize(new Frame
            {
                Version = HelloVersion,
                Kind = "hello",
                Token = Convert.ToBase64String(token),
                ReceiptVersion = 1,
                CommandVersion = 1,
                CommandReplyVersion = 1,
                MessengerVersion = 1,
                Loader = loader,
                LoaderVersion = loaderVersion,
                NativeMods = nativeMods,
                LegacyMods = legacyMods
            }, 0);
        }

        private static string Serialize(Frame frame, int replyBytes)
        {
            var text = Prefix + JsonSerializer.Serialize(frame, JsonOptions);
            var frameBytes = Utf8.GetByteCount(text);
            if (frame.Kind == "command-result" && frame.Message != null)
            {
                var excessBytes = frameBytes - MaxFrameBytes;
                if (excessBytes > 0)
                {
                    // Removing raw UTF-8 bytes removes at least as many serialized bytes. Two passes suffice.
                    frame.Message = LimitReply(frame.Message, Math.Max(0, replyBytes - excessBytes), out _, out _);
                    frame.ReplyTruncated = true;
                    text = Prefix + JsonSerializer.Serialize(frame, JsonOptions);
                    frameBytes = Utf8.GetByteCount(text);
                }
            }
            if (frameBytes > MaxFrameBytes)
                throw new ArgumentException("The notice protocol frame exceeds its encoded byte limit.");
            return text;
        }

        internal static bool TryDecode(string text, out Frame frame, out byte[] token)
        {
            frame = null;
            token = null;
            if (text == null || text.Length > MaxFrameBytes || !text.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            try
            {
                if (Utf8.GetByteCount(text) > MaxFrameBytes)
                    return false;

                frame = JsonSerializer.Deserialize<Frame>(text.AsSpan(Prefix.Length), JsonOptions);
                if (frame == null || !(frame.Version == 1 || frame.Version == HelloVersion && frame.Kind == "hello")
                    || frame.Token == null || frame.Token.Length != 44)
                    return false;

                Span<byte> decodedToken = stackalloc byte[32];
                if (!Convert.TryFromBase64String(frame.Token, decodedToken, out var tokenLength)
                    || tokenLength != decodedToken.Length)
                    return false;
                if ((frame.Kind is not ("hello" or "ack") && (frame.CommandReplyVersion != 0 || frame.MessengerVersion != 0))
                    || (frame.Kind is not ("messenger-request" or "messenger-message" or "messenger-result") && frame.MessengerSequence != 0)
                    || (frame.Kind != "command-result" && frame.ReplyTruncated)
                    || (frame.ReplyTruncated && frame.Message == null))
                    return false;
                if (frame.Kind == "hello"
                    ? !IsClaimName(frame.Loader) || !IsClaimVersion(frame.LoaderVersion)
                        || (frame.LoaderVersion != null && frame.Loader == null)
                        || !IsClaimCount(frame.NativeMods) || !IsClaimCount(frame.LegacyMods)
                    : frame.Loader != null || frame.LoaderVersion != null || frame.NativeMods != null
                        || frame.LegacyMods != null)
                    return false;

                MessengerWire.Payload payload = null;
                var valid = frame.Kind switch
                {
                    "hello" or "ack" => frame.Message == null && frame.NoticeId == null && frame.Outcome == null
                        && frame.ReceiptVersion is 0 or 1 && frame.CommandVersion is 0 or 1
                        && frame.CommandReplyVersion is 0 or 1 && frame.MessengerVersion is 0 or 1 && frame.CommandSequence == 0,
                    "messenger-request" or "messenger-message" or "messenger-result" => frame.MessengerSequence > 0
                        && frame.ReceiptVersion == 0 && frame.CommandVersion == 0 && frame.CommandSequence == 0
                        && frame.NoticeId == null && frame.Outcome == null
                        && MessengerWire.TryDecode(frame.Kind, frame.Message, out payload),
                    "notice" => IsMessageValid(frame.Message) && frame.ReceiptVersion == 0 && frame.Outcome == null
                        && frame.CommandVersion == 0 && frame.CommandSequence == 0
                        && (frame.NoticeId == null || frame.NoticeId != Guid.Empty),
                    "receipt" => frame.Message == null && frame.ReceiptVersion == 0
                        && frame.CommandVersion == 0 && frame.CommandSequence == 0
                        && frame.NoticeId.HasValue && frame.NoticeId.Value != Guid.Empty
                        && frame.Outcome is "accepted" or "hud" or "expired" or "evicted" or "failed",
                    "command" => frame.CommandVersion == 0 && frame.CommandSequence > 0
                        && frame.ReceiptVersion == 0 && frame.NoticeId == null && frame.Outcome == null
                        && IsCommandValid(frame.Message),
                    "command-result" => frame.CommandVersion == 0 && frame.CommandSequence > 0
                        && frame.ReceiptVersion == 0 && frame.NoticeId == null && IsReplyValid(frame.Message)
                        && frame.Outcome is "succeeded" or "invalid" or "denied" or "unavailable" or "failed" or "unknown" or "rejected",
                    _ => false
                };
                if (!valid)
                    return false;
                frame.MessengerPayload = payload;
                token = decodedToken.ToArray();
                return true;
            }
            catch (Exception error) when (error is JsonException or FormatException or EncoderFallbackException)
            {
                return false;
            }
        }

        private static bool IsClaimName(string value)
        {
            if (value == null)
                return true;
            if (value.Length is 0 or > MaxClaimNameLength)
                return false;
            foreach (var character in value)
            {
                if (character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-'))
                    return false;
            }
            return true;
        }

        private static bool IsClaimVersion(string value)
        {
            if (value == null)
                return true;
            if (value.Length is 0 or > MaxClaimVersionLength || value[0] == ' ' || value[^1] == ' ')
                return false;
            foreach (var character in value)
            {
                if (character is < ' ' or > '~' or '<' or '>')
                    return false;
            }
            return true;
        }

        private static bool IsClaimCount(int? value) => value is null or >= 0 and <= MaxClaimCount;

        internal static bool Matches(byte[] expected, byte[] received)
        {
            return expected != null && received != null && CryptographicOperations.FixedTimeEquals(expected, received);
        }

        internal sealed class Frame
        {
            public Frame() { }

            [JsonIgnore]
            internal MessengerWire.Payload MessengerPayload { get; set; }

            public int Version { get; set; }
            public string Kind { get; set; }
            public string Token { get; set; }
            public string Message { get; set; }
            public int ReceiptVersion { get; set; }
            public Guid? NoticeId { get; set; }
            public string Outcome { get; set; }
            public int CommandVersion { get; set; }
            public long CommandSequence { get; set; }
            public int CommandReplyVersion { get; set; }
            public bool ReplyTruncated { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public int MessengerVersion { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public long MessengerSequence { get; set; }
            // Client claims, sent only in a hello.
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string Loader { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public string LoaderVersion { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? NativeMods { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public int? LegacyMods { get; set; }
        }
    }
}
