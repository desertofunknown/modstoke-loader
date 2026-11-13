// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Modstoke.Localization;
using Sons.Gui.Options;
using Sons.Settings;
using UnityEngine;

namespace Modstoke.Loader
{
    internal enum OptionFormat { Decimal, Percent, Integer, Fps, Signed }

    /// <summary>
    /// One setting on the Options screen. The game's own option objects say what each of its settings is (its
    /// key, choices, range and default); values are read and written through the game's settings store, whose
    /// handlers apply them as the game's own menu does.
    /// </summary>
    internal abstract class OptionRow
    {
        protected OptionRow(string id) => Id = id;

        internal string Id { get; }
        /// <summary>Whether the setting could be read on this install; rows that cannot are left out.</summary>
        internal bool Ready { get; private protected set; }
        internal abstract string Value { get; }
        internal abstract string DefaultValue { get; }
        internal abstract bool IsDefault { get; }
        /// <summary>For a slider, where the value sits between its ends; null for other rows.</summary>
        internal virtual float? Fraction => null;
        /// <summary>For a choice, how many there are and which is chosen, for the marks under it.</summary>
        internal virtual int Count => 0;
        internal virtual int Index => -1;
        /// <summary>Steps the value; a choice wraps around when <paramref name="wrap"/> is set. True when it changed.</summary>
        internal abstract bool Step(int direction, bool wrap);
        internal virtual bool SetFraction(float fraction) => false;
        internal abstract bool Reset();
        /// <summary>For a row that opens something instead of holding a value.</summary>
        internal virtual Action Open => null;

        /// <summary>The stored value as it is now, to put back with <see cref="Restore"/>; null where not supported.</summary>
        internal virtual string Snapshot() => null;

        internal virtual void Restore(string value) { }

        /// <summary>When the setting can be changed, for one that depends on another; null for always.</summary>
        internal Func<bool> When { get; set; }

        internal bool Available
        {
            get
            {
                try { return When == null || When(); }
                catch (Exception) { return false; }
            }
        }

        protected static Strings Text => FrontEnd.Text;
    }

    /// <summary>
    /// A setting with named choices, read and written through the accessors it is given. Without a default
    /// it never shows as changed and has nothing to reset to.
    /// </summary>
    internal sealed class ChoiceRow : OptionRow
    {
        private readonly Func<string> _read;
        private readonly Func<string, bool> _write;
        private readonly string _default;
        private readonly (string Value, Func<string> Name)[] _choices;

        internal ChoiceRow(string id, Func<string> read, Func<string, bool> write, string @default,
            (string Value, Func<string> Name)[] choices) : base(id)
        {
            _read = read;
            _write = write;
            _default = @default;
            _choices = choices;
            Ready = choices.Length > 0;
        }

        private string Current => _read() ?? _default ?? "";
        internal override int Count => _choices.Length;
        internal override int Index => Array.FindIndex(_choices, choice => string.Equals(choice.Value, Current, StringComparison.OrdinalIgnoreCase));
        internal override string Value => NameOf(Current);
        internal override string DefaultValue => _default == null ? "" : NameOf(_default);
        internal override bool IsDefault => _default == null || string.Equals(Current, _default, StringComparison.OrdinalIgnoreCase);

        internal override bool Step(int direction, bool wrap)
        {
            var index = Index;
            // From a stored value the list does not hold, right goes to the first choice and left to the last.
            var next = index < 0 ? (direction > 0 ? 0 : _choices.Length - 1) : index + direction;
            next = wrap ? (next % _choices.Length + _choices.Length) % _choices.Length : Mathf.Clamp(next, 0, _choices.Length - 1);
            return next != index && _write(_choices[next].Value);
        }

        internal override bool Reset() => !IsDefault && _write(_default);

        internal override string Snapshot() => Current;

        internal override void Restore(string value)
        {
            if (value != null) _write(value);
        }

        private string NameOf(string value)
        {
            foreach (var choice in _choices)
                if (string.Equals(choice.Value, value, StringComparison.OrdinalIgnoreCase)) return choice.Name();
            return OptionText.Describe(value);
        }
    }

    /// <summary>A setting that is on or off, read and written through the accessors it is given.</summary>
    internal sealed class SwitchRow : OptionRow
    {
        private readonly Func<bool> _read;
        private readonly Action<bool> _write;
        private readonly bool _default;

        internal SwitchRow(string id, Func<bool> read, Action<bool> write, bool @default) : base(id)
        {
            _read = read;
            _write = write;
            _default = @default;
            Ready = true;
        }

        internal override int Count => 2;
        internal override int Index => _read() ? 1 : 0;
        internal override string Value => Text[_read() ? "options.value.on" : "options.value.off"];
        internal override string DefaultValue => Text[_default ? "options.value.on" : "options.value.off"];
        internal override bool IsDefault => _read() == _default;

        internal override bool Step(int direction, bool wrap)
        {
            var current = _read();
            var next = wrap ? !current : direction > 0;
            if (next == current) return false;
            _write(next);
            return true;
        }

        internal override bool Reset()
        {
            if (IsDefault) return false;
            _write(_default);
            return true;
        }
    }

    /// <summary>A line of text the player types, such as a server's name.</summary>
    internal sealed class TextRow : OptionRow
    {
        private readonly Func<string> _read;
        private readonly Func<string, bool> _write;
        private readonly string _default;

        internal TextRow(string id, Func<string> read, Func<string, bool> write, string @default, int maxLength) : base(id)
        {
            _read = read;
            _write = write;
            _default = @default;
            MaxLength = maxLength;
            Ready = true;
        }

        internal int MaxLength { get; }
        /// <summary>Opens Steam's on-screen keyboard for a controller and returns the field it types into, or null.</summary>
        internal Func<string, TMPro.TMP_InputField> PadKeyboard { get; set; }
        internal override string Value => _read() ?? "";
        internal override string DefaultValue => _default ?? "";
        internal override bool IsDefault => _default == null || Value == _default;
        internal override bool Step(int direction, bool wrap) => false;
        internal override bool Reset() => !IsDefault && _write(_default);

        /// <summary>Stores what was typed; false when it was not accepted, as an empty name is not.</summary>
        internal bool Write(string text) => text != Value && _write(text);
    }

    /// <summary>A game setting with named choices, stored as text.</summary>
    internal sealed class GameChoice : OptionRow
    {
        private readonly CustomMenuOptionGui _gui;
        private readonly string _key, _default;
        private readonly List<string> _choices = new();
        private readonly Func<string, string> _describe;

        internal GameChoice(string id, Func<CustomMenuOptionGui> create, Func<string, string> describe = null) : base(id)
        {
            _describe = describe ?? OptionText.Describe;
            var gui = _gui = create();
            _key = gui.SettingKey;
            _default = gui.GetDefaultStringValue() ?? "";
            var choices = gui.GetValueOptions();
            if (choices != null)
                for (var i = 0; i < choices.Count; i++)
                    if (!string.IsNullOrEmpty(choices[i])) _choices.Add(choices[i]);
            Ready = !string.IsNullOrEmpty(_key) && _choices.Count > 0;
        }

        private string Current => GameSettingsManager.GetSetting(_key, _default) ?? _default;
        internal override int Count => _choices.Count;
        internal override string Value => _describe(Current);
        internal override string DefaultValue => _describe(_default);
        internal override bool IsDefault => string.Equals(Current, _default, StringComparison.OrdinalIgnoreCase);

        // The game's own matching first, which knows how its stored values relate to its choices.
        internal override int Index
        {
            get
            {
                var current = Current;
                var exact = _choices.FindIndex(choice => string.Equals(choice, current, StringComparison.OrdinalIgnoreCase));
                if (exact >= 0) return exact;
                try
                {
                    var index = _gui.GetValueIndex(current);
                    if (index >= 0 && index < _choices.Count) return index;
                }
                catch (Exception)
                {
                    // Then the value is simply not one of the choices.
                }
                return -1;
            }
        }

        internal override bool Step(int direction, bool wrap)
        {
            var index = Index;
            // From a stored value the list does not hold, right goes to the first choice and left to the last.
            var next = index < 0 ? (direction > 0 ? 0 : _choices.Count - 1) : index + direction;
            if (wrap) next = (next % _choices.Count + _choices.Count) % _choices.Count;
            next = Mathf.Clamp(next, 0, _choices.Count - 1);
            if (next == index) return false;
            GameSettingsManager.SetSetting(_key, _choices[next]);
            return true;
        }

        internal override bool Reset()
        {
            if (IsDefault) return false;
            GameSettingsManager.SetSetting(_key, _default);
            return true;
        }

        internal override string Snapshot() => Current;

        internal override void Restore(string value)
        {
            if (value != null) GameSettingsManager.SetSetting(_key, value);
        }
    }

    /// <summary>A game setting that is on or off.</summary>
    internal sealed class GameToggle : OptionRow
    {
        private readonly string _key;
        private readonly bool _default;

        internal GameToggle(string id, Func<ToggleMenuOptionGui> create) : base(id)
        {
            var gui = create();
            _key = gui.SettingKey;
            _default = gui.GetDefaultBoolValue();
            Ready = !string.IsNullOrEmpty(_key);
        }

        private bool Current => GameSettingsManager.GetSetting(_key, _default);
        internal override int Count => 2;
        internal override int Index => Current ? 1 : 0;
        internal override string Value => Text[Current ? "options.value.on" : "options.value.off"];
        internal override string DefaultValue => Text[_default ? "options.value.on" : "options.value.off"];
        internal override bool IsDefault => Current == _default;

        // Left is off and right is on; a press flips it.
        internal override bool Step(int direction, bool wrap)
        {
            var next = wrap ? !Current : direction > 0;
            if (next == Current) return false;
            GameSettingsManager.SetSetting(_key, next);
            return true;
        }

        internal override bool Reset()
        {
            if (IsDefault) return false;
            GameSettingsManager.SetSetting(_key, _default);
            return true;
        }
    }

    /// <summary>A game setting on a slider, stored as a number.</summary>
    internal sealed class GameSlider : OptionRow
    {
        private readonly string _key;
        private readonly float _min, _max, _default, _step;
        private readonly bool _whole;
        private readonly OptionFormat _format;

        internal GameSlider(string id, Func<FloatOptionGui> create, OptionFormat format, int steps = 40) : base(id)
        {
            var gui = create();
            _key = gui.SettingKey;
            _format = format;
            if (gui.TryCast<IntSliderOptionGuiBase>() is { } whole)
            {
                _whole = true;
                _min = whole.GetMinIntValue();
                _max = whole.GetMaxIntValue();
            }
            else
            {
                _min = gui.GetMinValue();
                _max = gui.GetMaxValue();
            }
            _default = gui.GetDefaultFloatValue();
            if (_max < _min) (_min, _max) = (_max, _min);
            _step = _whole ? 1f : Mathf.Max((_max - _min) / steps, 0.0001f);
            Ready = !string.IsNullOrEmpty(_key) && _max > _min;
        }

        private float Current => _whole
            ? GameSettingsManager.GetSetting(_key, Mathf.RoundToInt(_default))
            : GameSettingsManager.GetSetting(_key, _default);

        internal override float? Fraction => Mathf.InverseLerp(_min, _max, Current);
        internal override string Value => Format(Current);
        internal override string DefaultValue => Format(_default);
        internal override bool IsDefault => Mathf.Abs(Current - _default) < _step * 0.5f;

        internal override bool Step(int direction, bool wrap) => Set(Current + direction * _step);

        internal override bool SetFraction(float fraction) => Set(Mathf.Lerp(_min, _max, Mathf.Clamp01(fraction)));

        internal override bool Reset() => Set(_default);

        private bool Set(float value)
        {
            // Values land on the slider's steps, measured from its low end.
            value = Mathf.Clamp(_min + Mathf.Round((value - _min) / _step) * _step, _min, _max);
            if (Mathf.Abs(value - Current) < _step * 0.01f) return false;
            if (_whole) GameSettingsManager.SetSetting(_key, Mathf.RoundToInt(value));
            else GameSettingsManager.SetSetting(_key, value);
            return true;
        }

        private string Format(float value)
        {
            var culture = Languages.CultureOf(Languages.Current);
            return _format switch
            {
                OptionFormat.Percent => Mathf.RoundToInt(value * 100f).ToString(culture) + "%",
                OptionFormat.Integer => Mathf.RoundToInt(value).ToString(culture),
                // At the top of its range the frame rate has no cap at all.
                OptionFormat.Fps => value >= _max - 0.5f ? Text["options.value.max"] : Mathf.RoundToInt(value).ToString(culture),
                OptionFormat.Signed => (value > 0.5f ? "+" : "") + Mathf.RoundToInt(value).ToString(culture),
                _ => value.ToString("0.00", culture)
            };
        }
    }

    /// <summary>A row that opens something, such as the game's own bindings screen.</summary>
    internal sealed class OptionAction : OptionRow
    {
        private readonly Action _open;

        internal OptionAction(string id, Action open) : base(id)
        {
            _open = open;
            Ready = true;
        }

        internal override string Value => Text["options.open"];
        internal override string DefaultValue => "";
        internal override bool IsDefault => true;
        internal override bool Step(int direction, bool wrap) => false;
        internal override bool Reset() => false;
        internal override Action Open => _open;
    }

    internal sealed record OptionSection(string Id, IReadOnlyList<OptionRow> Rows);

    internal sealed record OptionTab(string Id, IReadOnlyList<OptionSection> Sections);

    /// <summary>What the Options screen shows: the game's settings in Modstoke's grouping, then Modstoke's own.</summary>
    internal static class OptionsCatalog
    {
        private static readonly HashSet<string> Reported = new();

        internal static IReadOnlyList<string> TabIds { get; } = new[] { "display", "graphics", "gameplay", "controls", "audio", "modstoke" };

        /// <summary>Builds one tab's rows. A setting this install cannot read is left out and logged once.</summary>
        internal static OptionTab Tab(string id) => id switch
        {
            "display" => new OptionTab(id, new[]
            {
                Section("screen",
                    Choice("resolution", () => new ResolutionOptionGui(), OptionText.Resolution),
                    Choice("windowMode", () => new FullscreenOptionGui()),
                    Choice("vsync", () => new VSyncOptionGui()),
                    Slider("maxFps", () => new TargetFrameRateOptionGui(), OptionFormat.Fps, 54),
                    new ChoiceRow("graphicsApi", () => GraphicsApi.Choice, GraphicsApi.Write, GraphicsApi.Default, new (string, Func<string>)[]
                    {
                        ("auto", () => FrontEnd.Text["options.value.gameDefault"]),
                        ("d3d11", () => "DirectX 11"),
                        ("d3d12", () => "DirectX 12")
                    })),
                Section("picture",
                    Slider("gamma", () => new GammaOptionGui(), OptionFormat.Decimal),
                    Slider("brightness", () => new BrightnessOptionGui(), OptionFormat.Decimal)),
                Section("upscaling",
                    Choice("dlss", () => new DlssOptionGui()),
                    Choice("fsr3", () => new Fsr3OptionGui()),
                    Choice("dynamicResolution", () => new DynamicResUpscaleOptionGui()),
                    Choice("resolutionTarget", () => new DynamicResolutionTargetOptionGui()))
            }),
            "graphics" => new OptionTab(id, new[]
            {
                Section("preset",
                    Choice("qualityPreset", () => new QualityPresetOptionGui())),
                Section("world",
                    Choice("drawDistance", () => new DrawDistanceOptionGui()),
                    Choice("textures", () => new TextureResolutionOptionGui()),
                    Choice("grass", () => new GrassQualityOptionGui()),
                    Choice("billboards", () => new BillboardQualityOptionGui()),
                    Choice("terrainDetail", () => new TerrainTessellationQualityOptionGui()),
                    Choice("terrainDepth", () => new TerrainParallaxDistanceOptionGui()),
                    Choice("terrainFiltering", () => new TerrainAnisoLevelOptionGui()),
                    Choice("filtering", () => new AnisotropicQualityOptionGui())),
                Section("lighting",
                    Choice("shadows", () => new ShadowQualityOptionGui()),
                    Choice("ambientOcclusion", () => new AmbientOcclusionQualityOptionGui()),
                    Toggle("contactShadows", () => new ContactShadowsOptionGui()),
                    Toggle("microShadows", () => new MicroShadowingOptionGui()),
                    Toggle("reflections", () => new ScreenSpaceReflectionOptionGui()),
                    Choice("fog", () => new FogQualityOptionGui()),
                    Choice("clouds", () => new CloudTypeOptionGui())),
                Section("water",
                    Choice("water", () => new WaterQualityOptionGui()),
                    Toggle("waterWaves", () => new WaterDisplacementOptionGui())),
                Section("effects",
                    Choice("antiAliasing", () => new AntiAliasOptionGui()),
                    Toggle("bloom", () => new BloomOptionGui()),
                    Toggle("motionBlur", () => new MotionBlurOptionGui()),
                    Toggle("chromaticAberration", () => new ChromaticAberrationOptionGui()),
                    Toggle("filmGrain", () => new FilmGrainOptionGui()),
                    Choice("colorGrade", () => new ColorGradeOptionGui()))
            }),
            "gameplay" => new OptionTab(id, new[]
            {
                Section("general",
                    Choice("gameLanguage", () => new LanguageOptionGui(), OptionText.Language),
                    Toggle("subtitles", () => new SubtitlesOptionGui()),
                    Slider("fov", () => new FovOffsetOptionGui(), OptionFormat.Signed)),
                Section("hud",
                    Toggle("hud", () => new GuiOptionGui()),
                    Slider("hudScale", () => new HudGuiScaleOptionGui(), OptionFormat.Percent, 20),
                    Toggle("vitals", () => new VitalsGuiOptionGui()),
                    Toggle("minimap", () => new MiniMapGuiOptionGui()),
                    Toggle("interaction", () => new WorldInteractionGuiOptionGui()),
                    Toggle("building", () => new BuildingGuiOptionGui()),
                    Toggle("tutorials", () => new TutorialsGuiOptionGui()),
                    Toggle("reticle", () => new ProjectileReticleOptionGui()),
                    Toggle("nameTags", () => new PlayerNameTagsOptionGui())),
                Section("worldRules",
                    Toggle("treeRegrowth", () => new TreeRegrowthOptionGui()),
                    Toggle("structureDamage", () => new StructureDamageOptionGui()),
                    Choice("skipBuildAnimations", () => new SkipConstructionAnimationsOptionGui()),
                    Toggle("bigHeads", () => new BigHeadModeOptionGui()))
            }),
            "controls" => new OptionTab(id, new[]
            {
                Section("mouse",
                    Slider("mouseX", () => new MouseXSensitivityOptionGui(), OptionFormat.Decimal),
                    Slider("mouseY", () => new MouseYSensitivityOptionGui(), OptionFormat.Decimal),
                    Toggle("invertY", () => new InvertYOptionGui()),
                    Slider("aimX", () => new RangedWeaponAimingXSensitivityOptionGui(), OptionFormat.Decimal),
                    Slider("aimY", () => new RangedWeaponAimingYSensitivityOptionGui(), OptionFormat.Decimal)),
                Section("controller",
                    Slider("padX", () => new GamePadXSensitivityOptionGui(), OptionFormat.Decimal),
                    Slider("padY", () => new GamePadYSensitivityOptionGui(), OptionFormat.Decimal),
                    Slider("deadzone", () => new GamePadDeadzoneOptionGui(), OptionFormat.Decimal),
                    Toggle("rumble", () => new RumbleOptionGui())),
                Section("movement",
                    Toggle("crouchToggle", () => new CrouchToggleOptionGui()),
                    Toggle("sprintToggle", () => new SprintToggleOptionGui()),
                    Slider("inventorySpeed", () => new InventorySpeedSensitivityOptionGui(), OptionFormat.Decimal)),
                Section("bindings",
                    new OptionAction("bindings", OpenGameOptions))
            }),
            "audio" => new OptionTab(id, new[]
            {
                Section("volume",
                    Slider("master", () => new MasterVolumeOptionGui(), OptionFormat.Percent, 20),
                    Slider("music", () => new MusicVolumeOptionGui(), OptionFormat.Percent, 20),
                    Slider("effects", () => new SfxVolumeOptionGui(), OptionFormat.Percent, 20),
                    Slider("voices", () => new VoiceVolumeOptionGui(), OptionFormat.Percent, 20)),
                Section("sound",
                    Choice("dynamicRange", () => new DynamicRangeProfileOptionGui()),
                    Choice("voiceCount", () => new VoiceCountOptionGui()),
                    Choice("proximityChat", () => new ProximityVoiceChatOptionGui()))
            }),
            _ => new OptionTab("modstoke", new[]
            {
                Section("menus",
                    Loader("menus", "classicMenus", "false", new (string, Func<string>)[]
                    {
                        ("false", () => FrontEnd.Text["options.value.modstokeMenus"]),
                        ("true", () => FrontEnd.Text["options.value.gameMenus"])
                    }, FrontEnd.SettingChanged),
                    Loader("menuSize", "menuScale", "1", new[] { 0.9f, 1f, 1.1f, 1.2f }
                        .Select(scale => (scale.ToString(CultureInfo.InvariantCulture),
                            (Func<string>)(() => scale.ToString("P0", Languages.CultureOf(Languages.Current)))))
                        .ToArray(), FrontEnd.ScaleChanged)),
                Section("language",
                    Loader("textLanguage", "language", "auto",
                        new (string, Func<string>)[] { ("auto", () => FrontEnd.Text["options.value.followGame"]) }
                            .Concat(Languages.Known.Select(language => (language.Code, (Func<string>)(() => language.NativeName))))
                            .ToArray(),
                        LocalizationStartup.PreferenceChanged))
            })
        };

        // A Loader setting, kept in the Loader's own settings file.
        private static OptionRow Loader(string id, string key, string @default, (string Value, Func<string> Name)[] choices, Action changed)
            => new ChoiceRow(id, () => RuntimePreferences.ReadText(key, @default), value =>
            {
                if (!RuntimePreferences.WriteText(key, value, out _)) return false;
                changed();
                return true;
            }, @default, choices);

        // Bindings stay on the game's own controls screen for now: over the title its options scene, in a game the
        // pause menu's own options page.
        private static void OpenGameOptions()
        {
            if (!FrontEnd.AtTitle)
            {
                PauseHost.Options();
                return;
            }
            GameMenus.Options();
            FrontEnd.StepAside();
        }

        internal static OptionSection Section(string id, params OptionRow[] rows)
            => new(id, rows.Where(row => row != null && row.Ready).ToArray());

        private static OptionRow Choice(string id, Func<CustomMenuOptionGui> create, Func<string, string> describe = null)
            => Make(id, () => new GameChoice(id, create, describe));

        private static OptionRow Toggle(string id, Func<ToggleMenuOptionGui> create) => Make(id, () => new GameToggle(id, create));

        private static OptionRow Slider(string id, Func<FloatOptionGui> create, OptionFormat format, int steps = 40)
            => Make(id, () => new GameSlider(id, create, format, steps));

        /// <summary>Creates a row, or null when this install cannot read the setting; that is logged once.</summary>
        internal static OptionRow Make(string id, Func<OptionRow> create)
        {
            try
            {
                var row = create();
                if (row.Ready) return row;
                Report(id, "it offers no values here");
            }
            catch (Exception error)
            {
                Report(id, error.Message);
            }
            return null;
        }

        private static void Report(string id, string why)
        {
            if (Reported.Add(id)) RuntimeEntryPoint.LogSource?.LogInfo("Options: \"" + id + "\" is not shown: " + why + ".");
        }
    }

    /// <summary>How the game's stored values read on screen, in the player's language.</summary>
    internal static class OptionText
    {
        private static readonly Regex Size = new(@"^\s*(\d+)\s*[xX×]\s*(\d+)(.*)$", RegexOptions.Compiled);
        private static readonly Regex Code = new(@"^[A-Za-z]{2,3}([-_][A-Za-z]{2,4})?$", RegexOptions.Compiled);

        /// <summary>A known word ("High", "Off", "Fullscreen") in the player's language; anything else as words.</summary>
        internal static string Describe(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "—";
            var compact = Regex.Replace(raw.ToLowerInvariant(), "[^a-z0-9]", "");
            var key = "options.value." + compact;
            if (compact.Length > 0 && FrontEnd.Text.Has(key)) return FrontEnd.Text[key];
            return Words(raw);
        }

        internal static string Resolution(string raw)
        {
            var match = Size.Match(raw ?? "");
            return match.Success ? match.Groups[1].Value + " × " + match.Groups[2].Value + match.Groups[3].Value : Describe(raw);
        }

        /// <summary>The game names its languages by code; each is shown in its own language.</summary>
        internal static string Language(string raw)
            => raw != null && Code.IsMatch(raw.Trim()) ? Languages.Describe(raw.Trim()).NativeName : Describe(raw);

        // "HardSurvival", "hard_survival" and "HARD SURVIVAL" all read "Hard survival".
        private static string Words(string text)
        {
            var words = new StringBuilder();
            var afterLower = false;
            foreach (var c in text.Trim())
            {
                if (c is '_' or ' ')
                {
                    if (words.Length > 0 && words[words.Length - 1] != ' ') words.Append(' ');
                }
                else
                {
                    if (char.IsUpper(c) && afterLower) words.Append(' ');
                    words.Append(words.Length == 0 ? char.ToUpper(c, CultureInfo.InvariantCulture) : char.ToLowerInvariant(c));
                }
                afterLower = char.IsLower(c);
            }
            return words.ToString();
        }
    }
}
