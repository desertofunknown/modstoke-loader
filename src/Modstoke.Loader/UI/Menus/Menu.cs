// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Public Modstoke menu provider registry. Built-in loader pages, third-party
// mods, and private first-party tools should all declare menu surfaces here.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Loader
{
    public static partial class MenuRegistry
    {
        public const string ServiceKey = "openacai.loader.menu";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, MenuProviderSnapshot> Providers =
            new Dictionary<string, MenuProviderSnapshot>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, IMenuProvider> ProviderInstances =
            new Dictionary<string, IMenuProvider>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, long> ContentRevisions =
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private static IReadOnlyList<MenuProviderSnapshot> _snapshot;
        private static long _contentRevision;

        public static event Action<MenuProviderSnapshot> ProviderChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Providers.Clear();
                ProviderInstances.Clear();
                ContentRevisions.Clear();
                _snapshot = null;
                RegisterBuiltInNoLock(MenuProviderIds.AllMods, "All Mods", MenuProviderKinds.Tab, 100, "Show detected Modstoke-native and compatibility mods. Libraries are shown in their own tab.");
                RegisterBuiltInNoLock(MenuProviderIds.OpenAcaiMods, "Modstoke", MenuProviderKinds.Tab, 110, "Show Modstoke-native mod packages from the Modstoke/Mods folder.");
                RegisterBuiltInNoLock(MenuProviderIds.RedLoaderMods, "RedLoader", MenuProviderKinds.Tab, 120, "Show legacy RedLoader-style mods hosted through the Modstoke compatibility shim.");
                RegisterBuiltInNoLock(MenuProviderIds.Libraries, "Libraries", MenuProviderKinds.Tab, 130, "Show SDK/runtime helper libraries and dependency assemblies.");
                RegisterBuiltInNoLock(MenuProviderIds.BepInExPlugins, "BepInEx", MenuProviderKinds.Tab, 150, "Show legacy BepInEx plugin assemblies visible to the Modstoke compatibility shim.");
                RegisterBuiltInNoLock(MenuProviderIds.Issues, "Issues", MenuProviderKinds.Tab, 170, "Show mods and libraries with warnings, missing files, or duplicate identifiers.");
                RegisterBuiltInNoLock(MenuProviderIds.Extensions, "Ext", MenuProviderKinds.Tab, 190, "Show menu, settings, status, and action providers registered by mods and tools.");
                RegisterBuiltInNoLock(MenuProviderIds.Credits, "Credits", MenuProviderKinds.Credits, 900, "Show Modstoke Loader, upstream runtime, and mod author credits.");
                RegisterBuiltInNoLock(MenuProviderIds.ModConfig, "Mod Config", MenuProviderKinds.Settings, 300, "Edit detected mod configuration files through friendly or raw text modes.");
                RegisterBuiltInNoLock(MenuProviderIds.ChainloaderStatus, "Chainloader", MenuProviderKinds.Status, 690, "Show Modstoke bootstrap, preloader, and chainloader ownership state.");
                RegisterBuiltInNoLock(MenuProviderIds.ProtectedRuntime, "Protected Runtime", MenuProviderKinds.Status, 700, "Show disclosed protected-server and loader capability status when available.");
                PublishServiceNoLock();
            }
        }

        public static IReadOnlyList<MenuProviderSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return SnapshotNoLock();
            }
        }

        public static bool TryRegisterProvider(IMenuProvider provider)
        {
            var snapshot = CaptureProvider(provider, false);
            if (snapshot == null)
                return false;
            Action<MenuProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                if (Providers.ContainsKey(snapshot.Id))
                {
                    return false;
                }

                Providers[snapshot.Id] = snapshot;
                ProviderInstances[snapshot.Id] = provider;
                _snapshot = null;
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            DispatchProviderChanged(snapshot, provider, handlers);
            return true;
        }

        public static bool RegisterOrUpdateProvider(IMenuProvider provider)
        {
            return RegisterOrUpdateProvider(provider, false, false);
        }

        internal static bool RegisterOrUpdateBuiltInProvider(IMenuProvider provider)
        {
            return RegisterOrUpdateProvider(provider, true, true);
        }

        private static bool RegisterOrUpdateProvider(IMenuProvider provider, bool allowBuiltInReplacement, bool builtIn)
        {
            var snapshot = CaptureProvider(provider, builtIn);
            if (snapshot == null)
                return false;
            Action<MenuProviderSnapshot> handlers;
            lock (SyncRoot)
            {
                if (Providers.TryGetValue(snapshot.Id, out var existing) && existing.BuiltIn && !allowBuiltInReplacement)
                {
                    return false;
                }

                Providers[snapshot.Id] = snapshot;
                ProviderInstances[snapshot.Id] = provider;
                _snapshot = null;
                PublishServiceNoLock();
                handlers = ProviderChanged;
            }

            DispatchProviderChanged(snapshot, provider, handlers);
            return true;
        }

        /// <summary>Unregisters the current non-built-in provider for an ID, regardless of its owner.</summary>
        public static bool TryUnregisterProvider(string id)
        {
            return TryUnregisterProviderCore(id, null);
        }

        /// <summary>Unregisters a non-built-in provider only if the supplied instance still owns the ID.</summary>
        /// <remarks>A null expected provider never removes a registration.</remarks>
        public static bool TryUnregisterProvider(string id, IMenuProvider expectedProvider)
        {
            return expectedProvider != null && TryUnregisterProviderCore(id, expectedProvider);
        }

        private static bool TryUnregisterProviderCore(string id, IMenuProvider expectedProvider)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            id = id.Trim();
            lock (SyncRoot)
            {
                if (!Providers.TryGetValue(id, out var existing) || existing.BuiltIn)
                {
                    return false;
                }

                if (expectedProvider != null
                    && (!ProviderInstances.TryGetValue(id, out var currentProvider)
                        || !ReferenceEquals(currentProvider, expectedProvider)))
                {
                    return false;
                }

                Providers.Remove(id);
                ProviderInstances.Remove(id);
                ContentRevisions.Remove(id);
                _snapshot = null;
                PublishServiceNoLock();
                return true;
            }
        }

        /// <summary>
        /// Tells open menus that a provider's content, commands, forms or settings changed, so a page showing it
        /// refreshes without waiting for the next periodic refresh. Safe to call from any thread.
        /// </summary>
        /// <returns>False when no provider is registered under the ID.</returns>
        public static bool NotifyContentChanged(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            var id = providerId.Trim();
            lock (SyncRoot)
            {
                if (!Providers.ContainsKey(id))
                {
                    return false;
                }

                // One counter for all providers, so a provider registered again never repeats an old revision.
                ContentRevisions[id] = ++_contentRevision;
                return true;
            }
        }

        /// <summary>Zero until the provider first calls <see cref="NotifyContentChanged"/>.</summary>
        internal static long GetContentRevision(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return 0;
            }

            lock (SyncRoot)
            {
                return ContentRevisions.TryGetValue(providerId.Trim(), out var revision) ? revision : 0;
            }
        }

        public static bool TryGetProviderContent(string id, out MenuContentSnapshot content)
        {
            content = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (!TryGetRegistration(id, out var provider, out var registered))
                return false;

            var contentProvider = provider as IMenuContentProvider;
            if (contentProvider == null)
            {
                return false;
            }

            try
            {
                var created = contentProvider.CreateContent();
                if (!IsCurrentRegistration(registered, provider))
                    return false;
                content = created;
                return created != null;
            }
            catch (Exception ex)
            {
                ReportProviderFailure("content", id, ex);
                return false;
            }
        }

        public static bool TryGetProviderCommands(string id, out CommandProviderSnapshot commands)
        {
            commands = null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            if (!TryGetRegistration(id, out var provider, out var registered))
                return false;

            var commandProvider = provider as ICommandProvider;
            if (commandProvider == null)
            {
                return false;
            }

            try
            {
                var currentMetadata = CaptureProvider(provider, registered.BuiltIn);
                if (currentMetadata == null || !string.Equals(currentMetadata.Id, registered.Id, StringComparison.OrdinalIgnoreCase)
                    || !IsCurrentRegistration(registered, provider))
                    return false;
                var created = CommandProviderSnapshot.FromProvider(currentMetadata, commandProvider);
                if (!IsCurrentRegistration(registered, provider))
                    return false;
                commands = created;
                return created != null;
            }
            catch (Exception ex)
            {
                ReportProviderFailure("commands", id, ex);
                return false;
            }
        }

        internal static bool AcceptsArguments(string providerId) =>
            TryGetRegistration(providerId, out var provider, out _) && provider is IConsoleCommandProvider;

        /// <summary>
        /// Runs a provider command as <see cref="CommandOrigin.OtherMod"/>. A command whose current snapshot sets
        /// <see cref="CommandSnapshot.RequiresConfirmation"/> is refused, since this call carries no confirmation.
        /// </summary>
        public static bool TryInvokeProviderCommand(string providerId, string commandId, out CommandResultSnapshot result) =>
            TryInvokeProviderCommand(providerId, commandId, new CommandInvocation(CommandOrigin.OtherMod, "", false),
                out result);

        internal static bool TryInvokeProviderCommand(string providerId, string commandId, CommandInvocation invocation,
            out CommandResultSnapshot result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(commandId))
            {
                result = CommandResultSnapshot.Failure("Command provider id and command id are required.");
                return false;
            }

            TryGetRegistration(providerId, out var provider, out var registered);

            var commandProvider = provider as ICommandProvider;
            if (commandProvider == null)
            {
                result = CommandResultSnapshot.Failure("Provider does not expose commands.");
                return false;
            }

            try
            {
                if (!provider.Enabled || !IsCurrentRegistration(registered, provider))
                {
                    result = CommandResultSnapshot.Failure("Provider is no longer available.");
                    return false;
                }

                var consoleProvider = commandProvider as IConsoleCommandProvider;
                if (consoleProvider == null && invocation.Arguments.Length != 0)
                {
                    result = CommandResultSnapshot.Failure("This command does not take arguments.");
                    return false;
                }

                if (!invocation.Confirmed && RequiresConfirmation(commandProvider, commandId.Trim()))
                {
                    result = CommandResultSnapshot.Failure("This command requires explicit confirmation.");
                    return false;
                }

                var invoked = consoleProvider != null
                    ? consoleProvider.TryInvokeCommand(commandId.Trim(), invocation, out result)
                    : commandProvider.TryInvokeCommand(commandId.Trim(), out result);
                if (!invoked)
                {
                    if (result == null || result.Succeeded)
                    {
                        result = CommandResultSnapshot.Failure(result?.Detail ?? "Command was not invoked.");
                    }

                    return false;
                }

                if (result == null)
                {
                    result = CommandResultSnapshot.Success("Command invoked.");
                }

                RefreshAfterCommand(provider, registered);

                return result.Succeeded;
            }
            catch (Exception ex)
            {
                result = CommandResultSnapshot.Failure("Command failed: " + ex.Message);
                ReportProviderFailure("command", providerId + "/" + commandId, ex);
                return false;
            }
        }

        // Ids match without case so a provider that accepts any casing can't be reached past its own flag.
        private static bool RequiresConfirmation(ICommandProvider provider, string commandId) =>
            provider.GetCommands()?.Any(command => command != null && command.RequiresConfirmation
                && string.Equals(command.Id, commandId, StringComparison.OrdinalIgnoreCase)) == true;

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        private static MenuProviderSnapshot CaptureProvider(IMenuProvider provider, bool builtIn)
        {
            try
            {
                return MenuProviderSnapshot.FromProvider(provider, builtIn);
            }
            catch (Exception ex)
            {
                ReportProviderFailure("metadata", "registration", ex);
                return null;
            }
        }

        private static bool TryGetRegistration(string id, out IMenuProvider provider,
            out MenuProviderSnapshot registered)
        {
            lock (SyncRoot)
            {
                var key = id.Trim();
                ProviderInstances.TryGetValue(key, out provider);
                Providers.TryGetValue(key, out registered);
                return provider != null && registered != null;
            }
        }

        private static bool IsCurrentRegistration(MenuProviderSnapshot registered, IMenuProvider provider)
        {
            lock (SyncRoot)
                return registered != null && Providers.TryGetValue(registered.Id, out var current)
                    && ReferenceEquals(current, registered)
                    && ProviderInstances.TryGetValue(registered.Id, out var instance)
                    && ReferenceEquals(instance, provider);
        }

        private static void RefreshAfterCommand(IMenuProvider provider, MenuProviderSnapshot registered)
        {
            if (!IsCurrentRegistration(registered, provider))
                return;
            var updated = CaptureProvider(provider, registered.BuiltIn);
            if (updated == null || !string.Equals(updated.Id, registered.Id, StringComparison.OrdinalIgnoreCase))
                return;
            lock (SyncRoot)
            {
                if (!IsCurrentRegistration(registered, provider))
                    return;
                Providers[registered.Id] = updated;
                _snapshot = null;
                PublishServiceNoLock();
            }
        }

        internal static void ReportProviderFailure(string operation, string id, Exception error)
        {
            try
            {
                global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning(
                    "Modstoke menu provider " + operation + " failed for '" + id + "': " + error);
            }
            catch { }
        }

        private static void RegisterBuiltInNoLock(string id, string displayName, string kind, int priority, string description)
        {
            var provider = new MenuProviderDescriptor(
                id,
                displayName,
                kind,
                priority,
                "Modstoke Loader",
                description,
                false,
                true);
            var snapshot = MenuProviderSnapshot.FromProvider(provider, true);
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
                providers = SnapshotNoLock()
            };
        }

        private static IReadOnlyList<MenuProviderSnapshot> SnapshotNoLock()
        {
            return _snapshot ??= Array.AsReadOnly(Providers.Values
                .OrderBy(provider => provider.Priority)
                .ThenBy(provider => provider.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }

        private static void PublishServiceNoLock()
        {
            Modding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static void DispatchProviderChanged(MenuProviderSnapshot snapshot, IMenuProvider provider,
            Action<MenuProviderSnapshot> handlers)
        {
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                // An earlier subscriber may have replaced or unregistered this provider.
                if (!IsCurrentRegistration(snapshot, provider))
                    return;
                try { handler(snapshot); }
                catch (Exception ex) { ReportProviderFailure("event subscriber", snapshot.Id, ex); }
            }
        }
    }

    public interface IMenuProvider
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

    public interface IMenuContentProvider : IMenuProvider
    {
        MenuContentSnapshot CreateContent();
    }

    public interface ICommandProvider : IMenuProvider
    {
        IReadOnlyList<CommandSnapshot> GetCommands();
        bool TryInvokeCommand(string commandId, out CommandResultSnapshot result);
    }

    /// <summary>
    /// Optional hints for where a provider's page belongs in the mods menu. Blank values count as unset, and a
    /// provider without them keeps its default place.
    /// </summary>
    public interface IMenuProviderPresentation : IMenuProvider
    {
        /// <summary>The ID of the mod that owns this page, as the mod inventory lists it.</summary>
        string OwnerModId => null;

        /// <summary>A short label that keeps related pages from the same owner together.</summary>
        string Group => null;
    }

    public sealed class MenuProviderDescriptor : IMenuProviderPresentation
    {
        public MenuProviderDescriptor(
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
        public string OwnerModId { get; init; }
        public string Group { get; init; }
    }

    public sealed class MenuProviderSnapshot
    {
        private MenuProviderSnapshot(
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
            bool hasSettings,
            bool hasInputs,
            bool builtIn,
            string ownerModId,
            string group,
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
            HasSettings = hasSettings;
            HasInputs = hasInputs;
            BuiltIn = builtIn;
            OwnerModId = ownerModId;
            Group = group;
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
        public bool HasSettings { get; }
        public bool HasInputs { get; }
        public bool BuiltIn { get; }
        /// <summary>The provider's owning mod ID, or null when it does not declare one.</summary>
        public string OwnerModId { get; }
        /// <summary>The provider's group label, or null when it does not declare one.</summary>
        public string Group { get; }
        public string UpdatedUtc { get; }

        internal static MenuProviderSnapshot FromProvider(IMenuProvider provider, bool builtIn)
        {
            if (provider == null)
            {
                return null;
            }

            var id = provider.Id;
            if (string.IsNullOrWhiteSpace(id))
                return null;
            id = id.Trim();
            var displayName = provider.DisplayName;
            var kind = provider.Kind;
            var source = provider.Source;
            var presentation = provider as IMenuProviderPresentation;

            return new MenuProviderSnapshot(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName.Trim(),
                string.IsNullOrWhiteSpace(kind) ? MenuProviderKinds.Tab : kind.Trim(),
                provider.Priority,
                string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim(),
                provider.Description ?? "",
                provider.RequiresAuthorization,
                provider.Enabled,
                provider is IMenuContentProvider,
                provider is ICommandProvider,
                provider is IMenuSettingsProvider,
                provider is IMenuInputProvider,
                builtIn,
                OptionalText(presentation?.OwnerModId),
                OptionalText(presentation?.Group),
                DateTimeOffset.UtcNow.ToString("O"));
        }

        private static string OptionalText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    public sealed class CommandProviderSnapshot
    {
        private CommandProviderSnapshot(
            string providerId,
            string title,
            string subtitle,
            CommandSnapshot[] commands,
            string updatedUtc)
        {
            ProviderId = providerId;
            Title = string.IsNullOrWhiteSpace(title) ? providerId : title;
            Subtitle = subtitle ?? "";
            Commands = Array.AsReadOnly(commands ?? Array.Empty<CommandSnapshot>());
            UpdatedUtc = updatedUtc;
        }

        public string ProviderId { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public IReadOnlyList<CommandSnapshot> Commands { get; }
        public string UpdatedUtc { get; }

        internal static CommandProviderSnapshot FromProvider(MenuProviderSnapshot provider, ICommandProvider commandProvider)
        {
            if (provider == null || commandProvider == null)
            {
                return null;
            }

            CommandSnapshot[] commands;
            try
            {
                commands = (commandProvider.GetCommands() ?? Array.Empty<CommandSnapshot>())
                    .Where(command => command != null)
                    .ToArray();
            }
            catch (Exception ex)
            {
                MenuRegistry.ReportProviderFailure("command snapshot", provider.Id, ex);
                return null;
            }

            return new CommandProviderSnapshot(
                provider.Id,
                provider.DisplayName,
                provider.Description,
                commands,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class CommandSnapshot
    {
        public CommandSnapshot(
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

    public sealed class CommandResultSnapshot
    {
        private CommandResultSnapshot(bool succeeded, string status, string detail, string updatedUtc)
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

        public static CommandResultSnapshot Success(string detail)
        {
            return new CommandResultSnapshot(true, "READY", detail, DateTimeOffset.UtcNow.ToString("O"));
        }

        public static CommandResultSnapshot Failure(string detail)
        {
            return new CommandResultSnapshot(false, "FAILED", detail, DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public sealed class MenuContentSnapshot
    {
        public MenuContentSnapshot(string title, string subtitle, IEnumerable<MenuSectionSnapshot> sections)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Provider" : title;
            Subtitle = subtitle ?? "";
            Sections = Array.AsReadOnly((sections ?? Array.Empty<MenuSectionSnapshot>()).Where(section => section != null).ToArray());
        }

        public string Title { get; }
        public string Subtitle { get; }
        public IReadOnlyList<MenuSectionSnapshot> Sections { get; }
    }

    public sealed class MenuSectionSnapshot
    {
        public MenuSectionSnapshot(string title, IEnumerable<MenuRowSnapshot> rows)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Details" : title;
            Rows = Array.AsReadOnly((rows ?? Array.Empty<MenuRowSnapshot>()).Where(row => row != null).ToArray());
        }

        public string Title { get; }
        public IReadOnlyList<MenuRowSnapshot> Rows { get; }
    }

    public sealed class MenuRowSnapshot
    {
        public MenuRowSnapshot(string label, string value, string status, string detail)
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

    public static class MenuProviderKinds
    {
        public const string Tab = "tab";
        public const string Settings = "settings";
        public const string CommandGroup = "command-group";
        public const string Status = "status";
        public const string Credits = "credits";
    }

    public static class MenuProviderIds
    {
        private const string RetiredLifecycleProvider = "This status provider was retired. The ModHost provider lists native runtime hooks.";

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
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleDispatchPlanner = "openacai.menu.runtime.nativeLifecycleDispatch";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleBindings = "openacai.menu.runtime.nativeLifecycleInvocationAdapter";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleInvocationPlanner = "openacai.menu.runtime.nativeLifecycleInvocation";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleJournal = "openacai.menu.runtime.nativeLifecycleInvocationJournal";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleSchedulePlanner = "openacai.menu.runtime.nativeLifecycleInvocationScheduler";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleExecutionPlanner = "openacai.menu.runtime.nativeLifecycleInvocationExecutor";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleTokenResolver = "openacai.menu.runtime.nativeLifecycleInvocationTokens";
        [Obsolete(RetiredLifecycleProvider)]
        public const string LifecycleRegistry = "openacai.menu.runtime.nativeLifecycle";
        public const string ModHost = "openacai.menu.runtime.nativeModHost";
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

