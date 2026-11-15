// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Gui;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Clears the frame for photo mode: the pause menu's blur fades out, the game's screen-space canvases
    /// (subtitles, chat, notices, the pause panel and any mod's overlay) are switched off without deactivating
    /// anything, the low-health and drowning tints can be held back, and online the name tags go. Every change
    /// is recorded in the session's undo log and put back only where it is still as photo mode left it.
    /// </summary>
    internal sealed class PhotoUiHider
    {
        private const float FadeSeconds = 0.3f, SweepSeconds = 0.5f;
        // The front end draws at 4500; anything above (the mods menu, the console) keeps its own say.
        private const int TopOrder = 4500;
        private readonly PhotoUndo _undo;
        private readonly List<Volume> _tints = new();
        private Volume _pauseBlur;
        private float _pauseWeight, _fade, _nextSweep;
        private bool _logged, _tintsHeld;

        internal PhotoUiHider(PhotoUndo undo) => _undo = undo;

        /// <summary>Whether the pause menu's blur has faded out of the frame altogether.</summary>
        internal bool Clear => _pauseBlur == null || _fade >= 1f;

        internal void Begin(bool online)
        {
            HoldPauseBlur();
            FindTints();
            Sweep();
            if (online) HideNameTags();
        }

        internal void Tick(float dt, bool hideTints)
        {
            if (_pauseBlur != null && _fade < 1f)
            {
                _fade = Mathf.Min(1f, _fade + dt / FadeSeconds);
                if (Live(_pauseBlur)) _pauseBlur.weight = _fade >= 1f ? 0f : _pauseWeight * (1f - Moving.EaseOut(_fade));
            }
            if (hideTints != _tintsHeld) HoldTints(hideTints);
            if (Time.unscaledTime >= _nextSweep) Sweep();
        }

        // The pause menu's own depth of field blurs the whole frame; it eases away so the world comes into focus.
        private void HoldPauseBlur()
        {
            var menu = PauseHost.Current;
            if (menu == null) return;
            foreach (var volume in menu.GetComponentsInChildren<Volume>(true))
            {
                if (!Live(volume) || !volume.name.Contains("PauseMenuPostProcessingVolume", StringComparison.Ordinal)) continue;
                _pauseBlur = volume;
                _pauseWeight = volume.weight;
                var held = volume;
                var weight = volume.weight;
                _undo.Add("the pause menu's blur", () =>
                {
                    if (Live(held)) held.weight = weight;
                });
                return;
            }
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode found no pause menu blur to fade.");
        }

        private void FindTints()
        {
            foreach (var effect in Object.FindObjectsOfType<global::Sons.LowHealthEffect>())
                if (Live(effect) && Live(effect._targetVolume)) _tints.Add(effect._targetVolume);
            foreach (var effect in Object.FindObjectsOfType<global::Sons.DrowningEffect>())
                if (Live(effect) && Live(effect._targetVolume)) _tints.Add(effect._targetVolume);
            _undo.Add("the low-health and drowning tints", () => HoldTints(false));
        }

        // The game writes these volumes' weights from the player's state, so they are switched off instead, which
        // also leaves the time of day's own colour filter alone.
        private readonly List<Volume> _heldTints = new();

        private void HoldTints(bool held)
        {
            _tintsHeld = held;
            if (held)
            {
                foreach (var volume in _tints)
                {
                    if (!Live(volume) || !volume.enabled) continue;
                    volume.enabled = false;
                    _heldTints.Add(volume);
                }
                return;
            }
            foreach (var volume in _heldTints)
                if (Live(volume) && !volume.enabled) volume.enabled = true;
            _heldTints.Clear();
        }

        // Root canvases drawn over the screen, the game's and mods' alike. New ones are caught as they appear.
        private void Sweep()
        {
            _nextSweep = Time.unscaledTime + SweepSeconds;
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
            var names = _logged ? null : new List<string>();
            foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            {
                if (!Live(canvas) || !canvas.enabled || !canvas.isRootCanvas || canvas.renderMode == RenderMode.WorldSpace) continue;
                var transform = canvas.transform;
                if (transform.Pointer == own.Pointer || canvas.sortingOrder > TopOrder) continue;
                if (modal != null && modal && modal.IsChildOf(transform)) continue;
                canvas.enabled = false;
                names?.Add(canvas.name);
                var held = canvas;
                _undo.Add("the " + canvas.name + " canvas", () =>
                {
                    if (Live(held) && !held.enabled) held.enabled = true;
                });
            }
            if (names == null) return;
            _logged = true;
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode put away " + names.Count + " screen canvas(es): " + string.Join(", ", names) + ".");
        }

        private void HideNameTags()
        {
            try
            {
                var before = UiManager._playerNameTagsUiOffUserOverride;
                UiManager.SetShowPlayerNameTagsUiOffUserOverride(true);
                _undo.Add("the name tags", () => UiManager.SetShowPlayerNameTagsUiOffUserOverride(before));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not hide name tags: " + error.Message);
            }
        }

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;
    }
}
