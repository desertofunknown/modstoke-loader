// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Loader;

internal static partial class SettingsStartup
{
    private static bool _uiPrefabHooksEnabled, _uiPrefabHookFailureReported;

    private static void InstallUiPrefabPresentation(Action<string> warning)
    {
        HarmonyLib.Harmony harmony = null;
        try
        {
            harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".ui-prefab-presentation");
            var input = QualifyMethod(typeof(EventSystem), "OnEnable", 0x4813550, 352,
                "6F9E663B77CAB57DBDCD13EBB24FD3AA0D900024D87680F2FB2A3D410B41AA38");
            var sky = QualifyMethod(typeof(SkyManager), nameof(SkyManager.RegisterStaticLightingSky), 0x41AEED0, 784,
                "B4356D1ADD76419E858D0DB165CDEFBA2017F36FAA0C7B7272844C6423199EE8",
                new[] { typeof(StaticLightingSky) }, isStatic: true);
            harmony.Patch(input, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforePrefabEventSystemEnable)));
            harmony.Patch(sky, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforePrefabSkyRegistration)));
            _uiPrefabHooksEnabled = true;
            RuntimeEntryPoint.LogSource?.LogInfo("UI prefab scene input and sky isolation registered for the recognized game build.");
        }
        catch (Exception error)
        {
            _uiPrefabHooksEnabled = false;
            try { harmony?.UnpatchSelf(); }
            catch (Exception rollbackError) { error = new AggregateException(error, rollbackError); }
            warning?.Invoke("UI prefab scene isolation was not installed: " + error);
        }
    }

    private static bool BeforePrefabEventSystemEnable(EventSystem __instance, bool __runOriginal)
    {
        try
        {
            if (!_uiPrefabHooksEnabled || !__runOriginal || !UiPrefabScene.Owns(__instance))
                return true;
            // This noninteractive extraction scene must not register a second input owner.
            // Native OnDisable only unregisters this instance, leaving the current owner intact.
            __instance.enabled = false;
            return false;
        }
        catch (Exception error)
        {
            ReportUiPrefabHookFailure(error);
            return true;
        }
    }

    private static bool BeforePrefabSkyRegistration(StaticLightingSky __0, bool __runOriginal)
    {
        try
        {
            // Keep per-component sky setup/cleanup; exclude only its global registration.
            return !_uiPrefabHooksEnabled || !__runOriginal || !UiPrefabScene.Owns(__0);
        }
        catch (Exception error)
        {
            ReportUiPrefabHookFailure(error);
            return true;
        }
    }

    private static void ReportUiPrefabHookFailure(Exception error)
    {
        if (_uiPrefabHookFailureReported)
            return;
        _uiPrefabHookFailureReported = true;
        try { RuntimeEntryPoint.LogSource?.LogWarning("UI prefab scene isolation yielded to native lifecycle: " + error.Message); }
        catch { }
    }
}
