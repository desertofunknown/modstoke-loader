// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned input and keybind service for loader UI/runtime actions.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenACAI.Sons;
using UnityEngine;

namespace OpenACAI.Loader
{
    public static class OpenACAIInput
    {
        public const string ServiceKey = "openacai.loader.input";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAIKeybindSnapshot> Keybinds =
            new Dictionary<string, OpenACAIKeybindSnapshot>(StringComparer.OrdinalIgnoreCase);

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Keybinds.Clear();
                SetKeybindNoLock(OpenACAIKeybindSnapshot.Create(
                    OpenACAIKeybindIds.ToggleModsMenu,
                    "Toggle Mods Menu",
                    "Modstoke Loader",
                    KeyCode.F6,
                    true,
                    OpenACAIInputContexts.Any,
                    false,
                    "Open or close the Modstoke mods menu."));
                SetKeybindNoLock(OpenACAIKeybindSnapshot.Create(
                    OpenACAIKeybindIds.Back,
                    "Back",
                    "Modstoke Loader",
                    KeyCode.Escape,
                    true,
                    OpenACAIInputContexts.Any,
                    false,
                    "Return from the current OpenACAI menu view."));
                PublishServiceNoLock();
            }
        }

        public static bool TryRegisterKeybind(string id, string displayName, string category, KeyCode key, string tooltip)
        {
            return TryRegisterKeybind(id, displayName, category, key, OpenACAIInputContexts.Any, false, tooltip);
        }

        public static bool TryRegisterKeybind(
            string id,
            string displayName,
            string category,
            KeyCode key,
            string context,
            bool suppressWhenOpenAcaiUiVisible,
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

                SetKeybindNoLock(OpenACAIKeybindSnapshot.Create(
                    id,
                    displayName,
                    category,
                    key,
                    true,
                    context,
                    suppressWhenOpenAcaiUiVisible,
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
                OpenACAIKeybindSnapshot existing;
                if (!Keybinds.TryGetValue(id, out existing))
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
                OpenACAIKeybindSnapshot existing;
                if (!Keybinds.TryGetValue(id, out existing))
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

        public static bool IsPressed(string id)
        {
            return PollKeybind(id, Input.GetKeyDown);
        }

        public static bool IsHeld(string id)
        {
            return PollKeybind(id, Input.GetKey);
        }

        public static bool IsReleased(string id)
        {
            return PollKeybind(id, Input.GetKeyUp);
        }

        private static bool PollKeybind(string id, Func<KeyCode, bool> poll)
        {
            OpenACAIKeybindSnapshot keybind;
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(id) || !Keybinds.TryGetValue(id, out keybind) || !keybind.Enabled || keybind.Key == KeyCode.None)
                {
                    return false;
                }
            }

            string blockedReason;
            if (!IsContextAllowed(keybind, out blockedReason))
            {
                return false;
            }

            try
            {
                return poll(keybind.Key);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsContextAllowed(string id, out string blockedReason)
        {
            OpenACAIKeybindSnapshot keybind;
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

        public static IReadOnlyDictionary<string, OpenACAIKeybindSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, OpenACAIKeybindSnapshot>(Keybinds, StringComparer.OrdinalIgnoreCase);
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
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI input provider could not be registered.");
            }
        }

        private static void SetKeybindNoLock(OpenACAIKeybindSnapshot snapshot)
        {
            Keybinds[snapshot.Id] = snapshot;
        }

        private static void PublishServiceNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
            OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.RequestLoaderStateRefresh("input-keybinds-changed");
        }

        private static object CreateStateNoLock()
        {
            var snapshots = Keybinds.Values
                .OrderBy(keybind => keybind.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(keybind => keybind.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                keybindCount = snapshots.Length,
                enabledKeybindCount = snapshots.Count(keybind => keybind.Enabled),
                currentContext = CurrentContext(),
                openAcaiUiVisible = IsOpenAcaiUiVisible(),
                contexts = OpenACAIInputContexts.All,
                keybinds = snapshots
            };
        }

        private static bool IsContextAllowed(OpenACAIKeybindSnapshot keybind, out string blockedReason)
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

            if (keybind.SuppressWhenOpenAcaiUiVisible && IsOpenAcaiUiVisible())
            {
                blockedReason = "Modstoke UI is visible.";
                return false;
            }

            var context = NormalizeContext(keybind.Context);
            if (string.Equals(context, OpenACAIInputContexts.Any, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var sonsState = OpenACAISons.State;
            if (string.Equals(context, OpenACAIInputContexts.InWorld, StringComparison.OrdinalIgnoreCase))
            {
                if (sonsState.IsInWorld)
                {
                    return true;
                }

                blockedReason = "Sons world is not active.";
                return false;
            }

            if (string.Equals(context, OpenACAIInputContexts.NotInWorld, StringComparison.OrdinalIgnoreCase))
            {
                if (!sonsState.IsInWorld)
                {
                    return true;
                }

                blockedReason = "Sons world is active.";
                return false;
            }

            if (string.Equals(context, OpenACAIInputContexts.Title, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, OpenACAISonsSceneKind.Title, StringComparison.Ordinal))
                {
                    return true;
                }

                blockedReason = "Current scene is not the title scene.";
                return false;
            }

            if (string.Equals(context, OpenACAIInputContexts.Loading, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, OpenACAISonsSceneKind.Loading, StringComparison.Ordinal))
                {
                    return true;
                }

                blockedReason = "Current scene is not a loading scene.";
                return false;
            }

            if (string.Equals(context, OpenACAIInputContexts.GameScene, StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(sonsState.SceneKind, OpenACAISonsSceneKind.Game, StringComparison.Ordinal))
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
            var state = OpenACAISons.State;
            if (state.IsInWorld)
            {
                return OpenACAIInputContexts.InWorld;
            }

            if (string.Equals(state.SceneKind, OpenACAISonsSceneKind.Title, StringComparison.Ordinal))
            {
                return OpenACAIInputContexts.Title;
            }

            if (string.Equals(state.SceneKind, OpenACAISonsSceneKind.Loading, StringComparison.Ordinal))
            {
                return OpenACAIInputContexts.Loading;
            }

            if (string.Equals(state.SceneKind, OpenACAISonsSceneKind.Game, StringComparison.Ordinal))
            {
                return OpenACAIInputContexts.GameScene;
            }

            return OpenACAIInputContexts.NotInWorld;
        }

        private static string NormalizeContext(string context)
        {
            if (string.IsNullOrWhiteSpace(context))
            {
                return OpenACAIInputContexts.Any;
            }

            var trimmed = context.Trim();
            foreach (var known in OpenACAIInputContexts.All)
            {
                if (string.Equals(trimmed, known, StringComparison.OrdinalIgnoreCase))
                {
                    return known;
                }
            }

            return trimmed;
        }

        private static bool IsOpenAcaiUiVisible()
        {
            try
            {
                return OpenACAIUi.Snapshot().Values.Any(surface => surface != null && surface.Visible);
            }
            catch
            {
                return false;
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshots = Snapshot().Values
                .OrderBy(keybind => keybind.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(keybind => keybind.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new OpenACAIMenuContentSnapshot(
                "Input Bindings",
                "OpenACAI-owned keybind registration and context-gating state.",
                new[]
                {
                    new OpenACAIMenuSectionSnapshot(
                        "Input State",
                        new[]
                        {
                            Row("Service", ServiceKey, "READY", "Public OpenACAI keybind registry."),
                            Row("Registered", snapshots.Length.ToString(), "INFO", "Registered OpenACAI keybind count."),
                            Row("Enabled", snapshots.Count(keybind => keybind.Enabled).ToString(), "INFO", "Enabled OpenACAI keybind count."),
                            Row("Context", CurrentContext(), "INFO", "Current input context derived from Modstoke SDK scene/world state."),
                            Row("Modstoke UI", IsOpenAcaiUiVisible() ? "Visible" : "Hidden", IsOpenAcaiUiVisible() ? "INFO" : "READY", "Keybinds can opt into suppression while Modstoke UI is visible.")
                        }),
                    new OpenACAIMenuSectionSnapshot(
                        "Registered Keybinds",
                        snapshots.Select(CreateKeybindRow).ToArray())
                });
        }

        private static OpenACAIMenuRowSnapshot CreateKeybindRow(OpenACAIKeybindSnapshot keybind)
        {
            string blockedReason;
            var allowed = IsContextAllowed(keybind, out blockedReason);
            var status = !keybind.Enabled ? "DISABLED" : allowed ? "READY" : "GATED";
            var detail = keybind.Category
                + " | context "
                + keybind.Context
                + (keybind.SuppressWhenOpenAcaiUiVisible ? " | suppresses while OpenACAI UI is visible" : "")
                + (string.IsNullOrWhiteSpace(blockedReason) ? "" : " | " + blockedReason);
            return Row(keybind.DisplayName, keybind.KeyName, status, detail);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.InputBindings;
            public string DisplayName => "Input Bindings";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 320;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned keybind registration and context gates.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAIInput.CreateContent();
            }
        }
    }

    public static class OpenACAIKeybindIds
    {
        public const string ToggleModsMenu = "openacai.mods.toggle";
        public const string Back = "openacai.ui.back";
    }

    public static class OpenACAIInputContexts
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

    public sealed class OpenACAIKeybindSnapshot
    {
        private OpenACAIKeybindSnapshot(
            string id,
            string displayName,
            string category,
            KeyCode key,
            bool enabled,
            string context,
            bool suppressWhenOpenAcaiUiVisible,
            string tooltip)
        {
            Id = id;
            DisplayName = displayName;
            Category = category;
            Key = key;
            KeyName = key.ToString();
            Enabled = enabled;
            Context = string.IsNullOrWhiteSpace(context) ? OpenACAIInputContexts.Any : context;
            SuppressWhenOpenAcaiUiVisible = suppressWhenOpenAcaiUiVisible;
            Tooltip = tooltip;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Category { get; }
        public KeyCode Key { get; }
        public string KeyName { get; }
        public bool Enabled { get; }
        public string Context { get; }
        public bool SuppressWhenOpenAcaiUiVisible { get; }
        public string Tooltip { get; }

        internal static OpenACAIKeybindSnapshot Create(
            string id,
            string displayName,
            string category,
            KeyCode key,
            bool enabled,
            string context,
            bool suppressWhenOpenAcaiUiVisible,
            string tooltip)
        {
            return new OpenACAIKeybindSnapshot(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                string.IsNullOrWhiteSpace(category) ? "OpenACAI" : category,
                key,
                enabled,
                context,
                suppressWhenOpenAcaiUiVisible,
                tooltip);
        }

        internal OpenACAIKeybindSnapshot WithEnabled(bool enabled)
        {
            return new OpenACAIKeybindSnapshot(Id, DisplayName, Category, Key, enabled, Context, SuppressWhenOpenAcaiUiVisible, Tooltip);
        }

        internal OpenACAIKeybindSnapshot WithKey(KeyCode key)
        {
            return new OpenACAIKeybindSnapshot(Id, DisplayName, Category, key, Enabled, Context, SuppressWhenOpenAcaiUiVisible, Tooltip);
        }
    }
}

