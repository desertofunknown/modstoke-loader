// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned state surface for Sons menu button injection.

using System;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsMenuBridgeState
    {
        public const string ServiceKey = "openacai.sons.menuBridge";
        public const string TitleButtonId = "title.mods";
        public const string PauseButtonId = "pause.mods";

        private static readonly object SyncRoot = new object();
        private static OpenACAISonsMenuButtonState _titleButton = OpenACAISonsMenuButtonState.Create(TitleButtonId, "Title Mods Button");
        private static OpenACAISonsMenuButtonState _pauseButton = OpenACAISonsMenuButtonState.Create(PauseButtonId, "Pause Mods Button");
        private static string _sceneName;
        private static DateTimeOffset _lastUpdatedUtc = DateTimeOffset.UtcNow;
        private static bool _initialized;

        public static object CreateState()
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    PublishNoLock();
                    return;
                }

                _initialized = true;
                _lastUpdatedUtc = DateTimeOffset.UtcNow;
                PublishNoLock();
            }
        }

        internal static void MarkScene(string sceneName)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                _sceneName = sceneName;
                TouchNoLock();
                PublishNoLock();
            }
        }

        internal static void MarkButtonCleared(string id, string detail)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                var current = GetButtonNoLock(id);
                SetButtonNoLock(id, current.WithCleared(detail, DateTimeOffset.UtcNow));
                TouchNoLock();
                PublishNoLock();
            }
        }

        internal static void MarkButtonAttempt(string id, string phase, string detail)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                var current = GetButtonNoLock(id);
                SetButtonNoLock(id, current.WithAttempt(phase, detail, DateTimeOffset.UtcNow));
                TouchNoLock();
                PublishNoLock();
            }
        }

        internal static void MarkButtonAttached(string id, string parentPath, string detail)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                var current = GetButtonNoLock(id);
                SetButtonNoLock(id, current.WithAttached(parentPath, detail, DateTimeOffset.UtcNow));
                TouchNoLock();
                PublishNoLock();
            }
        }

        internal static void MarkButtonSkipped(string id, string detail)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                var current = GetButtonNoLock(id);
                SetButtonNoLock(id, current.WithSkipped(detail, DateTimeOffset.UtcNow));
                TouchNoLock();
                PublishNoLock();
            }
        }

        internal static void MarkButtonFailed(string id, string detail)
        {
            EnsureInitialized();
            lock (SyncRoot)
            {
                var current = GetButtonNoLock(id);
                SetButtonNoLock(id, current.WithFailed(detail, DateTimeOffset.UtcNow));
                TouchNoLock();
                PublishNoLock();
            }
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
            {
                return;
            }

            Initialize();
        }

        private static object CreateStateNoLock()
        {
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                sceneName = _sceneName,
                lastUpdatedUtc = _lastUpdatedUtc.ToString("O"),
                titleButton = _titleButton.CreateSnapshot(),
                pauseButton = _pauseButton.CreateSnapshot()
            };
        }

        private static void PublishNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void TouchNoLock()
        {
            _lastUpdatedUtc = DateTimeOffset.UtcNow;
        }

        private static OpenACAISonsMenuButtonState GetButtonNoLock(string id)
        {
            return string.Equals(id, PauseButtonId, StringComparison.OrdinalIgnoreCase)
                ? _pauseButton
                : _titleButton;
        }

        private static void SetButtonNoLock(string id, OpenACAISonsMenuButtonState state)
        {
            if (string.Equals(id, PauseButtonId, StringComparison.OrdinalIgnoreCase))
            {
                _pauseButton = state;
            }
            else
            {
                _titleButton = state;
            }
        }
    }

    public sealed class OpenACAISonsMenuButtonSnapshot
    {
        internal OpenACAISonsMenuButtonSnapshot(
            string id,
            string displayName,
            bool attached,
            int attempts,
            int successes,
            int failures,
            string parentPath,
            string phase,
            string detail,
            string lastUpdatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Attached = attached;
            Attempts = attempts;
            Successes = successes;
            Failures = failures;
            ParentPath = parentPath;
            Phase = phase;
            Detail = detail;
            LastUpdatedUtc = lastUpdatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public bool Attached { get; }
        public int Attempts { get; }
        public int Successes { get; }
        public int Failures { get; }
        public string ParentPath { get; }
        public string Phase { get; }
        public string Detail { get; }
        public string LastUpdatedUtc { get; }
    }

    internal sealed class OpenACAISonsMenuButtonState
    {
        private OpenACAISonsMenuButtonState(
            string id,
            string displayName,
            bool attached,
            int attempts,
            int successes,
            int failures,
            string parentPath,
            string phase,
            string detail,
            DateTimeOffset lastUpdatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Attached = attached;
            Attempts = attempts;
            Successes = successes;
            Failures = failures;
            ParentPath = parentPath;
            Phase = phase;
            Detail = detail;
            LastUpdatedUtc = lastUpdatedUtc;
        }

        private string Id { get; }
        private string DisplayName { get; }
        private bool Attached { get; }
        private int Attempts { get; }
        private int Successes { get; }
        private int Failures { get; }
        private string ParentPath { get; }
        private string Phase { get; }
        private string Detail { get; }
        private DateTimeOffset LastUpdatedUtc { get; }

        internal static OpenACAISonsMenuButtonState Create(string id, string displayName)
        {
            return new OpenACAISonsMenuButtonState(
                id,
                displayName,
                false,
                0,
                0,
                0,
                null,
                "Unknown",
                null,
                DateTimeOffset.UtcNow);
        }

        internal OpenACAISonsMenuButtonState WithCleared(string detail, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsMenuButtonState(
                Id,
                DisplayName,
                false,
                Attempts,
                Successes,
                Failures,
                null,
                "Cleared",
                detail,
                timestampUtc);
        }

        internal OpenACAISonsMenuButtonState WithAttempt(string phase, string detail, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsMenuButtonState(
                Id,
                DisplayName,
                Attached,
                Attempts + 1,
                Successes,
                Failures,
                ParentPath,
                string.IsNullOrWhiteSpace(phase) ? "Attempt" : phase,
                detail,
                timestampUtc);
        }

        internal OpenACAISonsMenuButtonState WithAttached(string parentPath, string detail, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsMenuButtonState(
                Id,
                DisplayName,
                true,
                Attempts,
                Successes + 1,
                Failures,
                parentPath,
                "Attached",
                detail,
                timestampUtc);
        }

        internal OpenACAISonsMenuButtonState WithSkipped(string detail, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsMenuButtonState(
                Id,
                DisplayName,
                Attached,
                Attempts,
                Successes,
                Failures,
                ParentPath,
                "Skipped",
                detail,
                timestampUtc);
        }

        internal OpenACAISonsMenuButtonState WithFailed(string detail, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsMenuButtonState(
                Id,
                DisplayName,
                Attached,
                Attempts,
                Successes,
                Failures + 1,
                ParentPath,
                "Failed",
                detail,
                timestampUtc);
        }

        internal OpenACAISonsMenuButtonSnapshot CreateSnapshot()
        {
            return new OpenACAISonsMenuButtonSnapshot(
                Id,
                DisplayName,
                Attached,
                Attempts,
                Successes,
                Failures,
                ParentPath,
                Phase,
                Detail,
                LastUpdatedUtc.ToString("O"));
        }
    }
}

