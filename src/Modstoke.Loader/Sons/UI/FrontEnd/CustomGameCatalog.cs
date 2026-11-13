// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using Sons.Gameplay.GameSetup;
using Il2CppStrings = Il2CppSystem.Collections.Generic.List<string>;

namespace Modstoke.Loader
{
    /// <summary>
    /// The rules of a custom game, as the game's own custom game page offers them: each is a key in the game's
    /// setup store with its choices and default, read and written there, so the game starts with them exactly as
    /// it would from its own page.
    /// </summary>
    internal static class CustomGameCatalog
    {
        /// <summary>The rules that only apply with creative building on, and are switched off with it.</summary>
        internal static readonly string[] CreativeRules = { "immortal", "placeFullLoad", "noCuttings", "oneHitTrees" };

        /// <summary>
        /// The rules on tabs. Multiplayer rules are only for a hosted game, and creative ones only once the
        /// game has unlocked Creative, as on the game's own page.
        /// </summary>
        internal static List<OptionTab> Tabs(bool multiplayer, bool creative)
        {
            var tabs = new List<OptionTab>
            {
                new("world", new[]
                {
                    OptionsCatalog.Section("seasons",
                        Choice("startingSeason", () => GameSetupManager.StartingSeasonKey,
                            () => GameSetupManager.DefaultStartingSeasonSettingValue, () => GameSetupManager.StartingSeasonSettings),
                        Choice("seasonLength", () => GameSetupManager.SeasonLengthKey,
                            () => GameSetupManager.DefaultSeasonLengthSettingValue, () => GameSetupManager.SeasonLengthSettings),
                        Choice("dayLength", () => GameSetupManager.DayLengthKey,
                            () => GameSetupManager.DefaultDayLengthSettingValue, () => GameSetupManager.DayLengthSettings),
                        Choice("precipitation", () => GameSetupManager.PrecipitationFrequencyKey,
                            () => GameSetupManager.DefaultPrecipitationFrequencySettingValue, () => GameSetupManager.PrecipitationFrequencySettings))
                }),
                new("survival", new[]
                {
                    OptionsCatalog.Section("player",
                        Choice("statsDamage", () => GameSetupManager.PlayerStatsDamageKey,
                            () => GameSetupManager.DefaultPlayerStatsDamageSettingValue, () => GameSetupManager.PlayerStatsDamageSettings),
                        Choice("consumables", () => GameSetupManager.ConsumableEffectsKey,
                            () => GameSetupManager.DefaultConsumableEffectsSettingValue, () => GameSetupManager.ConsumableEffectsSettings),
                        Choice("sleepingDelay", () => GameSetupManager.SleepingDelayKey,
                            () => GameSetupManager.DefaultSleepingDelaySettingValue, () => GameSetupManager.SleepingDelaySettings),
                        Choice("coldPenalties", () => GameSetupManager.ColdPenaltiesKey,
                            () => GameSetupManager.DefaultColdPenaltiesSettingValue, () => GameSetupManager.ColdPenaltiesSettings),
                        Choice("recovery", () => GameSetupManager.StatRegenerationPenaltyKey,
                            () => GameSetupManager.DefaultStatRegenerationPenaltySettingValue, () => GameSetupManager.StatRegenerationPenaltySettings),
                        Switch("pausedInInventory", () => GameSetupManager.PausedInInventoryKey, () => GameSetupManager.DefaultPausedInInventorySettingValue)),
                    OptionsCatalog.Section("supplies",
                        Switch("lessFood", () => GameSetupManager.ReducedFoodInContainersKey, () => GameSetupManager.DefaultReducedFoodInContainersSettingValue),
                        Switch("lessAmmo", () => GameSetupManager.ReducedAmmoInContainersKey, () => GameSetupManager.DefaultReducedAmmoInContainersSettingValue),
                        Switch("noRefill", () => GameSetupManager.SingleUseContainersKey, () => GameSetupManager.DefaultSingleUseContainersSettingValue)),
                    OptionsCatalog.Section("building",
                        Choice("buildingStrength", () => GameSetupManager.BuildingResistanceKey,
                            () => GameSetupManager.DefaultBuildingResistanceSettingValue, () => GameSetupManager.BuildingResistanceSettings),
                        Switch("ownTraps", () => GameSetupManager.PlayersTriggerTrapsKey, () => GameSetupManager.DefaultPlayersTriggerTrapsSettingValue))
                }),
                new("enemies", new[]
                {
                    OptionsCatalog.Section("enemies",
                        Switch("enemies", () => GameSetupManager.VailEnemySpawnKey, () => GameSetupManager.DefaultEnemySpawnSettingValue),
                        Choice("enemyHealth", () => GameSetupManager.VailEnemyHealthKey,
                            () => GameSetupManager.DefaultVailEnemyHealthSettingValue, () => GameSetupManager.VailEnemyHealthSettings),
                        Choice("enemyDamage", () => GameSetupManager.VailEnemyDamageKey,
                            () => GameSetupManager.DefaultVailEnemyDamageSettingValue, () => GameSetupManager.VailEnemyDamageSettings),
                        Choice("enemyArmor", () => GameSetupManager.VailEnemyArmourKey,
                            () => GameSetupManager.DefaultVailEnemyArmourSettingValue, () => GameSetupManager.VailEnemyArmourSettings),
                        Choice("enemyAggression", () => GameSetupManager.VailEnemyAggressionKey,
                            () => GameSetupManager.DefaultVailEnemyAggressionSettingValue, () => GameSetupManager.VailEnemyAggressionSettings),
                        Choice("searchParties", () => GameSetupManager.VailEnemySearchPartiesKey,
                            () => GameSetupManager.DefaultVailEnemySearchPartiesValue, () => GameSetupManager.VailEnemySearchPartiesSettings)),
                    OptionsCatalog.Section("animals",
                        Choice("animals", () => GameSetupManager.VailAnimalSpawnRateKey,
                            () => GameSetupManager.DefaultVailAnimalSpawnRateSettingValue, () => GameSetupManager.VailAnimalSpawnRateSettings))
                })
            };

            var rules = new List<OptionSection>();
            if (multiplayer)
                rules.Add(OptionsCatalog.Section("multiplayer",
                    Switch("cheats", () => GameSetupManager.MultiplayerCheatsKey, () => GameSetupManager.DefaultMultiplayerCheatsSettingValue),
                    Choice("pvpDamage", () => GameSetupManager.PvpDamageKey,
                        () => GameSetupManager.DefaultPvpDamageSettingValue, () => GameSetupManager.PvpDamageSettings)));
            if (creative)
            {
                var building = Switch("creative", () => GameSetupManager.CreativeModeKey, () => GameSetupManager.DefaultCreativeModeSettingValue);
                rules.Add(OptionsCatalog.Section("creative", building,
                    Under(building, Switch("immortal", () => GameSetupManager.PlayersGodModeKey, () => GameSetupManager.DefaultPlayersGodModeSettingValue)),
                    Under(building, OptionsCatalog.Make("placeFullLoad", () => new SwitchRow("placeFullLoad",
                        GameSetupManager.GetFreeFormForcePlaceFullLoadSetting, GameSetupManager.SetFreeFormForcePlaceFullLoadSetting,
                        GameSetupManager.DefaultFreeFormForcePlaceFullLoadSettingValue))),
                    Under(building, OptionsCatalog.Make("noCuttings", () => new SwitchRow("noCuttings",
                        GameSetupManager.GetConstructionSkipCuttingsSpawnSetting, GameSetupManager.SetConstructionSkipCuttingsSpawnSetting,
                        GameSetupManager.DefaultConstructionSkipCuttingsSpawnSettingValue))),
                    Under(building, Switch("oneHitTrees", () => GameSetupManager.OneHitToCutTreesKey, () => GameSetupManager.DefaultOneHitToCutTreesSettingValue))));
            }
            tabs.Add(new OptionTab("rules", rules));
            return tabs.Where(tab => tab.Sections.Any(section => section.Rows.Count > 0)).ToList();
        }

        // A rule that can only be changed while creative building is on.
        private static OptionRow Under(OptionRow creative, OptionRow row)
        {
            if (row != null && creative != null) row.When = () => creative.Index == 1;
            return row;
        }

        private static OptionRow Choice(string id, Func<string> key, Func<string> @default, Func<Il2CppStrings> choices)
            => OptionsCatalog.Make(id, () =>
            {
                var name = key();
                var fallback = @default();
                var list = choices();
                var values = new List<(string Value, Func<string> Name)>();
                if (list != null)
                    for (var i = 0; i < list.Count; i++)
                    {
                        var value = list[i];
                        if (!string.IsNullOrEmpty(value)) values.Add((value, () => OptionText.Describe(value)));
                    }
                return new ChoiceRow(id, () => GameSetupManager.GetSetting(name, fallback), value =>
                {
                    GameSetupManager.SetSetting(name, value);
                    return true;
                }, fallback, values.ToArray());
            });

        private static OptionRow Switch(string id, Func<string> key, Func<bool> @default)
            => OptionsCatalog.Make(id, () =>
            {
                var name = key();
                var fallback = @default();
                return new SwitchRow(id, () => GameSetupManager.GetSetting(name, fallback), value => GameSetupManager.SetSetting(name, value), fallback);
            });
    }
}
