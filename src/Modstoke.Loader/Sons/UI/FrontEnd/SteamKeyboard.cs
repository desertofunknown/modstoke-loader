// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Steam.VirtualKeyboard;
using Steamworks;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Steam's own gamepad keyboard, where Steam shows one: on a Steam Deck and in Big Picture mode. The result comes
    /// back through the game's own keyboard component, which holds the Steam callback, writes what was typed into its
    /// field and puts the game's input state back, so nothing here has to receive a callback from Steam itself.
    /// </summary>
    internal static class SteamKeyboard
    {
        // Steam needs a limit; a field without one gets a generous line.
        private const int Unlimited = 256;
        private static GameObject _holder;
        private static VirtualKeyboardField _component;
        private static TMP_InputField _field;
        private static IntPtr _callback;
        private static bool _failed;

        /// <summary>
        /// Opens the keyboard over the game with <paramref name="text"/> to edit, taking up to <paramref name="maxLength"/>
        /// characters (0 for no limit); false where Steam has none to show, or one is up already.
        /// </summary>
        internal static bool TryOpen(string text, int maxLength, bool secret, string title)
        {
            if (_failed || !Offered() || VirtualKeyboardField.GamepadTextInputDismissed_Callback != null) return false;
            try
            {
                Prepare();
                _field.SetText(text ?? "", false);
                _component.RegisterCallback();
                var shown = SteamUtils.ShowGamepadTextInput(
                    secret ? EGamepadTextInputMode.k_EGamepadTextInputModePassword : EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
                    EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine, title ?? "", (uint)(maxLength > 0 ? maxLength : Unlimited),
                    text ?? "");
                if (!shown)
                {
                    VirtualKeyboardField.DisposeCallback();
                    return false;
                }
                _callback = VirtualKeyboardField.GamepadTextInputDismissed_Callback?.Pointer ?? IntPtr.Zero;
                // As the game's own fields do: its controls rest until the dismissal handler turns this off again.
                global::Sons.Input.InputSystem.SetState(InputState.VirtualKeyboard, true);
                return true;
            }
            catch (Exception error)
            {
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Steam's on-screen keyboard could not open; the menus use their own: " + error.Message);
                return false;
            }
        }

        /// <summary>Whether the keyboard opened last is still up.</summary>
        internal static bool Open
        {
            get
            {
                var callback = VirtualKeyboardField.GamepadTextInputDismissed_Callback;
                return callback != null && callback.Pointer == _callback;
            }
        }

        /// <summary>
        /// Gives up on a keyboard Steam never closed: its callback goes, Steam is asked to put it away, and the game's
        /// controls come back as the dismissal handler would have brought them.
        /// </summary>
        internal static void Abandon()
        {
            if (!Open) return;
            try
            {
                VirtualKeyboardField.DisposeCallback();
                SteamUtils.DismissGamepadTextInput();
                global::Sons.Input.InputSystem.SetState(InputState.VirtualKeyboard, false);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Steam's on-screen keyboard could not be put away: " + error.Message);
            }
        }

        /// <summary>What the keyboard left once it closed: the typed text, or the text it opened with if the player backed out.</summary>
        internal static string Text => _field != null && _field ? _field.text ?? "" : "";

        // Steam answers false on the desktop outside Big Picture, where its keyboard would not appear.
        private static bool Offered()
        {
            try
            {
                return SteamUtils.IsSteamRunningOnSteamDeck() || SteamUtils.IsSteamInBigPictureMode();
            }
            catch (Exception)
            {
                // Steam is not running for this game.
                return false;
            }
        }

        // A field of the game's own kind, never shown, for the dismissal handler to write into.
        private static void Prepare()
        {
            if (_holder != null && _holder) return;
            _holder = new GameObject("Modstoke Steam Keyboard");
            _holder.SetActive(false);
            Object.DontDestroyOnLoad(_holder);
            _field = _holder.AddComponent<TMP_InputField>();
            _component = _holder.AddComponent<VirtualKeyboardField>();
            _component._field = _field;
        }
    }
}
