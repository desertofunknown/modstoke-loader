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

namespace Modstoke.Loader
{
    internal sealed partial class SettingsRegistration
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
            T Own<T>(T element) where T : SUiElement
            {
                element.SetParent(container);
                return element;
            }

            SContainerOptions Wrap(SUiElement element, ConfigEntry config, float prefHeight, Action customRevertAction = null)
            {
                return Own(SContainer).Horizontal(0, "CE")
                    .PHeight(prefHeight)
                    .Add(element)
                    .Add(Own(SContainer).PWidth(100) -
                         Own(SBgButton).Text("Revert").UpperCase().FontSize(16).FontColor("#c3ba8b")
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

                    var option = Own(SSlider)
                        .Text(entry.DisplayName)
                        .Range(entry.Min ?? 0, entry.Max ?? 10)
                        .Format("0.00").Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(container, option, entry, observable, value => RequireFinite(value), value => value);

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

                    var option = Own(SSlider)
                        .Text(entry.DisplayName)
                        .Range(entry.Min ?? 0, entry.Max ?? 10)
                        .IntStep()
                        .Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(container, option, entry, observable,
                        value => (int)Math.Clamp((double)MathF.Round(RequireFinite(value)), int.MinValue, int.MaxValue),
                        value => (float)value);

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

                    var option = Own(SToggle).Text(entry.DisplayName).Value(observable.Value).Bind(observable).FlexWidth(1);
                    BindConfig(container, option, entry, observable);

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
                        var option = Own(SOptions).Text(entry.DisplayName).Options(entry.Options.ToArray()).Value(observable.Value).Bind(observable)
                            .FlexWidth(1);
                        BindConfig(container, option, entry, observable);
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
                        var option = Own(STextbox).Text(entry.DisplayName).Value(observable.Value).Bind(observable).FlexWidth(1);
                        BindConfig(container, option, entry, observable);
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

                    var option = Own(SKeybind).Text(entry.DisplayName.ToUpper()).Config(entry).BindingInputHeight(55);
                    BindKeybind(container, option, entry);
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

                    var color = entry.Value;
                    var observable = new Observable<UnityEngine.Color>(new UnityEngine.Color(color.r, color.g, color.b));

                    var label = Own(SLabel)
                        .Text(entry.DisplayName)
                        .FontAutoSize(false)
                        .FontSize(20)
                        .UpperCase()
                        .FontColor(new UnityEngine.Color(0.834f, 0.7804f, 0.7804f))
                        .Alignment(TextAlignmentOptions.MidlineLeft)
                        .Dock(EDockType.Fill);
                    var option = Own(SColorWheel).Bind(observable).Pivot(1,0.5f).Anchor(AnchorType.MiddleRight).BgActive(false).Size(200, 200);
                    // The wheel exposes RGB only; keep the setting's existing alpha on edits.
                    BindConfig(container, option, entry, observable,
                        value => new UnityEngine.Color(value.r, value.g, value.b, entry.Value.a),
                        value => new UnityEngine.Color(value.r, value.g, value.b));
                    var entryContainer = Own(SContainer).Dock(EDockType.Fill).FlexWidth(1).Add(label).Add(option);

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

        private static void BindKeybind(SContainerOptions container, SKeybindOptions control, KeybindConfigEntry entry)
        {
            var detached = false;
            LemonAction<string, string> configChanged = null;

            void Unbind()
            {
                if (detached)
                    return;
                detached = true;
                entry.OnValueChanged.Unsubscribe(configChanged);
            }

            configChanged = (_, _) =>
            {
                if (detached)
                    return;
                if (!control.Root || !ReferenceEquals(control._keybindConfig, entry))
                {
                    Unbind();
                    return;
                }

                control.Config(entry);
            };

            ConfigBindings.GetOrCreateValue(container).Add(Unbind);
            entry.OnValueChanged.Subscribe(configChanged);
        }

        private static float RequireFinite(float value)
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "Settings sliders require a finite value.");
            return value;
        }

        private static void BindConfig<TWidget, T>(SContainerOptions container, SUiElement<TWidget, T> control,
            ConfigEntry<T> entry, Observable<T> observable)
        {
            BindConfig(container, control, entry, observable, value => value, value => value);
        }

        private static void BindConfig<TWidget, TConfig, TValue>(SContainerOptions container,
            SUiElement<TWidget, TValue> control, ConfigEntry<TConfig> entry, Observable<TValue> observable,
            Func<TValue, TConfig> toConfig, Func<TConfig, TValue> toControl)
        {
            var refreshing = false;
            var detached = false;
            LemonAction<TConfig, TConfig> configChanged = null;

            void Unbind()
            {
                if (detached)
                    return;
                detached = true;
                observable.OnValueChanged -= WriteToConfig;
                entry.OnValueChanged.Unsubscribe(configChanged);
                if (control.Root)
                    control.Unbind();
            }

            void RefreshFromConfig()
            {
                if (detached)
                    return;
                if (!control.Root)
                {
                    Unbind();
                    return;
                }

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

            void WriteToConfig(TValue value)
            {
                if (refreshing || detached)
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
            }

            // The control is already bound, so a nested correction cannot be overwritten by an older event.
            configChanged = (_, _) => RefreshFromConfig();
            ConfigBindings.GetOrCreateValue(container).Add(Unbind);
            observable.OnValueChanged += WriteToConfig;
            entry.OnValueChanged.Subscribe(configChanged);
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
