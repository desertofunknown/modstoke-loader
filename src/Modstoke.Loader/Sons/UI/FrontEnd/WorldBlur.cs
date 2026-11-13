// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Softens the live world behind a full-screen menu: a depth-of-field override on the title scene's camera
    /// that fades in when such a menu opens and out when it closes, so the scene stays alive but out of focus.
    /// </summary>
    internal static class WorldBlur
    {
        private const float FadeSeconds = 0.3f;
        // HDRP's quality level that means "use the values set on this override".
        private const int CustomQuality = 3;
        private static GameObject _root;
        private static Volume _volume;
        private static float _weight, _goal;
        private static bool _failed;

        /// <summary>Whether a menu wants the world behind it blurred this frame.</summary>
        internal static void Want(bool blurred) => _goal = blurred ? 1f : 0f;

        internal static void Tick()
        {
            if (_failed) return;
            if (_goal <= 0 && _weight <= 0) return;
            try
            {
                if (_volume == null || !_volume) Create();
                _weight = Mathf.MoveTowards(_weight, _goal, Time.unscaledDeltaTime / FadeSeconds);
                _volume.weight = Moving.EaseOut(_weight);
                // A volume at no weight still costs a little; it is switched off until wanted again.
                var on = _weight > 0.001f;
                if (_volume.enabled != on) _volume.enabled = on;
            }
            catch (Exception error)
            {
                _failed = true;
                RuntimeEntryPoint.LogSource?.LogWarning("The menus will not blur the world behind them: " + error.Message);
            }
        }

        private static void Create()
        {
            _root = new GameObject("Modstoke Menu Blur");
            Object.DontDestroyOnLoad(_root);
            // On the layer the game's own volumes use, which its cameras are set to see.
            foreach (var other in Object.FindObjectsOfType<Volume>())
            {
                if (other == null || !other || other.gameObject == _root) continue;
                _root.layer = other.gameObject.layer;
                break;
            }
            _volume = _root.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 1000f;
            _volume.weight = 0f;
            var profile = ScriptableObject.CreateInstance(Il2CppType.Of<VolumeProfile>()).Cast<VolumeProfile>();
            profile.hideFlags = HideFlags.HideAndDontSave;
            var focus = profile.Add(Il2CppType.Of<DepthOfField>(), true).Cast<DepthOfField>();
            focus.focusMode.value = DepthOfFieldMode.Manual;
            // Nothing near the camera blurs; everything past a couple of metres is fully soft.
            focus.nearFocusStart.value = 0f;
            focus.nearFocusEnd.value = 0f;
            focus.farFocusStart.value = 0.2f;
            focus.farFocusEnd.value = 2.5f;
            focus.quality.value = CustomQuality;
            focus.farSampleCount = 9;
            focus.farMaxBlur = 12f;
            focus.highQualityFiltering = true;
            _volume.sharedProfile = profile;
        }
    }
}
