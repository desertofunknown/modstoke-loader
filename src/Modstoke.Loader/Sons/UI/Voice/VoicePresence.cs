// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Sons;
using Sons.Gui;
using Sons.Multiplayer.Gui;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Voice on screen: bars beside a talking player's own name tag, and the local microphone on the HUD. The game pools
    /// its name tags and hands them from player to player as they come into view, so a lit mark follows whichever tag the
    /// talking player's link holds this frame and gives it up as soon as the link lets go. The marks hang on the tag's
    /// name text, so they hide with it: crouching, the name tags option, photo mode.
    /// </summary>
    internal static class VoicePresence
    {
        private const float RiseSeconds = 0.12f, FallSeconds = 0.35f, FindSeconds = 1f;
        private static readonly List<Speaker> Speakers = new();
        private static readonly List<TagMark> Marks = new();
        private static float _last;
        private static bool _failed, _described, _textless;

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer || _failed) return;
            var now = PlayerVoice.Now;
            var dt = Math.Min(now - _last, 0.1f);
            _last = now;
            try
            {
                MicIndicator.Tick(now, dt);
                var talkers = PlayerVoice.Talkers;
                if (talkers.Count == 0)
                {
                    if (Speakers.Count > 0) Clear();
                    return;
                }
                bool? hidden = null;
                foreach (var talker in talkers)
                {
                    var speaker = SpeakerFor(talker);
                    if (speaker == null) continue;
                    var speaking = talker.Speaking;
                    speaker.Level = VoiceBars.Step(speaker.Level, speaking ? 1f : 0f, dt / (speaking ? RiseSeconds : FallSeconds));
                    if (speaker.Level > 0f && !(hidden ??= TagsHidden())) Show(speaker, now);
                    else Release(speaker);
                }
            }
            catch (Exception error)
            {
                // The game's name tags and HUD carry on without the marks.
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Voice marks stopped: " + error);
                Clear();
                MicIndicator.Close();
            }
        }

        // Photo mode, and the game's switches for name tags and its in-world interface; the marks belong to the tags.
        private static bool TagsHidden()
            => PhotoMode.Active || UiManager._playerNameTagsUiOffUserOverride || UiManager._inWorldUiOffUserOverride
                || UiManager._allOffUserOverride;

        private static Speaker SpeakerFor(PlayerVoice.Talker talker)
        {
            foreach (var speaker in Speakers)
                if (ReferenceEquals(speaker.Talker, talker)) return speaker;
            if (!talker.Speaking) return null;
            var created = new Speaker(talker);
            Speakers.Add(created);
            return created;
        }

        private static void Show(Speaker speaker, float now)
        {
            var tag = Link(speaker, now)?._playerNameLinkedUi;
            if (tag is null)
            {
                Release(speaker);
                return;
            }
            var mark = speaker.Mark;
            try
            {
                if (mark == null || mark.Owner.Pointer != tag.Pointer || !ReferenceEquals(mark.For, speaker))
                {
                    Release(speaker);
                    if (!tag) return;
                    mark = MarkFor(tag);
                    if (mark == null) return;
                    // A pooled tag shows one player at a time; whoever had it last has already lost it.
                    if (mark.For != null) Release(mark.For);
                    if (!mark.Attach(speaker)) return;
                    speaker.Mark = mark;
                }
                mark.Paint(VoiceBars.Smooth(speaker.Level), now);
            }
            catch (Exception)
            {
                // The tag was destroyed under its link, as a world ends; its mark goes with it.
                if (mark != null) Marks.Remove(mark);
                speaker.Mark = null;
            }
        }

        private static void Release(Speaker speaker)
        {
            if (speaker.Mark == null) return;
            if (ReferenceEquals(speaker.Mark.For, speaker)) speaker.Mark.Hide();
            speaker.Mark = null;
        }

        // The talking player's name tag link, looked up once they talk and checked again each second, since a
        // character can be replaced under the same voice.
        private static PlayerNameUiLink Link(Speaker speaker, float now)
        {
            if (now < speaker.NextFind) return speaker.Link;
            speaker.NextFind = now + FindSeconds;
            if (speaker.Link != null && speaker.Link) return speaker.Link;
            try
            {
                var entity = BoltNetwork.FindEntity(new Bolt.NetworkId(speaker.Talker.Id));
                speaker.Link = entity != null && entity ? entity.GetComponentInChildren<PlayerNameUiLink>(true) : null;
            }
            catch (Exception)
            {
                // Bolt is between sessions; the next look is a second away.
                speaker.Link = null;
            }
            return speaker.Link;
        }

        private static TagMark MarkFor(PlayerName tag)
        {
            for (var i = Marks.Count - 1; i >= 0; i--)
            {
                var mark = Marks[i];
                if (mark.Owner.Pointer == tag.Pointer) return mark;
                if (!mark.Live) Marks.RemoveAt(i);
            }
            var created = TagMark.Create(tag);
            if (created != null) Marks.Add(created);
            else if (!_textless)
            {
                _textless = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Voice marks found no name text on the game's name tag.");
            }
            return created;
        }

        private static void Clear()
        {
            foreach (var speaker in Speakers) Release(speaker);
            Speakers.Clear();
        }

        private sealed class Speaker
        {
            internal readonly PlayerVoice.Talker Talker;
            internal PlayerNameUiLink Link;
            internal TagMark Mark;
            internal float Level, NextFind;

            internal Speaker(PlayerVoice.Talker talker) => Talker = talker;
        }

        // The bars on one of the game's pooled name tags. The mark stays on its tag between players and is laid out
        // again beside the name of whoever it is lit for.
        private sealed class TagMark
        {
            private const float CheckSeconds = 0.1f;
            private readonly TMP_Text[] _texts;
            private readonly GameObject[] _objects;
            private RectTransform _root;
            private CanvasGroup _group;
            private VoiceBars _bars;
            private int _anchor = -1;
            private float _nextCheck;
            private bool _shown;

            private TagMark(PlayerName owner, List<TMP_Text> texts)
            {
                Owner = owner;
                _texts = texts.ToArray();
                _objects = new GameObject[_texts.Length];
                for (var i = 0; i < _texts.Length; i++) _objects[i] = _texts[i].gameObject;
            }

            internal PlayerName Owner { get; }
            internal Speaker For { get; private set; }
            internal bool Live => Owner != null && Owner;

            // The tag's full name and the initials it shows from far away, in the game's order.
            internal static TagMark Create(PlayerName owner)
            {
                var texts = new List<TMP_Text>();
                Collect(owner._targetText, texts);
                Collect(owner._firstCharacterText, texts);
                return texts.Count == 0 ? null : new TagMark(owner, texts);
            }

            private static void Collect(Il2CppSystem.Collections.Generic.List<TMP_Text> list, List<TMP_Text> into)
            {
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    var text = list[i];
                    if (text != null && text) into.Add(text);
                }
            }

            internal bool Attach(Speaker speaker)
            {
                For = speaker;
                var showing = Showing();
                Place(showing >= 0 ? showing : Math.Max(_anchor, 0));
                return _root is not null;
            }

            internal void Paint(float level, float now)
            {
                // The game swaps a tag's near and far names with distance; the mark moves to the one showing.
                if (now >= _nextCheck)
                {
                    _nextCheck = now + CheckSeconds;
                    var showing = Showing();
                    if (showing >= 0 && showing != _anchor) Place(showing);
                }
                if (_root is null) return;
                if (!_shown)
                {
                    _shown = true;
                    _root.gameObject.SetActive(true);
                }
                _group.alpha = level;
                _bars.Paint(level, now);
            }

            internal void Hide()
            {
                For = null;
                if (!_shown) return;
                _shown = false;
                if (_root is not null && _root) _root.gameObject.SetActive(false);
            }

            private int Showing()
            {
                for (var i = 0; i < _objects.Length; i++)
                    if (_objects[i].activeInHierarchy) return i;
                return -1;
            }

            // Just left of the name as drawn, whatever the text's alignment, sized to its line, with a dark soft glow
            // under it so it reads over snow as well as at night.
            private void Place(int index)
            {
                if (_root is not null && _root) Object.Destroy(_root.gameObject);
                _root = null;
                _anchor = index;
                var text = _texts[index];
                if (text == null || !text) return;
                var rect = text.rectTransform.rect;
                var size = text.GetPreferredValues(text.text);
                var width = rect.width > 0 ? Math.Min(size.x, rect.width) : size.x;
                var line = size.y > 0 ? Math.Min(size.y, text.fontSize * 1.5f) : text.fontSize;
                var align = (int)text.alignment;
                var center = rect.center;
                var left = (align & 0x1) != 0 ? rect.xMin : (align & 0x4) != 0 ? rect.xMax - width : center.x - width / 2f;
                var middle = (align & 0x100) != 0 ? rect.yMax - line / 2f : (align & 0x400) != 0 ? rect.yMin + line / 2f : center.y;
                var height = line * 0.62f;
                _root = MenuKit.Box(text.rectTransform, "Modstoke voice");
                _root.anchorMin = _root.anchorMax = new Vector2(0.5f, 0.5f);
                _root.pivot = new Vector2(1f, 0.5f);
                _group = _root.gameObject.AddComponent<CanvasGroup>();
                _group.alpha = 0f;
                _bars = new VoiceBars(_root, "Bars", height);
                _bars.Tint(MenuKit.Green);
                _root.sizeDelta = new Vector2(_bars.Width, height);
                _root.anchoredPosition = new Vector2(left - line * 0.3f - center.x, middle - center.y);
                MenuKit.GlowBehind(_root, height * 0.6f, 0.5f);
                var layer = text.gameObject.layer;
                foreach (var child in _root.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
                _shown = true;
                if (_described) return;
                _described = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Voice marks sit on the game's name tags, beside \"" + text.name + "\" ("
                    + _texts.Length + " name text(s), alignment " + text.alignment + ").");
            }
        }
    }
}
