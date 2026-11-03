// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Modstoke.Loader
{
    /// <summary>Editable rows on the provider's own menu page. The Loader never publishes, saves or logs their values.</summary>
    /// <remarks>
    /// The Loader checks the scope, key, editability and value before calling <see cref="TrySetSetting"/>. The mods
    /// menu passes <see cref="CommandOrigin.ModsMenu"/>; other mods reach it through
    /// <see cref="MenuRegistry.TrySetProviderSetting"/> as <see cref="CommandOrigin.OtherMod"/>. Recheck the scope and
    /// authorization under the provider's own lock. Result details are shown in the menu tooltip, which is
    /// published, so keep private values out of them.
    /// </remarks>
    public interface IMenuSettingsProvider : IMenuProvider
    {
        MenuSettingsSnapshot GetSettings();
        bool TrySetSetting(Guid scope, string key, string value, CommandOrigin origin, out CommandResultSnapshot result);
    }

    public sealed class MenuSettingsSnapshot
    {
        public MenuSettingsSnapshot(Guid scope, IEnumerable<MenuSettingSnapshot> settings)
        {
            if (scope == Guid.Empty)
                throw new ArgumentException("A settings scope is required.", nameof(scope));
            var copy = settings?.ToArray();
            if (copy == null || copy.Length > 32)
                throw new ArgumentException("A provider page supports up to 32 settings.", nameof(settings));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (copy.Any(setting => setting == null || !keys.Add(setting.Key)))
                throw new ArgumentException("Setting keys must be unique.", nameof(settings));
            Scope = scope;
            Settings = Array.AsReadOnly(copy);
        }

        public Guid Scope { get; }
        public IReadOnlyList<MenuSettingSnapshot> Settings { get; }
    }

    public sealed class MenuSettingSnapshot
    {
        internal const int MaxValueLength = 256;

        private static readonly string[] Kinds =
        {
            ConfigValueKinds.Text, ConfigValueKinds.Boolean, ConfigValueKinds.Integer, ConfigValueKinds.Number,
            ConfigValueKinds.Choice
        };

        /// <remarks>
        /// Options are kept only for choices, and limits only for integers and numbers. The value must already be
        /// valid for the kind; booleans may use any letter case.
        /// </remarks>
        public MenuSettingSnapshot(string section, string key, string displayName, string kind, string value,
            string description, bool canEdit, IEnumerable<string> options = null,
            double? minimum = null, double? maximum = null, double? step = null)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 64)
                throw new ArgumentException("A setting key of up to 64 characters is required.", nameof(key));
            Kind = string.IsNullOrWhiteSpace(kind) ? ConfigValueKinds.Text
                : Array.Find(Kinds, known => string.Equals(known, kind.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("Unknown setting kind.", nameof(kind));
            Options = Array.Empty<string>();
            if (Kind == ConfigValueKinds.Choice)
            {
                var copy = (options ?? Array.Empty<string>()).ToArray();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                if (copy.Length == 0 || copy.Length > 32 || copy.Any(option => string.IsNullOrWhiteSpace(option)
                    || option.Length > 64 || option.Any(char.IsControl) || !seen.Add(option)))
                    throw new ArgumentException("A choice needs one to 32 unique options of up to 64 characters.", nameof(options));
                Options = Array.AsReadOnly(copy);
            }
            else if (Kind == ConfigValueKinds.Integer || Kind == ConfigValueKinds.Number)
            {
                if (!IsLimit(minimum) || !IsLimit(maximum) || minimum > maximum)
                    throw new ArgumentException("Limits must be finite and in order.", nameof(minimum));
                if (!IsLimit(step) || step <= 0)
                    throw new ArgumentOutOfRangeException(nameof(step));
                Minimum = minimum;
                Maximum = maximum;
                Step = step;
            }

            Section = string.IsNullOrWhiteSpace(section) ? "Settings" : section;
            Key = key;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? key : displayName;
            Value = Kind == ConfigValueKinds.Boolean ? (value ?? "").ToLowerInvariant() : value ?? "";
            Description = description ?? "";
            CanEdit = canEdit;
            if (!Accepts(Value))
                throw new ArgumentException("The value is not valid for the setting kind.", nameof(value));
        }

        public string Section { get; }
        public string Key { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public string Value { get; }
        public string Description { get; }
        public bool CanEdit { get; }
        public IReadOnlyList<string> Options { get; }
        public double? Minimum { get; }
        public double? Maximum { get; }
        public double? Step { get; }

        internal bool Accepts(string value) => value.Length <= MaxValueLength && Kind switch
        {
            ConfigValueKinds.Boolean => value == "true" || value == "false",
            ConfigValueKinds.Choice => Options.Contains(value),
            ConfigValueKinds.Integer or ConfigValueKinds.Number => TryParseNumber(value, out var number)
                && (Minimum == null || number >= Minimum) && (Maximum == null || number <= Maximum),
            _ => !value.Any(char.IsControl)
        };

        internal string AdjacentOption(int offset)
        {
            // A choice value is always one of its options.
            var index = 0;
            while (Options[index] != Value)
                index++;
            return Options[(index + Options.Count + offset) % Options.Count];
        }

        /// <summary>Steps from the edited text, or the current value when it does not parse, within the limits.</summary>
        internal string StepValue(string text, int direction)
        {
            if (!TryParseNumber(text, out var number))
                TryParseNumber(Value, out number);
            number = Math.Clamp(number + direction * Step.Value, Minimum ?? double.MinValue, Maximum ?? double.MaxValue);
            if (Kind == ConfigValueKinds.Integer)
                return number.ToString("0", CultureInfo.InvariantCulture);
            // Short form unless rounding would step past a limit that is not a short decimal.
            var stepped = number.ToString("0.##########", CultureInfo.InvariantCulture);
            return Accepts(stepped) ? stepped : number.ToString("0.#################", CultureInfo.InvariantCulture);
        }

        private bool TryParseNumber(string value, out double number)
        {
            if (Kind == ConfigValueKinds.Integer)
            {
                var parsed = long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole);
                number = whole;
                return parsed;
            }

            return double.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
        }

        private bool IsLimit(double? limit) => limit == null
            || (double.IsFinite(limit.Value) && (Kind != ConfigValueKinds.Integer || Math.Floor(limit.Value) == limit.Value));
    }

    public static partial class MenuRegistry
    {
        private static readonly HashSet<string> ReportedSettingFailures = new(StringComparer.OrdinalIgnoreCase);

        public static bool TryGetProviderSettings(string id, out MenuSettingsSnapshot snapshot)
        {
            snapshot = null;
            if (string.IsNullOrWhiteSpace(id))
                return false;
            if (!TryGetRegistration(id, out var provider, out var registered))
                return false;
            if (!(provider is IMenuSettingsProvider settingsProvider))
                return false;
            try
            {
                var created = settingsProvider.GetSettings();
                if (!IsCurrentRegistration(registered, provider))
                    return false;
                snapshot = created;
                return created != null;
            }
            catch (Exception error)
            {
                // Setting values may hold player IDs or roles, so only the exception type is reported, once.
                bool first;
                lock (ReportedSettingFailures) first = ReportedSettingFailures.Add(id);
                if (first)
                    RuntimeEntryPoint.LogSource?.LogWarning("Menu provider '" + id + "' could not build its settings ("
                        + error.GetType().Name + "); its settings section is hidden.");
                return false;
            }
        }

        public static bool TrySetProviderSetting(string providerId, Guid scope, string key, string value,
            out CommandResultSnapshot result) =>
            TrySetProviderSetting(providerId, scope, key, value, CommandOrigin.OtherMod, out result);

        internal static bool TrySetProviderSetting(string providerId, Guid scope, string key, string value,
            CommandOrigin origin, out CommandResultSnapshot result)
        {
            result = CommandResultSnapshot.Failure("This setting is no longer available.");
            if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(key) || value == null || scope == Guid.Empty)
                return false;
            if (!TryGetRegistration(providerId, out var provider, out var registered))
                return false;
            if (!(provider is IMenuSettingsProvider settingsProvider))
                return false;
            try
            {
                var settings = settingsProvider.GetSettings();
                if (!provider.Enabled || settings == null || settings.Scope != scope || !IsCurrentRegistration(registered, provider))
                    return false;
                var setting = settings.Settings.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
                if (setting == null || !setting.CanEdit)
                    return false;
                if (!setting.Accepts(value))
                {
                    result = CommandResultSnapshot.Failure("That value is not valid for this setting.");
                    return false;
                }

                var applied = settingsProvider.TrySetSetting(scope, key, value, origin, out result);
                if (result == null || (!applied && result.Succeeded))
                    result = applied ? CommandResultSnapshot.Success("Setting applied.")
                        : CommandResultSnapshot.Failure("The setting was not changed.");
                return applied && result.Succeeded;
            }
            catch
            {
                result = CommandResultSnapshot.Failure("The setting could not be updated.");
                return false;
            }
        }
    }
}
