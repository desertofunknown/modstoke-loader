// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;

namespace Modstoke.Loader
{
    internal enum MenuCue { Move, Forward, Activate, Back, On, Off }

    /// <summary>
    /// The game's own menu sounds (its user_interface bank), played for Modstoke's menus: a tick as focus moves,
    /// one for going forward, one for starting something, one for going back, and the switch sounds.
    /// </summary>
    internal static class MenuSound
    {
        private static readonly string[] Paths =
        {
            "event:/SotF Events/UI/menu_mouseover",
            "event:/SotF Events/UI/menu_forward",
            "event:/SotF Events/UI/menu_activate",
            "event:/SotF Events/UI/menu_back",
            "event:/SotF Events/UI/menu_toggleOn",
            "event:/SotF Events/UI/menu_toggleOff"
        };
        // A held key repeats quickly; its ticks are spaced so they stay ticks rather than a buzz.
        private static readonly float[] Spacing = { 0.06f, 0.08f, 0.15f, 0.08f, 0.06f, 0.06f };
        private static readonly float[] LastPlayed = new float[Paths.Length];
        private static int _failures;

        /// <summary>Goes up with every sound played, so a caller can tell whether an action already made one.</summary>
        internal static int Played { get; private set; }

        internal static void Play(MenuCue cue)
        {
            if (_failures >= 3) return;
            var index = (int)cue;
            var now = Time.unscaledTime;
            if (now - LastPlayed[index] < Spacing[index]) return;
            LastPlayed[index] = now;
            Played++;
            try
            {
                var listener = Camera.main;
                var position = listener != null && listener ? listener.transform.position : Vector3.zero;
                FMODCommon.PlayOneshot(Paths[index], position, FMODCommon.NetworkRole.None, Array.Empty<Il2CppSystem.Object>());
            }
            catch (Exception error)
            {
                // Menus work the same without sound; after a few failures they stop trying.
                if (++_failures == 3) RuntimeEntryPoint.LogSource?.LogWarning("Modstoke's menus play no sounds: " + error.Message);
            }
        }
    }
}
