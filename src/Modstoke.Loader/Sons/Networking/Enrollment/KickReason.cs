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
    /// <summary>
    /// Shows the reason a server wrote into a kick. The game receives it in the kick token, but its kick popup on
    /// the title screen and the server browser's status bar only show a generic localized line.
    /// </summary>
    internal static class KickReason
    {
        private const double ShowSeconds = 120;
        private static string _detail, _reason;
        private static double _detailUntil, _reasonUntil;
        private static bool _hooked, _logged;
        private static DynamicModalDialogController _dialog;
        private static UnityEngine.Localization.Components.LocalizeStringEvent _paused;

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        /// <summary>Adds a line to the next kick's reason, from a client that already knows why it will be refused.</summary>
        internal static void Expect(string detail)
        {
            _detail = detail;
            _detailUntil = Now + ShowSeconds;
        }

        internal static void Tick()
        {
            if (!_hooked)
            {
                _hooked = true;
                if (!Host.IsServer) Hook();
                return;
            }
            if (_dialog == null) return;
            try
            {
                if (!_dialog || !_dialog.gameObject.activeInHierarchy || Now >= _reasonUntil) Release();
                // Localization can finish after the popup opens and write its generic line again.
                else if (_dialog._gui.ContentText is { } text && text && text.text != _reason) text.text = _reason;
            }
            catch (Exception)
            {
                Release();
            }
        }

        private static void Hook()
        {
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-kick-reason");
            Patch(harmony, AccessTools.Method(typeof(CoopClientCallbacks), "HandleKickDisconnect", new[] { typeof(CoopKickToken) }),
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
                // A native kick sends a localization key; a reason written for players has spaces.
                var written = !string.IsNullOrEmpty(message) && message.Contains(' ') ? message : null;
                var detail = _detail != null && Now < _detailUntil ? _detail : null;
                _detail = null;
                _reason = written == null ? detail : detail == null ? written : written + "\n\n" + detail;
                _reasonUntil = Now + ShowSeconds;
                _logged = false;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The kick reason could not be read: " + error.Message);
            }
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
                _dialog = __result;
                gui.ContentText.text = _reason;
                if (!_logged) RuntimeEntryPoint.LogSource?.LogInfo("Showing the server's kick reason: " + _reason);
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
            }
            catch (Exception)
            {
                // The popup may already be gone with its scene.
            }
            _paused = null;
            _dialog = null;
        }

        private static bool Same(DynamicModalDialogController a, DynamicModalDialogController b)
            => b != null && a.Pointer == b.Pointer;
    }
}
