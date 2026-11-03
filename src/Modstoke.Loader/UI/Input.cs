// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Loader
{
    public static class InputBindings
    {
        public const string ServiceKey = "openacai.loader.input";

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, KeybindSnapshot> Keybinds =
            new(StringComparer.OrdinalIgnoreCase);
        private static FocusLease _currentFocus;
        private static int? _focusReleaseFrame;

        /// <summary>Reports SDK focus, including a release earlier this frame. Main-thread only.</summary>
        public static bool HasFocus
        {
            get
            {
                lock (SyncRoot)
                {
                    return _currentFocus != null || IsFocusReleaseFrameNoLock();
                }
            }
        }

#nullable enable annotations
        /// <summary>Captures SDK input for one owner; returns false if already owned.</summary>
        /// <remarks>
        /// Acquire, poll and dispose on Unity's main thread; dispose when the UI closes or unloads.
        /// Only allowed IDs remain eligible, subject to enabled/context checks. Raw game input is unaffected.
        /// Disposal suppresses all SDK input, including allowed IDs, until the next frame.
        /// </remarks>
        public static bool TryAcquireFocus(string ownerId, out IDisposable? focus, params string[] allowedKeybindIds)
        {
            focus = null;
            if (string.IsNullOrWhiteSpace(ownerId))
                return false;

            lock (SyncRoot)
            {
                if (_currentFocus != null)
                    return false;

                var allowedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (allowedKeybindIds != null)
                {
                    foreach (var id in allowedKeybindIds)
                    {
                        if (!string.IsNullOrWhiteSpace(id))
                            allowedIds.Add(id);
                    }
                }

                _currentFocus = new FocusLease(ownerId, allowedIds);
                focus = _currentFocus;
                return true;
            }
        }
#nullable restore annotations

        private static bool IsFocusReleaseFrameNoLock()
        {
            if (!_focusReleaseFrame.HasValue)
                return false;
            if (_focusReleaseFrame.Value == Time.frameCount)
                return true;

            _focusReleaseFrame = null;
            return false;
        }

        private static bool IsFocusAllowed(string id, out string blockedReason)
        {
            lock (SyncRoot)
            {
                if (IsFocusReleaseFrameNoLock())
                {
                    blockedReason = "Input focus was released this frame.";
                    return false;
                }

                if (_currentFocus != null && !_currentFocus.AllowedKeybindIds.Contains(id))
                {
                    blockedReason = _currentFocus.BlockedReason;
                    return false;
                }
            }

            blockedReason = "";
            return true;
        }

        private sealed class FocusLease : IDisposable
        {
            internal readonly string BlockedReason;
            internal readonly HashSet<string> AllowedKeybindIds;

            internal FocusLease(string ownerId, HashSet<string> allowedKeybindIds)
            {
                BlockedReason = "Input is captured by '" + ownerId + "'.";
                AllowedKeybindIds = allowedKeybindIds;
            }

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    // A disposed or reset lease cannot release a newer owner's focus.
                    if (!ReferenceEquals(_currentFocus, this))
                        return;

                    _focusReleaseFrame = Time.frameCount;
                    _currentFocus = null;
                }
            }
        }

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                _currentFocus = null;
                _focusReleaseFrame = null;
                Keybinds.Clear();
                SetKeybindNoLock(KeybindSnapshot.Create(
                    KeybindIds.ToggleModsMenu,
                    "Toggle Mods Menu",
                    "Modstoke Loader",
                    KeyCode.F6,
                    true,
                    InputContexts.Any,
                    false,
                    "Open or close the Modstoke mods menu."));
                SetKeybindNoLock(KeybindSnapshot.Create(
                    KeybindIds.Back,
                    "Back",
                    "Modstoke Loader",
                    KeyCode.Escape,
                    true,
                    InputContexts.Any,
                    false,
                    "Return from the current Modstoke menu view."));
                PublishServiceNoLock();
            }
        }

        public static bool TryRegisterKeybind(string id, string displayName, string category, KeyCode key, string tooltip)
        {
            return TryRegisterKeybind(id, displayName, category, key, InputContexts.Any, false, tooltip);
        }

        public static bool TryRegisterKeybind(
            string id,
            string displayName,
            string category,
            KeyCode key,
            string context,
            bool suppressWhenModstokeUiVisible,
            string tooltip)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (Keybinds.ContainsKey(id))
                {
                    return false;
                }

                SetKeybindNoLock(KeybindSnapshot.Create(
                    id,
                    displayName,
                    category,
                    key,
                    true,
                    context,
                    suppressWhenModstokeUiVisible,
                    tooltip));
                PublishServiceNoLock();
                return true;
            }
        }

        public static bool SetEnabled(string id, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!Keybinds.TryGetValue(id, out var existing))
                {
                    return false;
                }

                if (existing.Enabled == enabled)
                {
                    return true;
                }

                SetKeybindNoLock(existing.WithEnabled(enabled));
                PublishServiceNoLock();
                return true;
            }
        }

        public static bool SetKey(string id, KeyCode key)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!Keybinds.TryGetValue(id, out var existing))
                {
                    return false;
                }

                if (existing.Key == key)
                {
                    return true;
                }

                SetKeybindNoLock(existing.WithKey(key));
                PublishServiceNoLock();
                return true;
            }
        }

        public static bool TryUnregisterKeybind(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!Keybinds.Remove(id))
                {
                    return false;
                }

                PublishServiceNoLock();
                return true;
            }
        }

        public static bool IsPressed(string id) => PollKeybind(id, Input.GetKeyDown);

        public static bool IsHeld(string id) => PollKeybind(id, Input.GetKey);

        public static bool IsReleased(string id) => PollKeybind(id, Input.GetKeyUp);

        private static bool PollKeybind(string id, Func<KeyCode, bool> poll)
        {
            KeybindSnapshot keybind;
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(id) || !Keybinds.TryGetValue(id, out keybind) || !keybind.Enabled || keybind.Key == KeyCode.None)
                {
                    return false;
                }
            }

            try
            {
                if (!poll(keybind.Key))
                    return false;
            }
            catch
            {
                return false;
            }

            // Context checks can copy UI state; idle keys need no such work.
            return IsContextAllowed(keybind, out _);
        }

        public static bool IsContextAllowed(string id, out string blockedReason)
        {
            KeybindSnapshot keybind;
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(id) || !Keybinds.TryGetValue(id, out keybind))
                {
                    blockedReason = "Keybind is not registered.";
                    return false;
                }
            }

            return IsContextAllowed(keybind, out blockedReason);
        }

        public static IReadOnlyDictionary<string, KeybindSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, KeybindSnapshot>(Keybinds, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        internal static void RegisterMenuProvider()
        {
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning("Modstoke input provider could not be registered.");
            }
        }

        private static void SetKeybindNoLock(KeybindSnapshot snapshot)
        {
            Keybinds[snapshot.Id] = snapshot;
        }

        private static void PublishServiceNoLock()
        {
            Modding.SetService(ServiceKey, CreateStateNoLock());
            global::Modstoke.Loader.RuntimeEntryPoint.RequestLoaderStateRefresh("input-keybinds-changed");
        }

        private static object CreateStateNoLock()
        {
            var snapshots = CreateSortedSnapshotNoLock();
            return new
            {
                schema = 2,
                serviceKey = ServiceKey,
                keybindCount = snapshots.Length,
                enabledKeybindCount = snapshots.Count(keybind => keybind.Enabled),
                currentContext = CurrentContext(),
                modstokeUiVisible = IsModstokeUiVisible(),
                contexts = InputContexts.All,
                keybinds = snapshots
            };
        }

        private static KeybindSnapshot[] CreateSortedSnapshotNoLock()
        {
            return Keybinds.Values
                .OrderBy(keybind => keybind.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(keybind => keybind.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsContextAllowed(KeybindSnapshot keybind, out string blockedReason)
        {
            blockedReason = "";
            if (keybind == null)
            {
                blockedReason = "Keybind is not registered.";
                return false;
            }

            if (!keybind.Enabled)
            {
                blockedReason = "Keybind is disabled.";
                return false;
            }

            if (!IsFocusAllowed(keybind.Id, out blockedReason))
                return false;

            if (keybind.SuppressWhenModstokeUiVisible && IsModstokeUiVisible())
            {
                blockedReason = "Modstoke UI is visible.";
                return false;
            }

            var context = NormalizeContext(keybind.Context);
            if (string.Equals(context, InputContexts.Any, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sonsState = GameRuntime.State;
            if (string.Equals(context, InputContexts.InWorld, StringComparison.OrdinalIgnoreCase))
            {
                if (sonsState.IsInWorld)
                {
                    return true;
                }

                blockedReason = "Sons world is not active.";
                return false;
            }

            if (string.Equals(context, InputContexts.NotInWorld, StringComparison.OrdinalIgnoreCase))
            {
                if (!sonsState.IsInWorld)
                {
                    return true;
                }

                blockedReason = "Sons world is active.";
                return false;
            }

            if (string.Equals(context, InputContexts.Title, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, SceneKind.Title, StringComparison.Ordinal))
                {
                    return true;
                }

                blockedReason = "Current scene is not the title scene.";
                return false;
            }

            if (string.Equals(context, InputContexts.Loading, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, SceneKind.Loading, StringComparison.Ordinal))
                {
                    return true;
                }

                blockedReason = "Current scene is not a loading scene.";
                return false;
            }

            if (string.Equals(context, InputContexts.GameScene, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, SceneKind.Game, StringComparison.Ordinal))
                {
                    return true;
                }

                blockedReason = "Current scene is not the game scene.";
                return false;
            }

            blockedReason = "Unknown keybind context '" + keybind.Context + "'.";
            return false;
        }

        private static string CurrentContext()
        {
            var state = GameRuntime.State;
            if (state.IsInWorld)
            {
                return InputContexts.InWorld;
            }

            if (string.Equals(state.SceneKind, SceneKind.Title, StringComparison.Ordinal))
            {
                return InputContexts.Title;
            }

            if (string.Equals(state.SceneKind, SceneKind.Loading, StringComparison.Ordinal))
            {
                return InputContexts.Loading;
            }

            if (string.Equals(state.SceneKind, SceneKind.Game, StringComparison.Ordinal))
            {
                return InputContexts.GameScene;
            }

            return InputContexts.NotInWorld;
        }

        private static string NormalizeContext(string context)
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                return InputContexts.Any;
            }

            var trimmed = context.Trim();
            foreach (var known in InputContexts.All)
            {
                if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return trimmed;
        }

        private static bool IsModstokeUiVisible()
        {
            try
            {
                return UiState.Snapshot().Values.Any(surface => surface != null && surface.Visible);
            }
            catch
            {
                return false;
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            KeybindSnapshot[] snapshots;
            lock (SyncRoot)
            {
                snapshots = CreateSortedSnapshotNoLock();
            }

            var uiVisible = IsModstokeUiVisible();
            return new MenuContentSnapshot(
                "Input Bindings",
                "Modstoke keybind registration and context-gating state.",
                new[]
                {
                    new MenuSectionSnapshot(
                        "Input State",
                        new[]
                        {
                            new MenuRowSnapshot("Service", ServiceKey, "READY", "Public Modstoke keybind registry."),
                            new MenuRowSnapshot("Registered", snapshots.Length.ToString(), "INFO", "Registered Modstoke keybind count."),
                            new MenuRowSnapshot("Enabled", snapshots.Count(keybind => keybind.Enabled).ToString(), "INFO", "Enabled Modstoke keybind count."),
                            new MenuRowSnapshot("Context", CurrentContext(), "INFO", "Current input context derived from Modstoke SDK scene/world state."),
                            new MenuRowSnapshot("Modstoke UI", uiVisible ? "Visible" : "Hidden", uiVisible ? "INFO" : "READY", "Keybinds can opt into suppression while Modstoke UI is visible.")
                        }),
                    new MenuSectionSnapshot(
                        "Registered Keybinds",
                        snapshots.Select(CreateKeybindRow).ToArray())
                });
        }

        private static MenuRowSnapshot CreateKeybindRow(KeybindSnapshot keybind)
        {
            var allowed = IsContextAllowed(keybind, out var blockedReason);
            var status = !keybind.Enabled ? "DISABLED" : allowed ? "READY" : "GATED";
            var detail = keybind.Category
                + " | context "
                + keybind.Context
                + (keybind.SuppressWhenModstokeUiVisible ? " | suppresses while Modstoke UI is visible" : "")
                + (string.IsNullOrWhiteSpace(blockedReason) ? "" : " | " + blockedReason);
            return new MenuRowSnapshot(keybind.DisplayName, keybind.KeyName, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.InputBindings;
            public string DisplayName => "Input Bindings";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 320;
            public string Source => "Modstoke Loader";
            public string Description => "Show Modstoke keybind registration and context gates.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return InputBindings.CreateContent();
            }
        }
    }

    public static class KeybindIds
    {
        public const string ToggleModsMenu = "openacai.mods.toggle";
        public const string Back = "openacai.ui.back";
    }

    public static class InputContexts
    {
        public const string Any = "any";
        public const string Title = "title";
        public const string Loading = "loading";
        public const string GameScene = "game-scene";
        public const string InWorld = "in-world";
        public const string NotInWorld = "not-in-world";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            Any,
            Title,
            Loading,
            GameScene,
            InWorld,
            NotInWorld
        };
    }

    public sealed class KeybindSnapshot
    {
        private KeybindSnapshot(
            string id,
            string displayName,
            string category,
            KeyCode key,
            bool enabled,
            string context,
            bool suppressWhenModstokeUiVisible,
            string tooltip)
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            Key = key;
            KeyName = key.ToString();
            Enabled = enabled;
            Context = string.IsNullOrWhiteSpace(context) ? InputContexts.Any : context;
            SuppressWhenModstokeUiVisible = suppressWhenModstokeUiVisible;
            Tooltip = tooltip;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public KeyCode Key { get; }
        public string KeyName { get; }
        public bool Enabled { get; }
        public string Context { get; }
        public bool SuppressWhenModstokeUiVisible { get; }
        public string Tooltip { get; }

        [Obsolete("Use SuppressWhenModstokeUiVisible.")]
        [JsonIgnore]
        public bool SuppressWhenOpenAcaiUiVisible => SuppressWhenModstokeUiVisible;

        internal static KeybindSnapshot Create(
            string id,
            string displayName,
            string category,
            KeyCode key,
            bool enabled,
            string context,
            bool suppressWhenModstokeUiVisible,
            string tooltip)
        {
            return new KeybindSnapshot(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                string.IsNullOrWhiteSpace(category) ? "Modstoke" : category,
                key,
                enabled,
                context,
                suppressWhenModstokeUiVisible,
                tooltip);
        }

        internal KeybindSnapshot WithEnabled(bool enabled)
        {
            return new KeybindSnapshot(Id, DisplayName, Category, Key, enabled, Context, SuppressWhenModstokeUiVisible, Tooltip);
        }

        internal KeybindSnapshot WithKey(KeyCode key)
        {
            return new KeybindSnapshot(Id, DisplayName, Category, key, Enabled, Context, SuppressWhenModstokeUiVisible, Tooltip);
        }
    }
}

