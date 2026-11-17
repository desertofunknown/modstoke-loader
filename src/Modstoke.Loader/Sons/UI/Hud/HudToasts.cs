// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Sons;
using Sons.Gui;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The game's message list at the bottom left drawn as Modstoke toasts: tutorial hints with the key they name,
    /// pickups, "can't carry" lines and every general message, mods' SonsTools.ShowMessage among them. The game still
    /// decides what shows, for how long and in what order: its list keeps running under a CanvasGroup that hides it, and
    /// each toast follows one of its entries. The toasts keep out of photo mode, the pause and mods menus and dialogs, and
    /// go with the list when the game hides it. With classicMenus, or after a failure, the game's own list shows again.
    /// </summary>
    internal static class HudToasts
    {
        // With mods' HUDs, under the front end's 4500, so photo mode's and the mods menu's sweeps put the toasts away too.
        private const int SortingOrder = 120;
        // Design units from the lower left: the microphone's margin, and clear of a line of subtitles and the skip prompt.
        private const float Left = 34f, Bottom = 112f;
        private const float DeviceSeconds = 0.25f, MeasureSeconds = 0.5f, DialogSeconds = 0.25f;
        private static readonly List<Toast> Toasts = new();
        private static int _installationAttempted;
        private static GameObject _canvas;
        private static RectTransform _root;
        private static Transform _list;
        private static CanvasGroup _veil;
        private static Canvas _hudCanvas;
        private static float _unit, _last, _nextDevice, _nextMeasure, _nextDialog;
        private static bool _dirty, _failed, _pad, _shown = true, _dialog, _described;

        internal static void InstallHarmonyPatch(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0 || GameBuild.IsDedicatedServer) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                warning?.Invoke("HUD toasts are unavailable, so the game's own hints show: Harmony support is not ready.");
                return;
            }
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-hud-toasts");
            try
            {
                var shown = new HarmonyMethod(typeof(HudToasts), nameof(EntryShown));
                foreach (var name in new[] { nameof(AnimateSidebarElement.OnEnable), nameof(AnimateSidebarElement.SlideIn) })
                    harmony.Patch(AccessTools.Method(typeof(AnimateSidebarElement), name, Type.EmptyTypes)
                        ?? throw new MissingMethodException(nameof(AnimateSidebarElement), name), postfix: shown);
            }
            catch (Exception error)
            {
                harmony.UnpatchSelf();
                warning?.Invoke("HUD toasts are unavailable, so the game's own hints show: " + error.Message);
            }
        }

        /// <summary>Runs every frame; returns at once while no toast is up and nothing new was shown.</summary>
        internal static void Tick()
        {
            if (_failed || !_dirty && Toasts.Count == 0) return;
            try
            {
                if (!FrontEnd.Enabled)
                {
                    Release();
                    return;
                }
                if (_dirty) Scan();
                if (Toasts.Count > 0) Step();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        // Any of the game's sidebar entries switched on (the HUD coming back included), or slid back in while still on, as
        // another pickup of the same item does. The list goes out of sight here, before the frame the entry would first be
        // drawn in; its toast follows on the next tick.
        private static void EntryShown()
        {
            if (_failed) return;
            _dirty = true;
            try
            {
                Adopt();
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }

        // Hides the HUD's list under a CanvasGroup of its own, once per HUD; false while toasts can't be drawn.
        private static bool Adopt()
        {
            if (_list != null && _list && _veil != null && _veil) return true;
            _list = null;
            if (GameBuild.IsDedicatedServer || !FrontEnd.Enabled || !MenuKit.FontsReady) return false;
            var hud = HudGui._instance;
            var list = hud != null && hud ? hud._taskMessagesContainer : null;
            var canvas = list != null && list ? list.GetComponentInParent<Canvas>() : null;
            if (canvas == null || !canvas) return false;
            _hudCanvas = canvas.rootCanvas;
            _veil = list.gameObject.AddComponent<CanvasGroup>();
            _veil.alpha = 0f;
            _veil.interactable = false;
            _veil.blocksRaycasts = false;
            _list = list;
            if (!_described)
            {
                _described = true;
                RuntimeEntryPoint.LogSource?.LogInfo("HUD toasts draw the game's message list (" + list.childCount + " entries).");
            }
            return true;
        }

        // Every entry the game shows now; called when one was shown, so it never runs on a quiet frame.
        private static void Scan()
        {
            _dirty = false;
            if (!Adopt()) return;
            var root = Root();
            Measure();
            var now = Time.unscaledTime;
            _pad = FrontInput.PadActive;
            var count = _list.childCount;
            for (var i = 0; i < count; i++)
            {
                var entry = _list.GetChild(i);
                if (!entry.gameObject.activeSelf) continue;
                var toast = Find(entry);
                if (toast != null)
                {
                    toast.Refresh(now);
                    continue;
                }
                // An entry this can't draw would be invisible under the veil, so the game's list comes back whole.
                var motion = entry.GetComponentInChildren<AnimateSidebarElement>(true);
                if (motion == null) throw new InvalidOperationException("the entry \"" + entry.name + "\" has no sidebar animation");
                // One still on but already sliding out gets no toast: it would only fade at once. If the game slides
                // it back in, that comes here again.
                if (!motion._isActive) continue;
                toast = Toast.Create(root, entry, motion, _unit, _pad, now)
                    ?? throw new InvalidOperationException("the entry \"" + entry.name + "\" has no text where the game's entries keep it");
                Toasts.Add(toast);
            }
        }

        private static Toast Find(Transform entry)
        {
            foreach (var toast in Toasts)
                if (toast.Entry.Pointer == entry.Pointer) return toast;
            return null;
        }

        private static void Step()
        {
            var now = Time.unscaledTime;
            var dt = Math.Min(now - _last, 0.1f);
            _last = now;
            if (now >= _nextDevice)
            {
                _nextDevice = now + DeviceSeconds;
                _pad = FrontInput.PadActive;
            }
            if (now >= _nextMeasure)
            {
                _nextMeasure = now + MeasureSeconds;
                Measure();
            }
            Show(!Covered(now));
            Sort();
            // From the bottom up: the game's list ends at its foot.
            var y = 0f;
            for (var i = Toasts.Count - 1; i >= 0; i--)
            {
                var toast = Toasts[i];
                if (!toast.Step(now, dt, _pad))
                {
                    toast.Destroy();
                    Toasts.RemoveAt(i);
                    continue;
                }
                y += toast.Place(y);
            }
        }

        // The game's order, first entry on top; it changes only when an entry is added, so this rarely moves anything.
        private static void Sort()
        {
            for (var i = 1; i < Toasts.Count; i++)
            {
                var toast = Toasts[i];
                var j = i - 1;
                for (; j >= 0 && Toasts[j].Sibling > toast.Sibling; j--) Toasts[j + 1] = Toasts[j];
                Toasts[j + 1] = toast;
            }
        }

        // Photo mode, the menus and dialogs over the world, the game's own HUD switches, and its HUD canvas swept off by
        // anyone. A game dialog is looked for a few times a second only, as that is a call into the game.
        private static bool Covered(float now)
        {
            if (PhotoMode.Active || GameRuntime.State.PauseMenuOpen || MenuController.IsVisible || ScreenHost.IsOpen
                || SystemDialogs.Open || UiManager._allOffUserOverride || UiManager._hudOffUserOverride
                || !Toast.Alive(_hudCanvas) || !_hudCanvas.isActiveAndEnabled)
                return true;
            if (now >= _nextDialog)
            {
                _nextDialog = now + DialogSeconds;
                _dialog = FrontEnd.ModalShowing;
            }
            return _dialog;
        }

        private static void Show(bool shown)
        {
            if (shown == _shown) return;
            _shown = shown;
            _root.gameObject.SetActive(shown);
            // Bindings may have changed in the menu that was over the toasts.
            if (shown)
                foreach (var toast in Toasts) toast.Rekey(_pad);
        }

        // Scaled as the front end is, its menu size included, and laid out again when that changes.
        private static void Measure()
        {
            var unit = FrontEnd.S;
            if (unit == _unit) return;
            _unit = unit;
            _root.anchoredPosition = new Vector2(Left * unit, Bottom * unit);
            foreach (var toast in Toasts) toast.Resize(unit);
        }

        private static RectTransform Root()
        {
            if (_root != null && _root) return _root;
            if (_canvas == null || !_canvas)
            {
                _canvas = new GameObject("Modstoke Toasts");
                Object.DontDestroyOnLoad(_canvas);
                var canvas = _canvas.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = SortingOrder;
            }
            _root = MenuKit.Box(_canvas.transform, "Toasts");
            _root.anchorMin = _root.anchorMax = _root.pivot = Vector2.zero;
            _root.sizeDelta = Vector2.zero;
            _shown = true;
            _unit = 0f;
            return _root;
        }

        // The game's own list shows again and every toast goes at once.
        private static void Release()
        {
            foreach (var toast in Toasts) toast.Destroy();
            Toasts.Clear();
            if (_veil != null && _veil)
            {
                _veil.alpha = 1f;
                Object.Destroy(_veil);
            }
            _veil = null;
            _list = null;
            _hudCanvas = null;
            _dirty = false;
        }

        private static void Fail(Exception error)
        {
            _failed = true;
            RuntimeEntryPoint.LogSource?.LogWarning("HUD toasts stopped and the game's own hints are back: " + error);
            try
            {
                Release();
            }
            catch (Exception)
            {
                // Nothing further to undo.
            }
        }
    }
}
