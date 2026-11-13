// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using Sons.Gui;
using Sons.Save;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Modstoke's own menus, drawn on their own screen layer over the game's title scene: a stack of screens, one
    /// way of moving focus for mouse, keyboard and controller, a prompt bar that shows the device in use, and
    /// every text from the localization SDK. The game's own menus stay underneath as the way games are started,
    /// and come back whole with classicMenus=true in the Loader settings or if this layer ever fails.
    /// </summary>
    internal static class FrontEnd
    {
        /// <summary>Layouts are written as if the screen were this tall and scaled to the real height.</summary>
        internal const float DesignHeight = 860f;
        // Under the mods menu (4600), so it opens over these screens.
        private const int SortingOrder = 4500;
        private const float PromptHeight = 44f, TagHeight = 24f;

        private sealed class PromptView
        {
            internal RectTransform Rect;
            internal (RectTransform Rect, TextMeshProUGUI Label) Tag;
            internal TextMeshProUGUI Label;
            internal PromptSpec Spec;
        }

        private static readonly List<FrontScreen> Stack = new();
        private static readonly List<PromptView> PromptViews = new();
        private static GameObject _root;
        private static RectTransform _rect, _prompts;
        private static int _width, _height, _stackLayout;
        private static bool _failed, _pad, _retext, _subscribed, _promptsDirty, _shown, _hostWanted;
        private static long _nextSettingRead;
        private static bool _classic;
        private static float _asideUntil, _hostWantedAt;
        private static PromptView _hoverPrompt;

        internal static float S { get; private set; } = 1f;
        internal static float U(float design) => design * S;
        internal static Strings Text => LocalizationStartup.Text;
        internal static bool PadActive => _pad;

        /// <summary>Goes up when the screen size changes, which every layout on this layer is scaled from.</summary>
        internal static int LayoutVersion { get; private set; }

        /// <summary>False with classicMenus=true (or the older classicMultiplayerMenu) in the Loader settings.</summary>
        internal static bool Enabled
        {
            get
            {
                var now = Environment.TickCount64;
                if (now < _nextSettingRead) return !_classic && !_failed;
                _nextSettingRead = now + 5000;
                _classic = RuntimePreferences.ReadBoolean("classicMenus", false)
                    || RuntimePreferences.ReadBoolean("classicMultiplayerMenu", false);
                return !_classic && !_failed;
            }
        }

        /// <summary>The layer every Modstoke menu draws on, created on first use.</summary>
        internal static RectTransform Canvas()
        {
            if (_root != null && _root) return _rect;
            _root = new GameObject("Modstoke Front End");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            _rect = _root.GetComponent<RectTransform>();
            Measure();
            return _rect;
        }

        internal static void Push(FrontScreen screen)
        {
            var canvas = Canvas();
            if (Stack.Count > 0) Stack[Stack.Count - 1].Root.gameObject.SetActive(false);
            else _stackLayout = LayoutVersion;
            screen.Create(canvas);
            Stack.Add(screen);
            screen.Opened();
            screen.Appear();
            FrontInput.Pause();
            _promptsDirty = true;
        }

        internal static void Pop()
        {
            // The first screen is the title menu, which has nowhere to go back to.
            if (Stack.Count <= 1) return;
            Remove(Stack.Count - 1);
            var top = Stack[Stack.Count - 1];
            top.Root.gameObject.SetActive(_shown);
            top.DeviceChanged();
            top.Appear();
            FrontInput.Pause(0.15f);
            _promptsDirty = true;
        }

        /// <summary>A screen's prompts changed, for instance with what is selected.</summary>
        internal static void RefreshPrompts() => _promptsDirty = true;

        /// <summary>
        /// Hides this layer while the game loads what was just asked for (a save, another scene), so the menu
        /// is not left drawn over the game's fade. It returns by itself if nothing ends up loading.
        /// </summary>
        internal static void StepAside(float seconds = 6f) => _asideUntil = Time.unscaledTime + seconds;

        /// <summary>Opens the Host screen as soon as the title scene is back, for Host chosen on the Join screen.</summary>
        internal static void HostWhenTitleLoads()
        {
            _hostWanted = true;
            _hostWantedAt = Time.unscaledTime;
        }

        internal static void CancelHost() => _hostWanted = false;

        internal static void Tick()
        {
            if (GameBuild.IsDedicatedServer || _failed) return;
            try
            {
                Run();
            }
            catch (Exception error)
            {
                // The game's own menus come back, so a fault here never leaves the player without a menu.
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogError("Modstoke's menus stopped and the game's own are back: " + error);
                Shutdown();
            }
        }

        private static void Run()
        {
            var title = GameMenus.Bind();
            if (!Enabled)
            {
                if (Stack.Count > 0 || _shown) Shutdown();
                return;
            }
            if (!_subscribed)
            {
                _subscribed = true;
                Languages.Changed += () => _retext = true;
            }
            Measure();
            if (!title)
            {
                // Screens belong to the title scene; elsewhere only the layer itself stays for other menus.
                if (Stack.Count > 0) Clear();
                Show(false);
                _asideUntil = 0;
                return;
            }

            // The game's bar stays hidden: every way into its menus is on Modstoke's screens.
            GameMenus.HideBar(true);
            GameMenus.CloseReplaced();
            // The game's own pages and dialogs are under this layer, so it steps aside while one is up.
            if (GameMenus.DialogOpen || ModalOpen() || GameMenus.OtherSceneOpen || Time.unscaledTime < _asideUntil)
            {
                Show(false);
                FrontInput.Pause();
                return;
            }
            Canvas();
            if (Stack.Count > 0 && _stackLayout != LayoutVersion) Rebuild();
            if (Stack.Count == 0) Push(new TitleScreen());
            if (_hostWanted)
            {
                // A request the title screen never picked up must not fire on some later visit.
                var fresh = Time.unscaledTime - _hostWantedAt < 30f;
                _hostWanted = false;
                if (fresh && Stack.Count == 1) Push(new SavedGamesScreen(SaveGameType.Multiplayer));
            }
            Show(true);
            GameMenus.ClearSelection();

            var top = Stack[Stack.Count - 1];
            var pad = FrontInput.PadActive;
            if (pad != _pad)
            {
                _pad = pad;
                _promptsDirty = true;
                top.DeviceChanged();
            }
            if (_retext)
            {
                _retext = false;
                foreach (var screen in Stack) screen.Retext();
                _promptsDirty = true;
            }
            if (_promptsDirty) BuildPrompts(top);
            top.Animate();

            // The mods menu and the console draw over this layer and take input while open.
            if (MenuController.IsVisible || ConsoleOverlay.IsVisible)
            {
                FrontInput.Pause();
                return;
            }
            FrontInput.Read();
            if (!PromptInput()) top.HandleInput();
            if (Stack.Count > 0) Stack[Stack.Count - 1].Tick();
        }

        private static bool ModalOpen()
        {
            try
            {
                var manager = ModalDialogManager._instance;
                var current = manager != null && manager ? manager.Current : null;
                return current != null && current && current.gameObject.activeInHierarchy;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Follows the screen size, which every layout is scaled from.
        private static void Measure()
        {
            if (Screen.width == _width && Screen.height == _height) return;
            _width = Screen.width;
            _height = Screen.height;
            S = Mathf.Max(0.25f, _height / DesignHeight);
            LayoutVersion++;
        }

        private static void Rebuild()
        {
            _stackLayout = LayoutVersion;
            foreach (var screen in Stack)
            {
                if (screen.Root != null && screen.Root) Object.Destroy(screen.Root.gameObject);
                screen.Create(_rect);
                screen.Root.gameObject.SetActive(false);
            }
            var top = Stack[Stack.Count - 1];
            top.Root.gameObject.SetActive(true);
            // Focus goes back where the screen keeps it.
            top.Opened();
            top.Root.gameObject.SetActive(_shown);
            if (_prompts != null && _prompts) Object.Destroy(_prompts.gameObject);
            _prompts = null;
            _promptsDirty = true;
        }

        private static void Show(bool shown)
        {
            if (_shown == shown) return;
            _shown = shown;
            if (Stack.Count > 0) Stack[Stack.Count - 1].Root.gameObject.SetActive(shown);
            if (_prompts != null && _prompts) _prompts.gameObject.SetActive(shown);
            if (shown) FrontInput.Pause();
        }

        private static void Remove(int index)
        {
            var screen = Stack[index];
            Stack.RemoveAt(index);
            try { screen.Closed(); }
            catch (Exception error) { RuntimeEntryPoint.LogSource?.LogWarning("A menu screen did not close cleanly: " + error.Message); }
            if (screen.Root != null && screen.Root) Object.Destroy(screen.Root.gameObject);
        }

        private static void Clear()
        {
            for (var i = Stack.Count - 1; i >= 0; i--) Remove(i);
            if (_prompts != null && _prompts) Object.Destroy(_prompts.gameObject);
            _prompts = null;
            PromptViews.Clear();
        }

        private static void Shutdown()
        {
            try
            {
                Clear();
                _shown = false;
                GameMenus.HideBar(false);
            }
            catch (Exception)
            {
                // Nothing further to undo.
            }
        }

        // The prompt bar over the game's bottom strip: what each button or key does on the top screen.
        private static void BuildPrompts(FrontScreen top)
        {
            _promptsDirty = false;
            if (_prompts != null && _prompts) Object.Destroy(_prompts.gameObject);
            PromptViews.Clear();
            _hoverPrompt = null;
            // On the line the game's own choices sat on, and clear of its logo.
            float left = U(60), right = U(60), middle = U(52);
            if (GameMenus.TryBar(out _, out var barMiddle) && barMiddle > U(30) && barMiddle < U(150)) middle = barMiddle;
            if (GameMenus.TryLogo(out var logoLeft, out var logoRight))
            {
                if ((logoLeft + logoRight) / 2f > _width / 2f) right = Mathf.Max(right, _width - logoLeft + U(30));
                else left = Mathf.Max(left, logoRight + U(30));
            }
            var bottom = middle - U(PromptHeight) / 2f;
            _prompts = MenuKit.Box(_rect, "Prompts").Place(0, 0, 1, 0, left, bottom, right, -(bottom + U(PromptHeight)));
            _prompts.gameObject.SetActive(_shown);
            float fromLeft = 0, fromRight = 0;
            foreach (var spec in top.Prompts)
            {
                var key = _pad ? spec.Pad : spec.Key;
                // A prompt with no button on this device is not shown.
                if (key == null) continue;
                var view = new PromptView { Rect = MenuKit.Box(_prompts, spec.Label), Spec = spec };
                view.Tag = MenuKit.Tag(view.Rect, "Key", key, U(TagHeight));
                view.Label = MenuKit.Text(view.Rect, "Label", spec.Label.ToUpper(Languages.CultureOf(Languages.Current)), U(19), true, MenuKit.Soft);
                view.Label.characterSpacing = 14;
                var tagWidth = view.Tag.Rect.sizeDelta.x;
                var labelWidth = view.Label.GetPreferredValues(view.Label.text).x + U(4);
                var width = tagWidth + U(10) + labelWidth;
                view.Tag.Rect.At(0, (U(PromptHeight) - U(TagHeight)) / 2, tagWidth, U(TagHeight));
                view.Label.rectTransform.At(tagWidth + U(10), 0, labelWidth, U(PromptHeight));
                // Back sits on the left as in the game; everything else on the right.
                if (PromptViews.Count == 0)
                {
                    view.Rect.At(fromLeft, 0, width, U(PromptHeight));
                    fromLeft += width + U(38);
                }
                else
                {
                    view.Rect.AtRight(fromRight, 0, width, U(PromptHeight));
                    fromRight += width + U(38);
                }
                PromptViews.Add(view);
            }
        }

        // Prompts are clickable; true when a click was used here.
        private static bool PromptInput()
        {
            PromptView over = null;
            foreach (var view in PromptViews)
                if (view.Rect && RectTransformUtility.RectangleContainsScreenPoint(view.Rect, FrontInput.Pointer, null))
                    over = view;
            if (!ReferenceEquals(over, _hoverPrompt))
            {
                if (_hoverPrompt != null && _hoverPrompt.Label) _hoverPrompt.Label.color = MenuKit.Soft;
                _hoverPrompt = over;
                if (over != null) over.Label.color = MenuKit.Ink;
            }
            if (!FrontInput.Click || over?.Spec.Click == null) return false;
            over.Spec.Click();
            return true;
        }
    }
}
