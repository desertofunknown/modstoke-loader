// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using SonsSdk;
using SUI;
using Sui = SUI.SUI;
using SettingsConfigEntry = SUI.SettingsRegistry.SettingsConfigEntry;
using SettingsEntry = SUI.SettingsRegistry.SettingsEntry;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSettingsRegistration : ISdkSettingsRegistrationDriver
    {
        internal static readonly OpenACAISonsSettingsRegistration Instance = new();

        private OpenACAISonsSettingsRegistration() { }

        public void Create(ModBase mod, object settingsObject, Type settingsType,
            bool changesNeedRestart, Action callback, SContainerOptions userContent)
        {
            Action onClosed = null;
            const string callbackName = "OnSettingsUiClosed";
            // The hook is optional; keep Harmony's overload selection only when a declaration exists.
            var hasClassCallback = settingsType != null && AccessTools.FindIncludingBaseTypes(settingsType,
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

            var container = Sui.SContainer;
            var configList = new List<SettingsConfigEntry>();
            GenerateUi(mod, settingsObject, settingsType, container, configList);

            container.Root.DontDestroyOnLoad().HideAndDontSave().SetActive(false);
            container.Name($"{mod.ID}SettingsPanel");

            var entry = SettingsRegistry.SettingsEntries[mod.ID] =
                new SettingsEntry(container, changesNeedRestart, callback, configList)
                {
                    ConfigClassCallback = onClosed
                };

            var settingsEntryField = AccessTools.GetDeclaredFields(settingsType)
                .FirstOrDefault(field => field.FieldType == typeof(SettingsEntry));
            if (settingsEntryField != null)
                settingsEntryField.SetValue(settingsObject, entry);

            entry.UserContentContainer = userContent;
            if (userContent != null)
                userContent.RectTransform.SetParent(entry.Container.RectTransform, false);
        }
    }
}
