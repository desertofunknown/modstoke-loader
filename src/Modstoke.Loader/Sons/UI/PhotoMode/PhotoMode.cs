// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using Sons.Cutscenes;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Photo mode: a free camera inside the pause menu, with lens, colour and framing controls and full-size
    /// captures. The game's own camera is moved only while each frame renders, so gameplay never sees it, and
    /// everything parented to that camera (a held tablet, overlays, the rebreather) is hidden from the shot
    /// automatically. A mod that renders through a camera of its own takes the view, and photo mode then
    /// refuses to open.
    /// </summary>
    public static class PhotoMode
    {
        /// <summary>True from the moment photo mode has set itself up until it has put everything back.</summary>
        public static bool Active { get; private set; }

        /// <summary>
        /// True while photo mode is open in single player, where the pause holds the world still. World effects
        /// should freeze rather than hide while this is set, so they are in the shot. Online the world keeps
        /// going and this stays false.
        /// </summary>
        public static bool WorldFrozen { get; private set; }

        /// <summary>Raised on the game thread when photo mode opens (true) and once everything is back (false).</summary>
        public static event Action<bool> Changed;

        internal static void Set(bool active, bool frozen)
        {
            frozen &= active;
            if (Active == active && WorldFrozen == frozen) return;
            Active = active;
            WorldFrozen = frozen;
            var handlers = Changed;
            if (handlers == null) return;
            foreach (var handler in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<bool>)handler)(active);
                }
                catch (Exception error)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("A photo mode listener failed: " + error.Message);
                }
            }
        }
    }
}

namespace Modstoke.Loader
{
    /// <summary>
    /// Whether photo mode can open now, and its keyboard entry from the world. After repeated failures in one
    /// world it stays off until the next game is loaded.
    /// </summary>
    internal static class PhotoGate
    {
        private const int FailureLimit = 2;
        private const float CacheSeconds = 0.25f;
        private static int _failures;
        private static bool _subscribed, _open;
        private static float _checkedAt = -1;
        private static string _reason;

        /// <summary>
        /// Whether photo mode can open from the pause menu; <paramref name="reason"/> says why not, for the player.
        /// The answer is kept for a moment, as the pause menu asks every frame; <paramref name="fresh"/> asks again now.
        /// </summary>
        internal static bool CanOpen(out string reason, bool fresh = false)
        {
            var now = Time.unscaledTime;
            if (fresh || _checkedAt < 0 || now - _checkedAt >= CacheSeconds || now < _checkedAt)
            {
                _checkedAt = now;
                _open = Check(true, out var key);
                _reason = key == null ? null : FrontEnd.Text[key];
            }
            reason = _reason;
            return _open;
        }

        /// <summary>A session ended on a fault; enough of them switch photo mode off for this world.</summary>
        internal static void Failed()
        {
            _failures++;
            _checkedAt = -1;
        }

        /// <summary>The photo mode key, from the world: opens the game's pause menu and photo mode straight over it.</summary>
        internal static void PollHotkey()
        {
            Subscribe();
            if (PhotoSession.Current != null || !InputBindings.IsPressed(KeybindIds.PhotoMode)) return;
            if (ConsoleOverlay.IsVisible || MenuController.IsVisible || !FrontEnd.Enabled) return;
            var menu = PauseHost.Current;
            if (menu == null) return;
            try
            {
                if (menu._isActive || menu._isPauseMenuBlocked) return;
                if (!Check(false, out var key))
                {
                    RuntimeEntryPoint.LogSource?.LogInfo("Photo mode did not open: " + FrontEnd.Text[key]);
                    return;
                }
                FrontEnd.OverPause(() => PhotoScreen.Open(fromWorld: true));
                menu.Open();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not open the pause menu: " + error.Message);
            }
        }

        // A new world starts with photo mode available again and its look back at the game's.
        private static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            Modstoke.Sons.GameRuntime.SceneChanged += _ =>
            {
                PhotoSession.EndCurrent("the scene changed");
                _failures = 0;
                _checkedAt = -1;
                PhotoSettings.Forget();
            };
        }

        // Null key when it can open; otherwise the text key that says why not.
        private static bool Check(bool paused, out string key)
        {
            key = "photo.unavailable.view";
            try
            {
                if (_failures >= FailureLimit)
                {
                    key = "photo.unavailable.failed";
                    return false;
                }
                if (GameBuild.IsDedicatedServer || !FrontEnd.Enabled || paused && !PauseHost.Open) return false;
                var camera = LocalPlayer.MainCam;
                if (camera == null || !camera || !camera.isActiveAndEnabled) return false;
                // A vehicle or another mod rendering through its own camera owns the view.
                var main = Camera.main;
                if (main == null || !main || main.Pointer != camera.Pointer)
                {
                    key = "photo.unavailable.camera";
                    return false;
                }
                if (LocalPlayer.CurrentView != TheForest.Items.Inventory.PlayerInventory.PlayerViews.World) return false;
                if (CutsceneManager.IsCutscenePlaying())
                {
                    key = "photo.unavailable.cutscene";
                    return false;
                }
                var stats = LocalPlayer.Stats;
                if (stats != null && stats && (stats._dyingState != PlayerStats.DyingState.Alive || stats._playerIsDowned)) return false;
                if (FrontEnd.ModalShowing) return false;
                key = null;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
