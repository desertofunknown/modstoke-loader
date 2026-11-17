// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Gui;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The root canvases drawn flat over the screen: the game's HUD, hints, subtitles, chat, notices and pause panel,
    /// and mods' overlays. Modstoke's own full-screen surfaces sort at the front end's order or above and are left alone.
    /// </summary>
    internal static class ScreenCanvases
    {
        /// <summary>The front end's sorting order; the mods menu, loading view, screen host and console draw above it.</summary>
        internal const int FrontEndOrder = 4500;

        /// <summary>
        /// Switches off every enabled root screen canvas sorting at or below <paramref name="topOrder"/>, except the front
        /// end and the game's modal dialogs, and hands each to <paramref name="hidden"/>. Nothing is deactivated, so the
        /// game's own scripts keep running.
        /// </summary>
        internal static void HideBelow(int topOrder, Action<Canvas> hidden)
        {
            var own = FrontEnd.Canvas();
            Transform modal = null;
            try
            {
                var manager = ModalDialogManager._instance;
                if (manager != null && manager) modal = manager.transform;
            }
            catch (Exception)
            {
                // Then no dialog needs keeping.
            }
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!Live(canvas) || !canvas.enabled || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace) continue;
                var transform = canvas.transform;
                if (transform.Pointer == own.Pointer || canvas.sortingOrder > topOrder) continue;
                if (modal != null && modal && modal.IsChildOf(transform)) continue;
                canvas.enabled = false;
                hidden(canvas);
            }
        }

        internal static bool Live(Object value) => value is not null && !value.WasCollected && value;
    }

    /// <summary>
    /// Keeps the game's HUD off the screen while the mods menu is open over the world, where hint popups, the minimap
    /// and notices would otherwise read through its veil. Only canvases it switched off are put back, and only while
    /// they are still off.
    /// </summary>
    internal static class MenuHudHold
    {
        private const float SweepSeconds = 0.5f;
        private static readonly List<Canvas> Held = new();
        private static readonly Action<Canvas> Hold = Held.Add;
        private static bool _active;
        private static float _nextSweep;

        internal static void Begin()
        {
            _active = true;
            Sweep();
        }

        /// <summary>Catches canvases that appear, and ones the game switches back on, while the menu stays open.</summary>
        internal static void Tick()
        {
            if (!_active) return;
            for (var i = Held.Count - 1; i >= 0; i--)
            {
                var canvas = Held[i];
                if (!ScreenCanvases.Live(canvas)) Held.RemoveAt(i);
                else if (canvas.enabled) canvas.enabled = false;
            }
            if (Time.unscaledTime >= _nextSweep) Sweep();
        }

        internal static void End()
        {
            if (!_active) return;
            _active = false;
            foreach (var canvas in Held)
                if (ScreenCanvases.Live(canvas) && !canvas.enabled) canvas.enabled = true;
            Held.Clear();
        }

        private static void Sweep()
        {
            _nextSweep = Time.unscaledTime + SweepSeconds;
            try
            {
                ScreenCanvases.HideBelow(ScreenCanvases.FrontEndOrder, Hold);
            }
            catch (Exception error)
            {
                // The menu still works over a visible HUD, so what was held goes back until it next opens.
                End();
                RuntimeEntryPoint.LogSource?.LogWarning("The mods menu could not put the game's HUD away: " + error.Message);
            }
        }
    }
}
