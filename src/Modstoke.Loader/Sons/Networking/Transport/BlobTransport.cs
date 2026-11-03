// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using global::Sons.BlobNetworking;

namespace Modstoke.Sons
{
    /// <summary>Checks the source endpoint of incoming blocks on connected blob transports.</summary>
    public static class BlobTransport
    {
        public const string ServiceKey = "openacai.sons.blobTransport";

        private static BlobTransportState _state = new(
            BlobHookStatus.NotAttempted, "The blob endpoint guard has not been installed.");
        private static int _installationAttempted;
        private static int _reportedEndpointFailure;

        public static BlobTransportState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(BlobHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Blob endpoint guard was not installed: " + detail);
                return;
            }

            PublishState(BlobHookStatus.Installing, "Registering the blob endpoint guard.");
            try
            {
                var target = AccessTools.Method(typeof(global::Sons.BlobNetworking.BlobTransport), nameof(global::Sons.BlobNetworking.BlobTransport.ProcessBlock),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(BlobTransportEndpoint) });
                if (target == null)
                    throw new MissingMethodException(typeof(global::Sons.BlobNetworking.BlobTransport).FullName, "ProcessBlock(Byte[], BlobTransportEndpoint)");

                var prefix = new HarmonyMethod(typeof(global::Modstoke.Sons.BlobTransport), nameof(BeforeBlock))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-blob-endpoint");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(BlobHookStatus.Failed,
                    "Blob endpoint guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Blob endpoint guard failed: " + error);
                return;
            }

            PublishState(BlobHookStatus.Installed,
                "Blob endpoint guard registration succeeded. Live transfer enforcement remains unverified.");
        }

        private static bool BeforeBlock(global::Sons.BlobNetworking.BlobTransport __instance, BlobTransportEndpoint __1)
        {
            try
            {
                if (__instance == null)
                    return false;

                // This runs on the transport worker. Read its endpoint, not game-thread session state.
                var expected = __instance._connectEp;
                if (expected == null)
                    return true;

                // Native equality compares both the IP endpoint and Steam ID, without resolving addresses.
                return __1 != null && expected.Equals(__1);
            }
            catch (Exception error)
            {
                if (Interlocked.Exchange(ref _reportedEndpointFailure, 1) == 0)
                {
                    try
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning(
                            "Blob block rejected because its source endpoint could not be checked ("
                            + error.GetType().FullName + "). Further endpoint failures will not be logged.");
                    }
                    catch (Exception) { }
                }
                return false;
            }
        }

        private static void PublishState(BlobHookStatus status, string detail)
        {
            var state = new BlobTransportState(status, detail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }
    }

    public enum BlobHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class BlobTransportState
    {
        internal BlobTransportState(BlobHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Patch registration status, not evidence of a received, rejected or completed transfer.</summary>
        public BlobHookStatus Status { get; }
        public string Detail { get; }
    }
}
