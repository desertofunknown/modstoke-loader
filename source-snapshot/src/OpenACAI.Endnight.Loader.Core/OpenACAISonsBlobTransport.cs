// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.BlobNetworking;

namespace OpenACAI.Sons
{
    /// <summary>Checks the source endpoint of incoming blocks on connected blob transports.</summary>
    public static class OpenACAISonsBlobTransport
    {
        public const string ServiceKey = "openacai.sons.blobTransport";

        private static OpenACAISonsBlobTransportState _state = new(
            OpenACAISonsBlobHookStatus.NotAttempted, "The blob endpoint guard has not been installed.");
        private static int _installationAttempted;
        private static int _reportedEndpointFailure;

        public static OpenACAISonsBlobTransportState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(OpenACAISonsBlobHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Blob endpoint guard was not installed: " + detail);
                return;
            }

            PublishState(OpenACAISonsBlobHookStatus.Installing, "Registering the blob endpoint guard.");
            try
            {
                var target = AccessTools.Method(typeof(BlobTransport), nameof(BlobTransport.ProcessBlock),
                    new[] { typeof(Il2CppStructArray<byte>), typeof(BlobTransportEndpoint) });
                if (target == null)
                    throw new MissingMethodException(typeof(BlobTransport).FullName, "ProcessBlock(Byte[], BlobTransportEndpoint)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsBlobTransport), nameof(BeforeBlock))
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-blob-endpoint");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(OpenACAISonsBlobHookStatus.Failed,
                    "Blob endpoint guard registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Blob endpoint guard failed: " + error);
                return;
            }

            PublishState(OpenACAISonsBlobHookStatus.Installed,
                "Blob endpoint guard registration succeeded. Live transfer enforcement remains unverified.");
        }

        private static bool BeforeBlock(BlobTransport __instance, BlobTransportEndpoint __1)
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
                        OpenACAIEndnightRuntime.LogSource?.LogWarning(
                            "Blob block rejected because its source endpoint could not be checked ("
                            + error.GetType().FullName + "). Further endpoint failures will not be logged.");
                    }
                    catch (Exception) { }
                }
                return false;
            }
        }

        private static void PublishState(OpenACAISonsBlobHookStatus status, string detail)
        {
            var state = new OpenACAISonsBlobTransportState(status, detail);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
        }
    }

    public enum OpenACAISonsBlobHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class OpenACAISonsBlobTransportState
    {
        internal OpenACAISonsBlobTransportState(OpenACAISonsBlobHookStatus status, string detail)
        {
            Status = status;
            Detail = detail;
        }

        /// <summary>Patch registration status, not evidence of a received, rejected or completed transfer.</summary>
        public OpenACAISonsBlobHookStatus Status { get; }
        public string Detail { get; }
    }
}
