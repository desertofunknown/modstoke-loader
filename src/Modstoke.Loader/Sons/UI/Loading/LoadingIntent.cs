// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Modstoke.Localization;
using Sons.Gameplay.GameSetup;
using Sons.Save;
using UnityEngine;

namespace Modstoke.Loader
{
    internal enum LoadingSession { Single, Host, Join }

    /// <summary>
    /// What a world load is for, captured when the start is asked for: the menus that knew it are gone once the load
    /// begins. Values that are not known stay at -1 or empty and drop out of what the screen says.
    /// </summary>
    internal sealed record LoadingIntent
    {
        // The name the game gives every save it writes ("HardSurvival 9 Days 17:07:12"), which is no name to show.
        private static readonly Regex GameName = new(@"^\S+ \d+ Days \d{1,2}:\d{2}:\d{2}$", RegexOptions.Compiled);
        private static readonly string[] SeasonKeys = { "spring", "summer", "autumn", "winter" };

        internal LoadingSession Session { get; init; }
        /// <summary>False when nothing about the load could be read, as for a Steam invite; the screen then says Loading.</summary>
        internal bool Known { get; init; } = true;
        /// <summary>A new world rather than a saved one.</summary>
        internal bool Fresh { get; init; }
        internal SaveGameType Type { get; init; }
        internal uint SaveId { get; init; }
        internal string GameId { get; init; } = "";
        /// <summary>The save's own name, the server's or the lobby's; empty for a save the game named itself.</summary>
        internal string Name { get; init; } = "";
        /// <summary>The game's id for the mode: "Normal", "HardSurvival", "Custom"...</summary>
        internal string Mode { get; init; } = "";
        internal int Day { get; init; } = -1;
        internal int Hour { get; init; } = -1;
        internal int Minute { get; init; } = -1;
        /// <summary>Spring 0, Summer 1, Fall 2, Winter 3.</summary>
        internal int Season { get; init; } = -1;
        internal int Players { get; init; } = -1;
        internal int Limit { get; init; } = -1;
        internal bool Dedicated { get; init; }
        internal DateTime Saved { get; init; }
        internal float Asked { get; init; } = Time.unscaledTime;

        /// <summary>The kind of load its timings are kept under: sp-continue, sp-new, host-continue, host-new or join.</summary>
        internal string Kind => Session == LoadingSession.Join ? "join"
            : (Session == LoadingSession.Host ? "host-" : "sp-") + (Fresh ? "new" : "continue");

        /// <summary>Day or night at the save's hour: 0 day, 1 night, -1 unknown.</summary>
        internal int Light => LightAt(Hour);

        /// <summary>What this world's photos are filed under; see <see cref="WorldOf"/>.</summary>
        internal string World => WorldOf(GameId, Type.ToString(), SaveId);

        /// <summary>6:00 to 19:59 is day (0), the rest night (1); -1 for an unknown hour.</summary>
        internal static int LightAt(int hour) => hour < 0 ? -1 : hour >= 6 && hour < 20 ? 0 : 1;

        /// <summary>
        /// The key a world's photos are matched by: the game id its save keeps, which only multiplayer worlds have, else
        /// its save slot ("SinglePlayer/1813899769"); empty when neither is known, as for a world not saved yet.
        /// </summary>
        internal static string WorldOf(string gameId, string saveType, uint saveId)
        {
            if (!string.IsNullOrEmpty(gameId)) return gameId;
            return saveId != 0 && !string.IsNullOrEmpty(saveType) ? saveType + "/" + saveId.ToString(CultureInfo.InvariantCulture) : "";
        }

        private static Strings Text => FrontEnd.Text;

        internal static LoadingIntent FromSave(SaveGameType type, uint id, GameStateData data)
        {
            var name = (data.GameName ?? "").Trim();
            DateTime.TryParse(data.SaveTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var saved);
            return new LoadingIntent
            {
                Session = SessionOf(type),
                Type = type,
                SaveId = id,
                GameId = data.GameId ?? "",
                Name = GameName.IsMatch(name) ? "" : name,
                Mode = data.GameType ?? "",
                Day = data.GameDays,
                Hour = data.GameHours,
                Minute = data.GameMinutes,
                Saved = saved == default ? default : saved.ToLocalTime()
            };
        }

        /// <summary>A new game in <paramref name="mode"/>, read after the game's mode button has applied its rules.</summary>
        internal static LoadingIntent FromNew(SaveGameType type, string mode)
            => new() { Session = SessionOf(type), Type = type, Fresh = true, Mode = mode ?? "", Day = 1, Season = StartingSeason() };

        internal static LoadingIntent FromCustom(SaveGameType type) => FromNew(type, "Custom");

        /// <summary>Hosting the world the game's setup holds, under the server's name.</summary>
        internal static LoadingIntent ForHost(string server) => FromGameSetup() with { Session = LoadingSession.Host, Name = server ?? "" };

        /// <summary>The host's world as its lobby starts the game: the game's setup, the lobby's name and who is in it.</summary>
        internal static LoadingIntent FromHostedLobby()
            => ForHost(LobbyHost.Name) with { Players = LobbyHost.Members().Count, Limit = LobbyHost.Limit };

        /// <summary>A player joining the lobby they are in, as its Steam lobby data says.</summary>
        internal static LoadingIntent FromLobby()
        {
            var game = LobbyHost.Game;
            return new LoadingIntent
            {
                Session = LoadingSession.Join,
                Name = LobbyHost.Name ?? "",
                Mode = game.Mode,
                Fresh = !game.Saved,
                Players = LobbyHost.Members().Count,
                Limit = LobbyHost.Limit
            };
        }

        internal static LoadingIntent FromServerRow(string name, string mode, int players, int limit, bool dedicated)
            => new()
            {
                Session = LoadingSession.Join,
                Name = name ?? "",
                Mode = mode ?? "",
                Players = players,
                Limit = limit,
                Dedicated = dedicated
            };

        /// <summary>
        /// The load the game's setup describes, for starts no Modstoke menu saw: the death screen's new game, an
        /// invite, the game's own menus. A saved world is read from its save, a small read on the game thread.
        /// </summary>
        internal static LoadingIntent FromGameSetup()
        {
            try
            {
                var type = GameSetupManager.GetSaveGameType();
                if (type == SaveGameType.MultiplayerClient) return new LoadingIntent { Session = LoadingSession.Join, Type = type };
                var id = GameSetupManager.GetSelectedSaveId();
                if (id != 0)
                {
                    var data = SaveGameManager.GetData<GameStateData>(type, (int)id);
                    if (data != null) return FromSave(type, id, data);
                }
                var mode = GameSetupManager.TryGetMode(out var chosen) ? chosen.ToString() : "";
                return FromNew(type, mode) with { Known = mode.Length > 0 };
            }
            catch (Exception)
            {
                return new LoadingIntent { Known = false };
            }
        }

        internal static LoadingSession SessionOf(SaveGameType type) => type switch
        {
            SaveGameType.Multiplayer => LoadingSession.Host,
            SaveGameType.MultiplayerClient => LoadingSession.Join,
            _ => LoadingSession.Single
        };

        /// <summary>A season by the game's name for it ("Fall"), or "Autumn"; -1 for anything else.</summary>
        internal static int SeasonOf(string name) => name?.Trim().ToLowerInvariant() switch
        {
            "spring" => 0,
            "summer" => 1,
            "fall" or "autumn" => 2,
            "winter" => 3,
            _ => -1
        };

        internal static string SeasonName(int season) => season is >= 0 and <= 3 ? Text["options.value." + SeasonKeys[season]] : "";

        internal string Eyebrow()
        {
            var kind = !Known ? "load" : Session switch
            {
                LoadingSession.Join => "join",
                LoadingSession.Host => "host",
                _ => Fresh ? "new" : "continue"
            };
            return Text["loading.kind." + kind];
        }

        internal string Title()
        {
            if (Name.Length > 0) return Name;
            if (!Fresh && Day >= 0) return DayText();
            if (Mode.Length > 0) return HostSetupScreen.ModeName(Mode);
            return Text["loading.island"];
        }

        /// <summary>"Normal · Day 12 · 18:40 · Autumn · 3 of 8 players": the parts known for this load.</summary>
        internal string Details()
        {
            var parts = new List<string>(6);
            // What the title already says (the mode of an unnamed new game, the day of an unnamed save) is not repeated.
            if (Mode.Length > 0 && (Name.Length > 0 || !Fresh)) parts.Add(HostSetupScreen.ModeName(Mode));
            if (Day >= 0 && (Fresh || Name.Length > 0)) parts.Add(DayText());
            if (!Fresh && Hour >= 0)
                parts.Add(DateTime.Today.Add(new TimeSpan(Hour, Math.Max(0, Minute), 0)).ToString("t", Languages.CultureOf(Languages.Current)));
            if (Season >= 0) parts.Add(SeasonName(Season));
            if (Players >= 0 && Limit > 0) parts.Add(Text.Count("loading.players", Players, ("limit", Limit)));
            if (Dedicated) parts.Add(Text["loading.dedicated"]);
            return string.Join(" · ", parts);
        }

        /// <summary>When a continued save was written, worded as on the saves screen; empty otherwise.</summary>
        internal string SavedLine() => Fresh || Session == LoadingSession.Join ? "" : SavedGamesScreen.WhenOf(Saved);

        // Days as the saves screen and photo mode count them: the save's own count.
        private string DayText() => Text.Get("loading.day", ("day", Day));

        // The setting a mode button or the custom page left in the game's setup store.
        private static int StartingSeason()
        {
            try
            {
                return SeasonOf(GameSetupManager.GetSetting(GameSetupManager.StartingSeasonKey, GameSetupManager.DefaultStartingSeasonSettingValue));
            }
            catch (Exception)
            {
                return -1;
            }
        }
    }
}
