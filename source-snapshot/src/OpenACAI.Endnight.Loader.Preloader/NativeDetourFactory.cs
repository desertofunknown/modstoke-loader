// SPDX-License-Identifier: GPL-3.0-or-later
// Provider selection adapted from BepInEx's INativeDetour.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Provider = BepInEx.Unity.IL2CPP.Hook.INativeDetour.DetourProvider;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal static class NativeDetourFactory
    {
        // Dobby's native registry is shared; serialize our provider operations as well as hook state.
        internal static readonly object Sync = new object();
        private static readonly ConfigEntry<Provider> ConfiguredProvider = ConfigFile.CoreConfig.Bind(
            "Detours", "DetourProviderType", Provider.Default, "The native provider to use for managed detours");
        private static readonly HashSet<NativeDetour> LiveDetours = new HashSet<NativeDetour>();
        private static readonly Dictionary<string, bool> Availability = new Dictionary<string, bool>();
        private static DobbyLibrary _dobby;
        private static FunchookLibrary _funchook;
        private static string _selectedProvider = "";
        private static string _selectionMode = "";
        private static int _createdCount;
        private static int _failureCount;
        private static int _cleanupFailureCount;
        private static string _lastError = "";

        internal static NativeDetour Create(IntPtr original, Delegate target)
        {
            if (original == IntPtr.Zero)
                throw new ArgumentOutOfRangeException(nameof(original), "The native target address cannot be zero.");
            ArgumentNullException.ThrowIfNull(target);

            lock (Sync)
            {
                var configured = ConfiguredProvider.Value;
                NativeDetour detour;
                string selected;
                if (configured == Provider.Funchook)
                {
                    if (!TryFunchook(out var error))
                        throw new InvalidOperationException("Configured Funchook provider is unavailable: " + error);
                    detour = new NativeDetour(original, target, _funchook);
                    selected = "Funchook";
                }
                else if (TryDobby(out var dobbyError))
                {
                    detour = new NativeDetour(original, target, _dobby);
                    selected = "Dobby";
                }
                else
                {
                    if (configured == Provider.Dobby)
                        throw new InvalidOperationException("Configured Dobby provider is unavailable: " + dobbyError);
                    if (!TryFunchook(out var funchookError))
                        throw new InvalidOperationException($"No native detour provider is available. Dobby: {dobbyError}; Funchook: {funchookError}");
                    detour = new NativeDetour(original, target, _funchook);
                    selected = "Funchook";
                }

                var mode = configured == Provider.Dobby || configured == Provider.Funchook ? "configured" : "default";
                if (_selectedProvider != selected || _selectionMode != mode)
                {
                    _selectedProvider = selected;
                    _selectionMode = mode;
                    try { OpenACAIPreloaderLog.Log.LogDebug($"OpenACAI native detours use {selected} ({mode})."); }
                    catch { }
                    OpenACAIIL2CPPHookStatus.RecordProviderSelected(selected, mode);
                }
                _createdCount++;
                return detour;
            }
        }

        private static bool TryDobby(out string error)
        {
            error = "";
            var available = _dobby != null || DobbyLibrary.TryLoad(out _dobby, out error);
            RecordAvailability("Dobby", available);
            return available;
        }

        private static bool TryFunchook(out string error)
        {
            error = "";
            var available = _funchook != null || FunchookLibrary.TryLoad(out _funchook, out error);
            RecordAvailability("Funchook", available);
            return available;
        }

        private static void RecordAvailability(string provider, bool available)
        {
            if (!Availability.TryGetValue(provider, out var previous) || previous != available)
            {
                Availability[provider] = available;
                OpenACAIIL2CPPHookStatus.RecordProviderProbe(provider, available);
            }
        }

        // Native code cannot root managed callbacks. Retain each prepared hook until native cleanup succeeds.
        internal static void Retain(NativeDetour detour) => LiveDetours.Add(detour);
        internal static void Release(NativeDetour detour) => LiveDetours.Remove(detour);

        internal static bool HasLiveTarget(IntPtr target)
        {
            lock (Sync)
                return LiveDetours.Any(detour => detour.Target == target);
        }

        internal static void RequireCleanTarget(IntPtr target)
        {
            if (LiveDetours.Any(detour => detour.Target == target && (detour.CleanupFailed || detour.ApplicationFailed)))
                throw new InvalidOperationException($"A previous hook at 0x{target:X} has unresolved activation or cleanup. Restart the game before retrying.");
        }

        internal static void RecordFailure(string operation, Exception error)
        {
            _failureCount++;
            if (operation == "cleanup")
                _cleanupFailureCount++;
            // Reporting must not skip native cleanup or replace the operation's exception.
            _lastError = $"Native detour {operation} failed.";
            try { _lastError = $"Native detour {operation} failed: {error}"; }
            catch { }
            try { OpenACAIPreloaderLog.Log.LogError(_lastError); }
            catch { }
        }

        internal static object CreateState()
        {
            lock (Sync)
                return new
                {
                    engine = "OpenACAI",
                    selectedProvider = _selectedProvider,
                    selectionMode = _selectionMode,
                    providerAvailability = new Dictionary<string, bool>(Availability),
                    createdCount = _createdCount,
                    rootedCount = LiveDetours.Count,
                    appliedCount = LiveDetours.Count(detour => detour.IsApplied),
                    uncertainAppliedCount = LiveDetours.Count(detour => (detour.CleanupFailed || detour.ApplicationFailed) && detour.MayBeApplied),
                    processLifetimeCount = LiveDetours.Count(detour => detour.ProcessLifetime),
                    retainedAfterCleanupFailureCount = LiveDetours.Count(detour => detour.CleanupFailed),
                    failureCount = _failureCount,
                    cleanupFailureCount = _cleanupFailureCount,
                    lastError = _lastError
                };
        }
    }
}
