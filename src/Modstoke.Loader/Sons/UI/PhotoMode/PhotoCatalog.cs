// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Modstoke.Localization;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// The rows of photo mode's panel, tab by tab. Sliders that follow the game's own look default to what the game
    /// showed when photo mode opened, so a row at its default changes nothing.
    /// </summary>
    internal static class PhotoCatalog
    {
        // Ten minutes a step: an hour for half a second's hold on a key or the stick.
        private const float HourStep = 1f / 6;

        internal static IReadOnlyList<string> TabIds { get; } = new[] { "camera", "lens", "look", "world", "frame", "capture" };

        internal static OptionTab Tab(string id, PhotoSession session)
        {
            var s = session.Settings;
            var look = session.Look;
            return id switch
            {
                "camera" => new OptionTab(id, new[]
                {
                    Section("camera",
                        new NumberRow("focal", () => s.Focal, value => s.Focal = value, PhotoLens.MinFocal, PhotoLens.MaxFocal, 1f / 48,
                            () => session.EntryFocal, value => Unit("mm", value, "0"), log: true),
                        new NumberRow("roll", () => s.Roll, value => s.Roll = value, -45, 45, 1, () => 0, value => Unit("deg", value, "+0;-0;0")),
                        new NumberRow("speed", () => s.Speed, value => s.Speed = value, 0.25f, 4, 0.25f, () => 1, Times)),
                    Section("subject",
                        Shown("player", () => s.ShowPlayer, value => s.ShowPlayer = value),
                        Shown("held", () => s.ShowHeld, value => s.ShowHeld = value)),
                    Section("position",
                        new OptionAction("return", session.ReturnToPlayer, () => Text["photo.value.return"]))
                }),
                "lens" => new OptionTab(id, new[]
                {
                    Section("focus",
                        new SwitchRow("dof", () => s.Dof, session.SetDepthOfField, false),
                        Only(Choice("focusMode", () => s.FollowCentre ? "follow" : "manual", value => s.FollowCentre = value == "follow", "manual",
                            ("manual", "photo.value.manual"), ("follow", "photo.value.followCentre")), () => s.Dof),
                        new NumberRow("focusDistance", () => float.IsNaN(s.FocusDistance) ? 10f : s.FocusDistance, value => s.FocusDistance = value,
                            0.1f, 300, 1f / 60, () => session.FocusDefault, Metres, log: true) { When = () => s.Dof && !s.FollowCentre },
                        new ChoiceRow("aperture", () => s.Aperture.ToString(CultureInfo.InvariantCulture), value =>
                            {
                                s.Aperture = int.Parse(value, CultureInfo.InvariantCulture);
                                return true;
                            }, Array.IndexOf(PhotoLens.Stops, 2.8f).ToString(CultureInfo.InvariantCulture),
                            PhotoLens.Stops.Select((stop, index) => (index.ToString(CultureInfo.InvariantCulture),
                                (Func<string>)(() => Unit("f", stop, "0.#")))).ToArray()) { When = () => s.Dof },
                        new NumberRow("bokeh", () => s.Bokeh, value => s.Bokeh = value, 1, 6, 0.5f, () => 2, Times) { When = () => s.Dof }),
                    Section("lens",
                        Kept("vignette", () => s.Vignette, value => s.Vignette = value, 0, 0.8f, () => look.BaseVignette),
                        Kept("chromatic", () => s.Chromatic, value => s.Chromatic = value, 0, 1, () => look.BaseChromatic),
                        new NumberRow("distortion", () => s.Distortion, value => s.Distortion = value, -0.5f, 0.5f, 0.05f, () => 0, SignedPercent),
                        Kept("bloom", () => s.Bloom, value => s.Bloom = value, 0, 1, () => look.BaseBloom),
                        new NumberRow("panini", () => s.Panini, value => s.Panini = value, 0, 1, 0.05f, () => 0, Percent))
                }),
                "look" => new OptionTab(id, new[]
                {
                    Section("filter",
                        new ChoiceRow("preset", () => s.Preset, value =>
                            {
                                s.Choose(PhotoPresets.Find(value));
                                return true;
                            }, "none", PhotoPresets.All.Select(preset => (preset.Id, (Func<string>)(() => Text["photo.preset." + preset.Id]))).ToArray()),
                        new NumberRow("strength", () => s.Strength, value => s.Strength = value, 0, 1, 0.05f, () => 1, Percent)
                            { When = () => s.Preset != "none" }),
                    Section("light",
                        new NumberRow("exposure", () => s.Exposure, value => s.Exposure = value, -3, 3, 0.1f, () => 0, value => Unit("ev", value, "+0.0;-0.0;0")),
                        Delta("contrast", () => s.Contrast, value => s.Contrast = value, -50, 50),
                        Delta("saturation", () => s.Saturation, value => s.Saturation = value, -100, 50),
                        Delta("temperature", () => s.Temperature, value => s.Temperature = value, -60, 60),
                        Delta("tint", () => s.Tint, value => s.Tint = value, -60, 60)),
                    Section("texture",
                        Kept("grain", () => s.Grain, value => s.Grain = value, 0, 1, () => look.BaseGrain),
                        Choice("grainSize", () => GrainSize(s.GrainType < 0 ? look.BaseGrainType : s.GrainType), value => s.GrainType = value switch
                            {
                                "fine" => 0,
                                "medium" => 4,
                                _ => 8
                            }, GrainSize(look.BaseGrainType), ("fine", "photo.value.fine"), ("medium", "photo.value.medium"),
                            ("coarse", "photo.value.coarse"))),
                    Section("effects",
                        Shown("screenEffects", () => s.ScreenEffects, value => s.ScreenEffects = value, false),
                        new OptionAction("resetLook", session.ResetLook, () => Text["photo.value.reset"]))
                }),
                "world" => World(session),
                "frame" => new OptionTab(id, new[]
                {
                    Section("guides",
                        Choice("guide", () => s.Guide, value => s.Guide = value, "none",
                            PhotoSettings.Guides.Select(guide => (guide, "photo.value." + guide)).ToArray()),
                        new ChoiceRow("aspect", () => s.Aspect, value =>
                            {
                                s.Aspect = value;
                                return true;
                            }, "screen", PhotoSettings.Aspects.Select(aspect => (aspect, (Func<string>)(() => aspect == "screen"
                                ? Text["photo.value.screen"]
                                : aspect.Replace(".", Languages.CultureOf(Languages.Current).NumberFormat.NumberDecimalSeparator)))).ToArray()),
                        Choice("mask", () => s.BlackMask ? "black" : "dimmed", value => s.BlackMask = value == "black", "dimmed",
                            ("dimmed", "photo.value.dimmed"), ("black", "photo.value.black")))
                }),
                _ => new OptionTab("capture", new[]
                {
                    Section("image",
                        Resolution(session),
                        Choice("detail", () => s.Fine ? "fine" : "standard", value => s.Fine = value == "fine", "fine",
                            ("standard", "photo.value.standard"), ("fine", "photo.value.detailed"))),
                    Section("save",
                        Choice("folder", () => s.Pictures ? "pictures" : "modstoke", value => s.Pictures = value == "pictures", "pictures",
                            ("pictures", "photo.value.pictures"), ("modstoke", "photo.value.modstoke")),
                        SteamRunning() ? new SwitchRow("steam", () => s.Steam, value => s.Steam = value, true) : null,
                        new OptionAction("openFolder", () => PhotoLibrary.OpenFolder(s.Pictures), () => Text["photo.value.open"]))
                })
            };
        }

        // Online, or where the game's clocks cannot be held, the tab is empty and says why instead. The rows start at
        // the world as photo mode found it; clouds and fog start at the chosen weather's own until moved.
        private static OptionTab World(PhotoSession session)
        {
            var world = session.World;
            if (world.Unavailable != null) return new OptionTab("world", Array.Empty<OptionSection>());
            var s = session.Settings;
            var look = session.Look;
            return new OptionTab("world", new[]
            {
                Section("time",
                    new NumberRow("time", () => float.IsNaN(s.Hour) ? world.EntryHour : s.Hour,
                        value => s.Hour = Mathf.Abs(value - world.EntryHour) < HourStep / 2 ? float.NaN : value,
                        0, 24, HourStep, () => world.EntryHour, Clock) { When = () => world.CanSetTime }),
                Section("weather",
                    Only(Choice("weather", () => s.Weather, s.ChooseWeather, "asis",
                        world.Weathers.Select(weather => (weather, "photo.weather." + weather)).ToArray()), () => world.CanSetWeather),
                    new NumberRow("clouds", () => float.IsNaN(s.Clouds) ? world.CloudsFor(s.Weather) : s.Clouds,
                        value => s.Clouds = Mathf.Abs(value - world.CloudsFor(s.Weather)) < 0.025f ? float.NaN : value,
                        0, 1, 0.05f, () => world.CloudsFor(s.Weather), Percent) { When = () => world.CanSetWeather },
                    new NumberRow("fog", () => PhotoWorld.FogAmount(s),
                        value => s.Fog = Mathf.Abs(value - PhotoWorld.FogFor(s.Weather)) < 0.025f ? float.NaN : value,
                        0, 1, 0.05f, () => PhotoWorld.FogFor(s.Weather), Percent) { When = () => look.FogShown },
                    new OptionAction("resetWorld", session.ResetWorld, () => Text["photo.value.reset"]))
            });
        }

        // The sizes this machine can take, as the capture offers them; 4K unless the graphics card is short of memory.
        // The row names the size only, as the pixels do not fit beside it; the line over Take photo gives them.
        private static OptionRow Resolution(PhotoSession session)
        {
            var sizes = session.Resolutions;
            if (sizes.Count == 0) return null;
            var s = session.Settings;
            var fallback = session.DefaultResolution;
            return new ChoiceRow("resolution", () => sizes.Any(size => size.Id == s.Resolution) ? s.Resolution : fallback, value =>
                {
                    s.Resolution = value;
                    return true;
                }, fallback, sizes.Select(size => (size.Id, (Func<string>)(() => SizeName(size.Id, Culture)))).ToArray());
        }

        private static string SizeName(string id, CultureInfo culture)
        {
            if (string.Equals(id, "screen", StringComparison.OrdinalIgnoreCase)) return Text["photo.value.screen"];
            // "2x" reads as a multiple; "4k" and "8k" as the sizes people know them by.
            return id.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? id.Substring(0, id.Length - 1) + "×" : id.ToUpper(culture);
        }

        private static bool SteamRunning()
        {
            try { return Steamworks.SteamAPI.IsSteamRunning(); }
            catch (Exception) { return false; }
        }

        private static string GrainSize(int type) => type <= 1 ? "fine" : type <= 7 ? "medium" : "coarse";

        private static Strings Text => FrontEnd.Text;

        private static OptionSection Section(string id, params OptionRow[] rows) => OptionsCatalog.Section(id, rows);

        // A row that can only change while another setting allows it.
        private static OptionRow Only(OptionRow row, Func<bool> when)
        {
            row.When = when;
            return row;
        }

        private static ChoiceRow Choice(string id, Func<string> read, Action<string> write, string @default, params (string Value, string Key)[] choices)
            => new(id, read, value =>
            {
                write(value);
                return true;
            }, @default, choices.Select(choice => (choice.Value, (Func<string>)(() => Text[choice.Key]))).ToArray());

        private static ChoiceRow Shown(string id, Func<bool> read, Action<bool> write, bool @default = true)
            => Choice(id, () => read() ? "shown" : "hidden", value => write(value == "shown"), @default ? "shown" : "hidden",
                ("shown", "photo.value.shown"), ("hidden", "photo.value.hidden"));

        // A lens value that keeps the game's own until moved; back at the game's value it lets the game's through again.
        private static NumberRow Kept(string id, Func<float> read, Action<float> write, float min, float max, Func<float> game)
            => new(id, () => float.IsNaN(read()) ? game() : read(), value => write(Mathf.Abs(value - game()) < 0.005f ? float.NaN : value),
                min, max, 0.01f, game, Percent);

        private static NumberRow Delta(string id, Func<float> read, Action<float> write, float min, float max)
            => new(id, read, write, min, max, 1, () => 0, value => (value > 0.5f ? "+" : "") + Mathf.RoundToInt(value).ToString(Culture));

        private static CultureInfo Culture => Languages.CultureOf(Languages.Current);

        private static string Unit(string unit, float value, string format)
            => Text.Get("photo.unit." + unit, ("value", value.ToString(format, Culture)));

        private static string Times(float value) => value.ToString("0.##", Culture) + "×";

        private static string Percent(float value) => Mathf.RoundToInt(value * 100f).ToString(Culture) + "%";

        private static string SignedPercent(float value)
        {
            var whole = Mathf.RoundToInt(value * 100f);
            return (whole > 0 ? "+" : "") + whole.ToString(Culture) + "%";
        }

        private static string Metres(float value)
            => Unit("m", value, value < 1 ? "0.00" : value < 10 ? "0.0" : "0");

        // The player's own clock style; the right end of the slider is midnight again.
        private static string Clock(float hours)
            => DateTime.Today.AddMinutes(Mathf.RoundToInt(hours * 60f) % (24 * 60)).ToString("t", Culture);
    }
}
