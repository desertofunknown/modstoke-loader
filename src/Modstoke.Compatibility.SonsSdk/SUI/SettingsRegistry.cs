using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RedLoader;
using RedLoader.Assertions;
using SonsSdk;
using SonsSdk.Attributes;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Color = System.Drawing.Color;

namespace SUI;

using static SUI;

internal interface ISdkSettingsRegistrationDriver
{
    void Create(ModBase mod, object settingsObject, Type settingsType,
        bool changesNeedRestart, Action callback, SContainerOptions userContent);
    void GenerateUi(ModBase mod, object settingsObject, Type settingsType,
        SContainerOptions container, List<SettingsRegistry.SettingsConfigEntry> outConfigList);
    void CreatePanel();
    bool TryOpenPanel(string id, Action<bool> onClosed);
    void ClosePanel(bool returnToOwner);
    void RevertPanel();
    void UpdatePanel();
    bool CheckEntryForChanges(SettingsRegistry.SettingsEntry entry);
    void ParentEntry(SettingsRegistry.SettingsEntry entry, Transform parent);
    void UnparentEntry(SettingsRegistry.SettingsEntry entry);
    void RevertEntry(SettingsRegistry.SettingsEntry entry);
    void ToggleEntryCategory(SettingsRegistry.SettingsEntry entry, string identifier, bool show);
    void SaveEntry(SettingsRegistry.SettingsEntry entry);
    void InvokeEntryCallbacks(SettingsRegistry.SettingsEntry entry);
}

internal interface ISdkSettingsFallbackDriver : ISdkSettingsRegistrationDriver
{
    void RefreshConfigVisibility(SettingsRegistry.SettingsConfigEntry entry);
    void CreateConfigHeader(SettingsRegistry.SettingsConfigEntry entry);
    void CreateConfigSpacing(SettingsRegistry.SettingsConfigEntry entry);
    void SetEntryCharacterLimit(SettingsRegistry.SettingsEntry owner, ConfigEntry entry,
        int characterLimit, bool disableAutoSizing);
}

public class SettingsRegistry
{
    internal static readonly Dictionary<string, SettingsEntry> SettingsEntries = new();
    
    private static readonly Lazy<ISdkSettingsFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SettingsFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkSettingsFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkSettingsFallbackDriver>)))();
    });
    internal static ISdkSettingsFallbackDriver Fallback => FallbackDriver.Value;

    internal static BackgroundDefinition ButtonBg => new(
        ColorFromString("#796C4E"), 
        GetBackgroundSprite(EBackground.Round8), 
        Image.Type.Sliced);

    public static void CreateSettings<T>(ModBase mod, T settingsObject, bool changesNeedRestart = false, Action callback = null)
    {
        CreateSettingsInternal(mod, settingsObject, typeof(T), changesNeedRestart, callback, null);
    }
    
    public static void CreateSettings<T>(ModBase mod, T settingsObject, SContainerOptions userContent, bool changesNeedRestart = false, Action callback = null)
    {
        CreateSettingsInternal(mod, settingsObject, typeof(T), changesNeedRestart, callback, userContent);
    }
    
    public static void CreateSettings(
        ModBase mod, 
        object settingsObject,
        Type settingsType,
        bool changesNeedRestart = false, 
        Action callback = null)
    {
        CreateSettingsInternal(mod, settingsObject, settingsType, changesNeedRestart, callback, null);
    }
    
    public static void CreateSettings(
        ModBase mod, 
        object settingsObject,
        Type settingsType,
        SContainerOptions userContent,
        bool changesNeedRestart = false, 
        Action callback = null)
    {
        CreateSettingsInternal(mod, settingsObject, settingsType, changesNeedRestart, callback, userContent);
    }

    private static void CreateSettingsInternal(
        ModBase mod,
        object settingsObject,
        Type settingsType,
        bool changesNeedRestart,
        Action callback,
        SContainerOptions userContent)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.Create(mod, settingsObject, settingsType, changesNeedRestart, callback, userContent);
            return;
        }

        Fallback.Create(mod, settingsObject, settingsType, changesNeedRestart, callback, userContent);
    }

    internal static void GenerateUi(ModBase mod,
        object settingsObject,
        Type settingsType,
        SContainerOptions container,
        List<SettingsConfigEntry> outConfigList)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Settings.GenerateUi(mod, settingsObject, settingsType, container, outConfigList);
            return;
        }

        Fallback.GenerateUi(mod, settingsObject, settingsType, container, outConfigList);
    }

    internal static void LogIgnoredMember(System.Reflection.MemberInfo member, bool notIncluded)
    {
        RLog.Debug("Ignored field: " + member.Name + (notIncluded ? " in settings (not included)" : " in settings"));
    }

    internal static void LogAddedMember(System.Reflection.MemberInfo member, bool isProperty)
    {
        RLog.Debug((isProperty ? "Added property: " : "Added field: ") + member.Name + " to settings");
    }

    internal static void LogChangedEntry(ConfigEntry entry)
    {
        RLog.Debug($"{entry.DisplayName} has changed ({entry.GetDefaultValueAsString()} -> {entry.GetValueAsString()})");
    }

    public static bool HasSettings(string id)
    {
        return SettingsEntries.ContainsKey(id);
    }
    
    public static SettingsEntry GetEntry(string id)
    {
        if (SettingsEntries.TryGetValue(id, out var container))
        {
            return container;
        }
        
        return null;
    }
    
    public static bool TryGetEntry(string id, out SettingsEntry entry)
    {
        return SettingsEntries.TryGetValue(id, out entry);
    }

    public class SettingsConfigEntry
    {
        public readonly ConfigEntry ConfigEntry;
        public readonly SUiElement UiElement;
        public readonly SUiElement WrappedElement;
        public readonly SettingsUiHeader HeaderInfo;
        public readonly SettingsUiSpacing SpacingInfo;
        private bool _shouldBeVisible = true;

        public List<SUiElement> HeaderElements = new();

        public bool ShouldBeVisible
        {
            get => _shouldBeVisible;
            set
            {
                _shouldBeVisible = value;
                RefreshVisibility();
            }
        }

        public string CategoryName => ConfigEntry.Category.Identifier;
        
        public SettingsConfigEntry(ConfigEntry configEntry, SUiElement uiElement, SUiElement wrappedElement, SettingsUiHeader headerInfo = null, SettingsUiSpacing spacingInfo = null)
        {
            ConfigEntry = configEntry;
            UiElement = uiElement;
            HeaderInfo = headerInfo;
            SpacingInfo = spacingInfo;
            WrappedElement = wrappedElement;

            CreateSpacing();
            CreateHeader();
        }
        
        public void RefreshVisibility()
        {
            Fallback.RefreshConfigVisibility(this);
        }
        
        private void CreateHeader()
        {
            Fallback.CreateConfigHeader(this);
        }

        private void CreateSpacing()
        {
            Fallback.CreateConfigSpacing(this);
        }
    }

    public class SettingsEntry
    {
        public SContainerOptions Container;
        public SContainerOptions UserContentContainer;
        internal Action Callback;
        internal Action ConfigClassCallback;
        public bool ChangesNeedRestart;

        public List<SettingsConfigEntry> ConfigEntries;

        internal Dictionary<string, (SLabelDividerOptions, bool)> _dividers;

        public SettingsEntry()
        { }

        public SettingsEntry(
            SContainerOptions container,
            bool changesNeedRestart,
            Action callback,
            List<SettingsConfigEntry> configEntries)
        {
            Container = container;
            ChangesNeedRestart = changesNeedRestart;
            Callback = callback;
            ConfigEntries = configEntries;
        }

        public bool CheckForChanges()
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
                return driver.Settings.CheckEntryForChanges(this);

            return Fallback.CheckEntryForChanges(this);
        }

        public void ParentTo(Transform parent)
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.ParentEntry(this, parent);
                return;
            }

            Fallback.ParentEntry(this, parent);
        }

        public void Unparent()
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.UnparentEntry(this);
                return;
            }

            Fallback.UnparentEntry(this);
        }

        public void RevertSettings()
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.RevertEntry(this);
                return;
            }

            Fallback.RevertEntry(this);
        }

        /// <summary>
        /// Gets the ui container for a config entry. The container wraps the option element and the revert button.
        /// To get the actual option element use <see cref="GetWrappedElementForEntry"/>.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public SUiElement GetElementForEntry(ConfigEntry entry)
        {
            return ConfigEntries.Find(x => x.ConfigEntry == entry)?.UiElement;
        }
        
        /// <summary>
        /// Get the option element for a config entry. To get the container use <see cref="GetElementForEntry"/>.
        /// </summary>
        /// <param name="entry"></param>
        /// <returns></returns>
        public SUiElement GetWrappedElementForEntry(ConfigEntry entry)
        {
            return ConfigEntries.Find(x => x.ConfigEntry == entry)?.WrappedElement;
        }

        /// <summary>
        /// Sets the textboxes character limit. Make sure the config entry is a string entry.
        /// </summary>
        /// <param name="entry"></param>
        /// <param name="characterLimit"></param>
        /// <param name="disableAutoSizing">Disable auto sizing of the text inside the textbox</param>
        public void SetCharacterLimit(ConfigEntry entry, int characterLimit, bool disableAutoSizing = true)
        {
            Fallback.SetEntryCharacterLimit(this, entry, characterLimit, disableAutoSizing);
        }
        
        public void ToggleCategory(string identifier, bool show)
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.ToggleEntryCategory(this, identifier, show);
                return;
            }

            Fallback.ToggleEntryCategory(this, identifier, show);
        }

        public void Save()
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.SaveEntry(this);
                return;
            }

            Fallback.SaveEntry(this);
        }

        internal void InvokeCallbacks()
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
            {
                driver.Settings.InvokeEntryCallbacks(this);
                return;
            }

            Fallback.InvokeEntryCallbacks(this);
        }
    }
}
