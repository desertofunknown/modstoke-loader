// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Localization;
using Random = UnityEngine.Random;

namespace Modstoke.Loader
{
    /// <summary>What a tip can depend on, known when the screen is raised.</summary>
    internal readonly record struct TipContext(LoadingSession Session, bool Fresh, string Mode, int Season, bool Pad, bool PhotoKey,
        bool Mods, bool OwnPhotos)
    {
        internal bool Creative => string.Equals(Mode, "Creative", StringComparison.OrdinalIgnoreCase);
        internal bool Peaceful => string.Equals(Mode, "Peaceful", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A tip chosen for this load, its texts set: a header of its category, or of the control it teaches.</summary>
    internal sealed class PlannedTip
    {
        internal string Id, Text, Category, Control;
        internal ControlKey Pad, Key;
    }

    /// <summary>
    /// The loading screen's tips, written for this game and Modstoke: each one a text in Modstoke's language files
    /// (loading.tip.&lt;id&gt;), shown only where it applies, with tips not seen yet first and none repeated until
    /// every one that applies has been shown.
    /// </summary>
    internal static class LoadingTips
    {
        private const int PlannedCount = 4, HistoryLength = 80;
        private const float BasicWeight = 3f;

        private sealed record Tip(string Id, Func<TipContext, bool> When, float Weight = 1f, bool Basic = false,
            string Control = null, string PadControl = null, string Keybind = null, string Label = null, bool NeedsButton = false);

        private static readonly Func<TipContext, bool> Any = _ => true;
        private static readonly Func<TipContext, bool> Survival = c => !c.Creative;
        private static readonly Func<TipContext, bool> Hostile = c => !c.Peaceful;

        // Ids are the language keys under loading.tip; a tip that names a control does not name the button in its text.
        private static readonly Tip[] Catalog =
        {
            new("survival.vitals", Survival, Basic: true),
            new("survival.sickness", Survival, Basic: true),
            new("survival.cold", Survival),
            new("survival.rest", Survival),
            new("survival.shelter", Survival, Basic: true),
            new("survival.drying", Survival),
            new("survival.cooking", Survival),
            new("survival.strength", Any),
            new("building.logs", Any, Basic: true),
            new("building.placeSeveral", Any, Control: "BatchPlace", Label: "placeSeveral", NeedsButton: true),
            new("building.takeApart", Any, Control: "DismantleElement", Label: "takeApart", NeedsButton: true),
            new("building.book", Any, Control: "Book", Label: "book"),
            new("companions.kelvin", c => c.Session != LoadingSession.Join, Basic: true),
            new("combat.heavy", Hostile, Control: "PrimaryAction", Label: "heavyAttack", NeedsButton: true),
            new("combat.bushes", Hostile),
            new("combat.explosives", Hostile),
            new("combat.boneArmour", Any),
            new("combat.putAway", Any, Control: "Drop", Label: "putAway", NeedsButton: true),
            new("exploring.gps", Any, Basic: true, Control: "GpsTracker", PadControl: "Utility", Label: "gps"),
            new("exploring.light", Any, Basic: true, Control: "Lighter", Label: "light"),
            new("exploring.inventory", Any, Basic: true, Control: "Inventory", Label: "inventory", NeedsButton: true),
            new("exploring.hotkeys", c => !c.Pad),
            new("exploring.flippers", Any),
            new("exploring.knight", Any),
            new("exploring.traps", Survival),
            new("seasons.autumn", c => c.Season == 2, 3f),
            new("seasons.winter", c => c.Season == 3, 3f),
            new("seasons.lakes", c => c.Season == 3),
            new("multiplayer.voice", c => c.Session != LoadingSession.Single),
            new("multiplayer.content", c => c.Session == LoadingSession.Join),
            new("modstoke.photo", c => c.PhotoKey, Keybind: KeybindIds.PhotoMode, Label: "photo"),
            new("modstoke.photoWorld", c => c.Session == LoadingSession.Single),
            // Photos are matched to a saved world, which a join or a world not saved yet doesn't have.
            new("modstoke.photoHere", c => c.OwnPhotos && c.Session != LoadingSession.Join && !c.Fresh),
            new("modstoke.photoFolder", c => c.PhotoKey),
            new("modstoke.mods", c => c.Mods, Keybind: KeybindIds.ToggleModsMenu, Label: "mods"),
            new("modstoke.menuSize", Any),
            new("modstoke.classic", Any, 0.3f),
            new("modstoke.language", Any),
            new("modstoke.loadingArt", Any),
            new("creative.materials", c => c.Creative, Control: "ToggleInventoryMode", Label: "materials", NeedsButton: true)
        };

        /// <summary>The ids shown so far, oldest first; kept in the loading screen's record between game runs.</summary>
        internal static List<string> History { get; } = new();

        private static Strings Text => FrontEnd.Text;

        /// <summary>
        /// The first tip and the three after it, for the device in use. A tip whose text needs a button the device has
        /// no binding for is left out; its header falls back to the category on a device without one.
        /// </summary>
        internal static List<PlannedTip> Plan(TipContext context)
        {
            var unseen = new List<(Tip Tip, float Weight)>();
            var seen = new List<Tip>();
            foreach (var tip in Catalog)
            {
                if (!tip.When(context) || !Text.Has("loading.tip." + tip.Id)) continue;
                if (tip.NeedsButton && !Resolve(tip, context.Pad).Bound) continue;
                if (History.Contains(tip.Id)) seen.Add(tip);
                else unseen.Add((tip, tip.Weight * (tip.Basic && context.Fresh ? BasicWeight : 1f)));
            }
            var chosen = new List<Tip>(PlannedCount);
            while (chosen.Count < PlannedCount && unseen.Count > 0)
            {
                var total = 0f;
                foreach (var entry in unseen) total += entry.Weight;
                var roll = Random.value * total;
                var index = 0;
                while (index < unseen.Count - 1 && (roll -= unseen[index].Weight) > 0) index++;
                chosen.Add(unseen[index].Tip);
                unseen.RemoveAt(index);
            }
            // Once every tip that applies has been seen, the ones seen longest ago come back.
            seen.Sort((a, b) => History.IndexOf(a.Id).CompareTo(History.IndexOf(b.Id)));
            for (var i = 0; chosen.Count < PlannedCount && i < seen.Count; i++) chosen.Add(seen[i]);

            var planned = new List<PlannedTip>(chosen.Count);
            // An options path in a tip reads exactly as the Options screen names it, in every language.
            var places = new (string, object)[]
            {
                ("options", Text["title.options"]), ("modstoke", Text["options.tab.modstoke"]),
                ("menus", Text["options.section.menus"]), ("language", Text["options.section.language"])
            };
            foreach (var tip in chosen)
            {
                var category = tip.Id.Substring(0, tip.Id.IndexOf('.'));
                var label = tip.Label != null && Text.Has("loading.control." + tip.Label) ? Text["loading.control." + tip.Label] : null;
                planned.Add(new PlannedTip
                {
                    Id = tip.Id,
                    Text = Text.Get("loading.tip." + tip.Id, places),
                    Category = Text["loading.category." + category],
                    Control = label,
                    Pad = label == null ? default : Resolve(tip, true),
                    Key = label == null ? default : Resolve(tip, false)
                });
            }
            return planned;
        }

        /// <summary>Looks for the game's icons for every button a tip can show, at the title, so a load never searches for one.</summary>
        internal static void FindGlyphs()
        {
            foreach (var tip in Catalog)
            {
                if (tip.Label == null) continue;
                ControlGlyphs.Find(Resolve(tip, true));
                ControlGlyphs.Find(Resolve(tip, false));
            }
        }

        /// <summary>A tip came on screen; it goes to the end of the history.</summary>
        internal static void Shown(string id)
        {
            History.Remove(id);
            History.Add(id);
            if (History.Count > HistoryLength) History.RemoveAt(0);
        }

        private static ControlKey Resolve(Tip tip, bool pad)
        {
            if (tip.Keybind != null) return pad ? default : ControlGlyphs.Keybind(tip.Keybind);
            if (tip.Control == null) return default;
            if (!pad) return ControlGlyphs.Game(tip.Control, BindingDevice.Keyboard);
            var key = ControlGlyphs.Game(tip.Control, BindingDevice.Pad);
            return key.Bound || tip.PadControl == null ? key : ControlGlyphs.Game(tip.PadControl, BindingDevice.Pad);
        }
    }
}
