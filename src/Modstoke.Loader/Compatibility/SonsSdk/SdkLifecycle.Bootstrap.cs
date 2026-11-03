// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Harmony;
using RedLoader.Utils;
using global::Sons.Multiplayer.Dedicated;
using SonsSdk;
using Steamworks;

namespace Modstoke.Loader
{
    internal sealed partial class SdkLifecycle
    {
        public void InitializeBootstrapPatches()
        {
            BootstrapPatches.Apply();
        }

        public void PatchMaxPlayerLimit()
        {
            PlayerLimitPatch.Apply();
        }

        private sealed class BootstrapPatches
        {
            internal static void Apply()
            {
                var patcher = new ConfiguredPatcher<BootstrapPatches>(global::SonsSdk.SdkEntryPoint.Harmony);
                patcher.Prefix<SonsLaunch>(nameof(SonsLaunch.Start), nameof(SonsLaunchStart));

                if (!LoaderEnvironment.IsDedicatedServer)
                    return;

                PlayerLimitPatch.TryApply();
                patcher.Prefix<DedicatedServerBoostrap>(nameof(DedicatedServerBoostrap.CreateDedicated), nameof(BeforeServerCreation));
                patcher.Prefix(typeof(SteamGameServer), nameof(SteamGameServer.SetServerName), nameof(AlterServerName));
            }

            private static void SonsLaunchStart(SonsLaunch __instance)
            {
                __instance._titleSceneLoader._delay = 0f;
            }

            private static void BeforeServerCreation(DedicatedServerBoostrap __instance)
            {
                MainInitializer.InitDedicatedServer();
            }

            private static void AlterServerName(ref string pszServerName)
            {
                pszServerName = $"<color=red>[MODDED]</color> {pszServerName}";
            }
        }
    }
}
