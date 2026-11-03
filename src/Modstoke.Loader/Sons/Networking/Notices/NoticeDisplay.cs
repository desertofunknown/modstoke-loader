// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using TheForest.Utils;

namespace Modstoke.Sons
{
    internal enum NoticeDisplayOutcome
    {
        HudInvoked,
        Expired,
        QueueEvicted,
        Failed
    }

    internal static class NoticeDisplay
    {
        private const int QueueCapacity = 16;
        private const long MessageLifetimeMilliseconds = 120000;
        private const long DisplayIntervalMilliseconds = 6000;

        private static readonly Queue<(string Message, long ExpiresAt, Action<NoticeDisplayOutcome> Completion)> _messages = new(QueueCapacity);
        private static long _nextDisplayTick;

        internal static void Enqueue(string message, Action<NoticeDisplayOutcome> completion = null)
        {
            Action<NoticeDisplayOutcome> evicted = null;
            if (_messages.Count == QueueCapacity)
                evicted = _messages.Dequeue().Completion;

            _messages.Enqueue((message, Environment.TickCount64 + MessageLifetimeMilliseconds, completion));
            Complete(evicted, NoticeDisplayOutcome.QueueEvicted);
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
                Complete(expired.Completion, NoticeDisplayOutcome.Expired);
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
                Complete(message.Completion, NoticeDisplayOutcome.Failed);
                throw;
            }

            Complete(message.Completion, NoticeDisplayOutcome.HudInvoked);
        }

        private static void Complete(Action<NoticeDisplayOutcome> completion, NoticeDisplayOutcome outcome)
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
