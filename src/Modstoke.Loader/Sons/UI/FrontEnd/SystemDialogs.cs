// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Localization;
using Modstoke.Sons;
using Sons.Gui;
using Sons.Input;
using SonsSdk;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Draws the game's modal dialogs and mods' SonsSdk dialogs in the front end's design, on a layer of their own over
    /// every other Modstoke surface, so a dialog looks the same at the title, over the pause menu, the mods menu or
    /// photo mode, and in the world. While one is up it has the input: the screens under it stand still, mods'
    /// keybinds wait, and the game sits in its own modal-dialog input state. With classicMenus, or if this ever fails,
    /// dialogs are drawn as the game draws them.
    /// </summary>
    internal static class SystemDialogs
    {
        // Over the mods menu (4600), the loading view (4550) and mods' own screens (4700); the console (4800) stays on top.
        private const int SortingOrder = 4750;
        private static GameObject _root, _capture;
        private static RectTransform _rect;
        private static DialogScreen _screen;
        private static SystemDialog _shown;
        private static ModDialog _mod;
        private static NoticeDialog _notice;
        private static DynamicModalDialogController _gameStyle;
        private static readonly List<DynamicModalDialogGui> Concealed = new();
        private static ModalDialogManager _showing;
        private static string _language;
        private static int _shows, _layout, _dismissedFrame = -1;
        private static bool _hooked, _failed, _pad;

        /// <summary>Whether a system dialog has the screen and the input.</summary>
        internal static bool Open => _shown != null;

        /// <summary>
        /// Whether a dialog has the keys this frame: while one is up, and through the frame it was answered in, so the
        /// press that answered it reaches nothing that reads its keys later in that frame.
        /// </summary>
        internal static bool Holding => _shown != null || _dismissedFrame == Time.frameCount;

        /// <summary>Whether a game dialog is up in the game's own style, which the front end steps aside for.</summary>
        internal static bool GameStyleOpen => _failed ? FrontEnd.ModalShowing : Live(_gameStyle) && _gameStyle.gameObject.activeInHierarchy;

        private static bool Takes => !_failed && !GameBuild.IsDedicatedServer && FrontEnd.Enabled;

        /// <summary>Runs from the front end each frame before its own screens: follows, draws and answers dialogs.</summary>
        internal static void Tick(bool fontsReady)
        {
            if (_failed) return;
            try
            {
                if (!_hooked) Hook();
                Follow();
                if (_shown != null) Drive(fontsReady);
                // The game's input state goes back a frame after the answer, so the game never acts on its key either.
                else if (_dismissedFrame != Time.frameCount) Capture(false);
            }
            catch (Exception error)
            {
                // The game draws its dialogs again, so a fault here never leaves the player facing a hidden one.
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogError("Dialogs are back in the game's own style: " + error);
                Release();
            }
        }

        /// <summary>
        /// A mod opened a dialog through the SonsSdk shim; false when the front end isn't drawing, and the shim then
        /// shows its game-styled copy.
        /// </summary>
        internal static bool ShowMod(GenericModalDialog dialog, string title, string message)
        {
            if (!Takes) return false;
            _mod = new ModDialog(dialog, title, message);
            return true;
        }

        internal static void ModOption(GenericModalDialog dialog, int index, string text)
        {
            if (_mod != null && _mod.Dialog.Pointer == dialog.Pointer) _mod.SetOption(index, text);
        }

        internal static void CloseMod(GenericModalDialog dialog)
        {
            if (_mod == null || _mod.Dialog.Pointer != dialog.Pointer) return;
            if (ReferenceEquals(_shown, _mod)) Dismiss();
            _mod = null;
        }

        /// <summary>Shows a notice of Modstoke's own, with one answer that closes it; false when the front end isn't drawing.</summary>
        internal static bool Notify(string title, string body, DialogTone tone)
        {
            if (!Takes) return false;
            _notice = new NoticeDialog(title, body, tone, () => _notice = null);
            return true;
        }

        /// <summary>The player answered. The dialog goes first, so an answer that opens another dialog shows that one next.</summary>
        internal static void Answer(DialogChoice choice)
        {
            Dismiss();
            try
            {
                choice.Run();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A dialog answer did not complete: " + error.Message);
            }
        }

        /// <summary>The front end stopped: dialogs go back to the game's style, a mod's open one included.</summary>
        internal static void Release()
        {
            try
            {
                // The layer goes at once rather than fading: after a front-end failure nothing ticks a fade any more,
                // and a dialog left on screen, or one answered a moment ago and still fading, would catch every click.
                _screen = null;
                if (Live(_root)) Object.Destroy(_root);
                _root = null;
                _rect = null;
                if (_shown == null && _mod == null && _notice == null && Concealed.Count == 0 && !(Live(_capture) && _capture.activeSelf)) return;
                var mod = _mod;
                if (_shown != null) Dismiss();
                Capture(false);
                _mod = null;
                _notice = null;
                _gameStyle = null;
                foreach (var gui in Concealed) Reveal(gui);
                Concealed.Clear();
                if (mod != null && Live(mod.Dialog))
                {
                    mod.Dialog.gameObject.SetActive(true);
                    mod.Dialog._canvas.overrideSorting = true;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A dialog could not be handed back to the game: " + error.Message);
            }
        }

        // The game's dialog comes first: it holds the game's own state while it is up. A mod's waits under it, and a
        // notice of Modstoke's own under both.
        private static void Follow()
        {
            var game = CurrentGame(out var manager);
            if (game == null)
            {
                _gameStyle = null;
                var wanted = (SystemDialog)_mod ?? _notice;
                if (ReferenceEquals(_shown, wanted)) return;
                if (_shown != null) Dismiss();
                if (wanted != null) Present(wanted);
                return;
            }
            if (_shown is GameDialog shown && shown.Is(game, _shows) || Live(_gameStyle) && _gameStyle.Pointer == game.Pointer) return;
            _gameStyle = null;
            GameDialog dialog;
            try
            {
                dialog = new GameDialog(manager, game, _shows);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A game dialog is shown in the game's own style: " + error.Message);
                if (_shown is GameDialog) Dismiss();
                _gameStyle = game;
                Reveal(game._gui);
                return;
            }
            Conceal(game._gui);
            Present(dialog);
        }

        private static void Present(SystemDialog dialog)
        {
            if (_shown != null) Dismiss();
            _shown = dialog;
            FrontInput.Pause();
            FrontInput.Captured = true;
            // Applied again for each dialog: a game dialog that closed has cleared the state for itself.
            Capture(false);
            Capture(true);
        }

        private static void Dismiss()
        {
            _shown = null;
            _dismissedFrame = Time.frameCount;
            _screen?.Leave(true);
            _screen = null;
            FrontInput.Captured = false;
            // The press that answered must not act on the screen under the dialog as well.
            FrontInput.Pause(0.15f);
        }

        private static void Drive(bool fontsReady)
        {
            var dialog = _shown;
            var changed = dialog.Refresh();
            if (_screen == null)
            {
                // Not in Unity's plain font while the game's own is a moment away; the game's dialog stays hidden meanwhile.
                if (!fontsReady) return;
                _screen = new DialogScreen(dialog);
                Build();
                _screen.Appear();
            }
            else if (_layout != FrontEnd.LayoutVersion || changed && dialog.Shape != _screen.Shape) Build();
            else if (changed || Languages.Current != _language)
            {
                _language = Languages.Current;
                _screen.Retext();
            }
            var pad = FrontInput.PadActive;
            if (pad != _pad)
            {
                _pad = pad;
                _screen.DeviceChanged();
            }
            // The pause menu and the lobby blur the world themselves.
            var state = GameRuntime.State;
            if (FrontEnd.AtTitle || state.IsInWorld && !state.PauseMenuOpen) WorldBlur.Want(true);
            var screen = _screen;
            screen.Animate();
            // The console draws over the dialog and takes the keyboard while open.
            if (ConsoleOverlay.IsVisible) return;
            // Nothing under the dialog keeps a selection that Enter or the arrow keys would act on, as a mods menu row.
            GameMenus.ClearSelection();
            FrontInput.Read();
            if (!screen.PromptInput()) screen.HandleInput();
            if (ReferenceEquals(screen, _screen)) screen.Tick();
        }

        // Built again for a new screen size or when the dialog gains or loses a part, keeping its arrival fade going.
        private static void Build()
        {
            if (_screen.Root != null && _screen.Root) Object.Destroy(_screen.Root.gameObject);
            _screen.Create(Canvas());
            _screen.Opened();
            _layout = FrontEnd.LayoutVersion;
            _language = Languages.Current;
            _pad = FrontInput.PadActive;
        }

        private static RectTransform Canvas()
        {
            if (_root != null && _root) return _rect;
            _root = new GameObject("Modstoke Dialogs");
            Object.DontDestroyOnLoad(_root);
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            // Only so the dialog's dim stops clicks meant for the mods menu or a mod's screen under it.
            _root.AddComponent<GraphicRaycaster>();
            _rect = _root.GetComponent<RectTransform>();
            return _rect;
        }

        // The title and the multiplayer menu each have a dialog manager of their own; the one that showed a dialog last
        // is asked first, then the game's current one.
        private static DynamicModalDialogController CurrentGame(out ModalDialogManager manager)
        {
            manager = _showing;
            var current = Shown(manager);
            if (current != null) return current;
            manager = ModalDialogManager._instance;
            return Shown(manager);
        }

        private static DynamicModalDialogController Shown(ModalDialogManager manager)
        {
            var current = Live(manager) ? manager.Current : null;
            return Live(current) && current.gameObject.activeInHierarchy ? current : null;
        }

        // The game's dialog stays up, out of sight and out of reach of clicks and of its own Back and Submit handling.
        private static void Conceal(DynamicModalDialogGui gui)
        {
            if (!Live(gui)) return;
            // One per manager; a scene's manager goes with its scene.
            Concealed.RemoveAll(held => !Live(held));
            if (!Concealed.Exists(held => held.Pointer == gui.Pointer)) Concealed.Add(gui);
            var canvas = gui.GetComponent<Canvas>();
            if (Live(canvas)) canvas.enabled = false;
            var group = Group(gui);
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;
        }

        private static void Reveal(DynamicModalDialogGui gui)
        {
            if (!Live(gui)) return;
            var canvas = gui.GetComponent<Canvas>();
            if (Live(canvas)) canvas.enabled = true;
            var group = Group(gui);
            group.alpha = 1f;
            group.interactable = true;
            group.blocksRaycasts = true;
        }

        private static CanvasGroup Group(Component owner)
        {
            var group = owner.GetComponent<CanvasGroup>();
            return Live(group) ? group : owner.gameObject.AddComponent<CanvasGroup>();
        }

        // The game's modal-dialog input state and its cursor, through the same components its own interfaces use.
        private static void Capture(bool on)
        {
            if (on && !Live(_capture))
            {
                _capture = new GameObject("Modstoke Dialog Input");
                _capture.SetActive(false);
                Object.DontDestroyOnLoad(_capture);
                _capture.AddComponent<InputActionMapState>()._applyState = InputState.ModalDialog;
                var cursor = _capture.AddComponent<InputCursorState>();
                cursor._enabled = true;
                cursor._hardwareCursor = true;
                cursor._priority = 100;
            }
            if (!Live(_capture) || _capture.activeSelf == on) return;
            _capture.SetActive(on);
            // As the game's own dialogs do on closing, the menu's state goes on again over what the dialog's took off.
            if (!on && InputSystem.GetState(InputState.Menu))
            {
                InputSystem.SetState(InputState.Menu, false);
                InputSystem.SetState(InputState.Menu, true);
            }
        }

        // Hides each game dialog in the call that shows it, so its own style is never drawn for a frame.
        private static void Hook()
        {
            _hooked = true;
            try
            {
                var target = AccessTools.Method(typeof(ModalDialogManager), nameof(ModalDialogManager.Show_Internal),
                    new[] { typeof(DynamicModalDialogController), typeof(Il2CppReferenceArray<Il2CppSystem.Object>), typeof(string), typeof(bool) })
                    ?? throw new MissingMethodException("The game's dialog method was not found.");
                new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-system-dialogs")
                    .Patch(target, postfix: new HarmonyMethod(typeof(SystemDialogs), nameof(AfterShow)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A game dialog may show in the game's style for a frame: " + error.Message);
            }
        }

        // Inside the game's own call, so it never throws.
        private static void AfterShow(ModalDialogManager __instance, DynamicModalDialogController __result)
        {
            try
            {
                if (__result == null || !__result || !Takes) return;
                _shows++;
                _showing = __instance;
                Conceal(__result._gui);
            }
            catch (Exception)
            {
                // Then the dialog is hidden on the next frame instead.
            }
        }

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;
    }
}
