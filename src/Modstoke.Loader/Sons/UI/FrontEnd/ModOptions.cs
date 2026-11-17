// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Modstoke.Localization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>The Options tab a mod's section sits on.</summary>
    /// <remarks>The Accessibility tab is shown only while a mod has a section on it.</remarks>
    public enum OptionsPage { Display, Graphics, Gameplay, Controls, Audio, Accessibility, Modstoke }

    /// <summary>One row of a mod's Options section.</summary>
    /// <remarks>
    /// The name and description are read each time the row is drawn, so they follow the player's language. Values
    /// are read and written on the game thread through the accessors given; the mod keeps and saves them itself.
    /// </remarks>
    public abstract class OptionsSetting
    {
        private protected OptionsSetting(string id, Func<string> name, Func<string> about)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A setting needs an id.", nameof(id));
            Id = id;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            About = about;
        }

        public string Id { get; }
        public Func<string> Name { get; }
        /// <summary>What the setting does, shown beside the list while it has focus; null for nothing.</summary>
        public Func<string> About { get; }
        /// <summary>When the setting can be changed, for one that depends on another; null for always. Otherwise it shows dimmed.</summary>
        public Func<bool> When { get; init; }

        internal abstract OptionRow Row(string id);
    }

    /// <summary>A setting with named choices, stepped through with left and right.</summary>
    public sealed class OptionsChoice : OptionsSetting
    {
        private readonly (string Value, Func<string> Name)[] _choices;
        private readonly Func<string> _read;
        private readonly Action<string> _write;
        private readonly string _default;

        public OptionsChoice(string id, Func<string> name, Func<string> about, IReadOnlyList<(string Value, Func<string> Name)> choices,
            Func<string> read, Action<string> write, string @default) : base(id, name, about)
        {
            _choices = choices?.ToArray() ?? throw new ArgumentNullException(nameof(choices));
            if (_choices.Length == 0 || _choices.Any(choice => choice.Value == null || choice.Name == null))
                throw new ArgumentException("Every choice needs a value and a name.", nameof(choices));
            if (!_choices.Any(choice => choice.Value == @default)) throw new ArgumentException("The default is not one of the choices.", nameof(@default));
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _default = @default;
        }

        internal override OptionRow Row(string id) => new ChoiceRow(id, () => ModOptions.Call(id, _read, _default), value => ModOptions.Call(id, _write, value),
            _default, _choices.Select(choice => (choice.Value, (Func<string>)(() => ModOptions.Call(id, choice.Name, choice.Value)))).ToArray());
    }

    /// <summary>A setting that is on or off.</summary>
    public sealed class OptionsSwitch : OptionsSetting
    {
        private readonly Func<bool> _read;
        private readonly Action<bool> _write;
        private readonly bool _default;

        public OptionsSwitch(string id, Func<string> name, Func<string> about, Func<bool> read, Action<bool> write, bool @default)
            : base(id, name, about)
        {
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _default = @default;
        }

        internal override OptionRow Row(string id)
            => new SwitchRow(id, () => ModOptions.Call(id, _read, _default), value => ModOptions.Call(id, _write, value), _default);
    }

    /// <summary>A number on a slider. Values land on whole steps from <c>min</c>.</summary>
    public sealed class OptionsSlider : OptionsSetting
    {
        private readonly float _min, _max, _step, _default;
        private readonly Func<float> _read;
        private readonly Action<float> _write;
        private readonly Func<float, string> _format;

        /// <param name="format">How a value reads beside the slider; null shows it as a percentage of 0 to 1.</param>
        public OptionsSlider(string id, Func<string> name, Func<string> about, float min, float max, float step, Func<float> read,
            Action<float> write, float @default, Func<float, string> format = null) : base(id, name, about)
        {
            if (!(max > min) || !(step > 0)) throw new ArgumentException("A slider needs a range and a step.", nameof(step));
            _min = min;
            _max = max;
            _step = step;
            _default = Mathf.Clamp(@default, min, max);
            _read = read ?? throw new ArgumentNullException(nameof(read));
            _write = write ?? throw new ArgumentNullException(nameof(write));
            _format = format ?? Percent;
        }

        /// <summary>0 to 1 as a percentage, the way the Options screen shows its own ("75%").</summary>
        public static string Percent(float value) => Mathf.RoundToInt(value * 100f).ToString(Languages.CultureOf(Languages.Current)) + "%";

        internal override OptionRow Row(string id) => new NumberRow(id, () => ModOptions.Call(id, _read, _default), value => ModOptions.Call(id, _write, value),
            _min, _max, _step, () => _default, value => ModOptions.Call(id, () => _format(value), ""));
    }

    /// <summary>
    /// Sections mods add to the front end's Options screen. Each sits under its own heading after the tab's own
    /// settings and is drawn, navigated and reset like every other row there, with the keyboard, mouse or a controller.
    /// </summary>
    public static class ModOptions
    {
        // Row ids carry this, so a mod's ids never meet the Loader's own text keys or another mod's.
        private const string Prefix = "mod:";
        private static readonly object Sync = new();
        private static readonly List<Entry> Entries = new();
        private static readonly HashSet<string> Faulted = new();

        private sealed record Entry(string Id, string Tab, Func<string> Title, OptionRow[] Rows, Action Save);

        /// <summary>Adds a section; false when one with this id is already there.</summary>
        /// <param name="id">Unique across mods; start it with the mod's id.</param>
        /// <param name="save">Called shortly after a change and when Options closes, to write the mod's settings.</param>
        /// <remarks>An Options screen already open shows the section once it is opened again.</remarks>
        public static bool TryAddSection(string id, OptionsPage page, Func<string> title, IReadOnlyList<OptionsSetting> settings, Action save = null)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A section needs an id.", nameof(id));
            if (!Enum.IsDefined(page)) throw new ArgumentOutOfRangeException(nameof(page));
            if (title == null) throw new ArgumentNullException(nameof(title));
            if (settings == null || settings.Count == 0 || settings.Any(setting => setting == null))
                throw new ArgumentException("A section needs its settings.", nameof(settings));
            if (settings.Select(setting => setting.Id).Distinct(StringComparer.Ordinal).Count() != settings.Count)
                throw new ArgumentException("Setting ids repeat within the section.", nameof(settings));
            var sectionId = Prefix + id;
            var rows = settings.Select(setting =>
            {
                var rowId = sectionId + "/" + setting.Id;
                var row = setting.Row(rowId);
                row.When = setting.When;
                row.Name = () => Call(rowId, setting.Name, setting.Id);
                row.About = setting.About == null ? () => "" : () => Call(rowId, setting.About, "");
                return row;
            }).ToArray();
            lock (Sync)
            {
                if (Entries.Any(entry => entry.Id == sectionId)) return false;
                Entries.Add(new Entry(sectionId, OptionsCatalog.AllTabs[(int)page], () => Call(sectionId, title, id), rows, save));
                return true;
            }
        }

        public static bool TryRemoveSection(string id)
        {
            lock (Sync) return Entries.RemoveAll(entry => entry.Id == Prefix + id) > 0;
        }

        internal static bool Any(string tab)
        {
            lock (Sync) return Entries.Any(entry => entry.Tab == tab);
        }

        internal static IEnumerable<OptionSection> Sections(string tab)
        {
            lock (Sync) return Entries.Where(entry => entry.Tab == tab).Select(entry => new OptionSection(entry.Id, entry.Rows)).ToArray();
        }

        internal static bool TryTitle(string sectionId, out string title)
        {
            Entry entry;
            lock (Sync) entry = Entries.FirstOrDefault(candidate => candidate.Id == sectionId);
            title = entry?.Title();
            return entry != null;
        }

        internal static void Save()
        {
            Entry[] entries;
            lock (Sync) entries = Entries.ToArray();
            foreach (var entry in entries)
                if (entry.Save != null) Call(entry.Id, () => entry.Save());
        }

        // A mod's accessors are its own code: a throw is logged once and the row carries on with its default, so one
        // faulty setting never takes the screen down.
        internal static T Call<T>(string id, Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception error)
            {
                Fault(id, error);
                return fallback;
            }
        }

        internal static bool Call<T>(string id, Action<T> write, T value)
        {
            try
            {
                write(value);
                return true;
            }
            catch (Exception error)
            {
                Fault(id, error);
                return false;
            }
        }

        private static void Call(string id, Action action)
        {
            try { action(); }
            catch (Exception error) { Fault(id, error); }
        }

        private static void Fault(string id, Exception error)
        {
            lock (Sync)
            {
                if (!Faulted.Add(id)) return;
            }
            RuntimeEntryPoint.LogSource?.LogWarning("Options: \"" + id + "\" failed in its mod: " + error);
        }
    }
}
