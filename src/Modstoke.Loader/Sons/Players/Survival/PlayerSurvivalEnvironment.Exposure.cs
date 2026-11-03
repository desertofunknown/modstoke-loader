// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Modstoke.Loader;
using TheForest.Utils;
using TheForest.World;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Captures only the actual local source's global weather view. The held stat receiver
    /// is fenced, while native shared weather and its other receivers continue normally.
    /// Ordered native writer observations are retained until the envelope acknowledges them.
    /// </summary>
    internal sealed partial class PlayerSurvivalEnvironmentExposure : IDisposable
    {
        private const int MaximumPending = 256;
        private static PlayerSurvivalEnvironmentExposure _sourceOwner;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerLifecycle _lifecycle;
        private readonly PlayerStats _stats;
        private readonly WeatherSystem _weather;
        private readonly GameObject _root;
        private readonly int _statsId, _weatherId, _rootId;
        private readonly Guid _captureId = Guid.NewGuid();
        private const int IdentityPruneSlack = 64;
        private readonly Dictionary<(nint Pointer, int Instance), (ulong Id, WeatherSystemWetnessOverride Item)> _identities = new();
        private readonly Queue<PlayerExposureSnapshot> _pending = new();
        private PlayerExposureSnapshot _latest;
        private ulong _nextIdentity, _acknowledged;
        private bool _registered;
        private byte[] _graphHistory;
        private bool _graphTransferred;

        internal PlayerExposureSnapshot Baseline { get; private set; }
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }

        private PlayerSurvivalEnvironmentExposure(PlayerLifecycle lifecycle,
            PlayerStats stats, WeatherSystem weather)
        {
            _lifecycle = lifecycle; _stats = stats; _weather = weather; _root = lifecycle.Root;
            _statsId = stats.GetInstanceID(); _weatherId = weather.GetInstanceID(); _rootId = _root.GetInstanceID();
        }

        internal static bool TryCaptureSource(PlayerLifecycle lifecycle,
            PlayerSurvivalSnapshot survival, HarmonySupportSnapshot support,
            out PlayerSurvivalEnvironmentExposure owner, out string detail)
        {
            owner = null;
            detail = "Exposure requires the exact paused local source and its current survival capture.";
            if (lifecycle == null || survival == null || !lifecycle.IsPaused || Time.inFixedTimeStep
                || !PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || survival.SourceNetworkId != lifecycle.OriginalNetworkId || _sourceOwner != null) return false;
            PlayerSurvivalEnvironmentExposure candidate = null;
            try
            {
                if (!Same(lifecycle.OriginalEntity, LocalPlayer.Entity) || !Same(lifecycle.Root, LocalPlayer.GameObject))
                    return false;
                var stats = LocalPlayer.Stats;
                var weather = WeatherSystem.Instance;
                if (!Alive(stats) || !Alive(weather) || !weather.isActiveAndEnabled
                    || !Same(stats.gameObject, lifecycle.Root)
                    || stats._externalWetnessLevel != survival.ReadScalar(PlayerSurvivalScalar.ExternalWetness))
                    return false;
                if (!EnsureExposureHooks(support, out detail)) return false;
                candidate = new(lifecycle, stats, weather);
                candidate.RequireSource();
                int frame = Time.frameCount;
                candidate.CaptureGraph(out candidate._graphHistory);
                candidate.Baseline = candidate.Capture(PlayerExposureWriter.Capture, 0);
                candidate._latest = candidate.Baseline;
                if (Time.frameCount != frame || !lifecycle.IsPaused || !Same(lifecycle.OriginalEntity, LocalPlayer.Entity))
                    return false;
                if (!survival.TryBindExposureBaseline(candidate.Baseline, out detail)) return false;
                // Registration follows the capture in the same non-yielding game-thread call.
                // The shared weather stack is never disabled, cleared or replaced.
                _sourceOwner = candidate;
                candidate._registered = true;
                owner = candidate;
                detail = "Actual source override identities/order and retained snow captured; exact wetness receiver fenced.";
                return true;
            }
            catch (Exception error)
            { candidate?.Dispose(); detail = "Source exposure capture refused: " + error.Message; return false; }
        }

        internal bool TryPeekObservation(out PlayerExposureSnapshot observation, out string detail)
        {
            observation = null;
            detail = FailureReason ?? "No unacknowledged source exposure writer observation.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                RequireSource();
                if (_pending.Count == 0) return false;
                observation = _pending.Peek();
                detail = "Next ordered source observation retained until exact acknowledgement.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        internal bool TryAcknowledge(Guid capture, ulong sequence)
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || capture != _captureId) return false;
            if (sequence == _acknowledged) return true;
            if (_pending.Count == 0 || _pending.Peek().Sequence != sequence) return false;
            _pending.Dequeue(); _acknowledged = sequence;
            return true;
        }

        private void Observe(PlayerExposureWriter writer)
        {
            if (IsDisposed || FailureReason != null || _graphTransferred) return;
            try
            {
                RequireSource();
                // Shared smoothing runs every frame. It is not an override-history event;
                // servers consume their own raw wetness rather than streaming every sample.
                if ((writer is PlayerExposureWriter.Wetness or PlayerExposureWriter.RainToSnowChecked)
                    && StackMatchesLatest()) return;
                if (_pending.Count == MaximumPending)
                    throw new InvalidOperationException("Source exposure history overflowed before acknowledgement; no events were discarded.");
                var next = Capture(writer, checked(_latest.Sequence + 1));
                _pending.Enqueue(next); _latest = next;
            }
            catch (Exception error) { Fail(error.Message); }
        }

        private PlayerExposureSnapshot Capture(PlayerExposureWriter writer, ulong sequence)
        {
            var list = _weather._wetnessOverrides;
            if (list == null || list.WasCollected || list.Count > PlayerExposureSnapshot.MaximumOverrides)
                throw new InvalidOperationException("Native source override history is unavailable or exceeds the capture bound.");
            var items = new List<PlayerExposureOverride>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                // Native GetCurrentOverride ignores destroyed tails. Interior tombstones
                // have no effect on the ordering of live entries and are not identities.
                if (!Alive(item)) continue;
                var key = (item.Pointer, item.GetInstanceID());
                if (!_identities.TryGetValue(key, out var known))
                    _identities.Add(key, known = (checked(++_nextIdentity), item));
                items.Add(Describe(item, known.Id));
            }
            if (_identities.Count > items.Count + IdentityPruneSlack) PruneIdentities();
            return new(_captureId, _lifecycle.OriginalNetworkId, sequence, Time.frameCount, writer,
                _weather._wetnessCurrent, ReadRainIntensity(_weather), _stats._externalWetnessLevel,
                (int)_weather._cloudState == 3, _weather._isSnowing, items.ToArray(), _graphHistory);
        }

        // Only destroyed overrides are dropped: instance ids are never reused, so they cannot
        // return. Live overrides that leave the stack keep their ids for when they come back.
        private void PruneIdentities()
        {
            List<(nint Pointer, int Instance)> dead = null;
            foreach (var pair in _identities)
                if (!Alive(pair.Value.Item)) (dead ??= new()).Add(pair.Key);
            if (dead != null)
                foreach (var key in dead) _identities.Remove(key);
        }

        private void CaptureGraph(out byte[] bytes)
        {
            var character = LocalPlayer.FpCharacter;
            if (!Alive(character) || !character.transform.IsChildOf(_root.transform)
                || !Alive(character._primaryCollider)) throw new InvalidOperationException("Source exposure collider is unavailable.");
            var order = new List<WeatherSystemWetnessOverride>();
            foreach (var item in _weather._wetnessOverrides) if (Alive(item)) order.Add(item);
            PlayerExposureGraph.Create(character._primaryCollider, order, null, out bytes);
        }

        internal bool TryValidateHeldBaseline(out string detail)
        {
            detail = FailureReason ?? "The exact paused exposure capture must still own its native history.";
            if (IsDisposed || FailureReason != null || !_lifecycle.IsPaused || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                RequireSource(); CaptureGraph(out var current);
                if (!current.AsSpan().SequenceEqual(_graphHistory) || !StackMatchesLatest() || _latest.Sequence != 0)
                    throw new InvalidOperationException("Native exposure history changed after the settled capture; do not activate its old baseline.");
                detail = "Source exposure graph and ordered baseline remain unchanged under the hold.";
                return true;
            }
            catch (Exception error) { Fail(error.Message); detail = FailureReason; return false; }
        }

        // Call on the authenticated server preparation receipt, before releasing the
        // source hold. The receiver fence survives; obsolete source graph streaming does not.
        internal bool TryCompleteSourceHandoff(Guid capture, out string detail)
        {
            detail = "Exposure handoff requires the same captured graph and unchanged held source.";
            if (capture != _captureId || IsDisposed || FailureReason != null
                || Environment.CurrentManagedThreadId != _thread) return false;
            if (_graphTransferred) return true;
            if (!TryValidateHeldBaseline(out detail)) return false;
            _graphTransferred = true;
            detail = "Source graph observation retired; exact source wetness receiver remains fenced until coordinated disposal.";
            return true;
        }

        private bool StackMatchesLatest()
        {
            var list = _weather._wetnessOverrides;
            if (list == null || list.WasCollected || list.Count > PlayerExposureSnapshot.MaximumOverrides)
                throw new InvalidOperationException("Native source override list is unavailable.");
            int live = 0;
            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (!Alive(item)) continue;
                if (live == _latest.OverrideCount || !_identities.TryGetValue((item.Pointer, item.GetInstanceID()), out var known))
                    return false;
                var old = _latest.OverrideAt(live++);
                if (old.Identity != known.Id || old.Wetness != item._wetnessOverride || old.Rain != item._rainOverride) return false;
            }
            return live == _latest.OverrideCount && _latest.Snowing == _weather._isSnowing
                && _latest.Raining == ((int)_weather._cloudState == 3);
        }

        internal static PlayerExposureOverride Describe(WeatherSystemWetnessOverride value, ulong identity)
        {
            var scene = value.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || string.IsNullOrEmpty(scene.path))
                throw new InvalidOperationException("Override has no loaded scene identity.");
            var names = new Stack<string>();
            for (var current = value.transform; Alive(current); current = current.parent)
            {
                if (names.Count == 128) throw new InvalidOperationException("Override hierarchy exceeds its bound.");
                names.Push(current.name);
            }
            var path = new StringBuilder();
            foreach (string name in names)
                path.Append(name.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(name);
            var components = value.gameObject.GetComponents<WeatherSystemWetnessOverride>();
            int component = -1;
            for (int i = 0; i < components.Length; i++) if (Same(components[i], value)) { component = i; break; }
            if (component < 0) throw new InvalidOperationException("Override component identity was lost.");
            return new(identity, scene.path, path.ToString(), component, value._wetnessOverride, value._rainOverride);
        }

        internal static float ReadRainIntensity(WeatherSystem weather)
        {
            var intensities = weather._rainIntensities;
            int index = (int)weather._currentRainType;
            if (intensities == null || intensities.WasCollected || index < 0 || index >= intensities.Length
                || !float.IsFinite(intensities[index])) throw new InvalidOperationException("Native rain intensity is unavailable.");
            return intensities[index];
        }

        private void RequireSource()
        {
            if (IsDisposed || Environment.CurrentManagedThreadId != _thread || !GameBuild.IsRecognized
                || !Same(_stats, LocalPlayer.Stats) || _stats.GetInstanceID() != _statsId
                || !Same(_root, LocalPlayer.GameObject) || _root.GetInstanceID() != _rootId
                || !Same(_stats.gameObject, _root) || !Same(_lifecycle.Root, _root)
                || !Same(_weather, WeatherSystem.Instance) || _weather.GetInstanceID() != _weatherId
                || !_weather.isActiveAndEnabled || (_registered && !ReferenceEquals(_sourceOwner, this)))
                throw new InvalidOperationException("The exact source exposure binding changed.");
        }

        private void Fail(string detail) => FailureReason ??= detail;
        internal static bool Alive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        internal static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Retire source exposure on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            if (_registered && ReferenceEquals(_sourceOwner, this)) _sourceOwner = null;
            _registered = false;
            _pending.Clear();
        }
    }
}
