// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RedLoader;
using SonsSdk;
using SUI;
using Sui = SUI.SUI;
using SettingsConfigEntry = SUI.SettingsRegistry.SettingsConfigEntry;
using SettingsEntry = SUI.SettingsRegistry.SettingsEntry;

namespace Modstoke.Loader
{
    internal sealed partial class SettingsRegistration : ISdkSettingsRegistrationDriver
    {
        internal static readonly SettingsRegistration Instance = new();
        private static readonly ConditionalWeakTable<SContainerOptions, List<Action>> ConfigBindings = new();

        private SettingsRegistration() { }

        public void Create(ModBase mod, object settingsObject, Type settingsType,
            bool changesNeedRestart, Action callback, SContainerOptions userContent)
        {
            ArgumentNullException.ThrowIfNull(mod);
            ArgumentNullException.ThrowIfNull(settingsType);
            var id = mod.ID;
            ArgumentNullException.ThrowIfNull(id);
            var previous = SettingsRegistry.GetEntry(id);
            Action onClosed = null;
            const string callbackName = "OnSettingsUiClosed";
            // The hook is optional; keep Harmony's overload selection only when a declaration exists.
            var hasClassCallback = AccessTools.FindIncludingBaseTypes(settingsType,
                type => type.GetMember(callbackName, MemberTypes.Method, AccessTools.all).FirstOrDefault()) != null;
            var classCallback = hasClassCallback ? AccessTools.Method(settingsType, callbackName) : null;
            if (classCallback != null)
            {
                if (!classCallback.IsStatic && settingsObject == null)
                    throw new ArgumentNullException(nameof(settingsObject), "Instance settings callbacks require a settings object.");

                onClosed = classCallback.IsStatic
                    ? classCallback.CreateDelegate<Action>()
                    : classCallback.CreateDelegate<Action>(settingsObject);
            }

            var settingsEntryField = AccessTools.GetDeclaredFields(settingsType)
                .FirstOrDefault(field => field.FieldType == typeof(SettingsEntry));
            var previousFieldValue = settingsEntryField?.GetValue(settingsObject);
            var container = Sui.SContainer;
            var configList = new List<SettingsConfigEntry>();
            var entry = new SettingsEntry(container, changesNeedRestart, callback, configList)
            {
                ConfigClassCallback = onClosed,
                UserContentContainer = userContent
            };
            UnityEngine.Transform contentParent = null;
            var contentIndex = 0;
            var contentMoved = false;
            var fieldAssigned = false;
            try
            {
                container.Root.DontDestroyOnLoad().HideAndDontSave().SetActive(false);
                container.Name($"{id}SettingsPanel");
                GenerateUi(mod, settingsObject, settingsType, container, configList);

                if (previous != null && ReferenceEquals(SonsGameManager.ModSettingsUi._currentEntry, previous))
                    ClosePanel(returnToOwner: false);

                // A close callback or settings getter can register a newer replacement.
                if (!ReferenceEquals(SettingsRegistry.GetEntry(id), previous) ||
                    previous != null && ReferenceEquals(SonsGameManager.ModSettingsUi._currentEntry, previous))
                    throw new InvalidOperationException($"Settings for '{id}' changed during registration.");

                if (userContent != null)
                {
                    contentParent = userContent.RectTransform.parent;
                    contentIndex = userContent.RectTransform.GetSiblingIndex();
                    contentMoved = true;
                    userContent.RectTransform.SetParent(container.RectTransform, false);
                }

                if (settingsEntryField != null)
                {
                    settingsEntryField.SetValue(settingsObject, entry);
                    fieldAssigned = true;
                }

                SettingsRegistry.SettingsEntries[id] = entry;
            }
            catch
            {
                if (fieldAssigned)
                    CleanupSettings(() => settingsEntryField.SetValue(settingsObject, previousFieldValue));
                if (contentMoved)
                    CleanupSettings(() =>
                    {
                        userContent.RectTransform.SetParent(contentParent, false);
                        userContent.RectTransform.SetSiblingIndex(contentIndex);
                    });
                entry.UserContentContainer = null;
                ReleaseEntry(entry);
                throw;
            }

            if (previous != null)
            {
                if (ReferenceEquals(previous.UserContentContainer, userContent))
                    previous.UserContentContainer = null;
                ReleaseEntry(previous);
            }
        }

        private static void ReleaseEntry(SettingsEntry entry)
        {
            if (ConfigBindings.TryGetValue(entry.Container, out var bindings))
            {
                ConfigBindings.Remove(entry.Container);
                foreach (var unbind in bindings)
                    CleanupSettings(unbind);
            }

            foreach (var config in entry.ConfigEntries)
            {
                if (config.WrappedElement is SKeybindOptions keybind)
                    CleanupSettings(keybind.CancelRebind);
                CleanupSettings(config.UiElement.Remove);
                foreach (var header in config.HeaderElements)
                    CleanupSettings(header.Remove);
            }
            if (entry._dividers != null)
            {
                foreach (var divider in entry._dividers.Values)
                    CleanupSettings(divider.Item1.Remove);
            }

            // User content is supplied by the caller and may be reused by a later registration.
            if (entry.UserContentContainer is { } content && content.Root)
                CleanupSettings(() =>
                {
                    content.Root.SetActive(false);
                    content.RectTransform.SetParent(null, false);
                });
            CleanupSettings(entry.Container.Remove);
        }

        internal static void CleanupSettings(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception exception)
            {
                try { RLog.Warning("Failed to clean up settings UI: " + exception); }
                catch { /* Cleanup must not replace the original failure. */ }
            }
        }
    }
}
