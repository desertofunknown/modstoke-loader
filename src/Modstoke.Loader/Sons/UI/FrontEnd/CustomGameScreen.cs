// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Sons.Save;

namespace Modstoke.Loader
{
    /// <summary>
    /// The rules of a custom game, in place of the game's own custom game page: the world, survival, enemies,
    /// and for a hosted game the multiplayer rules. The game starts through that page's own start handler, so
    /// it begins exactly as it would from there; a hosted game goes on to the host's setup first.
    /// </summary>
    internal sealed class CustomGameScreen : SettingsScreen
    {
        private readonly bool _single;
        private readonly List<OptionTab> _tabs;
        private readonly string[] _tabIds;

        internal CustomGameScreen(SaveGameType type)
        {
            _single = type == SaveGameType.SinglePlayer;
            var creative = GameMenus.OfferedModes(type, new[] { "Creative" }).Length > 0;
            _tabs = CustomGameCatalog.Tabs(!_single, creative);
            _tabIds = _tabs.Select(tab => tab.Id).ToArray();
        }

        protected override IReadOnlyList<string> TabIds => _tabIds;
        protected override OptionTab BuildTab(string id) => _tabs.First(tab => tab.Id == id);
        protected override string Heading => Text["custom.heading"];
        protected override string TabName(string id) => Text["custom.tab." + id];
        protected override string SectionName(string id) => Text["custom.section." + id];
        protected override string ItemName(OptionRow row) => Text["custom.item." + row.Id + ".name"];
        protected override string ItemAbout(OptionRow row) => Text["custom.item." + row.Id + ".about"];
        protected override string PrimaryLabel => Text[_single ? "custom.start" : "common.next"];

        protected override string Summary
        {
            get
            {
                var changed = _tabs.SelectMany(tab => tab.Sections).SelectMany(section => section.Rows)
                    .Count(row => row.Available && !row.IsDefault);
                return changed == 0 ? Text["custom.unchanged"] : Text.Count("custom.changed", changed);
            }
        }

        // With creative building off, its rules go back to their defaults rather than stay set out of reach.
        protected override void RowChanged(OptionRow row)
        {
            if (row.Id != "creative" || row.Index == 1) return;
            foreach (var rule in _tabs.SelectMany(tab => tab.Sections).SelectMany(section => section.Rows))
                if (CustomGameCatalog.CreativeRules.Contains(rule.Id))
                    rule.Reset();
        }

        protected override void Primary()
        {
            try
            {
                GameMenus.StartCustomGame();
                RuntimeEntryPoint.LogSource?.LogInfo("The menu started a custom " + (_single ? "single player" : "hosted") + " game.");
                // A hosted game opens the host's setup next; a single player game is now loading.
                if (FrontEnd.TakePages()) MenuSound.Play(MenuCue.Forward);
                else
                {
                    MenuSound.Play(MenuCue.Activate);
                    FrontEnd.StepAside();
                }
            }
            catch (Exception error)
            {
                Problem(Text["games.startFailed"]);
                MenuSound.Play(MenuCue.Back);
                RuntimeEntryPoint.LogSource?.LogWarning("The custom game could not start: " + error);
            }
            FrontInput.Pause(0.5f);
        }
    }
}
