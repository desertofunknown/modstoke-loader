// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using Sons.Gui;
using Sons.Gui.Multiplayer.Notifications;
using Sons.Multiplayer;
using TMPro;

namespace Modstoke.Sons
{
    internal enum KickTitle { ModsDontMatch, CouldNotJoin }

    /// <summary>
    /// Shows the reason a server wrote into a kick. The game receives it in the kick token, but its kick popup on
    /// the title screen and the server browser's status bar only show a generic localized line.
    /// </summary>
    internal static class KickReason
    {
        private const double ShowSeconds = 120;
        private static string _expected, _detail, _title, _reason;
        private static bool _expectedWins;
        private static double _expectedUntil, _reasonUntil;
        private static bool _hooked, _logged;
        private static DynamicModalDialogController _dialog;
        private static UnityEngine.Localization.Components.LocalizeStringEvent _paused, _pausedTitle;

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>
        /// From a client that already knows why it will be refused: the popup's title, the reason it expects, shown
        /// if the kick's own text never arrives, and a detail line added to whichever reason is shown.
        /// </summary>
        /// <remarks>replacesServerText is for a reason this client built from the same facts the server used: it is
        /// shown in the player's language in place of the server's.</remarks>
        internal static void Expect(KickTitle title, string reason, string detail, bool replacesServerText = false)
        {
            _title = LocalizationStartup.Text.Get(title == KickTitle.ModsDontMatch
                ? "kick.title.modsDontMatch" : "kick.title.couldNotJoin");
            _expected = reason;
            _detail = detail;
            _expectedWins = replacesServerText;
            _expectedUntil = Now + ShowSeconds;
            _reason = Join(reason, detail);
            _reasonUntil = _expectedUntil;
            _logged = false;
        }

        // A reason belongs to the connection that expected or received it; the next start never shows it for another
        // server. Not on shutdown, which comes before the title screen shows the popup.
        internal static void ResetForStart()
        {
            Release();
            _expected = _detail = _title = _reason = null;
            _expectedWins = false;
            _kickedAt = double.MinValue;
        }

        private static string Join(string reason, string detail) => detail == null ? reason : reason + "\n\n" + detail;

        private static double _kickedAt = double.MinValue;

        internal static void Tick()
        {
            if (!_hooked)
            {
                _hooked = true;
                if (!Host.IsServer) Hook();
                return;
            }
            if (_reason == null) return;
            try
            {
                if (_dialog == null)
                {
                    // The popup opens after the title screen loads, by a path that may skip the hooked method.
                    if (Now >= _reasonUntil || Now < _nextLook) return;
                    _nextLook = Now + 0.2;
                    var manager = ModalDialogManager._instance;
                    if (manager == null || !manager) return;
                    foreach (var popup in new[] { manager.KickedFromGameDialog, manager.BannedFromGameDialog })
                        if (popup != null && popup && popup.gameObject.activeInHierarchy)
                        {
                            AfterDialog(popup);
                            break;
                        }
                    return;
                }
                if (!_dialog || !_dialog.gameObject.activeInHierarchy || Now >= _reasonUntil)
                {
                    Release();
                    _reason = null;
                }
                else
                {
                    // Localization can finish after the popup opens and write its generic lines again.
                    if (_dialog._gui.ContentText is { } text && text && text.text != _reason) text.text = _reason;
                    if (_title != null && TitleText(_dialog._gui) is { } title && title.text != _title) title.text = _title;
                }
            }
            catch (Exception)
            {
                Release();
            }
        }

        private static double _nextLook;

        private static void Hook()
        {
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-kick-reason");
            // Each kick path the game has; whichever runs first keeps the reason.
            Patch(harmony, AccessTools.Method(typeof(CoopClientCallbacks), "HandleKickDisconnect", new[] { typeof(CoopKickToken) }),
                prefix: nameof(BeforeKick));
            Patch(harmony, AccessTools.Method(typeof(global::Sons.Multiplayer.Dedicated.DedicatedServerBrowser),
                nameof(global::Sons.Multiplayer.Dedicated.DedicatedServerBrowser.OnKickedOrBanned), new[] { typeof(CoopKickToken) }),
                prefix: nameof(BeforeKick));
            Patch(harmony, AccessTools.Method(typeof(global::Sons.Gui.Multiplayer.CoopLobbyDialogGui),
                nameof(global::Sons.Gui.Multiplayer.CoopLobbyDialogGui.OnKickedOrBanned), new[] { typeof(CoopKickToken) }),
                prefix: nameof(BeforeKick));
            Patch(harmony, AccessTools.Method(typeof(ModalDialogManager), nameof(ModalDialogManager.Show_Internal),
                new[] { typeof(ModalDialogManager.Dialog), typeof(Il2CppReferenceArray<Il2CppSystem.Object>), typeof(string), typeof(bool) }),
                postfix: nameof(AfterDialog));
            Patch(harmony, AccessTools.Method(typeof(ModalDialogManager), nameof(ModalDialogManager.Show_Internal),
                new[] { typeof(DynamicModalDialogController), typeof(Il2CppReferenceArray<Il2CppSystem.Object>), typeof(string), typeof(bool) }),
                postfix: nameof(AfterDialog));
            Patch(harmony, AccessTools.Method(typeof(GameServerBrowserStatusBar), nameof(GameServerBrowserStatusBar.SetText),
                new[] { typeof(GameServerBrowserStatusBar.State), typeof(Il2CppReferenceArray<Il2CppSystem.Object>) }),
                postfix: nameof(AfterStatusText));
        }

        private static void Patch(HarmonyLib.Harmony harmony, System.Reflection.MethodInfo target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null) throw new MissingMethodException("The kick screen method was not found.");
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(typeof(KickReason), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(KickReason), postfix));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Kick reasons may show as a generic line: " + error.Message);
            }
        }

        private static void BeforeKick(CoopKickToken __0)
        {
            try
            {
                var message = __0?.KickMessage?.Trim();
                var written = !string.IsNullOrEmpty(message) && !IsNativeKey(message) ? message : null;
                var expected = _expected != null && Now < _expectedUntil;
                // Several kick paths can run for one kick; the first one keeps the reason.
                if (written == null && !expected || _reason != null && Now < _kickedAt + 5) return;
                _reason = !expected ? written
                    : written == null || _expectedWins ? Join(_expected, _detail) : Join(written, _detail);
                if (!expected) _title = null;
                _expected = _detail = null;
                _kickedAt = Now;
                _reasonUntil = Now + ShowSeconds;
                _logged = false;
                RuntimeEntryPoint.LogSource?.LogInfo("Kicked by the server" + (written == null ? "" : ": " + written)
                    + (_reason == written ? "" : ". Showing: " + _reason.Replace("\n\n", " ")));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The kick reason could not be read: " + error.Message);
            }
        }

        // A native kick sends a Multiplayer localization key such as HOST_BANNED_YOU. Anything else was written for
        // players, in the server's language, which need not separate words with spaces.
        private static bool IsNativeKey(string message)
        {
            foreach (var character in message)
                if (character is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or '_')) return false;
            return true;
        }

        private static void AfterDialog(DynamicModalDialogController __result)
        {
            try
            {
                if (_reason == null || Now >= _reasonUntil || __result == null || !__result) return;
                var manager = ModalDialogManager._instance;
                if (manager == null || !Same(__result, manager.KickedFromGameDialog) && !Same(__result, manager.BannedFromGameDialog))
                    return;
                var gui = __result._gui;
                if (gui == null || !gui || gui.ContentText == null || !gui.ContentText) return;
                Release();
                // The localized line rewrites the text on its next refresh, so it pauses while the reason shows.
                if (gui.ContentTextString is { } localized && localized)
                {
                    localized.enabled = false;
                    _paused = localized;
                }
                // The game titles this popup "Admin"; a refusal this game expected names what happened instead.
                if (_title != null && TitleText(gui) is { } title)
                {
                    gui.TitleTextString.enabled = false;
                    _pausedTitle = gui.TitleTextString;
                    title.text = _title;
                }
                _dialog = __result;
                gui.ContentText.text = _reason;
                if (!_logged) RuntimeEntryPoint.LogSource?.LogInfo("Showing the server's kick reason"
                    + (_title == null ? "" : " under \"" + _title + "\"") + ": " + _reason);
                _logged = true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The kick reason could not be shown: " + error.Message);
            }
        }

        private static void AfterStatusText(GameServerBrowserStatusBar __instance, GameServerBrowserStatusBar.State __0)
        {
            try
            {
                var localized = __instance._text;
                if (localized == null || !localized) return;
                var show = __0 == GameServerBrowserStatusBar.State.KickedFromGame && _reason != null && Now < _reasonUntil;
                localized.enabled = !show;
                if (show && localized.GetComponent<TMP_Text>() is { } text && text) text.text = _reason;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The kick reason could not be shown in the server browser: " + error.Message);
            }
        }

        private static void Release()
        {
            try
            {
                if (_paused != null && _paused) _paused.enabled = true;
                if (_pausedTitle != null && _pausedTitle) _pausedTitle.enabled = true;
            }
            catch (Exception)
            {
                // The popup may already be gone with its scene.
            }
            _paused = _pausedTitle = null;
            _dialog = null;
        }

        private static TMP_Text TitleText(DynamicModalDialogGui gui)
            => gui.TitleTextString is { } localized && localized && localized.GetComponent<TMP_Text>() is { } text && text
                ? text : null;

        private static bool Same(DynamicModalDialogController a, DynamicModalDialogController b)
            => b != null && a.Pointer == b.Pointer;
    }
}
