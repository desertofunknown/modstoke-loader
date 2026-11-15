// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using Modstoke.Sons;
using Sons.Atmosphere;
using Sons.Environment;
using TheForest.World;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// The World tab: time of day and weather for the photo, in single player only, where the pause holds the world
    /// still. The time goes on the game's own visual override, which keeps the real day, so the sun, sky, lights and
    /// ambience follow it while the real clock, and with it survival, saves and the season, never moves. Weather is
    /// drawn rather than simulated: cloud cover, wet ground, a frozen copy of the game's rain or snow, and fog (on the
    /// look's volume); the game's weather never changes state, and where the game keeps rain and snow out, so does the
    /// photo. While anything here is changed, the game's weather update, ambient temperature, AI clock, power grids and
    /// time triggers are held, as they would act on the photo's hour or clouds; what the game switches by the hour,
    /// such as solar power and night-only objects, stays as it is. Each part records its undo the first time it
    /// changes, and goes back exactly as it was when photo mode closes.
    /// </summary>
    internal sealed class PhotoWorld
    {
        private const int MinutesInDay = 24 * 60, SettleFrames = 8;
        private const float RainSeconds = 4f, SnowSeconds = 10f, SimulateStep = 0.05f;

        private readonly PhotoUndo _undo;
        private readonly Transform _eye;
        private readonly TimeOfDayHolder _holder;
        private readonly WeatherSystem _weather;
        private readonly CloudManager _clouds;
        private readonly float _lightClouds, _mediumClouds, _heavyClouds;
        private readonly List<Renderer> _hiddenRenderers = new();
        private readonly List<Behaviour> _hiddenFog = new();
        private Action _putTimeBack, _putCloudsBack, _putWetnessBack;
        private GameObject _copyRoot;
        private float _hour = float.NaN, _cover = float.NaN, _wetness = float.NaN, _fog;
        private string _fall = "asis";
        private bool _held, _fallsSaved, _fallsHidden;
        private int _changedFrame = -SettleFrames;

        internal PhotoWorld(PhotoUndo undo, Transform eye, bool online)
        {
            _undo = undo;
            _eye = eye;
            if (online)
            {
                Unavailable = "photo.world.online";
                return;
            }
            if (!PhotoPatches.WorldHooks)
            {
                Unavailable = "photo.world.version";
                return;
            }
            var forcedTime = false;
            if (TimeOfDayHolder.TryGetInstance(out var holder) && Live(holder) && EnvironmentReads.TryReadClock(out var clock, out _))
            {
                _holder = holder;
                EntryHour = (float)(clock.EffectiveTime.TotalHours % 24.0);
                // A cave or a cutscene holds a time of its own, and would write it over the photo's every frame.
                forcedTime = clock.IsOverrideActive;
                CanSetTime = !forcedTime;
            }
            var weather = WeatherSystem.Instance;
            if (Live(weather) && Live(weather._cloudManager))
            {
                _weather = weather;
                _clouds = weather._cloudManager;
                EntryClouds = _clouds._currentCloudStormFactor;
                _lightClouds = weather._lightRainCloudStormFactor;
                _mediumClouds = weather._mediumRainCloudStormFactor;
                _heavyClouds = weather._heavyRainCloudStormFactor;
                CanSetWeather = true;
                // The rain and snow follow the player, and the game keeps them out of caves, rain-blocked places and
                // dry zones by switching off the filter they fall under; there, as where it holds the time, the photo
                // leaves the rain, snow and wet ground as the game has them.
                var types = WeatherSystem.GetRainTypes();
                Sheltered = forcedTime || (Live(types) && Live(types.CaveFilter) && !types.CaveFilter.activeSelf);
                if (Sheltered) Weathers = Array.FindAll(PhotoSettings.Weathers, choice => FallFor(choice) is "asis" or "none");
            }
        }

        /// <summary>True while the game's weather update, ambient temperature, AI clock, power grids and time triggers are held for photo mode.</summary>
        internal static bool Held { get; private set; }

        /// <summary>The text key that says why the tab is off, or null when it can be used.</summary>
        internal string Unavailable { get; }

        internal bool CanSetTime { get; }
        internal bool CanSetWeather { get; }

        /// <summary>Whether the game keeps rain and snow out where the player is, so the photo cannot add them.</summary>
        internal bool Sheltered { get; }

        /// <summary>The weathers the Weather row offers here: all of them, or where sheltered none that bring rain or snow.</summary>
        internal string[] Weathers { get; } = PhotoSettings.Weathers;

        /// <summary>The hour of the day photo mode opened on, the Time of day row's default.</summary>
        internal float EntryHour { get; }

        /// <summary>The cloud cover photo mode opened on.</summary>
        internal float EntryClouds { get; }

        /// <summary>Whether the scene has had time to catch up with the last change, for a photo to wait on.</summary>
        internal bool Settled => Time.frameCount - _changedFrame >= SettleFrames;

        /// <summary>The cloud cover a weather choice starts from; as it is, the cover photo mode opened on.</summary>
        internal float CloudsFor(string weather) => weather switch
        {
            "clear" => 0f,
            "overcast" or "fog" => _lightClouds,
            "rain" or "snow" => _mediumClouds,
            "storm" => _heavyClouds,
            _ => EntryClouds
        };

        /// <summary>How much fog a weather choice adds, from none to a thick fog.</summary>
        internal static float FogFor(string weather) => weather switch
        {
            "rain" => 0.25f,
            "storm" => 0.4f,
            "snow" => 0.3f,
            "fog" => 0.75f,
            _ => 0f
        };

        /// <summary>The fog the look draws: the Fog row where it was moved, otherwise the weather's own.</summary>
        internal static float FogAmount(PhotoSettings settings) => float.IsNaN(settings.Fog) ? FogFor(settings.Weather) : settings.Fog;

        private static float WetnessFor(string weather) => weather switch
        {
            "clear" => 0f,
            "rain" or "storm" => 1f,
            _ => float.NaN
        };

        // "asis" leaves the game's rain and snow alone; "none" hides it; the others show a copy of the game's own.
        private static string FallFor(string weather) => weather switch
        {
            "asis" => "asis",
            "rain" or "storm" or "snow" => weather,
            _ => "none"
        };

        /// <summary>Puts the settings on the world where they changed; true when the scene changed and its history should start again.</summary>
        internal bool Apply(PhotoSettings settings)
        {
            if (Unavailable != null) return false;
            var changed = false;
            var hour = CanSetTime ? settings.Hour : float.NaN;
            if (!Same(hour, _hour))
            {
                SetHour(hour);
                changed = true;
            }
            if (CanSetWeather)
            {
                var weather = settings.Weather;
                var cover = !float.IsNaN(settings.Clouds) ? settings.Clouds : weather == "asis" ? float.NaN : CloudsFor(weather);
                if (!Same(cover, _cover))
                {
                    SetClouds(cover);
                    changed = true;
                }
                var wetness = Sheltered ? float.NaN : WetnessFor(weather);
                if (!Same(wetness, _wetness))
                {
                    SetWetness(wetness);
                    changed = true;
                }
                var fall = Sheltered ? "asis" : FallFor(weather);
                if (fall != _fall)
                {
                    SetFall(fall);
                    changed = true;
                }
            }
            // The look draws the fog; it is followed here so that a photo waits for the volumetric fog to settle too.
            var fog = FogAmount(settings);
            if (!Same(fog, _fog))
            {
                _fog = fog;
                changed = true;
            }
            if (changed) _changedFrame = Time.frameCount;
            return changed;
        }

        /// <summary>What is applied, for the console's status.</summary>
        internal string Describe()
        {
            if (Unavailable != null) return "off (" + FrontEnd.Text[Unavailable] + ")";
            var culture = CultureInfo.InvariantCulture;
            return "time " + (float.IsNaN(_hour) ? "as it is" : Clock(_hour))
                + ", clouds " + (float.IsNaN(_cover) ? "as they are" : _cover.ToString("0.##", culture))
                + ", wet ground " + (float.IsNaN(_wetness) ? "as it is" : _wetness.ToString("0.##", culture))
                + ", rain and snow " + (_fall == "asis" ? "as they are" : _fall) + (Sheltered ? " (kept out here by the game)" : "")
                + ", fog " + _fog.ToString("0.##", culture)
                + (Held ? "; the game's weather and clocks are held" : "");
        }

        internal static string Clock(float hours)
        {
            var minutes = Mathf.RoundToInt(hours * 60f) % MinutesInDay;
            return (minutes / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (minutes % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static bool Same(float a, float b) => float.IsNaN(a) ? float.IsNaN(b) : !float.IsNaN(b) && Mathf.Abs(a - b) < 0.0001f;

        // Registered before anything else here, so its undo runs last: the game's updates come back to a world that is
        // already as it was.
        private void Hold()
        {
            if (_held) return;
            _held = true;
            Held = true;
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode holds the world at " + Reading() + ".");
            _undo.Add("the game's weather and clock updates", () =>
            {
                Held = false;
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode gave the world back at " + Reading() + ".");
            });
        }

        // The real clock is never written, so no time passes for survival, cooking or growth, and the next frame hands
        // nothing on as elapsed. The override takes its day from the real clock, so the slider covers this day only.
        private void SetHour(float hour)
        {
            Hold();
            if (_putTimeBack == null)
            {
                var holder = _holder;
                var saved = holder._overrideTimeOfDay;
                var wasOverride = holder._usingOverrideTime;
                _putTimeBack = () =>
                {
                    if (!Live(holder)) return;
                    holder._overrideTimeOfDay = saved;
                    holder._usingOverrideTime = wasOverride;
                    holder.UpdateConnections(true);
                };
                _undo.Add("the time of day", _putTimeBack);
            }
            _hour = hour;
            if (float.IsNaN(hour))
            {
                _putTimeBack();
                return;
            }
            var minutes = Mathf.RoundToInt(hour * 60f) % MinutesInDay;
            TimeOfDayHolder.UseOverrideTime(new TimeOfDay(0, minutes / 60, minutes % 60));
            // Every receiver at once rather than the usual one sliced receiver a frame; the sun, sky, probes and
            // ambience follow from it.
            _holder.UpdateConnections(true);
        }

        // Locked, the clouds' own smoothing has nothing left to move towards; every cloud receiver redraws at once.
        private void SetClouds(float cover)
        {
            Hold();
            if (_putCloudsBack == null)
            {
                var clouds = _clouds;
                var current = clouds._currentCloudStormFactor;
                var velocity = clouds._cloudStormFactorVelocity;
                var locked = clouds._lockedCloudStormFactor;
                _putCloudsBack = () =>
                {
                    if (!Live(clouds)) return;
                    clouds._currentCloudStormFactor = current;
                    clouds._cloudStormFactorVelocity = velocity;
                    clouds._lockedCloudStormFactor = locked;
                    clouds.UpdateReceivers();
                };
                _undo.Add("the clouds", _putCloudsBack);
            }
            _cover = cover;
            if (float.IsNaN(cover)) _putCloudsBack();
            else _clouds.LockStormFactor(Mathf.Clamp01(cover));
        }

        // The wetness receivers only store the value; the player's own wetness and a fire's drain build from it over
        // time, and no time passes while photo mode is open.
        private void SetWetness(float wetness)
        {
            Hold();
            if (_putWetnessBack == null)
            {
                var weather = _weather;
                var target = weather._wetnessTarget;
                var current = weather._wetnessCurrent;
                var velocity = weather._wetnessVelocity;
                _putWetnessBack = () =>
                {
                    if (!Live(weather)) return;
                    weather._wetnessTarget = target;
                    weather._wetnessCurrent = current;
                    weather._wetnessVelocity = velocity;
                    weather.UpdateEnvironmentWetness();
                };
                _undo.Add("the wet ground", _putWetnessBack);
            }
            _wetness = wetness;
            if (float.IsNaN(wetness))
            {
                _putWetnessBack();
                return;
            }
            _weather._wetnessTarget = wetness;
            _weather._wetnessCurrent = wetness;
            _weather._wetnessVelocity = 0f;
            _weather.UpdateEnvironmentWetness();
        }

        // The game's own rain is started and stopped over time, with sound, AI stimuli and a save behind it, so it is
        // left running and only hidden; the photo's rain or snow is a copy of the game's effect.
        private void SetFall(string fall)
        {
            Hold();
            if (!_fallsSaved)
            {
                _fallsSaved = true;
                _undo.Add("the rain and snow", () =>
                {
                    DropCopy();
                    ShowFalls();
                });
            }
            _fall = fall;
            DropCopy();
            if (fall == "asis")
            {
                ShowFalls();
                return;
            }
            var types = WeatherSystem.GetRainTypes();
            if (!Live(types))
            {
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode found no rain effects to show or hide.");
                return;
            }
            var source = fall switch
            {
                "rain" => types.RainMedium,
                "storm" => types.RainHeavy,
                "snow" => types.SnowMedium,
                _ => null
            };
            var hidden = HideFalls(types);
            if (Live(source)) Copy(types.transform, source, hidden, fall == "snow" ? SnowSeconds : RainSeconds);
        }

        // Every particle renderer and rain fog under the game's rain, the old effects and the winter's constant snow
        // included, switched off where it was on; the particles themselves keep their frozen place.
        private HashSet<IntPtr> HideFalls(RainTypes types)
        {
            if (!_fallsHidden)
            {
                _fallsHidden = true;
                foreach (var renderer in types.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    if (!Live(renderer) || !renderer.enabled) continue;
                    renderer.enabled = false;
                    _hiddenRenderers.Add(renderer);
                }
                foreach (var fog in types.GetComponentsInChildren<LocalVolumetricFog>(true))
                {
                    if (!Live(fog) || !fog.enabled) continue;
                    fog.enabled = false;
                    _hiddenFog.Add(fog);
                }
                RuntimeEntryPoint.LogSource?.LogInfo("Photo mode hid " + _hiddenRenderers.Count + " rain and snow renderer(s) and "
                    + _hiddenFog.Count + " rain fog volume(s).");
            }
            var hidden = new HashSet<IntPtr>();
            foreach (var renderer in _hiddenRenderers) hidden.Add(renderer.Pointer);
            return hidden;
        }

        // Put back only where it is still as photo mode left it.
        private void ShowFalls()
        {
            foreach (var renderer in _hiddenRenderers)
                if (Live(renderer) && !renderer.enabled) renderer.enabled = true;
            foreach (var fog in _hiddenFog)
                if (Live(fog) && !fog.enabled) fog.enabled = true;
            _hiddenRenderers.Clear();
            _hiddenFog.Clear();
            _fallsHidden = false;
        }

        // The copy is made under an inactive root and has every script, sound, light, fog volume and collider taken off
        // before it can wake, so nothing of the game's runs or registers twice. It stands where the game keeps its
        // rain, over the player, and is simulated forward in short steps (prewarm is not in this build) and left
        // paused: a frozen frame of falling rain.
        private void Copy(Transform rain, RainEffectController source, HashSet<IntPtr> hidden, float seconds)
        {
            _copyRoot = new GameObject("Modstoke Photo Weather");
            _copyRoot.SetActive(false);
            var root = _copyRoot.transform;
            root.SetPositionAndRotation(rain.position, rain.rotation);
            var copy = Object.Instantiate(source.gameObject, root, true);
            // Twice over, as a component another one requires can only go once that one has.
            for (var pass = 0; pass < 2; pass++)
                foreach (var behaviour in copy.GetComponentsInChildren<Behaviour>(true))
                    Object.DestroyImmediate(behaviour);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(collider);
            if (copy.GetComponentsInChildren<Behaviour>(true).Length > 0)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photo mode could not strip the game's " + source.name + " to copy it; the photo has no rain.");
                DropCopy();
                return;
            }
            // The game's renderers may be hidden by now; the copy draws the ones the game would.
            var originals = source.GetComponentsInChildren<ParticleSystemRenderer>(true);
            var copies = copy.GetComponentsInChildren<ParticleSystemRenderer>(true);
            for (var i = 0; i < copies.Length && i < originals.Length; i++)
                copies[i].enabled = originals[i].enabled || hidden.Contains(originals[i].Pointer);
            copy.SetActive(true);
            root.position = _eye.position;
            _copyRoot.SetActive(true);
            var particles = 0;
            foreach (var system in copy.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (!TopLevel(system, root)) continue;
                for (var time = 0f; time < seconds; time += SimulateStep) system.Simulate(SimulateStep, true, time == 0f, false);
            }
            foreach (var system in copy.GetComponentsInChildren<ParticleSystem>(true)) particles += system.particleCount;
            RuntimeEntryPoint.LogSource?.LogInfo("Photo mode drew " + particles + " particle(s) from the game's " + source.name + ".");
        }

        private static bool TopLevel(ParticleSystem system, Transform root)
        {
            for (var parent = system.transform.parent; parent != null && parent.Pointer != root.Pointer; parent = parent.parent)
                if (parent.GetComponent<ParticleSystem>() != null) return false;
            return true;
        }

        private void DropCopy()
        {
            if (Live(_copyRoot))
            {
                _copyRoot.SetActive(false);
                Object.Destroy(_copyRoot);
            }
            _copyRoot = null;
        }

        // The game's own values, for the log on the way in and out, so the two can be compared.
        private string Reading()
        {
            var culture = CultureInfo.InvariantCulture;
            var parts = new List<string>();
            if (Live(_holder))
                parts.Add("clock " + new TimeSpan(_holder._timeOfDay.Ticks).ToString(@"d\.hh\:mm\:ss", culture)
                    + (_holder._usingOverrideTime ? " shown as " + new TimeSpan(_holder._overrideTimeOfDay.Ticks).ToString(@"hh\:mm", culture) : ""));
            if (Live(_clouds))
                parts.Add(string.Format(culture, "clouds {0:0.###} (locked {1:0.###})", _clouds._currentCloudStormFactor, _clouds._lockedCloudStormFactor));
            if (Live(_weather))
                parts.Add(string.Format(culture, "wetness {0:0.###}, rain {1}, cloud state {2}, rain lasting {3:0.#} s",
                    _weather._wetnessCurrent, _weather._currentRainType, _weather._cloudState, _weather._activeRainDurationSeconds));
            return parts.Count > 0 ? string.Join(", ", parts) : "nothing it could read";
        }

        private static bool Live(Object value) => value is not null && !value.WasCollected && value;
    }
}
