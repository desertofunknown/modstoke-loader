// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Display text and status tone shared by the Modstoke Mods menu renderers.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Loader;

namespace Modstoke.Loader
{
    internal static class MenuText
    {
        internal const string DefaultTooltip = "MODS - Select a mod to view details or edit its configuration.";
        internal const string NoConfigFileNotice = "No file-backed config was detected for this mod. Some RedLoader mods register live settings only after they are hosted.";
        private const string MetaSeparator = "  ·  ";
        private const int MaxSliderDecimals = 4;

        internal static readonly (string Project, string Credit, string Role, string Notes)[] Credits =
        {
            ("Modstoke Loader", "Alex Cooper / OpenACAI Inc", "Developer", "by OpenACAI. Independent mod loader for Sons of the Forest."),
            ("Modstoke SDK", "by OpenACAI", "Mod API", "Public modding APIs; BepInEx, RedLoader and SonsSdk identities remain compatible."),
            ("Modstoke", "by OpenACAI", "Website / catalog", "Intended address: modstoke.com. Services are not confirmed live."),
            ("BepInEx", "BepInEx contributors", "Upstream loader", "Modstoke Loader builds on BepInEx IL2CPP runtime foundations."),
            ("RedLoader", "Toni Macaroni", "Upstream SOTF loader", "RedLoader and SonsSdk behavior informed the compatibility layer and mod menu support."),
            ("SonsSdk", "Toni Macaroni and contributors", "Mod API", "SonsSdk APIs remain credited for RedLoader mod compatibility."),
            ("Mod authors", "Community creators", "Mods", "Each mod page shows its manifest author when one is available.")
        };

        internal static readonly (string Text, string Tooltip)[] CreditNotes =
        {
            ("Licensing: Modstoke Loader code is GPL-3.0-or-later. Upstream projects retain their own licenses.", "CREDITS - BepInEx, RedLoader, and SonsSdk remain credited upstream work."),
            ("OpenACAI is the nonprofit publisher. Not affiliated with or endorsed by Endnight Games.", "Compatible with Sons of the Forest; an independent community project.")
        };

        private static readonly HashSet<string> InfoTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "hosted", "connected", "online"
        };

        private static readonly HashSet<string> GoodTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "ready", "ok", "okay", "enabled", "running", "active", "loaded"
        };

        // Off and disabled are a choice, not a fault, so they stay neutral.
        private static readonly HashSet<string> BadTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "missing", "error", "errors", "failed", "fail", "failing", "failure", "blocked"
        };

        private static readonly HashSet<string> WarnTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "check", "warn", "warning", "warnings", "pending", "auth", "authorization", "unauthorized", "required"
        };

        private static readonly Dictionary<string, MenuTone> NegatedTokens = new Dictionary<string, MenuTone>(StringComparer.Ordinal)
        {
            ["inactive"] = MenuTone.Neutral,
            ["unloaded"] = MenuTone.Neutral,
            ["disconnected"] = MenuTone.Bad,
            ["offline"] = MenuTone.Bad
        };

        internal static string StatusText(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return "-";
            }

            if (string.Equals(entry.Status, "hosted", StringComparison.OrdinalIgnoreCase)) return "HOSTED";
            if (string.Equals(entry.Status, "ready", StringComparison.OrdinalIgnoreCase)) return "READY";
            if (string.Equals(entry.Status, "missing", StringComparison.OrdinalIgnoreCase)) return "MISSING";
            if (string.Equals(entry.Status, "warning", StringComparison.OrdinalIgnoreCase)) return "CHECK";
            if (string.Equals(entry.Status, "disabled", StringComparison.OrdinalIgnoreCase)) return "OFF";
            return "INFO";
        }

        internal static MenuTone EntryTone(ModInventoryEntry entry)
        {
            var status = entry?.Status ?? "";
            if (string.Equals(status, "hosted", StringComparison.OrdinalIgnoreCase)) return MenuTone.Info;
            if (string.Equals(status, "ready", StringComparison.OrdinalIgnoreCase)) return MenuTone.Good;
            if (string.Equals(status, "missing", StringComparison.OrdinalIgnoreCase)) return MenuTone.Bad;
            if (string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase)) return MenuTone.Warn;
            return MenuTone.Neutral;
        }

        internal static string TypeText(ModInventoryEntry entry)
        {
            if (ModLoaderTypes.IsLibrary(entry?.LoaderType)
                && string.Equals(entry?.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase))
            {
                return "Modstoke library";
            }

            if (string.Equals(entry?.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase)) return "Modstoke";
            if (ModLoaderTypes.IsLegacyBepInEx(entry?.LoaderType)) return "BepInEx shim";
            if (string.Equals(entry?.LoaderType, ModLoaderTypes.RedLoaderLibrary, StringComparison.OrdinalIgnoreCase)) return "RL lib shim";
            if (ModLoaderTypes.IsLegacyRedLoader(entry?.LoaderType)) return "RL mod shim";
            return "Unknown";
        }

        /// <summary>The line under a mod's title, such as "by OpenACAI · v0.5.218 · Modstoke mod", leaving out what is unknown.</summary>
        internal static string ModMeta(ModInventoryEntry entry)
        {
            if (entry == null)
            {
                return "";
            }

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.Author))
            {
                parts.Add("by " + entry.Author.Trim());
            }

            var version = (entry.Version ?? entry.AssemblyVersion)?.Trim();
            if (!string.IsNullOrEmpty(version))
            {
                parts.Add(char.IsDigit(version[0]) ? "v" + version : version);
            }

            var kind = KindText(entry);
            if (kind.Length > 0)
            {
                parts.Add(kind);
            }

            return string.Join(MetaSeparator, parts);
        }

        // The categories TypeText names, worded for the page header.
        private static string KindText(ModInventoryEntry entry)
        {
            var library = ModLoaderTypes.IsLibrary(entry.LoaderType);
            if (string.Equals(entry.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase)) return library ? "Modstoke library" : "Modstoke mod";
            if (ModLoaderTypes.IsLegacyBepInEx(entry.LoaderType)) return "BepInEx plugin";
            if (ModLoaderTypes.IsLegacyRedLoader(entry.LoaderType)) return library ? "RedLoader library" : "RedLoader mod";
            return "";
        }

        internal static string DetailText(ModInventoryEntry entry)
        {
            if (entry == null) return "";
            if (!string.IsNullOrWhiteSpace(entry.Issue)) return entry.Issue;
            if (entry.HostedByOpenAcai) return "Running through Modstoke";
            if (entry.DataOnly) return "Data-only Modstoke library";
            if (string.Equals(entry.SourceFamily, ModSourceFamilies.OpenAcai, StringComparison.OrdinalIgnoreCase)) return "Modstoke-native package detected";
            if (entry.Hostable) return "Detected and ready for controlled hosting";
            if (!entry.HasAssembly && entry.ManifestPath != null) return "Manifest only";
            return entry.Store ?? entry.ExpectedLocation ?? "";
        }

        internal static string VersionText(ModInventoryEntry entry)
        {
            return entry?.Version ?? entry?.AssemblyVersion ?? "-";
        }

        internal static string ConfigFileMeta(ModInventoryEntry entry)
        {
            return "Config file  |  " + (string.IsNullOrWhiteSpace(entry?.ConfigFormat) ? "Unknown format" : entry.ConfigFormat)
                + (string.IsNullOrWhiteSpace(entry?.ConfigPath) ? "" : "  |  " + entry.ConfigPath);
        }

        /// <summary>
        /// The status strip: loader version, mod, library and hosted counts, issues highlighted in amber, and Vortex state.
        /// Mods and libraries are counted the way the All Mods and Libraries chips count them.
        /// </summary>
        internal static string StatusLine(ModInventoryState inventory, string loaderVersion)
        {
            var version = "Modstoke Loader v" + loaderVersion;
            if (inventory == null)
            {
                return version + "  |  Inventory is not ready";
            }

            var entries = inventory.Entries ?? Array.Empty<ModInventoryEntry>();
            var libraries = entries.Count(entry => ModLoaderTypes.IsLibrary(entry.LoaderType));
            var issues = entries.Count(entry => !string.IsNullOrWhiteSpace(entry.IssueCode));
            return version
                + "  |  " + Counted(entries.Length - libraries, "mod", "mods") + ", "
                + Counted(libraries, "library", "libraries") + ", "
                + (inventory.Summary?.Hosted ?? 0).ToString(CultureInfo.InvariantCulture) + " hosted"
                + "  |  " + (issues > 0 ? "<color=#FFC757>" + Counted(issues, "issue", "issues").ToUpperInvariant() + "</color>" : "No issues")
                + "  |  " + (inventory.VortexDeploymentDetected ? "Vortex detected" : "No Vortex deployment");
        }

        internal static string Counted(int count, string singular, string plural)
        {
            return count.ToString(CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : plural);
        }

        internal static string ProviderSurfaceDetail(MenuProviderSnapshot provider)
        {
            if (provider == null)
            {
                return "Metadata only";
            }

            if (provider.HasContent && provider.HasCommands)
            {
                return "Structured content and commands";
            }

            if (provider.HasCommands)
            {
                return "Command provider";
            }

            return provider.HasContent ? "Structured content" : provider.HasSettings ? "Provider settings" : "Metadata only";
        }

        internal static string ProviderCommandStatus(CommandSnapshot command)
        {
            if (command == null)
            {
                return "";
            }

            if (!command.Enabled)
            {
                return "OFF";
            }

            return string.IsNullOrWhiteSpace(command.Status) ? "READY" : command.Status.Trim().ToUpperInvariant();
        }

        internal static string ProviderCommandActionText(CommandSnapshot command)
        {
            if (command == null)
            {
                return "";
            }

            if (!command.Enabled)
            {
                return "DISABLED";
            }

            return command.RequiresConfirmation ? "CONFIRM" : "RUN";
        }

        internal static string ProviderCommandTooltip(CommandSnapshot command)
        {
            if (command == null)
            {
                return "COMMAND - No command selected.";
            }

            var detail = string.IsNullOrWhiteSpace(command.Description)
                ? ProviderCommandActionText(command)
                : command.Description;
            return command.DisplayName + " - " + detail;
        }

        internal static string ProviderSettingStatus(ConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "";
            }

            if (!setting.CanEdit)
            {
                return "READONLY";
            }

            return setting.RequiresRestart ? "RESTART" : "READY";
        }

        internal static string ProviderSettingDetail(ConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "";
            }

            var kind = string.IsNullOrWhiteSpace(setting.Kind) ? "text" : setting.Kind;
            if (setting.RequiresRestart)
            {
                return kind + "; restart required";
            }

            return string.IsNullOrWhiteSpace(setting.Tooltip) ? kind : setting.Tooltip;
        }

        internal static string ProviderSettingTooltip(ConfigValueSnapshot setting)
        {
            if (setting == null)
            {
                return "SETTING - No setting selected.";
            }

            var detail = string.IsNullOrWhiteSpace(setting.Tooltip) ? ProviderSettingDetail(setting) : setting.Tooltip;
            return setting.DisplayName + " - " + detail;
        }

        internal static string SettingDetail(MenuSettingSnapshot setting)
        {
            return string.IsNullOrWhiteSpace(setting.Description) ? setting.Kind : setting.Description;
        }

        // Tooltips are published with the UI state, so they name the setting but never carry its value.
        internal static string SettingTooltip(MenuSettingSnapshot setting)
        {
            return setting.DisplayName + " - " + SettingDetail(setting);
        }

        /// <summary>The option before or after <paramref name="value"/>, wrapping around; a value that is not an option starts from the ends.</summary>
        internal static string AdjacentOption(IEnumerable<string> options, string value, int offset)
        {
            var choices = (options ?? Array.Empty<string>())
                .Where(option => !string.IsNullOrWhiteSpace(option))
                .ToArray();
            if (choices.Length == 0)
            {
                return value ?? "";
            }

            var index = Array.FindIndex(choices, option => string.Equals(option, value, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return offset > 0 ? choices[0] : choices[choices.Length - 1];
            }

            return choices[((index + offset) % choices.Length + choices.Length) % choices.Length];
        }

        /// <summary>Steps a number field from its edited text, or <paramref name="fallback"/> when that does not parse, within the limits.</summary>
        internal static string StepNumber(MenuControl control, string text, string fallback, int direction)
        {
            if (!TryParseNumber(text, out var number) && !TryParseNumber(fallback, out number))
            {
                number = 0d;
            }

            number += direction * (control.Step ?? 1d);
            number = Math.Clamp(number, control.Min ?? double.MinValue, control.Max ?? double.MaxValue);
            return FormatNumber(control, number);
        }

        internal static bool TryParseNumber(string value, out double number)
        {
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
        }

        internal static string FormatNumber(MenuControl control, double value)
        {
            return control.ValueKind == ConfigValueKinds.Integer
                ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                : value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The label beside a slider, rounded to the decimal places of its step and minimum (at most four) so float noise
        /// never shows. The value handed to handlers stays as <see cref="FormatNumber"/> writes it.
        /// </summary>
        internal static string SliderLabel(MenuControl control, string value)
        {
            if (!TryParseNumber(value, out var number))
            {
                return value ?? "";
            }

            var decimals = StepDecimals(control);
            var rounded = Math.Round(number, decimals, MidpointRounding.AwayFromZero);
            return (rounded == 0d ? 0d : rounded).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        private static int StepDecimals(MenuControl control)
        {
            if (control.ValueKind == ConfigValueKinds.Integer)
            {
                return 0;
            }

            // Snapped values are min + k * step, so the label needs the places of both.
            var decimals = Decimals(control.Min ?? 0d);
            if (control.Step > 0d && Math.Round(control.Step.Value, MaxSliderDecimals) > 0d)
            {
                return Math.Max(decimals, Decimals(control.Step.Value));
            }

            // Without a usable step, show about a hundredth of the range.
            var range = Math.Abs((control.Max ?? 1d) - (control.Min ?? 0d));
            var rangeDecimals = range > 0d && double.IsFinite(range) ? (int)Math.Ceiling(2d - Math.Log10(range)) : 2;
            return Math.Max(decimals, Math.Clamp(rangeDecimals, 2, MaxSliderDecimals));
        }

        private static int Decimals(double value)
        {
            // Values often arrive as widened floats, such as 0.01f read back as 0.0099999998.
            var rounded = Math.Round(value, MaxSliderDecimals);
            for (var decimals = 0; decimals < MaxSliderDecimals; decimals++)
            {
                var scaled = rounded * Math.Pow(10d, decimals);
                if (Math.Abs(scaled - Math.Round(scaled)) < 1e-6)
                {
                    return decimals;
                }
            }

            return MaxSliderDecimals;
        }

        /// <summary>A number control with a step, which gets - and + buttons.</summary>
        internal static bool IsStepped(MenuControl control)
        {
            return (control.ValueKind == ConfigValueKinds.Integer || control.ValueKind == ConfigValueKinds.Number) && control.Step > 0d;
        }

        /// <summary>Tone for a free-form status by whole words, so "Inactive" or "Not connected" never reads as healthy.</summary>
        internal static MenuTone StatusTone(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return MenuTone.Neutral;
            }

            var tokens = Tokens(status);
            for (var i = 0; i < tokens.Count; i++)
            {
                if (NegatedTokens.TryGetValue(tokens[i], out var negated))
                {
                    return negated;
                }

                if ((tokens[i] == "not" || tokens[i] == "no") && i + 1 < tokens.Count && IsStatusToken(tokens[i + 1]))
                {
                    return MenuTone.Neutral;
                }
            }

            if (tokens.Any(InfoTokens.Contains)) return MenuTone.Info;
            if (tokens.Any(GoodTokens.Contains)) return MenuTone.Good;
            if (tokens.Any(BadTokens.Contains)) return MenuTone.Bad;
            if (tokens.Any(WarnTokens.Contains)) return MenuTone.Warn;
            return MenuTone.Neutral;
        }

        /// <summary>
        /// The status a row shows as a pill: empty when it only says the row is in its normal state, such as READY or OK,
        /// so the pills that remain are the ones worth reading.
        /// </summary>
        internal static string NotableStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return "";
            }

            var tokens = Tokens(status);
            return tokens.Count > 0 && tokens.All(GoodTokens.Contains) ? "" : status;
        }

        /// <summary>The more serious of two tones, for a group that shows its worst member.</summary>
        internal static MenuTone WorseTone(MenuTone a, MenuTone b)
        {
            return ToneRank(b) > ToneRank(a) ? b : a;
        }

        internal static string BoolDisplay(bool value)
        {
            return value ? "ENABLED" : "DISABLED";
        }

        internal static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
            {
                return value ?? "";
            }

            return value.Substring(0, Math.Max(0, max - 1)) + ".";
        }

        private static bool IsStatusToken(string token)
        {
            return InfoTokens.Contains(token) || GoodTokens.Contains(token) || BadTokens.Contains(token)
                || WarnTokens.Contains(token) || NegatedTokens.ContainsKey(token);
        }

        private static int ToneRank(MenuTone tone)
        {
            switch (tone)
            {
                case MenuTone.Bad: return 4;
                case MenuTone.Warn: return 3;
                case MenuTone.Info: return 2;
                case MenuTone.Good: return 1;
                default: return 0;
            }
        }

        private static List<string> Tokens(string value)
        {
            var tokens = new List<string>();
            var start = -1;
            for (var i = 0; i <= value.Length; i++)
            {
                var word = i < value.Length && char.IsLetterOrDigit(value[i]);
                if (word && start < 0)
                {
                    start = i;
                }
                else if (!word && start >= 0)
                {
                    tokens.Add(value.Substring(start, i - start).ToLowerInvariant());
                    start = -1;
                }
            }

            return tokens;
        }
    }
}
