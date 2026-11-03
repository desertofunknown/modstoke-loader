// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;
using SaveHookPatchState = SonsSdk.SaveHookPatchState;
using SdkSaveHooks = SonsSdk.SdkEvents.SavingCallbackPatches;

namespace Modstoke.Sons
{
    public static class SaveLifecycle
    {
        public const string ServiceKey = "openacai.sons.saveLifecycle";

        private static readonly object SyncRoot = new object();
        private static SaveLifecycleSnapshot _state = SaveLifecycleSnapshot.CreateUnknown();
        private static HarmonyLib.Harmony _harmony;
        private static bool _initialized;
        private static int _beforeLoadCount;
        private static int _afterLoadCount;
        private static int _beforeSaveCount;
        private static int _afterSaveCount;

        public static event Action<SaveLifecycleEvent> Lifecycle;

        public static SaveLifecycleSnapshot State
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

            if (registerProvider && !MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK save lifecycle provider could not be registered.");
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

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot harmonySupport, Action<string> addStartupWarning)
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

            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-save-lifecycle");
            var installedTargets = new List<string>();

            TryPatch(
                harmony,
                "SaveGameManager.Load",
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Load), new[] { typeof(string), typeof(SaveGameType) }),
                nameof(SaveLifecyclePatches.BeforeLoad),
                nameof(SaveLifecyclePatches.AfterLoad),
                nameof(SaveLifecyclePatches.LoadFinalizer),
                installedTargets,
                addStartupWarning);

            TryPatch(
                harmony,
                "SaveGameManager.Save",
                AccessTools.Method(typeof(SaveGameManager), nameof(SaveGameManager.Save), new[] { typeof(string), typeof(string), typeof(bool) }),
                nameof(SaveLifecyclePatches.BeforeSave),
                nameof(SaveLifecyclePatches.AfterSave),
                nameof(SaveLifecyclePatches.SaveFinalizer),
                installedTargets,
                addStartupWarning);

            lock (SyncRoot)
            {
                _harmony = harmony;
            }

            var allTargetsInstalled = installedTargets.Count == 2;
            var status = allTargetsInstalled ? "ready" : installedTargets.Count == 0 ? "failed" : "partial";
            var detailText = allTargetsInstalled
                ? "Modstoke save/load lifecycle patches are installed."
                : "Modstoke save/load lifecycle patches installed " + installedTargets.Count + " of 2 target(s).";
            MarkPatchStatus(allTargetsInstalled, status, detailText, installedTargets.ToArray());
        }

        internal static void PublishBeforeLoad(string directory, string saveGameType, uint? selectedSaveId)
        {
            Publish("Load", "Before", directory, null, saveGameType, null, selectedSaveId);
        }

        internal static void PublishAfterLoad(string directory, string saveGameType, uint? selectedSaveId, bool success)
        {
            Publish("Load", "After", directory, null, saveGameType, null, selectedSaveId, success);
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
            uint? selectedSaveId,
            bool? success = null)
        {
            var timestamp = DateTimeOffset.UtcNow;
            var evt = new SaveLifecycleEvent(
                operation,
                phase,
                directory,
                gameName,
                saveGameType,
                savePlayerOnly,
                selectedSaveId,
                timestamp,
                success);
            Action<SaveLifecycleEvent> handlers;
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

            Modding.Dispatch("OpenACAISonsSaveLifecycle." + operation + "." + phase, handlers, evt);
            if (relayLegacy)
            {
                RelayLegacySonsSdkEvent(evt);
            }

            var eventLabel = operation.ToLowerInvariant() + "-" + phase.ToLowerInvariant();
            Timings.Mark("sons.saveLifecycle", eventLabel, directory);
            RuntimeEntryPoint.RequestLoaderStateRefresh("sons-save-lifecycle-" + eventLabel);
        }

        private static void RelayLegacySonsSdkEvent(SaveLifecycleEvent evt)
        {
            if (string.Equals(evt.Operation, "Load", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(evt.Phase, "Before", StringComparison.OrdinalIgnoreCase))
                {
                    LegacyLifecycleBridge.PublishBeforeLoadSave();
                    return;
                }

                LegacyLifecycleBridge.PublishAfterLoadSave();
                return;
            }

            if (string.Equals(evt.Operation, "Save", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(evt.Phase, "Before", StringComparison.OrdinalIgnoreCase))
                {
                    LegacyLifecycleBridge.PublishBeforeSave();
                    return;
                }

                LegacyLifecycleBridge.PublishAfterSave(evt.SavePlayerOnly.GetValueOrDefault());
            }
        }

        private static void TryPatch(
            HarmonyLib.Harmony harmony,
            string label,
            MethodInfo target,
            string prefixName,
            string postfixName,
            string finalizerName,
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

                var patchType = typeof(SaveLifecyclePatches);
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(AccessTools.Method(patchType, prefixName)),
                    postfix: new HarmonyMethod(AccessTools.Method(patchType, postfixName)),
                    finalizer: new HarmonyMethod(AccessTools.Method(patchType, finalizerName)));
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
            Modding.SetService(ServiceKey, _state);
        }

        private static MenuContentSnapshot CreateContent()
        {
            var snapshot = State;
            return new MenuContentSnapshot(
                "Sons Save Lifecycle",
                "Sons save/load hook state and legacy SonsSdk relay status.",
                new[]
                {
                    CreateHookSection(snapshot),
                    CreateEventSection(snapshot)
                });
        }

        private static MenuSectionSnapshot CreateHookSection(SaveLifecycleSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Lifecycle Hooks",
                new[]
                {
                    Row("Service", snapshot.ServiceKey, PatchStatus(snapshot), snapshot.PatchDetail),
                    Row("Hook Model", snapshot.HookModel, snapshot.HarmonyPatchesInstalled ? "READY" : "INFO", "Modstoke Harmony patch surface for Sons save/load events."),
                    Row("Patch Status", snapshot.PatchStatus, PatchStatus(snapshot), snapshot.PatchDetail),
                    Row("Installed Targets", FormatTargets(snapshot.InstalledTargets), snapshot.HarmonyPatchesInstalled ? "READY" : snapshot.InstalledTargets.Count > 0 ? "CHECK" : "PENDING", "SaveGameManager targets currently patched by Modstoke."),
                    Row("Legacy Relay", snapshot.LegacySonsSdkRelayEnabled ? "Enabled" : "Disabled", snapshot.LegacySonsSdkRelayEnabled ? "READY" : "INFO", snapshot.LegacySonsSdkRelayReason)
                });
        }

        private static MenuSectionSnapshot CreateEventSection(SaveLifecycleSnapshot snapshot)
        {
            return new MenuSectionSnapshot(
                "Observed Events",
                new[]
                {
                    Row("Last Event", string.IsNullOrWhiteSpace(snapshot.LastEvent) ? "None" : snapshot.LastEvent, string.IsNullOrWhiteSpace(snapshot.LastEvent) ? "PENDING" : "INFO", "Most recent save lifecycle event."),
                    Row("Native Result", snapshot.LastSuccess.HasValue ? snapshot.LastSuccess.Value ? "Accepted" : "Rejected" : "Unverified", snapshot.LastSuccess == false ? "FAILED" : "INFO", "Loads report the native return value. A returned save call does not confirm persisted data."),
                    Row("Save Scope", snapshot.LastSavePlayerOnly.HasValue ? snapshot.LastSavePlayerOnly.Value ? "Local player only" : "World" : "Not a save event", "INFO", "A world save does not save remote players' local inventory."),
                    Row("Last Directory", string.IsNullOrWhiteSpace(snapshot.LastDirectory) ? "None" : snapshot.LastDirectory, string.IsNullOrWhiteSpace(snapshot.LastDirectory) ? "PENDING" : "INFO", "Sons save directory observed on the most recent event."),
                    Row("Before Load", snapshot.BeforeLoadCount.ToString(), "INFO", "Before-load events observed by Modstoke."),
                    Row("After Load", snapshot.AfterLoadCount.ToString(), "INFO", "After-load events observed by Modstoke."),
                    Row("Before Save", snapshot.BeforeSaveCount.ToString(), "INFO", "Before-save events observed by Modstoke."),
                    Row("After Save", snapshot.AfterSaveCount.ToString(), "INFO", "After-save events observed by Modstoke.")
                });
        }

        private static string PatchStatus(SaveLifecycleSnapshot snapshot)
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

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
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

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsSaveLifecycle;
            public string DisplayName => "Sons Save Lifecycle";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 723;
            public string Source => "Modstoke Loader";
            public string Description => "Show Sons save/load lifecycle hook state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return SaveLifecycle.CreateContent();
            }
        }
    }

    internal static class SaveLifecyclePatches
    {
        internal static void BeforeLoad(string dir, SaveGameType saveGameType, bool __runOriginal, out WorldRecordLoad __state)
        {
            __state = null;
            if (!__runOriginal) return;
            __state = WorldRecords.BeforeNativeLoad(dir, saveGameType);
            SaveLifecycle.PublishBeforeLoad(dir, saveGameType.ToString(), TryGetSelectedSaveId());
        }

        internal static void AfterLoad(string dir, SaveGameType saveGameType, bool __result, bool __runOriginal, WorldRecordLoad __state)
        {
            WorldRecords.AfterNativeLoad(__state, __runOriginal && __result);
            SaveLifecycle.PublishAfterLoad(dir, saveGameType.ToString(), TryGetSelectedSaveId(), __runOriginal && __result);
        }

        internal static bool BeforeSave(string dir, string gameName, bool savePlayerOnly, bool __runOriginal, out WorldRecordSave __state)
        {
            __state = null;
            if (!__runOriginal) return true;
            if (!WorldRecords.BeforeNativeSave(savePlayerOnly, out __state))
            {
                __state = new WorldRecordSave { Refused = true };
                return false;
            }
            SaveLifecycle.PublishBeforeSave(dir, gameName, savePlayerOnly);
            return true;
        }

        internal static void AfterSave(string dir, string gameName, bool savePlayerOnly, bool __runOriginal, WorldRecordSave __state)
        {
            if (__state?.Refused == true) return;
            WorldRecords.AfterNativeSave(__state);
            if (__runOriginal) SaveLifecycle.PublishAfterSave(dir, gameName, savePlayerOnly);
        }

        internal static Exception LoadFinalizer(Exception __exception, WorldRecordLoad __state)
        {
            if (__exception != null) WorldRecords.AfterNativeLoad(__state, false);
            return __exception;
        }

        internal static Exception SaveFinalizer(Exception __exception, WorldRecordSave __state)
        {
            if (__exception != null) WorldRecords.AfterNativeSave(__state);
            return __exception;
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

    public sealed class SaveLifecycleSnapshot
    {
        private SaveLifecycleSnapshot(
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
            bool? lastSuccess,
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
            LastSuccess = lastSuccess;
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
        /// <summary>The native After Load result; null for before-events and saves, whose persistence is not verified.</summary>
        public bool? LastSuccess { get; }
        public int BeforeLoadCount { get; }
        public int AfterLoadCount { get; }
        public int BeforeSaveCount { get; }
        public int AfterSaveCount { get; }

        internal static SaveLifecycleSnapshot CreateUnknown()
        {
            return new SaveLifecycleSnapshot(
                DateTimeOffset.UtcNow.ToString("O"),
                SaveLifecycle.ServiceKey,
                "openacai-harmony-savegame-manager",
                false,
                "not-installed",
                "Modstoke save/load lifecycle patches have not been installed yet.",
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
                null,
                0,
                0,
                0,
                0);
        }

        internal SaveLifecycleSnapshot WithLegacyRelay(bool enabled, string reason, DateTimeOffset timestampUtc)
        {
            return new SaveLifecycleSnapshot(
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
                LastSuccess,
                BeforeLoadCount,
                AfterLoadCount,
                BeforeSaveCount,
                AfterSaveCount);
        }

        internal SaveLifecycleSnapshot WithPatchStatus(
            bool installed,
            string status,
            string detail,
            IReadOnlyCollection<string> installedTargets,
            DateTimeOffset timestampUtc)
        {
            return new SaveLifecycleSnapshot(
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
                LastSuccess,
                BeforeLoadCount,
                AfterLoadCount,
                BeforeSaveCount,
                AfterSaveCount);
        }

        internal SaveLifecycleSnapshot WithEvent(
            SaveLifecycleEvent evt,
            int beforeLoadCount,
            int afterLoadCount,
            int beforeSaveCount,
            int afterSaveCount)
        {
            return new SaveLifecycleSnapshot(
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
                evt.Success,
                beforeLoadCount,
                afterLoadCount,
                beforeSaveCount,
                afterSaveCount);
        }
    }

    public sealed class SaveLifecycleEvent
    {
        internal SaveLifecycleEvent(
            string operation,
            string phase,
            string directory,
            string gameName,
            string saveGameType,
            bool? savePlayerOnly,
            uint? selectedSaveId,
            DateTimeOffset timestampUtc,
            bool? success)
        {
            Operation = operation;
            Phase = phase;
            Directory = directory;
            GameName = gameName;
            SaveGameType = saveGameType;
            SavePlayerOnly = savePlayerOnly;
            SelectedSaveId = selectedSaveId;
            TimestampUtc = timestampUtc;
            Success = success;
        }

        public string Operation { get; }
        public string Phase { get; }
        public string Directory { get; }
        public string GameName { get; }
        public string SaveGameType { get; }
        public bool? SavePlayerOnly { get; }
        public uint? SelectedSaveId { get; }
        public DateTimeOffset TimestampUtc { get; }
        /// <summary>
        /// The native Boolean result for an After Load event; null for before-events and saves.
        /// True means the game accepted the load, not that every subscriber succeeded.
        /// Save returns no success result; its postfix does not prove a completed or durable write.
        /// </summary>
        public bool? Success { get; }
    }
}
