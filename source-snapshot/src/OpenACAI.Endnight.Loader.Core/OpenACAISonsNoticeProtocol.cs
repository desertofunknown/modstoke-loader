// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace OpenACAI.Sons
{
    internal static class OpenACAISonsNoticeProtocol
    {
        internal const string Prefix = "\u001eOpenACAI.Notice:";
        internal const int MaxMessageBytes = 256;
        internal const int MaxCommandBytes = 256;
        // Includes JSON escaping and the capability token, not just the displayed UTF-8 text.
        private const int MaxFrameBytes = 1024;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 4
        };

        internal static bool IsMessageValid(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || message.Length > MaxMessageBytes)
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
            if (message == null || message.Length > MaxCommandBytes || !OpenACAISonsCommands.IsCommandTextValid(message))
                return false;
            try { return Utf8.GetByteCount(message) <= MaxCommandBytes; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static string Encode(string kind, byte[] token, string message = null,
            int receiptVersion = 0, Guid? noticeId = null, string outcome = null,
            int commandVersion = 0, long commandSequence = 0)
        {
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
                CommandSequence = commandSequence
            };
            return Prefix + JsonSerializer.Serialize(frame, JsonOptions);
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
                if (frame == null || frame.Version != 1 || frame.Token == null || frame.Token.Length != 44)
                    return false;

                token = Convert.FromBase64String(frame.Token);
                if (token.Length != 32)
                    return false;

                return frame.Kind switch
                {
                    "hello" or "ack" => frame.Message == null && frame.NoticeId == null && frame.Outcome == null
                        && frame.ReceiptVersion is 0 or 1 && frame.CommandVersion is 0 or 1 && frame.CommandSequence == 0,
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
                        && frame.ReceiptVersion == 0 && frame.NoticeId == null && frame.Message == null
                        && frame.Outcome is "succeeded" or "invalid" or "denied" or "unavailable" or "failed" or "unknown" or "rejected",
                    _ => false
                };
            }
            catch (Exception error) when (error is JsonException or FormatException or EncoderFallbackException)
            {
                return false;
            }
        }

        internal static bool Matches(byte[] expected, byte[] received)
        {
            return expected != null && received != null && CryptographicOperations.FixedTimeEquals(expected, received);
        }

        internal sealed class Frame
        {
            public Frame() { }

            public int Version { get; set; }
            public string Kind { get; set; }
            public string Token { get; set; }
            public string Message { get; set; }
            public int ReceiptVersion { get; set; }
            public Guid? NoticeId { get; set; }
            public string Outcome { get; set; }
            public int CommandVersion { get; set; }
            public long CommandSequence { get; set; }
        }
    }
}
