// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public OpenACAI menu provider registry. Built-in loader pages, third-party
// mods, and private first-party tools should all declare menu surfaces here.

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenACAI.Loader
{
    public static class OpenACAIMenu
    {
        public const string ServiceKey = "openacai.loader.menu";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAIMenuProviderSnapshot> Providers =
            new Dictionary<string, OpenACAIMenuProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IOpenACAIMenuProvider> ProviderInstances =
            new Dictionary<string, IOpenACAIMenuProvider>(StringComparer.OrdinalIgnoreCase);

        public static event Action<OpenACAIMenuProviderSnapshot> ProviderChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Providers.Clear();
                ProviderInstances.Clear();
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.AllMods, "All Mods", OpenACAIMenuProviderKinds.Tab, 100, "Show detected Modstoke-native and compatibility mods. Libraries are shown in their own tab.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.OpenAcaiMods, "Modstoke", OpenACAIMenuProviderKinds.Tab, 110, "Show Modstoke-native mod packages from the OpenACAI/Mods runtime root.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.RedLoaderMods, "RedLoader", OpenACAIMenuProviderKinds.Tab, 120, "Show legacy RedLoader-style mods hosted through the OpenACAI compatibility shim.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.Libraries, "Libraries", OpenACAIMenuProviderKinds.Tab, 130, "Show SDK/runtime helper libraries and dependency assemblies.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.BepInExPlugins, "BepInEx", OpenACAIMenuProviderKinds.Tab, 150, "Show legacy BepInEx plugin assemblies visible to the OpenACAI compatibility shim.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.Issues, "Issues", OpenACAIMenuProviderKinds.Tab, 170, "Show mods and libraries with warnings, missing files, or duplicate identifiers.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.Extensions, "Ext", OpenACAIMenuProviderKinds.Tab, 190, "Show menu, settings, status, and action providers registered by mods and tools.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.Credits, "Credits", OpenACAIMenuProviderKinds.Credits, 900, "Show Modstoke Loader, upstream runtime, and mod author credits.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.ModConfig, "Mod Config", OpenACAIMenuProviderKinds.Settings, 300, "Edit detected mod configuration files through friendly or raw text modes.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.ChainloaderStatus, "Chainloader", OpenACAIMenuProviderKinds.Status, 690, "Show OpenACAI bootstrap, preloader, and chainloader ownership state.");
                RegisterBuiltInNoLock(OpenACAIMenuProviderIds.ProtectedRuntime, "Protected Runtime", OpenACAIMenuProviderKinds.Status, 700, "Show disclosed protected-server and loader capability status when available.");
                PublishServiceNoLock();
            }
        }

        public static IReadOnlyList<OpenACAIMenuProviderSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return SnapshotNoLock();
            }
        }

        public static bool TryRegisterProvider(IOpenACAIMenuProvider provider)
        {
            OpenACAIMenuProviderSnapshot snapshot;
            Action<OpenACAIMenuProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                snapshot = OpenACAIMenuProviderSnapshot.FromProvider(provider, false);
                if (snapshot == null || Providers.ContainsKey(snapshot.Id))
                {
                    return false;
                }

                Providers[snapshot.Id] = snapshot;
                ProviderInstances[snapshot.Id] = provider;
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            DispatchProviderChanged(snapshot, handlers);
            return true;
        }

        public static bool RegisterOrUpdateProvider(IOpenACAIMenuProvider provider)
        {
            return RegisterOrUpdateProvider(provider, false, false);
        }

        internal static bool RegisterOrUpdateBuiltInProvider(IOpenACAIMenuProvider provider)
        {
            return RegisterOrUpdateProvider(provider, true, true);
        }

        private static bool RegisterOrUpdateProvider(IOpenACAIMenuProvider provider, bool allowBuiltInReplacement, bool builtIn)
        {
            OpenACAIMenuProviderSnapshot snapshot;
            Action<OpenACAIMenuProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                snapshot = OpenACAIMenuProviderSnapshot.FromProvider(provider, builtIn);
                if (snapshot == null)
                {
                    return false;
                }

                OpenACAIMenuProviderSnapshot existing;
                if (Providers.TryGetValue(snapshot.Id, out existing) && existing.BuiltIn && !allowBuiltInReplacement)
                {
                    return false;
                }

                Providers[snapshot.Id] = snapshot;
                ProviderInstances[snapshot.Id] = provider;
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            DispatchProviderChanged(snapshot, handlers);
            return true;
        }

        public static bool TryUnregisterProvider(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            lock (SyncRoot)
            {
                OpenACAIMenuProviderSnapshot existing;
                if (!Providers.TryGetValue(id, out existing) || existing.BuiltIn)
                {
                    return false;
                }

                Providers.Remove(id);
                ProviderInstances.Remove(id);
                PublishServiceNoLock();
                return true;
            }
        }

        public static bool TryGetProviderContent(string id, out OpenACAIMenuContentSnapshot content)
        {
            content = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            IOpenACAIMenuProvider provider;
            lock (SyncRoot)
            {
                ProviderInstances.TryGetValue(id, out provider);
            }

            var contentProvider = provider as IOpenACAIMenuContentProvider;
            if (contentProvider == null)
            {
                return false;
            }

            try
            {
                content = contentProvider.CreateContent();
                return content != null;
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI menu provider content failed for '" + id + "': " + ex.Message);
                return false;
            }
        }

        public static bool TryGetProviderCommands(string id, out OpenACAICommandProviderSnapshot commands)
        {
            commands = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            IOpenACAIMenuProvider provider;
            lock (SyncRoot)
            {
                ProviderInstances.TryGetValue(id, out provider);
            }

            var commandProvider = provider as IOpenACAICommandProvider;
            if (commandProvider == null)
            {
                return false;
            }

            try
            {
                var providerSnapshot = OpenACAIMenuProviderSnapshot.FromProvider(provider, false);
                commands = OpenACAICommandProviderSnapshot.FromProvider(providerSnapshot, commandProvider);
                return commands != null;
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI menu provider commands failed for '" + id + "': " + ex.Message);
                return false;
            }
        }

        public static bool TryInvokeProviderCommand(string providerId, string commandId, out OpenACAICommandResultSnapshot result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(commandId))
            {
                result = OpenACAICommandResultSnapshot.Failure("Command provider id and command id are required.");
                return false;
            }

            IOpenACAIMenuProvider provider;
            lock (SyncRoot)
            {
                ProviderInstances.TryGetValue(providerId.Trim(), out provider);
            }

            var commandProvider = provider as IOpenACAICommandProvider;
            if (commandProvider == null)
            {
                result = OpenACAICommandResultSnapshot.Failure("Provider does not expose commands.");
                return false;
            }

            try
            {
                if (!commandProvider.TryInvokeCommand(commandId.Trim(), out result))
                {
                    if (result == null)
                    {
                        result = OpenACAICommandResultSnapshot.Failure("Command was not invoked.");
                    }

                    return false;
                }

                if (result == null)
                {
                    result = OpenACAICommandResultSnapshot.Success("Command invoked.");
                }

                lock (SyncRoot)
                {
                    var providerKey = provider.Id.Trim();
                    OpenACAIMenuProviderSnapshot existing;
                    var builtIn = Providers.TryGetValue(providerKey, out existing) && existing.BuiltIn;
                    Providers[providerKey] = OpenACAIMenuProviderSnapshot.FromProvider(provider, builtIn);
                    PublishServiceNoLock();
                }

                return result.Succeeded;
            }
            catch (Exception ex)
            {
                result = OpenACAICommandResultSnapshot.Failure("Command failed: " + ex.Message);
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI menu provider command failed for '" + providerId + "/" + commandId + "': " + ex.Message);
                return false;
            }
        }

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        private static void RegisterBuiltInNoLock(string id, string displayName, string kind, int priority, string description)
        {
            var provider = new OpenACAIMenuProviderDescriptor(
                id,
                displayName,
                kind,
                priority,
                "Modstoke Loader",
                description,
                false,
                true);
            var snapshot = OpenACAIMenuProviderSnapshot.FromProvider(provider, true);
            if (snapshot != null)
            {
                Providers[snapshot.Id] = snapshot;
                ProviderInstances[snapshot.Id] = provider;
            }
        }

        private static object CreateStateNoLock()
        {
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                providerCount = Providers.Count,
                commandProviderCount = Providers.Values.Count(provider => provider.HasCommands),
                providers = SnapshotNoLock().ToArray()
            };
        }

        private static IReadOnlyList<OpenACAIMenuProviderSnapshot> SnapshotNoLock()
        {
            return Providers.Values
                .OrderBy(provider => provider.Priority)
                .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static void PublishServiceNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void DispatchProviderChanged(OpenACAIMenuProviderSnapshot snapshot, Action<OpenACAIMenuProviderSnapshot> handlers)
        {
            try
            {
                handlers?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI menu provider event subscriber failed: " + ex.Message);
            }
        }
    }

    public interface IOpenACAIMenuProvider
    {
        string Id { get; }
        string DisplayName { get; }
        string Kind { get; }
        int Priority { get; }
        string Source { get; }
        string Description { get; }
        bool RequiresAuthorization { get; }
        bool Enabled { get; }
    }

    public interface IOpenACAIMenuContentProvider : IOpenACAIMenuProvider
    {
        OpenACAIMenuContentSnapshot CreateContent();
    }

    public interface IOpenACAICommandProvider : IOpenACAIMenuProvider
    {
        IReadOnlyList<OpenACAICommandSnapshot> GetCommands();
        bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result);
    }

    public sealed class OpenACAIMenuProviderDescriptor : IOpenACAIMenuProvider
    {
        public OpenACAIMenuProviderDescriptor(
            string id,
            string displayName,
            string kind,
            int priority,
            string source,
            string description,
            bool requiresAuthorization,
            bool enabled)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Priority = priority;
            Source = source;
            Description = description;
            RequiresAuthorization = requiresAuthorization;
            Enabled = enabled;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public int Priority { get; }
        public string Source { get; }
        public string Description { get; }
        public bool RequiresAuthorization { get; }
        public bool Enabled { get; }
    }

    public sealed class OpenACAIMenuProviderSnapshot
    {
        private OpenACAIMenuProviderSnapshot(
            string id,
            string displayName,
            string kind,
            int priority,
            string source,
            string description,
            bool requiresAuthorization,
            bool enabled,
            bool hasContent,
            bool hasCommands,
            bool builtIn,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Priority = priority;
            Source = source;
            Description = description;
            RequiresAuthorization = requiresAuthorization;
            Enabled = enabled;
            HasContent = hasContent;
            HasCommands = hasCommands;
            BuiltIn = builtIn;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public int Priority { get; }
        public string Source { get; }
        public string Description { get; }
        public bool RequiresAuthorization { get; }
        public bool Enabled { get; }
        public bool HasContent { get; }
        public bool HasCommands { get; }
        public bool BuiltIn { get; }
        public string UpdatedUtc { get; }

        internal static OpenACAIMenuProviderSnapshot FromProvider(IOpenACAIMenuProvider provider, bool builtIn)
        {
            if (provider == null || string.IsNullOrWhiteSpace(provider.Id))
            {
                return null;
            }

            return new OpenACAIMenuProviderSnapshot(
                provider.Id.Trim(),
                string.IsNullOrWhiteSpace(provider.DisplayName) ? provider.Id.Trim() : provider.DisplayName.Trim(),
                string.IsNullOrWhiteSpace(provider.Kind) ? OpenACAIMenuProviderKinds.Tab : provider.Kind.Trim(),
                provider.Priority,
                string.IsNullOrWhiteSpace(provider.Source) ? "Unknown" : provider.Source.Trim(),
                provider.Description ?? "",
                provider.RequiresAuthorization,
                provider.Enabled,
                provider is IOpenACAIMenuContentProvider,
                provider is IOpenACAICommandProvider,
                builtIn,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAICommandProviderSnapshot
    {
        private OpenACAICommandProviderSnapshot(
            string providerId,
            string title,
            string subtitle,
            IReadOnlyList<OpenACAICommandSnapshot> commands,
            string updatedUtc)
        {
            ProviderId = providerId;
            Title = string.IsNullOrWhiteSpace(title) ? providerId : title;
            Subtitle = subtitle ?? "";
            Commands = commands ?? Array.Empty<OpenACAICommandSnapshot>();
            UpdatedUtc = updatedUtc;
        }

        public string ProviderId { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public IReadOnlyList<OpenACAICommandSnapshot> Commands { get; }
        public string UpdatedUtc { get; }

        internal static OpenACAICommandProviderSnapshot FromProvider(OpenACAIMenuProviderSnapshot provider, IOpenACAICommandProvider commandProvider)
        {
            if (provider == null || commandProvider == null)
            {
                return null;
            }

            IReadOnlyList<OpenACAICommandSnapshot> commands;
            try
            {
                commands = (commandProvider.GetCommands() ?? Array.Empty<OpenACAICommandSnapshot>())
                    .Where(command => command != null)
                    .ToArray();
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("OpenACAI command provider snapshot failed for '" + provider.Id + "': " + ex.Message);
                commands = Array.Empty<OpenACAICommandSnapshot>();
            }

            return new OpenACAICommandProviderSnapshot(
                provider.Id,
                provider.DisplayName,
                provider.Description,
                commands,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAICommandSnapshot
    {
        public OpenACAICommandSnapshot(
            string id,
            string displayName,
            string description,
            string status,
            bool enabled,
            bool requiresConfirmation)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "command" : id.Trim();
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Id : displayName.Trim();
            Description = description ?? "";
            Status = status ?? "";
            Enabled = enabled;
            RequiresConfirmation = requiresConfirmation;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Description { get; }
        public string Status { get; }
        public bool Enabled { get; }
        public bool RequiresConfirmation { get; }
    }

    public sealed class OpenACAICommandResultSnapshot
    {
        private OpenACAICommandResultSnapshot(bool succeeded, string status, string detail, string updatedUtc)
        {
            Succeeded = succeeded;
            Status = string.IsNullOrWhiteSpace(status) ? (succeeded ? "READY" : "FAILED") : status;
            Detail = detail ?? "";
            UpdatedUtc = updatedUtc;
        }

        public bool Succeeded { get; }
        public string Status { get; }
        public string Detail { get; }
        public string UpdatedUtc { get; }

        public static OpenACAICommandResultSnapshot Success(string detail)
        {
            return new OpenACAICommandResultSnapshot(true, "READY", detail, DateTimeOffset.UtcNow.ToString("O"));
        }

        public static OpenACAICommandResultSnapshot Failure(string detail)
        {
            return new OpenACAICommandResultSnapshot(false, "FAILED", detail, DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class OpenACAIMenuContentSnapshot
    {
        public OpenACAIMenuContentSnapshot(string title, string subtitle, IEnumerable<OpenACAIMenuSectionSnapshot> sections)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Provider" : title;
            Subtitle = subtitle ?? "";
            Sections = (sections ?? Array.Empty<OpenACAIMenuSectionSnapshot>()).Where(section => section != null).ToArray();
        }

        public string Title { get; }
        public string Subtitle { get; }
        public IReadOnlyList<OpenACAIMenuSectionSnapshot> Sections { get; }
    }

    public sealed class OpenACAIMenuSectionSnapshot
    {
        public OpenACAIMenuSectionSnapshot(string title, IEnumerable<OpenACAIMenuRowSnapshot> rows)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Details" : title;
            Rows = (rows ?? Array.Empty<OpenACAIMenuRowSnapshot>()).Where(row => row != null).ToArray();
        }

        public string Title { get; }
        public IReadOnlyList<OpenACAIMenuRowSnapshot> Rows { get; }
    }

    public sealed class OpenACAIMenuRowSnapshot
    {
        public OpenACAIMenuRowSnapshot(string label, string value, string status, string detail)
        {
            Label = string.IsNullOrWhiteSpace(label) ? "Item" : label;
            Value = value ?? "";
            Status = status ?? "";
            Detail = detail ?? "";
        }

        public string Label { get; }
        public string Value { get; }
        public string Status { get; }
        public string Detail { get; }
    }

    public static class OpenACAIMenuProviderKinds
    {
        public const string Tab = "tab";
        public const string Settings = "settings";
        public const string CommandGroup = "command-group";
        public const string Status = "status";
        public const string Credits = "credits";
    }

    public static class OpenACAIMenuProviderIds
    {
        public const string AllMods = "openacai.menu.mods.all";
        public const string OpenAcaiMods = "openacai.menu.mods.openacai";
        public const string RedLoaderMods = "openacai.menu.mods.redloader";
        public const string Libraries = "openacai.menu.mods.libraries";
        public const string BepInExPlugins = "openacai.menu.mods.bepinex";
        public const string Issues = "openacai.menu.mods.issues";
        public const string Extensions = "openacai.menu.mods.extensions";
        public const string Credits = "openacai.menu.mods.credits";
        public const string ModConfig = "openacai.menu.mods.config";
        public const string InputBindings = "openacai.menu.input.bindings";
        public const string ChainloaderStatus = "openacai.menu.runtime.chainloader";
        public const string ProtectedRuntime = "openacai.menu.protectedRuntime";
        public const string NativeLifecycleDispatcher = "openacai.menu.runtime.nativeLifecycleDispatch";
        public const string NativeLifecycleInvocationAdapter = "openacai.menu.runtime.nativeLifecycleInvocationAdapter";
        public const string NativeLifecycleInvocationBridge = "openacai.menu.runtime.nativeLifecycleInvocation";
        public const string NativeLifecycleInvocationJournal = "openacai.menu.runtime.nativeLifecycleInvocationJournal";
        public const string NativeLifecycleInvocationScheduler = "openacai.menu.runtime.nativeLifecycleInvocationScheduler";
        public const string NativeLifecycleInvocationExecutor = "openacai.menu.runtime.nativeLifecycleInvocationExecutor";
        public const string NativeLifecycleInvocationTokenResolver = "openacai.menu.runtime.nativeLifecycleInvocationTokens";
        public const string NativeLifecycleRegistry = "openacai.menu.runtime.nativeLifecycle";
        public const string NativeModHost = "openacai.menu.runtime.nativeModHost";
        public const string BepInExPluginCompatibility = "openacai.menu.runtime.bepInExPluginCompatibility";
        public const string RuntimeLibraries = "openacai.menu.runtime.libraries";
        public const string SonsAssets = "openacai.menu.sons.assets";
        public const string SonsBuilding = "openacai.menu.sons.building";
        public const string SonsCrafting = "openacai.menu.sons.crafting";
        public const string SonsEnvironment = "openacai.menu.sons.environment";
        public const string SonsInventory = "openacai.menu.sons.inventory";
        public const string SonsItemRegistration = "openacai.menu.sons.itemRegistration";
        public const string SonsItems = "openacai.menu.sons.items";
        public const string SonsNetworking = "openacai.menu.sons.networking";
        public const string SonsRuntimeState = "openacai.menu.sons.runtimeState";
        public const string SonsSaveData = "openacai.menu.sons.saveData";
        public const string SonsSaveLifecycle = "openacai.menu.sons.saveLifecycle";
        public const string SonsSaveRegistry = "openacai.menu.sons.saveRegistry";
    }
}

