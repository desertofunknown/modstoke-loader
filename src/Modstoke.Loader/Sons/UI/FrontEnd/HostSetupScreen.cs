// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Localization;
using Sons.Gameplay.GameSetup;
using Sons.Multiplayer;
using Sons.Save;
using Sons.Settings;

namespace Modstoke.Loader
{
    /// <summary>
    /// The last step before hosting, in place of the game's host options: the server's name, how many players
    /// it takes and whether it is listed. The values go into the game's own multiplayer settings and the game's
    /// own start button starts the server, so hosting runs exactly as it does from the game's page.
    /// </summary>
    internal sealed class HostSetupScreen : SettingsScreen
    {
        private const int NameLength = 40;
        private static readonly string[] Tab = { "server" };
        private readonly OptionTab _tab;
        private readonly string _world, _about;

        internal HostSetupScreen()
        {
            (_world, _about) = Describe();
            var name = OptionsCatalog.Make("serverName", () => new TextRow("serverName", MultiplayerUtilities.GetServerName, SetName,
                MultiplayerUtilities.GetDefaultServerName(), NameLength) { PadKeyboard = GameMenus.NameKeyboard });
            _tab = new OptionTab("server", new[] { OptionsCatalog.Section("server", name, Players(), Visibility()) });
        }

        protected override IReadOnlyList<string> TabIds => Tab;
        protected override OptionTab BuildTab(string id) => _tab;
        protected override string Heading => Text["games.hostTitle"];
        protected override string Title => _world;
        protected override string TabName(string id) => Text["host.section." + id];
        protected override string SectionName(string id) => Text["host.section." + id];
        protected override string ItemName(OptionRow row) => Text["host.item." + row.Id + ".name"];
        protected override string ItemAbout(OptionRow row) => Text["host.item." + row.Id + ".about"];
        protected override string PrimaryLabel => Text["host.start"];
        protected override string Summary => _about;

        protected override void Save()
        {
            try
            {
                GameSettingsManager.Save();
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The host's settings could not be saved: " + error.Message);
            }
        }

        protected override void Primary()
        {
            string name;
            try
            {
                name = MultiplayerUtilities.SanitizeServerName(MultiplayerUtilities.GetServerName() ?? "", true);
            }
            catch (Exception)
            {
                name = "";
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                Problem(Text["host.nameNeeded"]);
                MenuSound.Play(MenuCue.Back);
                FocusRow("serverName");
                return;
            }
            try
            {
                Save();
                GameMenus.StartHosting(name);
                MenuSound.Play(MenuCue.Activate);
                FrontEnd.StepAside(10f);
                RuntimeEntryPoint.LogSource?.LogInfo("The menu started hosting.");
            }
            catch (Exception error)
            {
                Problem(Text["games.startFailed"]);
                MenuSound.Play(MenuCue.Back);
                RuntimeEntryPoint.LogSource?.LogWarning("Hosting could not start: " + error);
            }
            FrontInput.Pause(0.5f);
        }

        // The game cleans the name the way its own field does; one that comes out empty is refused.
        private static bool SetName(string typed)
        {
            var name = MultiplayerUtilities.SanitizeServerName(typed ?? "", true);
            if (string.IsNullOrWhiteSpace(name)) return false;
            MultiplayerUtilities.SetServerName(name);
            return true;
        }

        private static OptionRow Players() => OptionsCatalog.Make("players", () =>
        {
            var low = MultiplayerUtilities.MinClientCount;
            var high = MultiplayerUtilities.MaxClientCount;
            var choices = new List<(string Value, Func<string> Name)>();
            for (var count = low; count <= high; count++)
            {
                var text = count.ToString(CultureInfo.InvariantCulture);
                choices.Add((text, () => int.Parse(text, CultureInfo.InvariantCulture).ToString(Languages.CultureOf(Languages.Current))));
            }
            return new ChoiceRow("players", () => MultiplayerUtilities.GetServerClientCount().ToString(CultureInfo.InvariantCulture), value =>
            {
                MultiplayerUtilities.SetServerClientCount(int.Parse(value, CultureInfo.InvariantCulture));
                return true;
            }, null, choices.ToArray());
        });

        private static OptionRow Visibility() => OptionsCatalog.Make("visibility", () => new ChoiceRow("visibility",
            () => MultiplayerUtilities.GetServerIsPrivate() ? "private" : "public", value =>
            {
                MultiplayerUtilities.SetServerIsPrivate(value == "private");
                return true;
            }, null, new (string, Func<string>)[]
            {
                ("public", () => FrontEnd.Text["host.value.public"]),
                ("private", () => FrontEnd.Text["host.value.private"])
            }));

        // The world being hosted: a saved game by its name, or a new one by its mode.
        private static (string World, string About) Describe()
        {
            try
            {
                var id = GameSetupManager.GetSelectedSaveId();
                if (id != 0)
                {
                    var data = SaveGameManager.GetData<GameStateData>(SaveGameType.Multiplayer, (int)id);
                    if (data != null)
                    {
                        var mode = ModeName(data.GameType);
                        var about = Text.Get("games.modeAndDay", ("mode", mode), ("days", data.GameDays));
                        var name = (data.GameName ?? "").Trim();
                        return (name.Length > 0 ? name : about, about);
                    }
                }
                var current = GameSetupManager.TryGetMode(out var chosen) ? chosen.ToString() : "";
                return (Text["games.newGame"], current.Length > 0 ? ModeName(current) : null);
            }
            catch (Exception)
            {
                return (Text["games.newGame"], null);
            }
        }

        private static string ModeName(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return "";
            var key = "mode." + char.ToLowerInvariant(mode[0]) + mode.Substring(1);
            return Text.Has(key) ? Text[key] : mode;
        }
    }
}
