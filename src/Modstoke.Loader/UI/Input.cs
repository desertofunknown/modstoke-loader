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
    public static partial class InputBindings
    {
        public const string ServiceKey = "openacai.loader.input";
        /// <summary>The longest hold a keybind can ask for.</summary>
        public const float MaximumHoldSeconds = 10f;

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, KeybindSnapshot> Keybinds =
            new(StringComparer.OrdinalIgnoreCase);
        private static FocusLease _currentFocus;
        private static int? _focusReleaseFrame;

        /// <summary>Counts every change to the registered keybinds, so a mod caching key names or prompts knows when to read them again.</summary>
        public static int Revision { get; private set; }

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
        /// <summary>The owner ID passed to <see cref="TryAcquireFocus"/> for the current focus, or null. Main-thread only.</summary>
        /// <remarks>Lets a mod wait for specific owners (the mods menu, the console) and not others.</remarks>
        public static string? FocusOwnerId
        {
            get
            {
                lock (SyncRoot)
                {
                    return _currentFocus?.OwnerId;
                }
            }
        }

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
            // A system dialog is modal over every surface, the mods menu's lease included, through the frame its answer
            // was pressed in; only the console still opens.
            if (SystemDialogs.Holding && !string.Equals(id, ConsoleOverlay.KeybindId, StringComparison.OrdinalIgnoreCase))
            {
                blockedReason = "A dialog is open.";
                return false;
            }

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
            internal readonly string OwnerId;
            internal readonly string BlockedReason;
            internal readonly HashSet<string> AllowedKeybindIds;

            internal FocusLease(string ownerId, HashSet<string> allowedKeybindIds)
            {
                OwnerId = ownerId;
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
                Tracks.Clear();
                _saved = null;
                RegisterNoLock(KeybindSnapshot.Create(
                    KeybindIds.ToggleModsMenu,
                    "Toggle Mods Menu",
                    "Modstoke Loader",
                    KeyCode.F6,
                    true,
                    InputContexts.Any,
                    false,
                    "Open or close the Modstoke mods menu."));
                RegisterNoLock(KeybindSnapshot.Create(
                    KeybindIds.Back,
                    "Back",
                    "Modstoke Loader",
                    KeyCode.Escape,
                    true,
                    InputContexts.Any,
                    false,
                    "Return from the current Modstoke menu view."));
                RegisterNoLock(KeybindSnapshot.Create(
                    KeybindIds.PhotoMode,
                    "Photo Mode",
                    "Modstoke Loader",
                    KeyCode.F10,
                    true,
                    InputContexts.InWorld,
                    false,
                    "Open photo mode from the world, or close it again."));
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
            return TryRegisterKeybind(id, displayName, category, key, PadBinding.None, context, suppressWhenModstokeUiVisible, tooltip);
        }

        /// <summary>Registers a keybind with a key and a controller side; either may be none.</summary>
        /// <remarks>
        /// <paramref name="key"/> and <paramref name="pad"/> are the defaults: a player's own choice from the Mods menu is
        /// saved and applied in their place. With <paramref name="holdSeconds"/> above zero, <see cref="IsPressed"/> waits
        /// for that long a hold, on either device. Returns false for a taken ID or a hold outside 0 to
        /// <see cref="MaximumHoldSeconds"/>.
        /// </remarks>
        public static bool TryRegisterKeybind(
            string id,
            string displayName,
            string category,
            KeyCode key,
            PadBinding pad,
            string context,
            bool suppressWhenModstokeUiVisible,
            string tooltip,
            float holdSeconds = 0)
        {
            if (string.IsNullOrWhiteSpace(id) || !(holdSeconds >= 0 && holdSeconds <= MaximumHoldSeconds))
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (Keybinds.ContainsKey(id))
                {
                    return false;
                }

                RegisterNoLock(KeybindSnapshot.Create(
                    id,
                    displayName,
                    category,
                    key,
                    pad,
                    holdSeconds,
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

        /// <summary>Changes the key; the player's binding from then on, saved with the Loader's settings.</summary>
        public static bool SetKey(string id, KeyCode key) => Rebind(id, key, null);

        /// <summary>Changes the controller side; the player's binding from then on, saved with the Loader's settings.</summary>
        public static bool SetPad(string id, PadBinding pad) => Rebind(id, null, pad);

        /// <summary>Puts the key and controller side back to the ones the keybind was registered with.</summary>
        public static bool ResetToDefault(string id)
        {
            KeybindSnapshot keybind;
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(id) || !Keybinds.TryGetValue(id, out keybind))
                {
                    return false;
                }
            }

            return Rebind(id, keybind.DefaultKey, keybind.DefaultPad);
        }

        /// <summary>
        /// Declares the game actions a keybind deliberately stands in for on the mod's own objects, such as the game's
        /// Interact on a mod's door, so the keybind is not warned off them.
        /// </summary>
        /// <remarks>
        /// Names are the game's input actions or Keys and buttons rows ("Take", "Interact", "Use"). Clashes with every
        /// other game action and keybind are still reported. Replaces any earlier list; no names clears it.
        /// </remarks>
        public static bool ShareGameActions(string id, params string[] gameActions)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            var shared = (gameActions ?? Array.Empty<string>()).Where(action => !string.IsNullOrWhiteSpace(action))
                .Select(action => action.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            lock (SyncRoot)
            {
                if (!Keybinds.TryGetValue(id, out var existing))
                {
                    return false;
                }

                SetKeybindNoLock(existing.WithSharedGameActions(shared));
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

                Tracks.Remove(id);
                PublishServiceNoLock();
            }

            CancelCapture(id, "That binding is no longer registered.");
            return true;
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

        private static void RegisterNoLock(KeybindSnapshot snapshot)
        {
            Keybinds[snapshot.Id] = WithSavedNoLock(snapshot);
        }

        private static void PublishServiceNoLock()
        {
            Revision++;
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

            if (string.Equals(context, InputContexts.GameControlsOn, StringComparison.OrdinalIgnoreCase)
                || string.Equals(context, InputContexts.GameControlsOff, StringComparison.OrdinalIgnoreCase))
            {
                var off = string.Equals(context, InputContexts.GameControlsOff, StringComparison.OrdinalIgnoreCase);
                if (sonsState.IsInWorld && GameControlsOff() == off)
                {
                    return true;
                }

                blockedReason = !sonsState.IsInWorld ? "Sons world is not active."
                    : off ? "The game's own controls are on." : "The game's own controls are off.";
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

        // Seats and full-screen devices hold the game's input in its Console state, which turns its world actions off.
        private static bool GameControlsOff()
        {
            try
            {
                return global::Sons.Input.InputSystem.GetState(TheForest.Utils.InputState.Console);
            }
            catch (Exception)
            {
                return false;
            }
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
    }

    public static class KeybindIds
    {
        public const string ToggleModsMenu = "openacai.mods.toggle";
        public const string Back = "openacai.ui.back";
        /// <summary>Opens photo mode from the world (F10 by default) and closes it again.</summary>
        public const string PhotoMode = "openacai.photo.toggle";
    }

    public static class InputContexts
    {
        public const string Any = "any";
        public const string Title = "title";
        public const string Loading = "loading";
        public const string GameScene = "game-scene";
        public const string InWorld = "in-world";
        public const string NotInWorld = "not-in-world";
        /// <summary>In the world with the game's own controls live: on foot, not in a seat or full-screen device.</summary>
        public const string GameControlsOn = "game-controls-on";
        /// <summary>
        /// In the world while a seat or full-screen device holds the game's input (its Console state). Only the holder
        /// reads its keybinds then, so they clash with nothing of the game's and with no other mod's held-input keybinds.
        /// </summary>
        public const string GameControlsOff = "game-controls-off";

        public static IReadOnlyList<string> All { get; } = new[]
        {
            Any,
            Title,
            Loading,
            GameScene,
            InWorld,
            NotInWorld,
            GameControlsOn,
            GameControlsOff
        };
    }

    public sealed class KeybindSnapshot
    {
        private KeybindSnapshot(
            string id,
            string displayName,
            string category,
            KeyCode key,
            PadBinding pad,
            KeyCode defaultKey,
            PadBinding defaultPad,
            float holdSeconds,
            IReadOnlyList<string> sharedGameActions,
            bool enabled,
            string context,
            bool suppressWhenModstokeUiVisible,
            string tooltip)
        {
            Id = id;
            SharedGameActions = sharedGameActions;
            DisplayName = displayName;
            Category = category;
            Key = key;
            KeyName = key.ToString();
            Pad = pad;
            DefaultKey = defaultKey;
            DefaultPad = defaultPad;
            HoldSeconds = holdSeconds;
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
        [JsonIgnore]
        public PadBinding Pad { get; }
        /// <summary><see cref="Pad"/> by its button names, as saved: "None", "South" or "LeftShoulder+RightShoulder".</summary>
        public string PadName => Pad.ToString();
        /// <summary>The key the keybind was registered with, which a reset goes back to.</summary>
        public KeyCode DefaultKey { get; }
        [JsonIgnore]
        public PadBinding DefaultPad { get; }
        /// <summary>How long it must be held before it counts as pressed; 0 for a plain press.</summary>
        public float HoldSeconds { get; }
        public bool IsDefault => Key == DefaultKey && Pad == DefaultPad;
        /// <summary>The game actions it stands in for on purpose, which no clash is reported against; see <see cref="InputBindings.ShareGameActions"/>.</summary>
        public IReadOnlyList<string> SharedGameActions { get; }
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
            return Create(id, displayName, category, key, PadBinding.None, 0, enabled, context, suppressWhenModstokeUiVisible, tooltip);
        }

        internal static KeybindSnapshot Create(
            string id,
            string displayName,
            string category,
            KeyCode key,
            PadBinding pad,
            float holdSeconds,
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
                pad,
                key,
                pad,
                holdSeconds,
                Array.Empty<string>(),
                enabled,
                context,
                suppressWhenModstokeUiVisible,
                tooltip);
        }

        internal KeybindSnapshot WithEnabled(bool enabled)
        {
            return new KeybindSnapshot(Id, DisplayName, Category, Key, Pad, DefaultKey, DefaultPad, HoldSeconds, SharedGameActions,
                enabled, Context, SuppressWhenModstokeUiVisible, Tooltip);
        }

        internal KeybindSnapshot WithBinding(KeyCode key, PadBinding pad)
        {
            return new KeybindSnapshot(Id, DisplayName, Category, key, pad, DefaultKey, DefaultPad, HoldSeconds, SharedGameActions,
                Enabled, Context, SuppressWhenModstokeUiVisible, Tooltip);
        }

        internal KeybindSnapshot WithSharedGameActions(IReadOnlyList<string> sharedGameActions)
        {
            return new KeybindSnapshot(Id, DisplayName, Category, Key, Pad, DefaultKey, DefaultPad, HoldSeconds, sharedGameActions,
                Enabled, Context, SuppressWhenModstokeUiVisible, Tooltip);
        }
    }
}