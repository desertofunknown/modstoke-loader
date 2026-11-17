// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Modstoke.Loader
{
    /// <summary>
    /// A mod's config file, its simple settings laid out like every other Modstoke screen: an on or off value as a
    /// switch, anything else as text. Edits wait until Save writes them and Revert drops them, and leaving with edits
    /// not saved asks first. The file is held in the Mods menu's editing state, whose text editor takes a file this
    /// screen can't list.
    /// </summary>
    internal sealed class ConfigScreen : SettingsScreen
    {
        private const int MaxLength = 512;
        private static readonly string[] Tabs = { "file" };
        private readonly Dictionary<string, ConfigSettingSnapshot> _settings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _sections = new(StringComparer.Ordinal);
        private readonly string _entryId;
        private ModInventoryEntry _entry;
        private bool _saved, _handedOver;

        /// <summary>An on or off value; the dot beside a changed row marks an edit not saved yet, as the file names no defaults.</summary>
        private sealed class Switch : OptionRow
        {
            private readonly ConfigSettingSnapshot _setting;

            internal Switch(ConfigSettingSnapshot setting) : base(RowId(setting))
            {
                _setting = setting;
                Ready = true;
            }

            private bool On => IsOn(Current(_setting));
            internal override int Count => 2;
            internal override int Index => On ? 1 : 0;
            internal override string Value => Text[On ? "options.value.on" : "options.value.off"];
            internal override string DefaultValue => "";
            internal override bool IsDefault => On == IsOn(_setting.Value);
            internal override bool Reset() => false;

            internal override bool Step(int direction, bool wrap)
            {
                var next = wrap ? !On : direction > 0;
                if (next == On) return false;
                // Back to what the file says keeps its own spelling, which drops the edit.
                MenuState.SetFriendlyDraft(_setting, next == IsOn(_setting.Value) ? _setting.Value : next ? "true" : "false");
                return true;
            }
        }

        internal ConfigScreen(ModInventoryEntry entry)
        {
            _entry = entry;
            _entryId = MenuKeys.EntryId(entry);
            Load();
        }

        protected override IReadOnlyList<string> TabIds => Tabs;
        protected override string Title => _entry.DisplayName ?? "";
        protected override string TabName(string id) => Title;
        protected override string SectionName(string id) => _sections.TryGetValue(id, out var name) ? name : Text["config.section.file"];
        protected override bool CanReset => false;
        protected override string PrimaryLabel => Text["config.save"];
        protected override string EmptyText => Text["config.unreadable"];

        protected override string Heading
        {
            get
            {
                var file = Path.GetFileName(_entry.ConfigPath ?? "");
                return file.Length > 0 ? Text["config.heading"] + " · " + file : Text["config.heading"];
            }
        }

        // Over Save: that there is something to save, or that the last save went through.
        protected override string Summary => MenuState.IsConfigDirty ? Text["config.unsaved"] : _saved ? Text["config.saved"] : null;

        protected override string ItemName(OptionRow row)
            => _settings.TryGetValue(row.Id, out var setting) ? OptionText.Words(setting.Key) : Text["config.textEditor"];

        protected override string ItemAbout(OptionRow row)
        {
            var lines = new List<string>();
            if (_settings.TryGetValue(row.Id, out var setting))
            {
                if (!string.IsNullOrWhiteSpace(setting.Tooltip)) lines.Add(setting.Tooltip.Trim());
            }
            else
            {
                if (MenuState.FriendlySettings.Length == 0) lines.Add(Text["config.empty"]);
                lines.Add(Text[FrontEnd.PadActive ? "config.textEditorAboutPad" : "config.textEditorAbout"]);
            }
            if (!string.IsNullOrWhiteSpace(_entry.ConfigPath))
                lines.Add("<size=80%><color=" + MenuKit.Html(MenuKit.Faint) + ">" + _entry.ConfigPath + "</color></size>");
            return string.Join("\n\n", lines);
        }

        protected override IReadOnlyList<(string Label, string Value)> ItemFacts(OptionRow row)
        {
            if (!_settings.TryGetValue(row.Id, out var setting)) return null;
            return new[]
            {
                (Text["config.fact.key"], setting.Key),
                (Text["config.fact.saved"], string.IsNullOrEmpty(setting.Value) ? "—" : setting.Value)
            };
        }

        protected override OptionTab BuildTab(string id)
        {
            _settings.Clear();
            _sections.Clear();
            var sections = new List<OptionSection>();
            // A file that could not be read is never written, so nothing is listed and the empty list says why.
            if (!MenuState.ConfigCanSave) return new OptionTab(id, sections);
            List<OptionRow> rows = null;
            string section = null;
            foreach (var setting in MenuState.FriendlySettings)
            {
                // Sections follow the file, so a name that comes back later starts a new one.
                if (rows == null || !string.Equals(section, setting.Section, StringComparison.OrdinalIgnoreCase))
                {
                    section = setting.Section;
                    rows = new List<OptionRow>();
                    var sectionId = "section:" + sections.Count.ToString(CultureInfo.InvariantCulture);
                    _sections[sectionId] = section ?? "";
                    sections.Add(new OptionSection(sectionId, rows));
                }
                var row = Row(setting);
                _settings[row.Id] = setting;
                rows.Add(row);
            }
            sections.Add(OptionsCatalog.Section("file", new OptionAction("text", OpenText)));
            return new OptionTab(id, sections);
        }

        protected override void RowChanged(OptionRow row) => _saved = false;

        protected override void Primary()
        {
            if (!Unsaved()) return;
            if (!MenuState.SaveConfig(() => RuntimeEntryPoint.RefreshModInventory(), out _))
            {
                MenuSound.Play(MenuCue.Back);
                Problem(Text["config.saveFailed"]);
                RuntimeEntryPoint.LogSource?.LogWarning("The config file of " + _entry.DisplayName + " was not saved: " + MenuState.ConfigStatus);
                return;
            }
            // Saving rescans the mods, so the entry and the file's settings are both new.
            _entry = MenuState.ConfigEntry ?? _entry;
            _saved = true;
            MenuSound.Play(MenuCue.Activate);
            Rebuild();
        }

        internal override IReadOnlyList<PromptSpec> Prompts
            => base.Prompts.Append(new PromptSpec(Text["confirm.revert"], "X", "DEL", Revert)).ToArray();

        internal override void OnAlt1() => Revert();

        internal override bool OnBack()
        {
            CommitEdit();
            if (!Unsaved()) return false;
            Leave(() => FrontEnd.Pop(true));
            return true;
        }

        /// <summary>Runs <paramref name="leave"/> now, or once the player agrees to drop edits that are not saved.</summary>
        internal void Leave(Action leave)
        {
            // Text still being typed counts as an edit, as a click on Back or the Mods key arrives before it is kept.
            CommitEdit();
            if (!Unsaved())
            {
                leave();
                return;
            }
            FrontEnd.Push(new ConfirmScreen("config.unsavedTitle", "config.unsavedBody", "config.discard", "config.keepEditing", () =>
            {
                MenuState.RevertConfig();
                leave();
            }, cautious: true));
        }

        internal override void Tick()
        {
            base.Tick();
            // Back from the text editor, which closed the file behind it: it is read again with whatever was saved there.
            // The editor shows in the frame it is asked for, so this waits until it has gone again.
            if (!_handedOver || MenuController.IsVisible) return;
            _handedOver = false;
            Load();
            Rebuild();
        }

        internal override void Closed()
        {
            base.Closed();
            MenuState.CloseConfig();
        }

        private static string RowId(ConfigSettingSnapshot setting) => "line:" + setting.LineIndex.ToString(CultureInfo.InvariantCulture);

        private static string Current(ConfigSettingSnapshot setting)
            => MenuState.TryGetFriendlyDraft(setting.LineIndex, out var draft) ? draft : setting.Value ?? "";

        private static bool IsOn(string value) => LoaderConfig.TryParseBoolean(value?.Trim(), out var on) && on;

        // The file names no kinds, so an on or off value is told by its text; 0 and 1 stay numbers.
        private static OptionRow Row(ConfigSettingSnapshot setting)
        {
            var value = setting.Value?.Trim();
            if (value != "0" && value != "1" && LoaderConfig.TryParseBoolean(value, out _)) return new Switch(setting);
            return new TextRow(RowId(setting), () => Current(setting), text =>
            {
                MenuState.SetFriendlyDraft(setting, text);
                return true;
            }, setting.Value ?? "", MaxLength);
        }

        private void Load()
        {
            _entry = MenuState.FindEntry(_entryId) ?? _entry;
            MenuState.CloseConfig();
            MenuState.OpenConfig(_entry, MenuController.MaxConfigEditorBytes);
        }

        // Whether there are edits to save. The editing state is the Mods menu's, so something else may have closed or
        // reopened the file since the rows were built; then it is read again, and nothing the old rows held is written.
        private bool Unsaved()
        {
            var open = MenuState.ConfigEntry;
            if (open != null && MenuKeys.EntryId(open) == _entryId && MenuState.FriendlyMode) return MenuState.IsConfigDirty;
            Load();
            Rebuild();
            return false;
        }

        private void Revert()
        {
            if (!Unsaved()) return;
            MenuSound.Play(MenuCue.Back);
            MenuState.RevertConfig();
            Problem(null);
            Rebuild();
        }

        // That editor reads the file afresh, so edits made here are saved or dropped first.
        private void OpenText()
        {
            if (Unsaved())
            {
                Problem(Text["config.saveFirst"]);
                return;
            }
            _handedOver = true;
            MenuController.ShowConfigText(_entry);
        }
    }
}
