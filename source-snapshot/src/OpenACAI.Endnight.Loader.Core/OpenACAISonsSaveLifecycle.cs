// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned Sons save/load lifecycle service.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using SaveHookPatchState = SonsSdk.SaveHookPatchState;
using SdkSaveHooks = SonsSdk.SdkEvents.SavingCallbackPatches;

namespace OpenACAI.Sons
{
    public static class OpenACAISonsSaveLifecycle
    {
        public const string ServiceKey = "openacai.sons.saveLifecycle";

        private static readonly object SyncRoot = new object();
        private static OpenACAISonsSaveLifecycleSnapshot _state = OpenACAISonsSaveLifecycleSnapshot.CreateUnknown();
        private static HarmonyLib.Harmony _harmony;
        private static bool _initialized;
        private static int _beforeLoadCount;
        private static int _afterLoadCount;
        private static int _beforeSaveCount;
        private static int _afterSaveCount;

        public static event Action<OpenACAISonsSaveLifecycleEvent> Lifecycle;

        public static OpenACAISonsSaveLifecycleSnapshot State
        {
            get
            {
                lock (SyncRoot)
                {
                    return _state;
                }
            }
        }

        internal static void Initialize()
        {
            var registerProvider = false;
            lock (SyncRoot)
            {
                if (_initialized)
                {
                    return;
                }

                _initialized = true;
                PublishServiceLocked();
                registerProvider = true;
            }

            if (registerProvider && !OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK save lifecycle provider could not be registered.");
            }
        }

        internal static void RefreshLegacySonsSdkRelay()
        {
            ReadLegacyRelayPolicy();
        }

        private static (bool Load, bool Save) ReadLegacyRelayPolicy()
        {
            var loadState = SdkSaveHooks.LoadPatchState;
            var saveState = SdkSaveHooks.SavePatchState;
            var relayLoad = loadState == SaveHookPatchState.NotAttempted;
            var relaySave = saveState == SaveHookPatchState.NotAttempted;
            var enabled = relayLoad || relaySave;
            var reason = "SDK load hooks: " + DescribeSdkHookState(loadState)
                + "; save hooks: " + DescribeSdkHookState(saveState) + ".";

            lock (SyncRoot)
            {
                if (_state.LegacySonsSdkRelayEnabled != enabled
                    || !string.Equals(_state.LegacySonsSdkRelayReason, reason, StringComparison.Ordinal))
                {
                    _state = _state.WithLegacyRelay(enabled, reason, DateTimeOffset.UtcNow);
                    PublishServiceLocked();
                }
            }

            return (relayLoad, relaySave);
        }

        private static string DescribeSdkHookState(SaveHookPatchState state)
        {
            switch (state)
            {
                case SaveHookPatchState.NotAttempted:
                    return "not attempted (fallback enabled)";
                case SaveHookPatchState.Applying:
                    return "applying (fallback suppressed)";
                case SaveHookPatchState.Applied:
                    return "registration returned successfully (fallback disabled)";
                case SaveHookPatchState.Failed:
                    return "installation failed, native state uncertain (fallback suppressed)";
                default:
                    return "unknown state (fallback suppressed)";
            }
        }

        internal static void InstallHarmonyPatches(OpenACAIHarmonySupportSnapshot harmonySupport, Action<string> addStartupWarning)
        {
            lock (SyncRoot)
            {
                if (_harmony != null)
                {
                    return;
                }
            }

            if (harmonySupport == null || !harmonySupport.SafeForHarmonyPatches)
            {
                var detail = harmonySupport == null
                    ? "Harmony support probe was unavailable."
                    : harmonySupport.Detail;
                addStartupWarning?.Invoke("Sons save/load lifecycle patches were not installed: " + detail);
                MarkPatchStatus(false, "disabled", detail, Array.Empty<string>());
                return;
            }

            var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-save-lifecycle");
            var installedTargets = new List<string>();

            TryPatch(
                harmony,
                "SaveGameManager.Load",
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Load), new[] { typeof(string), typeof(SaveGameType) }),
                nameof(OpenACAISonsSaveLifecyclePatches.BeforeLoad),
                nameof(OpenACAISonsSaveLifecyclePatches.AfterLoad),
                installedTargets,
                addStartupWarning);

            TryPatch(
                harmony,
                "SaveGameManager.Save",
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Save), new[] { typeof(string), typeof(string), typeof(bool) }),
                nameof(OpenACAISonsSaveLifecyclePatches.BeforeSave),
                nameof(OpenACAISonsSaveLifecyclePatches.AfterSave),
                installedTargets,
                addStartupWarning);

            lock (SyncRoot)
            {
                _harmony = harmony;
            }

            var allTargetsInstalled = installedTargets.Count == 2;
            var status = allTargetsInstalled ? "ready" : installedTargets.Count == 0 ? "failed" : "partial";
            var detailText = allTargetsInstalled
                ? "OpenACAI save/load lifecycle patches are installed."
                : "OpenACAI save/load lifecycle patches installed " + installedTargets.Count + " of 2 target(s).";
            MarkPatchStatus(allTargetsInstalled, status, detailText, installedTargets.ToArray());
        }

        internal static void PublishBeforeLoad(string directory, string saveGameType, uint? selectedSaveId)
        {
            Publish("Load", "Before", directory, null, saveGameType, null, selectedSaveId);
        }

        internal static void PublishAfterLoad(string directory, string saveGameType, uint? selectedSaveId)
        {
            Publish("Load", "After", directory, null, saveGameType, null, selectedSaveId);
        }

        internal static void PublishBeforeSave(string directory, string gameName, bool savePlayerOnly)
        {
            Publish("Save", "Before", directory, gameName, null, savePlayerOnly, null);
        }

        internal static void PublishAfterSave(string directory, string gameName, bool savePlayerOnly)
        {
            Publish("Save", "After", directory, gameName, null, savePlayerOnly, null);
        }

        private static void Publish(
            string operation,
            string phase,
            string directory,
            string gameName,
            string saveGameType,
            bool? savePlayerOnly,
            uint? selectedSaveId)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var evt = new OpenACAISonsSaveLifecycleEvent(
                operation,
                phase,
                directory,
                gameName,
                saveGameType,
                savePlayerOnly,
                selectedSaveId,
                timestamp);
            Action<OpenACAISonsSaveLifecycleEvent> handlers;
            var relayPolicy = ReadLegacyRelayPolicy();
            var relayLegacy = string.Equals(operation, "Load", StringComparison.Ordinal)
                ? relayPolicy.Load
                : relayPolicy.Save;

            lock (SyncRoot)
            {
                if (string.Equals(operation, "Load", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(phase, "Before", StringComparison.OrdinalIgnoreCase))
                    {
                        _beforeLoadCount++;
                    }
                    else
                    {
                        _afterLoadCount++;
                    }
                }
                else if (string.Equals(operation, "Save", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(phase, "Before", StringComparison.OrdinalIgnoreCase))
                    {
                        _beforeSaveCount++;
                    }
                    else
                    {
                        _afterSaveCount++;
                    }
                }

                _state = _state.WithEvent(
                    evt,
                    _beforeLoadCount,
                    _afterLoadCount,
                    _beforeSaveCount,
                    _afterSaveCount);
                PublishServiceLocked();
                handlers = Lifecycle;
            }

            OpenACAIModding.Dispatch("OpenACAISonsSaveLifecycle." + operation + "." + phase, handlers, evt);
            if (relayLegacy)
            {
                RelayLegacySonsSdkEvent(evt);
            }

            OpenACAITimings.Mark("sons.saveLifecycle", operation.ToLowerInvariant() + "-" + phase.ToLowerInvariant(), directory);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("sons-save-lifecycle-" + operation.ToLowerInvariant() + "-" + phase.ToLowerInvariant());
        }

        private static void RelayLegacySonsSdkEvent(OpenACAISonsSaveLifecycleEvent evt)
        {
            if (string.Equals(evt.Operation, "Load", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(evt.Phase, "Before", StringComparison.OrdinalIgnoreCase))
                {
                    OpenACAILegacyLifecycleBridge.PublishBeforeLoadSave();
                    return;
                }

                OpenACAILegacyLifecycleBridge.PublishAfterLoadSave();
                return;
            }

            if (string.Equals(evt.Operation, "Save", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(evt.Phase, "Before", StringComparison.OrdinalIgnoreCase))
                {
                    OpenACAILegacyLifecycleBridge.PublishBeforeSave();
                    return;
                }

                OpenACAILegacyLifecycleBridge.PublishAfterSave(evt.SavePlayerOnly.GetValueOrDefault());
            }
        }

        private static void TryPatch(
            HarmonyLib.Harmony harmony,
            string label,
            MethodInfo target,
            string prefixName,
            string postfixName,
            ICollection<string> installedTargets,
            Action<string> addStartupWarning)
        {
            try
            {
                if (target == null)
                {
                    addStartupWarning?.Invoke(label + " save lifecycle patch target was not found.");
                    return;
                }

                var patchType = typeof(OpenACAISonsSaveLifecyclePatches);
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(AccessTools.Method(patchType, prefixName)),
                    postfix: new HarmonyMethod(AccessTools.Method(patchType, postfixName)));
                installedTargets.Add(label);
            }
            catch (Exception ex)
            {
                addStartupWarning?.Invoke(label + " save lifecycle patch failed: " + DescribeException(ex));
            }
        }

        private static void MarkPatchStatus(bool installed, string status, string detail, IReadOnlyCollection<string> installedTargets)
        {
            lock (SyncRoot)
            {
                _state = _state.WithPatchStatus(installed, status, detail, installedTargets, DateTimeOffset.UtcNow);
                PublishServiceLocked();
            }
        }

        private static void PublishServiceLocked()
        {
            OpenACAIModding.SetService(ServiceKey, _state);
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var snapshot = State;
            return new OpenACAIMenuContentSnapshot(
                "Sons Save Lifecycle",
                "OpenACAI-owned Sons save/load hook state and legacy SonsSdk relay status.",
                new[]
                {
                    CreateHookSection(snapshot),
                    CreateEventSection(snapshot)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateHookSection(OpenACAISonsSaveLifecycleSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Lifecycle Hooks",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, PatchStatus(snapshot), snapshot.PatchDetail),
                    Row("Hook Model", snapshot.HookModel, snapshot.HarmonyPatchesInstalled ? "READY" : "INFO", "OpenACAI-owned Harmony patch surface for Sons save/load events."),
                    Row("Patch Status", snapshot.PatchStatus, PatchStatus(snapshot), snapshot.PatchDetail),
                    Row("Installed Targets", FormatTargets(snapshot.InstalledTargets), snapshot.HarmonyPatchesInstalled ? "READY" : snapshot.InstalledTargets.Count > 0 ? "CHECK" : "PENDING", "SaveGameManager targets currently patched by OpenACAI."),
                    Row("Legacy Relay", snapshot.LegacySonsSdkRelayEnabled ? "Enabled" : "Disabled", snapshot.LegacySonsSdkRelayEnabled ? "READY" : "INFO", snapshot.LegacySonsSdkRelayReason)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateEventSection(OpenACAISonsSaveLifecycleSnapshot snapshot)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Observed Events",
                new[]
                {
                    Row("Last Event", string.IsNullOrWhiteSpace(snapshot.LastEvent) ? "None" : snapshot.LastEvent, string.IsNullOrWhiteSpace(snapshot.LastEvent) ? "PENDING" : "INFO", "Most recent OpenACAI save lifecycle event."),
                    Row("Last Directory", string.IsNullOrWhiteSpace(snapshot.LastDirectory) ? "None" : snapshot.LastDirectory, string.IsNullOrWhiteSpace(snapshot.LastDirectory) ? "PENDING" : "INFO", "Sons save directory observed on the most recent event."),
                    Row("Before Load", snapshot.BeforeLoadCount.ToString(), "INFO", "Before-load events observed by OpenACAI."),
                    Row("After Load", snapshot.AfterLoadCount.ToString(), "INFO", "After-load events observed by OpenACAI."),
                    Row("Before Save", snapshot.BeforeSaveCount.ToString(), "INFO", "Before-save events observed by OpenACAI."),
                    Row("After Save", snapshot.AfterSaveCount.ToString(), "INFO", "After-save events observed by OpenACAI.")
                });
        }

        private static string PatchStatus(OpenACAISonsSaveLifecycleSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "MISSING";
            }

            if (snapshot.HarmonyPatchesInstalled)
            {
                return "READY";
            }

            return string.Equals(snapshot.PatchStatus, "failed", StringComparison.OrdinalIgnoreCase)
                ? "FAILED"
                : string.Equals(snapshot.PatchStatus, "not-installed", StringComparison.OrdinalIgnoreCase)
                    ? "PENDING"
                    : "CHECK";
        }

        private static string FormatTargets(IReadOnlyCollection<string> installedTargets)
        {
            return installedTargets == null || installedTargets.Count == 0
                ? "None"
                : string.Join(", ", installedTargets);
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private static string DescribeException(Exception exception)
        {
            if (exception == null)
            {
                return "unknown exception";
            }

            var parts = new List<string>();
            var current = exception;
            var depth = 0;
            while (current != null && depth < 4)
            {
                parts.Add(current.GetType().Name + ": " + current.Message);
                current = current.InnerException;
                depth++;
            }

            return string.Join(" -> ", parts);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsSaveLifecycle;
            public string DisplayName => "Sons Save Lifecycle";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 723;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons save/load lifecycle hook state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsSaveLifecycle.CreateContent();
            }
        }
    }

    internal static class OpenACAISonsSaveLifecyclePatches
    {
        internal static void BeforeLoad(string dir, SaveGameType saveGameType)
        {
            OpenACAISonsSaveLifecycle.PublishBeforeLoad(dir, saveGameType.ToString(), TryGetSelectedSaveId());
        }

        internal static void AfterLoad(string dir, SaveGameType saveGameType)
        {
            OpenACAISonsSaveLifecycle.PublishAfterLoad(dir, saveGameType.ToString(), TryGetSelectedSaveId());
        }

        internal static void BeforeSave(string dir, string gameName, bool savePlayerOnly)
        {
            OpenACAISonsSaveLifecycle.PublishBeforeSave(dir, gameName, savePlayerOnly);
        }

        internal static void AfterSave(string dir, string gameName, bool savePlayerOnly)
        {
            OpenACAISonsSaveLifecycle.PublishAfterSave(dir, gameName, savePlayerOnly);
        }

        private static uint? TryGetSelectedSaveId()
        {
            try
            {
                return GameSetupManager.GetSelectedSaveId();
            }
            catch
            {
                return null;
            }
        }
    }

    public sealed class OpenACAISonsSaveLifecycleSnapshot
    {
        private OpenACAISonsSaveLifecycleSnapshot(
            string generatedUtc,
            string serviceKey,
            string hookModel,
            bool harmonyPatchesInstalled,
            string patchStatus,
            string patchDetail,
            IReadOnlyCollection<string> installedTargets,
            bool legacySonsSdkRelayEnabled,
            string legacySonsSdkRelayReason,
            string lastEvent,
            string lastOperation,
            string lastPhase,
            string lastDirectory,
            string lastGameName,
            string lastSaveGameType,
            bool? lastSavePlayerOnly,
            uint? lastSelectedSaveId,
            int beforeLoadCount,
            int afterLoadCount,
            int beforeSaveCount,
            int afterSaveCount)
        {
            GeneratedUtc = generatedUtc;
            ServiceKey = serviceKey;
            HookModel = hookModel;
            HarmonyPatchesInstalled = harmonyPatchesInstalled;
            PatchStatus = patchStatus;
            PatchDetail = patchDetail;
            InstalledTargets = installedTargets;
            LegacySonsSdkRelayEnabled = legacySonsSdkRelayEnabled;
            LegacySonsSdkRelayReason = legacySonsSdkRelayReason;
            LastEvent = lastEvent;
            LastOperation = lastOperation;
            LastPhase = lastPhase;
            LastDirectory = lastDirectory;
            LastGameName = lastGameName;
            LastSaveGameType = lastSaveGameType;
            LastSavePlayerOnly = lastSavePlayerOnly;
            LastSelectedSaveId = lastSelectedSaveId;
            BeforeLoadCount = beforeLoadCount;
            AfterLoadCount = afterLoadCount;
            BeforeSaveCount = beforeSaveCount;
            AfterSaveCount = afterSaveCount;
        }

        public string GeneratedUtc { get; }
        public string ServiceKey { get; }
        public string HookModel { get; }
        public bool HarmonyPatchesInstalled { get; }
        public string PatchStatus { get; }
        public string PatchDetail { get; }
        public IReadOnlyCollection<string> InstalledTargets { get; }
        public bool LegacySonsSdkRelayEnabled { get; }
        public string LegacySonsSdkRelayReason { get; }
        public string LastEvent { get; }
        public string LastOperation { get; }
        public string LastPhase { get; }
        public string LastDirectory { get; }
        public string LastGameName { get; }
        public string LastSaveGameType { get; }
        public bool? LastSavePlayerOnly { get; }
        public uint? LastSelectedSaveId { get; }
        public int BeforeLoadCount { get; }
        public int AfterLoadCount { get; }
        public int BeforeSaveCount { get; }
        public int AfterSaveCount { get; }

        internal static OpenACAISonsSaveLifecycleSnapshot CreateUnknown()
        {
            return new OpenACAISonsSaveLifecycleSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                OpenACAISonsSaveLifecycle.ServiceKey,
                "openacai-harmony-savegame-manager",
                false,
                "not-installed",
                "OpenACAI save/load lifecycle patches have not been installed yet.",
                Array.Empty<string>(),
                true,
                "SDK save-hook setup has not been observed yet.",
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0);
        }

        internal OpenACAISonsSaveLifecycleSnapshot WithLegacyRelay(bool enabled, string reason, DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsSaveLifecycleSnapshot(
                timestampUtc.ToString("O"),
                ServiceKey,
                HookModel,
                HarmonyPatchesInstalled,
                PatchStatus,
                PatchDetail,
                InstalledTargets,
                enabled,
                reason,
                LastEvent,
                LastOperation,
                LastPhase,
                LastDirectory,
                LastGameName,
                LastSaveGameType,
                LastSavePlayerOnly,
                LastSelectedSaveId,
                BeforeLoadCount,
                AfterLoadCount,
                BeforeSaveCount,
                AfterSaveCount);
        }

        internal OpenACAISonsSaveLifecycleSnapshot WithPatchStatus(
            bool installed,
            string status,
            string detail,
            IReadOnlyCollection<string> installedTargets,
            DateTimeOffset timestampUtc)
        {
            return new OpenACAISonsSaveLifecycleSnapshot(
                timestampUtc.ToString("O"),
                ServiceKey,
                HookModel,
                installed,
                status,
                detail,
                installedTargets ?? Array.Empty<string>(),
                LegacySonsSdkRelayEnabled,
                LegacySonsSdkRelayReason,
                LastEvent,
                LastOperation,
                LastPhase,
                LastDirectory,
                LastGameName,
                LastSaveGameType,
                LastSavePlayerOnly,
                LastSelectedSaveId,
                BeforeLoadCount,
                AfterLoadCount,
                BeforeSaveCount,
                AfterSaveCount);
        }

        internal OpenACAISonsSaveLifecycleSnapshot WithEvent(
            OpenACAISonsSaveLifecycleEvent evt,
            int beforeLoadCount,
            int afterLoadCount,
            int beforeSaveCount,
            int afterSaveCount)
        {
            return new OpenACAISonsSaveLifecycleSnapshot(
                evt.TimestampUtc.ToString("O"),
                ServiceKey,
                HookModel,
                HarmonyPatchesInstalled,
                PatchStatus,
                PatchDetail,
                InstalledTargets,
                LegacySonsSdkRelayEnabled,
                LegacySonsSdkRelayReason,
                evt.Operation + "." + evt.Phase,
                evt.Operation,
                evt.Phase,
                evt.Directory,
                evt.GameName,
                evt.SaveGameType,
                evt.SavePlayerOnly,
                evt.SelectedSaveId,
                beforeLoadCount,
                afterLoadCount,
                beforeSaveCount,
                afterSaveCount);
        }
    }

    public sealed class OpenACAISonsSaveLifecycleEvent
    {
        internal OpenACAISonsSaveLifecycleEvent(
            string operation,
            string phase,
            string directory,
            string gameName,
            string saveGameType,
            bool? savePlayerOnly,
            uint? selectedSaveId,
            DateTimeOffset timestampUtc)
        {
            Operation = operation;
            Phase = phase;
            Directory = directory;
            GameName = gameName;
            SaveGameType = saveGameType;
            SavePlayerOnly = savePlayerOnly;
            SelectedSaveId = selectedSaveId;
            TimestampUtc = timestampUtc;
        }

        public string Operation { get; }
        public string Phase { get; }
        public string Directory { get; }
        public string GameName { get; }
        public string SaveGameType { get; }
        public bool? SavePlayerOnly { get; }
        public uint? SelectedSaveId { get; }
        public DateTimeOffset TimestampUtc { get; }
    }
}
