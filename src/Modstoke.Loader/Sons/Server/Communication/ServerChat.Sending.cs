// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using Bolt;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ServerChat
    {
        /// <summary>Maximum native reply length in UTF-16 code units.</summary>
        public const int MaxMessageLength = 512;

        private static int _reportedSendFailure;

        /// <summary>
        /// Queues plain, single-line game chat to one exact current remote session on the game thread.
        /// No client mod, handshake or receipt is required. Queued does not prove delivery or display.
        /// The caller owns authorization; the server sender is zero, never a client-claimed identity.
        /// Empty text, controls, line separators, unpaired surrogates, rich-text angle brackets and
        /// negotiated protocol frames are rejected. No local-player or reconnected-session fallback exists.
        /// </summary>
        public static ChatSendResult SendToSession(ServerSession recipient, string plainText)
        {
            if (State.Status != ServerChatHookStatus.Installed || !ServerSessions.HooksInstalled)
                return ChatSendResult.Unavailable;
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
                return ChatSendResult.WrongThread;
            if (!IsPlainTextValid(plainText))
                return ChatSendResult.InvalidMessage;

            ChatEvent chat = null;
            var sendAttempted = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                    return ChatSendResult.NotServer;
                var connection = ServerSessions.ResolveCurrent(recipient);
                if (connection == null)
                    return ServerSessions.HooksInstalled
                        ? ChatSendResult.SessionUnavailable : ChatSendResult.Unavailable;

                chat = ChatEvent.Create(connection, ReliabilityModes.ReliableOrdered);
                chat.Sender = new NetworkId(0);
                chat.Message = plainText;

                var current = ServerSessions.ResolveCurrent(recipient);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != connection.ConnectionId)
                    return ServerSessions.HooksInstalled
                        ? ChatSendResult.SessionUnavailable : ChatSendResult.Unavailable;

                // Send may queue before throwing; storage ownership is uncertain once it begins.
                sendAttempted = true;
                chat.Send();
                return ChatSendResult.Queued;
            }
            catch (Exception error)
            {
                ReportSendFailure(error);
                return sendAttempted ? ChatSendResult.DeliveryUncertain : ChatSendResult.Failed;
            }
            finally
            {
                if (chat != null && !sendAttempted)
                {
                    try { chat.FreeStorage(); }
                    catch (Exception error) { ReportSendFailure(error); }
                }
            }
        }

        internal static bool IsPlainTextValid(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxMessageLength
                || text.StartsWith(NoticeProtocol.Prefix, StringComparison.Ordinal))
                return false;
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                if (char.IsControl(character) || character is '\u2028' or '\u2029' or '<' or '>')
                    return false;
                if (char.IsHighSurrogate(character))
                {
                    if (++index == text.Length || !char.IsLowSurrogate(text[index]))
                        return false;
                }
                else if (char.IsLowSurrogate(character))
                    return false;
            }
            return true;
        }

        private static void ReportSendFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedSendFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server chat sending failed ("
                    + error.GetType().FullName + "). Further send failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ChatSendResult
    {
        /// <summary>Native Send returned; delivery and display remain unconfirmed.</summary>
        Queued,
        Unavailable,
        WrongThread,
        NotServer,
        SessionUnavailable,
        InvalidMessage,
        Failed,
        /// <summary>Native Send began and threw; do not retry as if nothing was queued.</summary>
        DeliveryUncertain,
        /// <summary>A command requested tracking on GameChat. No reply was sent.</summary>
        UnsupportedTracking
    }
}
