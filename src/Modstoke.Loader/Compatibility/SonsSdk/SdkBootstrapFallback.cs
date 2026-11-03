// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility bootstrap patches adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using Harmony;
using RedLoader.Utils;
using global::Sons.Multiplayer.Dedicated;
using SonsSdk;
using Steamworks;

namespace Modstoke.Loader;

internal sealed class SdkBootstrapFallback : ISdkBootstrapFallbackDriver
{
    private static readonly SdkBootstrapFallback Driver = new();

    internal static ISdkBootstrapFallbackDriver Create() => Driver;

    public void Initialize()
    {
        MiscPatches._patcher = new ConfiguredPatcher<MiscPatches>(global::SonsSdk.SdkEntryPoint.Harmony);
        MiscPatches._patcher.Prefix<SonsLaunch>(nameof(SonsLaunch.Start), nameof(MiscPatches.SonsLaunchStart));

        if (LoaderEnvironment.IsDedicatedServer)
        {
            MiscPatches.DoServerPatches();
        }
    }

    public void DoServerPatches()
    {
        PlayerLimitPatch.TryApply();
        MiscPatches._patcher.Prefix<DedicatedServerBoostrap>(nameof(DedicatedServerBoostrap.CreateDedicated), nameof(MiscPatches.BeforeServerCreation));
        MiscPatches._patcher.Prefix(typeof(SteamGameServer), nameof(SteamGameServer.SetServerName), nameof(MiscPatches.AlterServerName));
    }

    public void SonsLaunchStart(SonsLaunch __instance)
    {
        __instance._titleSceneLoader._delay = 0f;
    }

    public void BeforeServerCreation(DedicatedServerBoostrap __instance)
    {
        MainInitializer.InitDedicatedServer();
    }

    public void AlterServerName(ref string pszServerName)
    {
        pszServerName = $"<color=red>[MODDED]</color> {pszServerName}";
    }

    public void PatchMaxPlayerLimit()
    {
        PlayerLimitPatch.Apply();
    }
}
