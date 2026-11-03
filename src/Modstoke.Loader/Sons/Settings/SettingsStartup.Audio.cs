// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using GameAudioSettings = global::Sons.Settings.AudioSettings;

namespace Modstoke.Loader
{
    internal static partial class SettingsStartup
    {
        private static bool _audioVoiceCountIdempotenceEnabled;
        private static int _audioVoiceCountFailureReported;

        private static void InstallAudioVoiceCountIdempotence(Action<string> warning)
        {
            HarmonyLib.Harmony harmony = null;
            try
            {
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-audio-voice-count");
                var changed = QualifyMethod(typeof(GameAudioSettings), nameof(GameAudioSettings.VoiceCountChanged),
                    0x2EC0F00, 960, "641963D995329B3E9C44D257E9B8DEF868F8795213B9FEFF5D70D6DEA78AAF49",
                    parameters: new[] { typeof(string) }, isStatic: false);
                harmony.Patch(changed, prefix: new HarmonyMethod(typeof(SettingsStartup), nameof(BeforeAudioVoiceCountChanged)));
                _audioVoiceCountIdempotenceEnabled = true;
                RuntimeEntryPoint.LogSource?.LogInfo("Audio voice count idempotence registered for the recognized game build.");
            }
            catch (Exception error)
            {
                _audioVoiceCountIdempotenceEnabled = false;
                try { harmony?.UnpatchSelf(); }
                catch (Exception rollbackError) { error = new AggregateException(error, rollbackError); }
                warning?.Invoke("Audio voice count idempotence was not installed: " + error);
            }
        }

        private static bool BeforeAudioVoiceCountChanged(string __0, bool __runOriginal)
        {
            if (!_audioVoiceCountIdempotenceEnabled || !__runOriginal)
                return true;

            try
            {
                // Native uses Integer style and IL2CPP's current NumberFormatInfo for both parses.
                // The managed host's culture need not match the game's culture.
                if (!Il2CppSystem.Int32.TryParse(__0, out int requested))
                    Il2CppSystem.Int32.TryParse("128", out requested);

                // Preserve the pending configuration even when the live system is unavailable
                // or still has a different channel count. Never reinitialize FMOD here.
                FMOD_StudioSystem._voiceCount = requested;
                if (!FMOD_StudioSystem.TryGetInstance(out var studioSystem, false) || !studioSystem)
                    return true;

                var result = studioSystem._coreSystem.getSoftwareChannels(out int actual);
                if (result != FMOD.RESULT.OK)
                {
                    ReportAudioVoiceCountFailure("FMOD getSoftwareChannels returned " + result);
                    return true;
                }

                return requested != actual;
            }
            catch (Exception error)
            {
                ReportAudioVoiceCountFailure(error.Message);
                return true;
            }
        }

        private static void ReportAudioVoiceCountFailure(string detail)
        {
            if (Interlocked.Exchange(ref _audioVoiceCountFailureReported, 1) != 0)
                return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Audio voice count idempotence yielded to the native callback: " + detail); }
            catch { }
        }
    }
}
