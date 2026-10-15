// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Utils;

namespace OpenACAI.Sons
{
    internal enum OpenACAISonsNoticeDisplayOutcome
    {
        HudInvoked,
        Expired,
        QueueEvicted,
        Failed
    }

    internal static class OpenACAISonsNoticeDisplay
    {
        private const int QueueCapacity = 16;
        private const long MessageLifetimeMilliseconds = 120000;
        private const long DisplayIntervalMilliseconds = 6000;

        private static readonly Queue<(string Message, long ExpiresAt, Action<OpenACAISonsNoticeDisplayOutcome> Completion)> _messages = new(QueueCapacity);
        private static long _nextDisplayTick;

        internal static void Enqueue(string message, Action<OpenACAISonsNoticeDisplayOutcome> completion = null)
        {
            Action<OpenACAISonsNoticeDisplayOutcome> evicted = null;
            if (_messages.Count == QueueCapacity)
                evicted = _messages.Dequeue().Completion;

            _messages.Enqueue((message, Environment.TickCount64 + MessageLifetimeMilliseconds, completion));
            Complete(evicted, OpenACAISonsNoticeDisplayOutcome.QueueEvicted);
        }

        internal static void Clear()
        {
            _messages.Clear();
            _nextDisplayTick = 0;
        }

        internal static void Tick()
        {
            if (_messages.Count == 0)
                return;

            var now = Environment.TickCount64;
            while (_messages.Count > 0 && _messages.Peek().ExpiresAt <= now)
            {
                var expired = _messages.Dequeue();
                Complete(expired.Completion, OpenACAISonsNoticeDisplayOutcome.Expired);
            }

            if (_messages.Count == 0 || now < _nextDisplayTick || !LocalPlayer.IsInWorld)
                return;

            var hud = HudGui._instance;
            if (hud is null || hud.WasCollected || !hud)
                return;

            var message = _messages.Dequeue();
            _nextDisplayTick = now + DisplayIntervalMilliseconds;
            try
            {
                hud.DisplayGeneralMessage("[Server] " + message.Message, 6f, null);
            }
            catch
            {
                Complete(message.Completion, OpenACAISonsNoticeDisplayOutcome.Failed);
                throw;
            }

            Complete(message.Completion, OpenACAISonsNoticeDisplayOutcome.HudInvoked);
        }

        private static void Complete(Action<OpenACAISonsNoticeDisplayOutcome> completion, OpenACAISonsNoticeDisplayOutcome outcome)
        {
            try
            {
                completion?.Invoke(outcome);
            }
            catch
            {
                // Completion failures must not interrupt queue or HUD processing.
            }
        }
    }
}
