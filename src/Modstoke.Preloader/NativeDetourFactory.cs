// SPDX-License-Identifier: GPL-3.0-or-later
// Provider selection adapted from BepInEx's INativeDetour.cs.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Provider = BepInEx.Unity.IL2CPP.Hook.INativeDetour.DetourProvider;

namespace Modstoke.Preloader
{
    internal static class NativeDetourFactory
    {
        // Dobby's native registry is shared; serialize our provider operations as well as hook state.
        internal static readonly object Sync = new object();
        private static readonly Lazy<ConfigEntry<Provider>> ConfiguredProvider = new(() => ConfigFile.CoreConfig.Bind(
            "Detours", "DetourProviderType", Provider.Default, "The native provider to use for managed detours"));
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
                try
                {
                    var configured = ConfiguredProvider.Value.Value;
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
                        try { PreloaderLog.Log.LogDebug($"Modstoke native detours use {selected} ({mode})."); }
                        catch { }
                        HookStatus.RecordProviderSelected(selected, mode);
                    }
                    _createdCount++;
                    return detour;
                }
                catch (Exception error)
                {
                    RecordFailure("create", error);
                    throw;
                }
            }
        }

        internal static NativeDetour CreateRedLoader(IntPtr original, Delegate target, DobbyLibrary library)
        {
            if (original == IntPtr.Zero)
                throw new ArgumentOutOfRangeException(nameof(original), "The native target address cannot be zero.");
            ArgumentNullException.ThrowIfNull(target);

            lock (Sync)
            {
                try
                {
                    var detour = new NativeDetour(original, target, library);
                    _createdCount++;
                    return detour;
                }
                catch (Exception error)
                {
                    RecordFailure("create", error);
                    throw;
                }
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
                HookStatus.RecordProviderProbe(provider, available);
            }
        }

        // Native code cannot root managed callbacks. Retain each prepared hook until native cleanup succeeds.
        internal static void Retain(NativeDetour detour) => LiveDetours.Add(detour);
        internal static void Release(NativeDetour detour) => LiveDetours.Remove(detour);

        internal static bool HasLiveTarget(IntPtr target)
        {
            lock (Sync)
            {
                foreach (var detour in LiveDetours)
                    if (detour.Target == target)
                        return true;
                return false;
            }
        }

        internal static void RequireCleanTarget(IntPtr target)
        {
            foreach (var detour in LiveDetours)
                if (detour.Target == target && (detour.CleanupFailed || detour.ApplicationFailed))
                    throw new InvalidOperationException($"A previous hook at 0x{target:X} has unresolved activation or cleanup. Restart the game before retrying.");
        }

        internal static void RecordFailure(string operation, Exception error)
        {
            // Reporting must not skip native cleanup or replace the operation's exception.
            var message = $"Native detour {operation} failed.";
            try { message = $"Native detour {operation} failed: {error}"; }
            catch { }
            lock (Sync)
            {
                _failureCount++;
                if (operation == "cleanup")
                    _cleanupFailureCount++;
                _lastError = message;
            }
            try { PreloaderLog.Log.LogError(message); }
            catch { }
        }

        internal static object CreateState()
        {
            lock (Sync)
            {
                var appliedCount = 0;
                var uncertainAppliedCount = 0;
                var processLifetimeCount = 0;
                var retainedAfterCleanupFailureCount = 0;
                foreach (var detour in LiveDetours)
                {
                    if (detour.IsApplied)
                        appliedCount++;
                    if ((detour.CleanupFailed || detour.ApplicationFailed) && detour.MayBeApplied)
                        uncertainAppliedCount++;
                    if (detour.ProcessLifetime)
                        processLifetimeCount++;
                    if (detour.CleanupFailed)
                        retainedAfterCleanupFailureCount++;
                }
                return new
                {
                    engine = "OpenACAI",
                    selectedProvider = _selectedProvider,
                    selectionMode = _selectionMode,
                    providerAvailability = new Dictionary<string, bool>(Availability),
                    createdCount = _createdCount,
                    rootedCount = LiveDetours.Count,
                    appliedCount,
                    uncertainAppliedCount,
                    processLifetimeCount,
                    retainedAfterCleanupFailureCount,
                    failureCount = _failureCount,
                    cleanupFailureCount = _cleanupFailureCount,
                    lastError = _lastError
                };
            }
        }
    }
}
