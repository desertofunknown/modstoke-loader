// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>
    /// The multiplayer lobby, in place of the game's: who is in, with their Steam pictures and an open place for
    /// each free slot, the microphone mode, and for the host the start. A player who joined waits here for the
    /// host. The game's own lobby keeps running underneath and does the work.
    /// </summary>
    internal sealed class LobbyScreen : FrontScreen
    {
        private const float StripHeight = 143, ListTop = 150, RowHeight = 66, RowGap = 4, DetailsWidth = 440;
        private const float RefreshSeconds = 0.5f, AvatarSize = 48;
        private const int MaxSlots = 8;

        private sealed class SlotView
        {
            internal RectTransform Rect;
            internal RawImage Avatar;
            internal Image Placeholder;
            internal TextMeshProUGUI Name, Tag;
            internal Focusable Focus;
            internal LobbyMember Member;
        }

        private readonly List<SlotView> _slots = new();
        private Glide _mark;
        private TextMeshProUGUI _heading, _title, _line, _count, _micValue, _status, _startLabel, _world, _worldLine, _facts;
        private Image _startFill;
        private Focusable _mic, _start;
        private string _shown = "";
        private (string World, string About)? _hostWorld;
        private float _nextRefresh;
        private bool _built;

        protected override void Build()
        {
            _built = false;
            _slots.Clear();
            var pad = U(60);
            MenuKit.Backdrop(Root, U(StripHeight - 24), U(110));
            _heading = MenuKit.Text(Root, "Heading", "", U(14), true, MenuKit.Muted);
            _heading.characterSpacing = 32;
            _heading.rectTransform.At(pad, U(30), U(900), U(18));
            _title = MenuKit.Text(Root, "Title", "", U(50), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _title.characterSpacing = 5;
            _title.rectTransform.At(pad, U(46), U(1100), U(66));
            _line = MenuKit.Text(Root, "Line", "", U(17), false, MenuKit.Soft);
            _line.rectTransform.At(pad, U(112), U(1100), U(26));

            var list = MenuKit.Box(Root, "Players").Place(0, 0, 1, 1, pad, U(StripHeight), pad + U(DetailsWidth + 40), U(ListTop));
            MenuKit.Hairline(list, "Top", 0);
            var label = MenuKit.Text(list, "Label", Text["lobby.players"].ToUpper(Languages.CultureOf(Languages.Current)), U(13), true, MenuKit.Muted);
            label.characterSpacing = 28;
            label.rectTransform.Strip(U(16), U(20), U(16), 0);
            _count = MenuKit.Text(list, "Count", "", U(14), true, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            _count.rectTransform.Strip(U(16), U(20), 0, U(16));
            _mark = new Glide(list, false);
            // The game takes up to eight players; rows past the lobby's own limit stay hidden.
            for (var i = 0; i < MaxSlots; i++) _slots.Add(Slot(list, i));
            new Cascade(_slots.Select(slot => slot.Rect));

            BuildDetails(pad);
            _built = true;
        }

        private SlotView Slot(RectTransform list, int index)
        {
            var rect = MenuKit.Box(list, "Slot " + index).Strip(U(46 + index * (RowHeight + RowGap)), U(RowHeight), 0, U(8));
            var view = new SlotView { Rect = rect };
            var picture = MenuKit.Box(rect, "Avatar").At(U(14), U((RowHeight - AvatarSize) / 2), U(AvatarSize), U(AvatarSize));
            view.Placeholder = MenuKit.Fill(picture, null, MenuKit.Cell);
            var avatar = MenuKit.Box(picture, "Picture").Place(0, 0, 1, 1);
            view.Avatar = avatar.gameObject.AddComponent<RawImage>();
            view.Avatar.raycastTarget = false;
            // Steam's pictures arrive upside down in Unity's texture space.
            view.Avatar.uvRect = new Rect(0, 1, 1, -1);
            view.Name = MenuKit.Text(rect, "Name", "", U(20), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            view.Name.rectTransform.Place(0, 0, 1, 1, U(AvatarSize + 32), 0, U(150), 0);
            view.Tag = MenuKit.Text(rect, "Tag", "", U(14), true, MenuKit.Muted, TextAlignmentOptions.MidlineRight);
            view.Tag.characterSpacing = 14;
            view.Tag.rectTransform.Place(1, 0, 1, 1, -U(150), 0, U(16), 0);
            view.Focus = Add(rect, () => ShowProfile(view), focused => PaintSlot(view, focused), enabled: () => view.Member != null);
            view.Focus.Mark = _mark;
            view.Focus.Flash = Flashing(view.Name);
            return view;
        }

        private void BuildDetails(float pad)
        {
            var details = MenuKit.Box(Root, "Details").Place(1, 0, 1, 1, -(pad + U(DetailsWidth)), U(StripHeight), pad, U(ListTop));
            MenuKit.Hairline(details, "Top", 0);
            var culture = Languages.CultureOf(Languages.Current);

            // What the game is: the world, its mode and how it is reached.
            var game = MenuKit.Text(details, "Game", Text["lobby.game"].ToUpper(culture), U(13), true, MenuKit.Muted);
            game.characterSpacing = 28;
            game.rectTransform.Strip(U(16), U(20), 0, 0);
            _world = MenuKit.Text(details, "World", "", U(24), true, MenuKit.Ink, TextAlignmentOptions.MidlineLeft, true);
            _world.rectTransform.Strip(U(44), U(32), 0, 0);
            _worldLine = MenuKit.Text(details, "WorldLine", "", U(16), false, MenuKit.Soft);
            _worldLine.rectTransform.Strip(U(78), U(24), 0, 0);
            _facts = MenuKit.Text(details, "Facts", "", U(15), false, MenuKit.Muted);
            _facts.rectTransform.Strip(U(102), U(24), 0, 0);

            var voice = MenuKit.Text(details, "Voice", Text["lobby.voice"].ToUpper(culture), U(13), true, MenuKit.Muted);
            voice.characterSpacing = 28;
            voice.rectTransform.Strip(U(148), U(20), 0, 0);
            MenuKit.Hairline(details, "VoiceRule", U(140));
            var mic = MenuKit.Box(details, "Microphone").Strip(U(176), U(48), 0, 0);
            var micLabel = MenuKit.Text(mic, "Label", Text["lobby.microphone"], U(18), false, MenuKit.Soft);
            micLabel.rectTransform.Place(0, 0, 1, 1, U(18), 0, U(200), 0);
            _micValue = MenuKit.Text(mic, "Value", "", U(17), true, MenuKit.Ink, TextAlignmentOptions.MidlineRight);
            _micValue.rectTransform.Place(1, 0, 1, 1, -U(220), 0, U(18), 0);
            var micMark = new Glide(details, false);
            _mic = Add(mic, ToggleMic, focused => micLabel.color = focused ? MenuKit.Ink : MenuKit.Soft);
            _mic.Mark = micMark;
            _mic.Flash = Flashing(micLabel);

            _status = MenuKit.Text(details, "Status", "", U(16), false, MenuKit.Soft, TextAlignmentOptions.BottomLeft);
            _status.enableWordWrapping = true;
            _status.rectTransform.Place(0, 0, 1, 0, 0, U(88), 0, -U(150));
            if (!LobbyHost.IsHost) return;
            var go = MenuKit.Box(details, "Start").Place(0, 0, 1, 0, 0, U(20), 0, -U(78));
            _startFill = MenuKit.Fill(go, MenuKit.Brush, MenuKit.Soft);
            _startLabel = MenuKit.Text(go, "Label", "", U(21), true, MenuKit.Dark, TextAlignmentOptions.Center);
            _startLabel.characterSpacing = 16;
            _startLabel.rectTransform.Place(0, 0, 1, 1);
            _start = Add(go, StartGame, focused => PaintStart(focused), enabled: () => LobbyHost.Ready && !LobbyHost.Starting);
            _start.Flash = Flashing(_startLabel);
        }

        internal override void Retext()
        {
            var culture = Languages.CultureOf(Languages.Current);
            _heading.text = Text[LobbyHost.IsHost ? "lobby.headingHost" : "lobby.headingJoined"].ToUpper(culture);
            if (_startLabel != null) _startLabel.text = Text["custom.start"].ToUpper(culture);
            _shown = "";
            Refresh();
        }

        internal override void Opened()
        {
            Refresh();
            Focus(_start != null && _start.Usable ? _start : _slots.FirstOrDefault(slot => slot.Focus.Usable)?.Focus ?? First());
        }

        // The lobby is the first screen here; Back leaves it, after asking when that ends it for everyone.
        internal override bool OnBack()
        {
            if (LobbyHost.IsHost)
                FrontEnd.Push(new ConfirmScreen("lobby.leaveTitle", "lobby.leaveBody", "lobby.leave", "common.cancel", LobbyHost.Leave, cautious: true));
            else
            {
                MenuSound.Play(MenuCue.Back);
                LobbyHost.Leave();
            }
            return true;
        }

        internal override void OnAlt1()
        {
            var view = _slots.FirstOrDefault(slot => ReferenceEquals(slot.Focus, Focused));
            if (!CanRemove(view)) return;
            var member = view.Member;
            FrontEnd.Push(new ConfirmScreen("lobby.removeTitle", "lobby.removeBody", "lobby.remove", "common.cancel",
                () => LobbyHost.Remove(member.Id), cautious: true));
        }

        internal override void OnAlt2()
        {
            MenuSound.Play(MenuCue.Forward);
            LobbyHost.Invite();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
        {
            get
            {
                var prompts = new List<PromptSpec>
                {
                    new(Text["lobby.leave"], "B", "ESC", () => OnBack()),
                    new(Text["lobby.invite"], "Y", "F2", OnAlt2)
                };
                var focused = _slots.FirstOrDefault(slot => ReferenceEquals(slot.Focus, Focused));
                if (focused?.Member != null) prompts.Add(new PromptSpec(Text["lobby.profile"], "A", "ENTER", null));
                if (CanRemove(focused))
                    prompts.Add(new PromptSpec(Text["lobby.remove"], "X", "DEL", OnAlt1));
                return prompts;
            }
        }

        internal override bool Blurs => false;

        protected override void FocusChanged(Focusable previous, Focusable current) => FrontEnd.RefreshPrompts();

        internal override void Tick()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh();
        }

        private static bool CanRemove(SlotView view) => view?.Member != null && LobbyHost.IsHost && !view.Member.Self;

        private void StartGame()
        {
            if (!LobbyHost.Ready || LobbyHost.Starting) return;
            MenuSound.Play(MenuCue.Activate);
            LobbyHost.Start();
            Refresh();
        }

        private void ToggleMic()
        {
            var wasOn = LobbyHost.MicAlwaysOn;
            LobbyHost.ToggleMic();
            MenuSound.Play(wasOn ? MenuCue.Off : MenuCue.On);
            _mic.Flash?.Pulse();
            Refresh();
        }

        private void Refresh()
        {
            if (!_built) return;
            var culture = Languages.CultureOf(Languages.Current);
            var name = LobbyHost.Name;
            _title.text = (name.Length > 0 ? name : Text["lobby.unnamed"]).ToUpper(culture);
            var members = LobbyHost.Members();
            var limit = Mathf.Clamp(Math.Max(LobbyHost.Limit, members.Count), 1, MaxSlots);
            _count.text = Text.Get("lobby.count", ("count", members.Count), ("limit", limit));
            var open = limit - members.Count;
            _line.text = open > 0 ? Text.Count("lobby.openSlots", open) : Text["lobby.full"];

            // Rows are bound again only when someone joins, leaves or their picture arrives.
            var key = string.Join("|", members.Select(member => member.Id + (member.Avatar != null ? "+" : "-") + member.Name));
            if (key != _shown)
            {
                _shown = key;
                for (var i = 0; i < _slots.Count; i++)
                {
                    var slot = _slots[i];
                    slot.Member = i < members.Count ? members[i] : null;
                    slot.Rect.gameObject.SetActive(i < limit);
                    PaintSlot(slot, ReferenceEquals(slot.Focus, Focused));
                }
                FrontEnd.RefreshPrompts();
            }

            PaintGame();
            _micValue.text = Text[LobbyHost.MicAlwaysOn ? "lobby.micAlwaysOn" : "lobby.micPushToTalk"];
            _status.text = LobbyHost.IsHost
                ? Text[LobbyHost.Starting ? "lobby.starting" : LobbyHost.Ready ? "lobby.ready" : "lobby.creating"]
                : Text[LobbyHost.Joining ? "lobby.joining" : "lobby.waiting"];
            PaintStart(ReferenceEquals(Focused, _start));
        }

        // The host knows its world from its own setup; a player who joined reads what the Steam lobby says.
        private void PaintGame()
        {
            var game = LobbyHost.Game;
            var facts = new List<string>();
            if (LobbyHost.IsHost)
            {
                // Read from the save once; it does not change while the lobby is open.
                _hostWorld ??= HostSetupScreen.Describe();
                _world.text = _hostWorld.Value.World;
                _worldLine.text = _hostWorld.Value.About ?? "";
                facts.Add(Text[LobbyHost.Private ? "host.value.private" : "host.value.public"]);
            }
            else
            {
                _world.text = Text[game.Saved ? "lobby.savedWorld" : "lobby.newWorld"];
                _worldLine.text = game.Mode.Length > 0 ? HostSetupScreen.ModeName(game.Mode) : "";
            }
            if (game.Password) facts.Add(Text["lobby.password"]);
            _facts.text = string.Join(" · ", facts);
        }

        private void ShowProfile(SlotView view)
        {
            if (view.Member == null) return;
            MenuSound.Play(MenuCue.Forward);
            try
            {
                LobbyHost.ShowProfile(view.Member.Id);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Steam's overlay could not show the profile: " + error.Message);
            }
        }

        private void PaintSlot(SlotView view, bool focused)
        {
            var member = view.Member;
            view.Avatar.texture = member?.Avatar;
            view.Avatar.enabled = member?.Avatar != null;
            view.Placeholder.color = member == null ? MenuKit.Faded(0.04f) : MenuKit.Cell;
            view.Name.text = member?.Name ?? Text["lobby.openSlot"];
            view.Name.color = member == null ? MenuKit.Faint : focused ? MenuKit.Ink : MenuKit.Soft;
            view.Tag.text = member == null ? "" : (member.Host ? Text["lobby.host"] : member.Self ? Text["lobby.you"] : "")
                .ToUpper(Languages.CultureOf(Languages.Current));
        }

        private void PaintStart(bool focused)
        {
            if (_startFill == null) return;
            var usable = LobbyHost.Ready && !LobbyHost.Starting;
            _startFill.color = !usable ? MenuKit.Disabled : focused ? MenuKit.Ink : MenuKit.Soft;
            _startLabel.color = usable ? MenuKit.Dark : MenuKit.Faint;
        }
    }
}
