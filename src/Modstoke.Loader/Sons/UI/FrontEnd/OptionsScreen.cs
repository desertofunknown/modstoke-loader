// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Settings;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// Options: the game's settings and Modstoke's own on tabs. Every change applies at once and is saved
    /// shortly after, as well as when the screen closes.
    /// </summary>
    internal sealed class OptionsScreen : SettingsScreen
    {
        private const float SaveDelay = 1.5f;
        // A change of resolution or window mode can leave the screen unreadable; it is kept only when confirmed.
        private static readonly HashSet<string> DisplayRows = new() { "resolution", "windowMode" };
        private static int _lastTab;
        private float _saveAt;

        internal OptionsScreen() : base(_lastTab) { }

        protected override IReadOnlyList<string> TabIds => OptionsCatalog.TabIds;
        protected override OptionTab BuildTab(string id) => OptionsCatalog.Tab(id);
        protected override string Heading => Text["options.title"];
        protected override string TabName(string id) => Text["options.tab." + id];
        protected override string SectionName(string id) => Text["options.section." + id];
        protected override string ItemName(OptionRow row) => Text["options.item." + row.Id + ".name"];
        protected override string ItemAbout(OptionRow row)
        {
            var about = Text["options.item." + row.Id + ".about"];
            if (row.Id != "graphicsApi") return about;
            // What this start is drawing with, and anything that keeps the choice from applying.
            var running = GraphicsApi.Running switch { "d3d12" => "DirectX 12", "d3d11" => "DirectX 11", _ => SystemInfo.graphicsDeviceVersion };
            about += "\n\n" + Text.Get("options.graphicsApi.running", ("api", running));
            if (GraphicsApi.FellBack) about += "\n" + Text["options.graphicsApi.fellBack"];
            if (GraphicsApi.Hung) about += "\n" + Text["options.graphicsApi.hung"];
            if (LaunchOptionsChooseApi) about += "\n" + Text["options.graphicsApi.launchOptions"];
            return about;
        }

        private static bool LaunchOptionsChooseApi
            => Array.Exists(Environment.GetCommandLineArgs(), argument => argument.StartsWith("-force-", StringComparison.OrdinalIgnoreCase));

        protected override void TabChanged(int index) => _lastTab = index;

        protected override void RowChanged(OptionRow row)
        {
            _saveAt = Time.unscaledTime + SaveDelay;
            // The engine picks its graphics API as the game starts, so the change waits for a restart; at the title
            // it can be had at once, while in a game it waits for the next start.
            if (row.Id != "graphicsApi" || LaunchOptionsChooseApi || !FrontEnd.AtTitle) return;
            FrontEnd.Push(new ConfirmScreen("confirm.restartTitle", "confirm.restartBody", "confirm.restart", "confirm.later",
                () => GameRestart.Now(Save)));
        }

        protected override bool NeedsConfirm(OptionRow row) => DisplayRows.Contains(row.Id);

        protected override void Confirm(OptionRow row, string before)
            => FrontEnd.Push(new ConfirmScreen("confirm.keepDisplayTitle", "confirm.keepDisplayBody", "confirm.keep", "confirm.revert",
                Save, () =>
                {
                    row.Restore(before);
                    Save();
                    Refresh();
                }, seconds: 15f));

        internal override void Tick()
        {
            base.Tick();
            if (_saveAt > 0 && Time.unscaledTime >= _saveAt) Save();
        }

        protected override void Save()
        {
            _saveAt = 0;
            try
            {
                GameSettingsManager.Save();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The game's settings could not be saved: " + error.Message);
            }
        }
    }
}
