// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility save callbacks adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Reflection;
using HarmonyLib;
using global::Sons.Save;
using SonsSdk;
using SaveCallbacks = SonsSdk.SdkEvents.SavingCallbackPatches;

namespace Modstoke.Loader;

internal sealed partial class SdkEventFallback
{
    public void BeforeLoad(string directory, SaveGameType saveGameType)
        => SdkLifecycle.LegacyFallback.BeforeLoad(directory, saveGameType);

    public void AfterLoad(string directory, SaveGameType saveGameType)
        => SdkLifecycle.LegacyFallback.AfterLoad(directory, saveGameType);

    public void BeforeSave(string directory, string gameName, bool savePlayerOnly)
        => SdkLifecycle.LegacyFallback.BeforeSave(directory, gameName, savePlayerOnly);

    public void AfterSave(string directory, string gameName, bool savePlayerOnly)
        => SdkLifecycle.LegacyFallback.AfterSave(directory, gameName, savePlayerOnly);

    public void PatchSavingCallbacks()
    {
        SaveCallbacks.ApplyPatch(
            AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Load), new[] { typeof(string), typeof(SaveGameType) }),
            nameof(SaveCallbacks.BeforeLoad), nameof(SaveCallbacks.AfterLoad), ref SaveCallbacks._loadPatchState);
        SaveCallbacks.ApplyPatch(
            AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Save), new[] { typeof(string), typeof(string), typeof(bool) }),
            nameof(SaveCallbacks.BeforeSave), nameof(SaveCallbacks.AfterSave), ref SaveCallbacks._savePatchState);
    }

    public void ApplySavePatch(MethodInfo target, string prefixName, string postfixName, ref SaveHookPatchState state)
    {
        if (state == SaveHookPatchState.Applied)
            return;
        if (state != SaveHookPatchState.NotAttempted)
            throw new InvalidOperationException("Save callback patching cannot be retried while native hook state is uncertain.");

        ArgumentNullException.ThrowIfNull(target);
        var prefix = new HarmonyMethod(typeof(SaveCallbacks), prefixName);
        var postfix = new HarmonyMethod(typeof(SaveCallbacks), postfixName);

        state = SaveHookPatchState.Applying;
        try
        {
            global::SonsSdk.SdkEntryPoint.Harmony.Patch(target, prefix: prefix, postfix: postfix);
            state = SaveHookPatchState.Applied;
        }
        catch
        {
            // A failed detour attempt may already have changed native state.
            state = SaveHookPatchState.Failed;
            throw;
        }
    }
}
