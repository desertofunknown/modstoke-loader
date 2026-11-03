// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Modstoke.Loader;
using SonsSdk;

namespace Modstoke.Loader
{
    /// <summary>Local runtime observations, not client integrity or multiplayer admission decisions.</summary>
    public static class LoadedMods
    {
        public const int MaxEntries = 512;
        private static int _ownerThreadId;

        internal static void Initialize() =>
            Interlocked.CompareExchange(ref _ownerThreadId, Environment.CurrentManagedThreadId, 0);

        /// <summary>
        /// Copies owned activation records on the startup/Unity thread, without disk scans or
        /// mod callbacks. Libraries and code loaded outside these hosts are not covered.
        /// Capture again after lifecycle changes; never cache the result as authorization.
        /// </summary>
        public static LoadedModsReadStatus TryCapture(out LoadedModsSnapshot snapshot)
        {
            snapshot = null;
            if (_ownerThreadId == 0) return LoadedModsReadStatus.Unavailable;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return LoadedModsReadStatus.WrongThread;
            if (!RuntimeEntryPoint.StartupCompleted) return LoadedModsReadStatus.Unavailable;
            try
            {
                var native = ModRuntime.State;
                var entries = new List<LoadedModEntry>();
                var truncated = BepInExPluginHost.ActivationObservationsTruncated
                    || native.Mods.Count > MaxEntries || LoaderState.HostedRedLoaderMods.Count > MaxEntries;
                var unreadable = false;
                foreach (var mod in native.Mods.Take(MaxEntries))
                {
                    if (entries.Count == MaxEntries) { truncated = true; break; }
                    var state = mod.Failed ? ModActivationState.NativeFailed
                        : mod.Active ? ModActivationState.NativeActive
                        : mod.Loaded && native.Stopped ? ModActivationState.NativeStopped
                        : mod.Status == "Deferred" ? ModActivationState.NativeDeferred
                        : ModActivationState.NativeNotActivated;
                    Add("modstoke", mod.ModId, mod.Version, state, false, false);
                }
                foreach (var mod in LoaderState.HostedRedLoaderMods.Take(MaxEntries))
                {
                    if (entries.Count == MaxEntries) { truncated = true; break; }
                    try
                    {
                        var manifest = (mod as SonsMod)?.Manifest;
                        Add("redloader", manifest?.Id ?? mod.ID, manifest?.Version,
                            mod.Registered ? ModActivationState.RedLoaderRegistered
                                : ModActivationState.RedLoaderUnregistered, false, false);
                    }
                    catch (Exception) { unreadable = true; }
                }
                foreach (var plugin in BepInExPluginHost.CaptureActivations())
                {
                    if (entries.Count == MaxEntries) { truncated = true; break; }
                    Add("bepinex", plugin.Id, plugin.Version,
                        plugin.Registered ? ModActivationState.BepInExLoadCompletedRegistered
                            : ModActivationState.BepInExLoadCompletedUnregistered,
                        plugin.Direct, plugin.VersionNormalized);
                }
                var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
                foreach (var entry in entries)
                {
                    if (!counts.TryGetValue(entry.Loader, out var ids))
                    {
                        ids = new Dictionary<string, int>(entry.Loader == "bepinex"
                            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
                        counts.Add(entry.Loader, ids);
                    }
                    ids.TryGetValue(entry.Id, out var count);
                    ids[entry.Id] = count + 1;
                }
                var copied = entries.Select(entry => counts[entry.Loader][entry.Id] > 1
                    ? new LoadedModEntry(entry.Loader,
                        entry.Id, entry.Version, entry.State, entry.DirectActivation, entry.VersionNormalized,
                        entry.MetadataValid, true)
                    : entry)
                    .OrderBy(entry => entry.Loader, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Version, StringComparer.Ordinal).ToArray();
                snapshot = new LoadedModsSnapshot(Array.AsReadOnly(copied), native.LoadCompleted,
                    native.ExecutionEnabled, native.Stopped, truncated, unreadable);
                return LoadedModsReadStatus.Success;

                void Add(string loader, string id, string version, ModActivationState state,
                    bool direct, bool normalized)
                {
                    // Shortening an identity or version could create a different matching value.
                    if (!Fits(id, 256) || !Fits(version, 128)) { unreadable = true; return; }
                    entries.Add(new LoadedModEntry(loader, id ?? "", version ?? "", state,
                        direct, normalized, ValidFittedText(id) && ValidFittedText(version), false));
                }
            }
            catch (Exception)
            {
                // Exception details can contain local paths or mod data.
                return LoadedModsReadStatus.Failed;
            }
        }

        private static bool Fits(string value, int maxScalars)
        {
            if (value == null) return true;
            var scalars = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (++scalars > maxScalars) return false;
                var character = value[index];
                if (char.IsHighSurrogate(character))
                {
                    if (++index == value.Length || !char.IsLowSurrogate(value[index])) return false;
                }
                else if (char.IsLowSurrogate(character)) return false;
            }
            return true;
        }

        internal static bool ValidText(string value, int maxScalars) =>
            Fits(value, maxScalars) && ValidFittedText(value);

        private static bool ValidFittedText(string value)
        {
            if (string.IsNullOrEmpty(value) || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
                return false;
            foreach (var character in value)
                if (char.IsControl(character)) return false;
            return true;
        }
    }

    public enum LoadedModsReadStatus { Success, Unavailable, WrongThread, Failed }
    public enum ModActivationState
    {
        NativeNotActivated, NativeDeferred, NativeActive, NativeFailed, NativeStopped,
        RedLoaderRegistered, RedLoaderUnregistered,
        BepInExLoadCompletedRegistered, BepInExLoadCompletedUnregistered
    }
    public enum NativeRequirementStatus
    {
        Matched, Missing, VersionMismatch, Inactive, Ambiguous, Unavailable, InvalidRequirement
    }

    /// <summary>Copied metadata with host-specific activation semantics, without paths or executable objects.</summary>
    public sealed class LoadedModEntry
    {
        internal LoadedModEntry(string loader, string id, string version,
            ModActivationState state, bool direct, bool normalized, bool metadataValid, bool ambiguous)
        {
            Loader = loader; Id = id; Version = version; State = state;
            DirectActivation = direct; VersionNormalized = normalized;
            MetadataValid = metadataValid; Ambiguous = ambiguous;
        }
        public string Loader { get; }
        public string Id { get; }
        public string Version { get; }
        public ModActivationState State { get; }
        public bool DirectActivation { get; }
        public bool VersionNormalized { get; }
        public bool MetadataValid { get; }
        public bool Ambiguous { get; }
    }

    /// <summary>A local observation. Successful reads can still be truncated or contain unreadable entries.</summary>
    public sealed class LoadedModsSnapshot
    {
        internal LoadedModsSnapshot(IReadOnlyList<LoadedModEntry> entries,
            bool nativeLoadCompleted, bool nativeExecutionEnabled, bool nativeStopped, bool truncated, bool unreadable)
        {
            Entries = entries; NativeLoadCompleted = nativeLoadCompleted;
            NativeExecutionEnabled = nativeExecutionEnabled; NativeStopped = nativeStopped;
            Truncated = truncated; HasUnreadableEntries = unreadable;
            ObservedAtUtc = DateTimeOffset.UtcNow;
        }
        public int Schema => 1;
        public DateTimeOffset ObservedAtUtc { get; }
        public IReadOnlyList<LoadedModEntry> Entries { get; }
        public bool NativeLoadCompleted { get; }
        public bool NativeExecutionEnabled { get; }
        public bool NativeStopped { get; }
        public bool Truncated { get; }
        public bool HasUnreadableEntries { get; }

        /// <summary>
        /// Compares this snapshot only. Native IDs follow the host's ordinal-ignore-case lookup;
        /// versions are exact. Legacy registrations and libraries cannot satisfy a native requirement.
        /// </summary>
        public NativeRequirementStatus CheckNativeRequirement(string id, string version)
        {
            if (!LoadedMods.ValidText(id, 256) || !LoadedMods.ValidText(version, 128))
                return NativeRequirementStatus.InvalidRequirement;
            if (Truncated || HasUnreadableEntries || !NativeLoadCompleted || !NativeExecutionEnabled || NativeStopped)
                return NativeRequirementStatus.Unavailable;
            LoadedModEntry candidate = null;
            for (var index = 0; index < Entries.Count; index++)
            {
                var entry = Entries[index];
                if (entry.Loader != "modstoke" || !string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (candidate != null || entry.Ambiguous) return NativeRequirementStatus.Ambiguous;
                candidate = entry;
            }
            if (candidate == null) return NativeRequirementStatus.Missing;
            if (!candidate.MetadataValid) return NativeRequirementStatus.Unavailable;
            if (candidate.State != ModActivationState.NativeActive) return NativeRequirementStatus.Inactive;
            return string.Equals(candidate.Version, version, StringComparison.Ordinal)
                ? NativeRequirementStatus.Matched : NativeRequirementStatus.VersionMismatch;
        }
    }
}
