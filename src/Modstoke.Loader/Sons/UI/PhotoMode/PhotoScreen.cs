// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// Photo mode's panel: the settings screen docked at the side over the live world, its tabs for the camera,
    /// lens, look, frame and capture, and Take photo at its foot. The interface can be hidden for a clear view,
    /// leaving only the frame's mask; Back first brings it back, then leaves.
    /// </summary>
    internal sealed class PhotoScreen : SettingsScreen
    {
        private const float KeyDelay = 0.25f;
        private readonly PhotoSession _session;
        private KeyCode _key = KeyCode.None;
        private float _openedAt;
        private bool _hidden, _busy, _leaving;

        private PhotoScreen(PhotoSession session) : base(0) => _session = session;

        /// <summary>
        /// Sets photo mode up and shows its panel; false, with the reason on the pause screen, when it could not.
        /// <paramref name="fromWorld"/> is the photo key's way in, which leaves straight back to the game.
        /// </summary>
        internal static bool Open(bool fromWorld)
        {
            var session = PhotoSession.Begin(fromWorld, out var reason);
            if (session == null)
            {
                PauseScreen.Notice(reason);
                MenuSound.Play(MenuCue.Back);
                return false;
            }
            var screen = new PhotoScreen(session);
            session.Screen = screen;
            try
            {
                FrontEnd.Push(screen);
                return true;
            }
            catch (Exception error)
            {
                if (screen.Root != null && screen.Root) UnityEngine.Object.Destroy(screen.Root.gameObject);
                session.Fail(error);
                return false;
            }
        }

        protected override SettingsLayout Layout => SettingsLayout.Docked;
        protected override IReadOnlyList<string> TabIds => PhotoCatalog.TabIds;
        protected override OptionTab BuildTab(string id) => PhotoCatalog.Tab(id, _session);
        // Online is all the heading has room for; the overlay's hint says the world keeps going.
        protected override string Heading => _session.Online ? Text["photo.title"] + "  ·  " + Text["photo.onlineShort"] : Text["photo.title"];
        protected override string TabName(string id) => Text["photo.tab." + id];
        protected override string SectionName(string id) => Text["photo.section." + id];
        protected override string ItemName(OptionRow row) => Text["photo.item." + row.Id + ".name"];
        protected override string ItemAbout(OptionRow row) => Text["photo.item." + row.Id + ".about"];
        protected override string PrimaryLabel => _session.Busy ? Text["photo.developing"] : Text["photo.capture"];
        protected override string Summary => _session.CaptureNote;
        protected override void Primary() => Shoot();
        protected override void RowChanged(OptionRow row) => _session.RowChanged(row.Id);
        protected override void Save() => _session.Settings.SaveCapture();

        // The world itself is the backdrop; the game's pause blur fades out instead.
        internal override bool Blurs => false;
        internal override bool ShowsPrompts => !_hidden;
        protected override bool Hidden => _hidden;

        internal override IReadOnlyList<PromptSpec> Prompts => new[]
        {
            new PromptSpec(Text["common.back"], "B", "ESC", Leave),
            new PromptSpec(Text["photo.capture"], "Y", "F2", Shoot),
            new PromptSpec(Text["options.reset"], "X", "DEL", OnAlt1),
            new PromptSpec(Text["photo.hide"], "VIEW", "H", () => SetHidden(true))
        };

        /// <summary>Paints the rows again, after something outside the panel changed a setting.</summary>
        internal void Repaint() => Refresh();

        /// <summary>A photo asked for could not be taken after all; the reason shows over Take photo.</summary>
        internal void Refused(string reason)
        {
            if (!string.IsNullOrEmpty(reason)) Problem(reason);
            MenuSound.Play(MenuCue.Back);
        }

        internal void ToggleHidden() => SetHidden(!_hidden);

        // Take photo plays its own sound, the shutter or a refusal, rather than the menu's step forward.
        internal override void OnAlt2() => Shoot();

        internal override void Opened()
        {
            base.Opened();
            _openedAt = Time.unscaledTime;
            // Built again at a new screen size, the panel stays hidden if it was.
            if (_hidden && Group != null && Group) Group.alpha = 0f;
            try
            {
                _key = InputBindings.Snapshot().TryGetValue(KeybindIds.PhotoMode, out var bind) && bind.Enabled ? bind.Key : KeyCode.None;
            }
            catch (Exception)
            {
                _key = KeyCode.None;
            }
        }

        internal override void Closed()
        {
            base.Closed();
            _session.EndNow("its screen closed");
        }

        internal override bool OnBack()
        {
            if (_hidden) SetHidden(false);
            else Leave();
            return true;
        }

        // While the interface is hidden only Back (to show it) and Take photo reach the panel.
        protected override bool CaptureInput()
        {
            if (!_hidden) return base.CaptureInput();
            if (FrontInput.Back) SetHidden(false);
            else if (FrontInput.Alt2) Shoot();
            return true;
        }

        internal override void Tick()
        {
            base.Tick();
            if (_session.Ended)
            {
                // Ended from elsewhere (the pause closing, a fault): back to the pause screen.
                if (_leaving) return;
                _leaving = true;
                FrontEnd.Pop(true);
                return;
            }
            if (_key != KeyCode.None && Time.unscaledTime - _openedAt > KeyDelay && UnityEngine.Input.GetKeyDown(_key))
            {
                Leave();
                return;
            }
            // A click on a prompt belongs to the prompt, as one on the panel does, and does not turn the camera.
            var overPanel = !_hidden && (FrontEnd.OverPrompt
                || PanelRect != null && RectTransformUtility.RectangleContainsScreenPoint(PanelRect, FrontInput.Pointer, null));
            _session.Tick(_hidden, overPanel, Dragging);
            if (_busy == _session.Busy) return;
            _busy = _session.Busy;
            RefreshPrimary();
        }

        // Back from the pause menu's entry returns to the pause screen; from the photo key it returns to the game.
        private void Leave()
        {
            if (_leaving) return;
            _leaving = true;
            var fromWorld = _session.FromWorld;
            _session.EndNow("closed by the player");
            MenuSound.Play(MenuCue.Back);
            if (fromWorld) PauseHost.Resume();
            else FrontEnd.Pop(true);
        }

        // One photo at a time: while one develops the button already says so, and a press is simply refused.
        private void Shoot()
        {
            if (_session.Ended) return;
            if (_session.Busy) MenuSound.Play(MenuCue.Back);
            else if (_session.Shoot(out var reason)) Problem(null);
            else Refused(reason);
            _busy = _session.Busy;
            RefreshPrimary();
        }

        private void SetHidden(bool hidden)
        {
            if (_hidden == hidden) return;
            _hidden = hidden;
            if (Group != null && Group) Group.alpha = hidden ? 0f : 1f;
            MenuSound.Play(hidden ? MenuCue.Off : MenuCue.On);
        }
    }
}
