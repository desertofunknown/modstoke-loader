// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RedLoader;
using SonsSdk.Attributes;
using SUI;
using TMPro;
using UnityEngine.UI;
using static SUI.SUI;
using SettingsConfigEntry = SUI.SettingsRegistry.SettingsConfigEntry;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSettingsRegistration
    {
        private static BackgroundDefinition ButtonBg => new(
            ColorFromString("#796C4E"),
            GetBackgroundSprite(EBackground.Round8),
            Image.Type.Sliced);

        public void GenerateUi(ModBase mod,
            object settingsObject,
            Type settingsType,
            SContainerOptions container,
            List<SettingsConfigEntry> outConfigList)
        {
            SContainerOptions Wrap(SUiElement element, ConfigEntry config, float prefHeight, Action customRevertAction = null)
            {
                return SContainer.Horizontal(0, "CE")
                    .PHeight(prefHeight)
                    .Add(element)
                    .Add(SContainer.PWidth(100) -
                         SBgButton.Text("Revert").UpperCase().FontSize(16).FontColor("#c3ba8b")
                             .Background(ButtonBg)
                             .Ppu(3)
                             .Notify(customRevertAction ?? config.ResetToDefault)
                             .Dock(EDockType.Fill)
                             .Margin(7, 0, 7, 7)
                         );
            }

            var defaultHeight = 65f;

            foreach (var field in GetMembers(settingsType))
            {
                var headerAttr = field.Attributes.FirstOrDefault(x => x is SettingsUiHeader) as SettingsUiHeader;
                var spacingAttr = field.Attributes.FirstOrDefault(x => x is SettingsUiSpacing) as SettingsUiSpacing;

                if (field.Type == typeof(ConfigEntry<float>))
                {
                    var entry = (ConfigEntry<float>) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var observable = new Observable<float>(entry.Value);

                    var option = SSlider
                        .Text(entry.DisplayName)
                        .Range(entry.Min ?? 0, entry.Max ?? 10)
                        .Format("0.00").Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(entry, observable);

                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        option.Tooltip(entry.Description);
                    }

                    var optionContainer = Wrap(option, entry, defaultHeight);

                    container.Add(optionContainer);
                    outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                }
                else if (field.Type == typeof(ConfigEntry<int>))
                {
                    var entry = (ConfigEntry<int>) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var observable = new Observable<float>(entry.Value);

                    var option = SSlider
                        .Text(entry.DisplayName)
                        .Range(entry.Min ?? 0, entry.Max ?? 10)
                        .IntStep()
                        .Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(entry, observable, value => (int)value, value => (float)value);

                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        option.Tooltip(entry.Description);
                    }

                    var optionContainer = Wrap(option, entry, defaultHeight);

                    container.Add(optionContainer);
                    outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                }
                else if (field.Type == typeof(ConfigEntry<bool>))
                {
                    var entry = (ConfigEntry<bool>) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var observable = new Observable<bool>(entry.Value);

                    var option = SToggle.Text(entry.DisplayName).Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(entry, observable);

                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        option.Tooltip(entry.Description);
                    }

                    var optionContainer = Wrap(option, entry, defaultHeight);

                    container.Add(optionContainer);
                    outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                }
                else if (field.Type == typeof(ConfigEntry<string>))
                {
                    var entry = (ConfigEntry<string>) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var observable = new Observable<string>(entry.Value);

                    if (entry.HasOptions)
                    {
                        var option = SOptions.Text(entry.DisplayName).Options(entry.Options.ToArray()).Value(observable.Value).Bind(observable)
                            .FlexWidth(1);
                        BindConfig(entry, observable);
                        var optionContainer = Wrap(option, entry, defaultHeight);

                        if (!string.IsNullOrEmpty(entry.Description))
                        {
                            option.Tooltip(entry.Description);
                        }

                        container.Add(optionContainer);
                        outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                    }
                    else
                    {
                        var option = STextbox.Text(entry.DisplayName).Value(observable.Value).Bind(observable).FlexWidth(1);
                        BindConfig(entry, observable);
                        var optionContainer = Wrap(option, entry, defaultHeight);

                        if (!string.IsNullOrEmpty(entry.Description))
                        {
                            option.Tooltip(entry.Description);
                        }

                        container.Add(optionContainer);
                        outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                    }
                }
                else if(field.Type == typeof(KeybindConfigEntry))
                {
                    var entry = (KeybindConfigEntry) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var option = SKeybind.Text(entry.DisplayName.ToUpper()).Config(entry).BindingInputHeight(55);
                    var optionContainer = Wrap(option, entry, defaultHeight, option.RevertToDefault);

                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        option.Tooltip(entry.Description);
                    }

                    container.Add(optionContainer);
                    outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                }
                else if(field.Type == typeof(ConfigEntry<UnityEngine.Color>))
                {
                    var entry = (ConfigEntry<UnityEngine.Color>) field.GetValue(settingsObject);
                    if (entry == null)
                    {
                        continue;
                    }

                    var observable = new Observable<UnityEngine.Color>(entry.Value);

                    var label = SLabel
                        .Text(entry.DisplayName)
                        .FontAutoSize(false)
                        .FontSize(20)
                        .UpperCase()
                        .FontColor(new UnityEngine.Color(0.834f, 0.7804f, 0.7804f))
                        .Alignment(TextAlignmentOptions.MidlineLeft)
                        .Dock(EDockType.Fill);
                    var option = SColorWheel.Bind(observable).Pivot(1,0.5f).Anchor(AnchorType.MiddleRight).BgActive(false).Size(200, 200);
                    BindConfig(entry, observable);
                    var entryContainer = SContainer.Dock(EDockType.Fill).FlexWidth(1).Add(label).Add(option);

                    if (!string.IsNullOrEmpty(entry.Description))
                    {
                        label.Tooltip(entry.Description);
                    }

                    var optionContainer = Wrap(entryContainer, entry, 200);

                    container.Add(optionContainer);
                    outConfigList.Add(new(entry, optionContainer, option, headerAttr, spacingAttr));
                }
            }
        }

        private static void BindConfig<T>(ConfigEntry<T> entry, Observable<T> observable)
        {
            BindConfig(entry, observable, value => value, value => value);
        }

        private static void BindConfig<TConfig, TValue>(ConfigEntry<TConfig> entry, Observable<TValue> observable,
            Func<TValue, TConfig> toConfig, Func<TConfig, TValue> toControl)
        {
            var refreshing = false;

            void RefreshFromConfig()
            {
                var wasRefreshing = refreshing;
                refreshing = true;
                try
                {
                    observable.Value = toControl(entry.Value);
                }
                finally
                {
                    refreshing = wasRefreshing;
                }
            }

            // Bind the control first, so a nested correction is not overwritten by an older event.
            observable.OnValueChanged += _ =>
            {
                if (refreshing)
                    return;

                try
                {
                    entry.Value = toConfig(observable.Value);
                }
                catch
                {
                    try
                    {
                        RefreshFromConfig();
                    }
                    catch
                    {
                        // Preserve the original validation or write failure.
                    }

                    throw;
                }

                RefreshFromConfig();
            };

            entry.OnValueChanged.Subscribe((_, _) => RefreshFromConfig());
            RefreshFromConfig();
        }

        private static List<MemberInfo> GetMembers(Type type)
        {
            var members = new List<MemberInfo>();

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

            var mode = type.GetCustomAttribute<SettingsUiMode>()?.Mode ?? SettingsUiMode.ESettingsUiMode.OptOut;

            foreach (var field in fields)
            {
                if(field.Name.EndsWith("__BackingField"))
                    continue;

                if (mode == SettingsUiMode.ESettingsUiMode.OptOut && field.GetCustomAttribute<SettingsUiIgnore>() != null)
                {
                    RLog.Debug("Ignored field: " + field.Name + " in settings");
                    continue;
                }

                if (mode == SettingsUiMode.ESettingsUiMode.OptIn && field.GetCustomAttribute<SettingsUiInclude>() == null)
                {
                    RLog.Debug("Ignored field: " + field.Name + " in settings (not included)");
                    continue;
                }

                RLog.Debug("Added field: " + field.Name + " to settings");
                members.Add(new FieldMemberInfo(field));
            }

            foreach (var property in properties)
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;

                if (mode == SettingsUiMode.ESettingsUiMode.OptOut && property.GetCustomAttribute<SettingsUiIgnore>() != null)
                {
                    RLog.Debug("Ignored field: " + property.Name + " in settings");
                    continue;
                }

                if (mode == SettingsUiMode.ESettingsUiMode.OptIn && property.GetCustomAttribute<SettingsUiInclude>() == null)
                {
                    RLog.Debug("Ignored field: " + property.Name + " in settings (not included)");
                    continue;
                }

                RLog.Debug("Added property: " + property.Name + " to settings");
                members.Add(new PropertyMemberInfo(property));
            }

            return members;
        }

        private abstract class MemberInfo
        {
            public abstract Type Type { get; }
            public abstract object GetValue(object obj);
            public List<Attribute> Attributes { get; init; }
        }

        private class FieldMemberInfo : MemberInfo
        {
            private readonly FieldInfo _fieldInfo;

            public FieldMemberInfo(FieldInfo fieldInfo)
            {
                _fieldInfo = fieldInfo;
                Attributes = fieldInfo.GetCustomAttributes().ToList();
            }

            public override Type Type => _fieldInfo.FieldType;
            public override object GetValue(object obj) => _fieldInfo.GetValue(obj);
        }

        private class PropertyMemberInfo : MemberInfo
        {
            private readonly PropertyInfo _propertyInfo;

            public PropertyMemberInfo(PropertyInfo propertyInfo)
            {
                _propertyInfo = propertyInfo;
                Attributes = propertyInfo.GetCustomAttributes().ToList();
            }

            public override Type Type => _propertyInfo.PropertyType;
            public override object GetValue(object obj) => _propertyInfo.GetValue(obj);
        }
    }
}
