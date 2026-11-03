// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Injection;
using global::Sons.Environment;
using global::Sons.Gameplay;
using global::Sons.Multiplayer;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalRuntime
    {
        private StatOverrideSet _sleepAwakeOverrides, _sleepAsleepOverrides;
        private StatOverrideSet _originalAwakeOverrides, _originalAsleepOverrides, _originalActiveOverrides;
        private PlayerSleepReceiver _sleepReceiver;
        private ISleepingReceiver _nativeSleepReceiver;
        private Il2CppSystem.Collections.Generic.List<ISleepingReceiver> _sleepReceivers;
        private SleepingManager _dedicatedSleepManager;
        private TimeOfDayHolder _dedicatedSleepClock;
        private Il2CppSystem.Action _dedicatedWake;
        private IntPtr _observedWakeCallbacks;
        private bool _dedicatedAwaitingWake;
        private readonly PlayerSurvivalRate[] _observedRates = new PlayerSurvivalRate[9];
        private readonly string[] _sleepStatNames = new string[9];
        private uint _observedFadeDelegates;
        private bool _sleepProfilesInstalled, _sleepRegistered, _sleepPrepared;
        private SleepRates _lastSleepRates;
        private PlayerSurvivalSleepState _sleepState, _appliedSleepState;

        /// <summary>Latest native transition; queued numeric frames can still precede this state.</summary>
        public PlayerSurvivalSleepState ObservedSleep => _sleepState;

        private sealed class SleepRates
        {
            internal readonly PlayerSurvivalRate[] Values;
            internal readonly uint FadeDelegates;
            internal readonly PlayerSurvivalSleepState State;

            internal SleepRates(ReadOnlySpan<PlayerSurvivalRate> values, uint delegates,
                PlayerSurvivalSleepState state)
            { Values = values.ToArray(); FadeDelegates = delegates; State = state; }
        }

        private void PrepareSleepProfiles()
        {
            EnsureSleepContinuationHook();
            _originalAwakeOverrides = _vitals._playerAwakeStatOverrides;
            _originalAsleepOverrides = _vitals._playerAsleepStatOverrides;
            _originalActiveOverrides = _manager._overrides;
            if (!_settings.AsleepOverrides.IsPresent)
                throw new InvalidOperationException("The approved actor has no native sleeping override set.");
            _sleepAwakeOverrides = _settings.AwakeOverrides.CreateActorCopy(_values);
            _sleepAsleepOverrides = _settings.AsleepOverrides.CreateActorCopy(_values);
            _observedFadeDelegates = _settings.FadeDelegateMask;
            for (int i = 0; i < _values.Length; i++)
            {
                _observedRates[i] = new(_values[i]._fadeRate, _values[i]._regenRate);
                _sleepStatNames[i] = _values[i].GetName();
                if (string.IsNullOrEmpty(_sleepStatNames[i]))
                    throw new InvalidOperationException("A registered survival stat name is unavailable.");
            }
            _sleepState = ReadSleepState(0, 0, _settings.IsAsleep);
            _appliedSleepState = _sleepState;
            _sleepPrepared = true;
        }

        private void ActivateSleepProfiles()
        {
            if (!_sleepPrepared || _sleepProfilesInstalled)
                throw new InvalidOperationException("The actor's sleep profiles are not prepared exactly once.");
            _sleepReceivers = SleepingManager.Receivers;
            if (_sleepReceivers == null || _sleepReceivers.WasCollected)
                throw new InvalidOperationException("The native sleeping receiver registry is unavailable.");
            int originalIndex = -1;
            for (int i = 0; i < _sleepReceivers.Count; i++)
            {
                var receiver = _sleepReceivers[i];
                if (receiver == null || receiver.WasCollected || receiver.Pointer != _stats.Pointer) continue;
                if (originalIndex != -1) throw new InvalidOperationException("The server actor has duplicate sleep registrations.");
                originalIndex = i;
            }
            if (!ClassInjector.IsTypeRegisteredInIl2Cpp(typeof(PlayerSleepReceiver)))
                ClassInjector.RegisterTypeInIl2Cpp<PlayerSleepReceiver>(new()
                { Interfaces = new[] { typeof(ISleepingReceiver) } });
            _sleepReceiver = _stats.gameObject.AddComponent<PlayerSleepReceiver>();
            if (!SleepAlive(_sleepReceiver)) throw new InvalidOperationException("The actor's sleep receiver could not be created.");
            _sleepReceiver.Bind(this);
            _nativeSleepReceiver = _sleepReceiver.TryCast<ISleepingReceiver>();
            if (_nativeSleepReceiver == null || _nativeSleepReceiver.WasCollected
                || _nativeSleepReceiver.Pointer != _sleepReceiver.Pointer)
                throw new InvalidOperationException("The native actor sleep interface is unavailable.");

            // Copy every mutable entry instead of changing a shared prefab ScriptableObject.
            _sleepProfilesInstalled = true;
            _vitals._playerAwakeStatOverrides = _sleepAwakeOverrides;
            _vitals._playerAsleepStatOverrides = _sleepAsleepOverrides;
            _manager._overrides = _sleepState.IsAsleep ? _sleepAsleepOverrides : _sleepAwakeOverrides;
            // Replace only this server actor's mixed LocalPlayer callback, preserving order.
            _sleepRegistered = true;
            if (originalIndex >= 0) _sleepReceivers[originalIndex] = _nativeSleepReceiver;
            else SleepingManager.Register(_nativeSleepReceiver);
            if (GameServerManager.IsDedicatedServer)
            {
                if (!SleepingManager.TryGetInstance(out _dedicatedSleepManager) || !SleepAlive(_dedicatedSleepManager)
                    || !TimeOfDayHolder.TryGetInstance(out _dedicatedSleepClock) || !SleepAlive(_dedicatedSleepClock))
                    throw new InvalidOperationException("The dedicated actor's actual native sleeping manager/clock is unavailable.");
                _dedicatedWake = (Il2CppSystem.Action)ReceiveDedicatedTimeJumpComplete;
                TimeOfDayHolder.OnTimeJumpComplete = TimeOfDayHolder.OnTimeJumpComplete == null ? _dedicatedWake
                    : Il2CppSystem.Delegate.Combine(TimeOfDayHolder.OnTimeJumpComplete, _dedicatedWake).Cast<Il2CppSystem.Action>();
                _dedicatedAwaitingWake = _sleepState.IsAsleep;
            }
            RequireSleepBinding();
        }

        /// <summary>
        /// Called only by the exact SnapshotSleepingTimes native prefix, after the native
        /// enemy-interruption check. Dedicated
        /// continuation has no local PlayerStats receiver; reuse this registered receiver
        /// and the native time-jump completion event, never a synthetic sleep clock.
        /// </summary>
        internal static void BeforeNativeSleepFadeComplete(SleepingManager manager)
        {
            if (!GameServerManager.IsDedicatedServer || !BoltNetwork.isServer || !SleepAlive(manager)) return;
            var receivers = SleepingManager.Receivers;
            if (receivers == null || receivers.WasCollected) return;
            for (int i = 0; i < receivers.Count; i++)
            {
                var receiver = receivers[i];
                if (receiver == null || receiver.WasCollected) continue;
                receiver.TryCast<PlayerSleepReceiver>()?.ReceiveDedicatedSleepFade(manager);
            }
        }

        internal void ReceiveDedicatedSleepFade(SleepingManager manager)
        {
            if (IsDisposed || FailureReason != null) return;
            try
            {
                RequireCurrent();
                if (!SleepAlive(manager) || _dedicatedSleepManager == null || manager.Pointer != _dedicatedSleepManager.Pointer)
                    throw new InvalidOperationException("The dedicated sleep continuation belongs to another native manager.");
                // DoSleepInternal may already have delivered the original receiver entry.
                if (!_sleepState.IsAsleep) ReceiveNativeSleep(_nativeSleepReceiver.Pointer, true);
            }
            catch (Exception error) { Fault("Dedicated sleep continuation failed: " + error.Message, out _); }
        }

        private void ReceiveDedicatedTimeJumpComplete()
        {
            if (IsDisposed || FailureReason != null || !_dedicatedAwaitingWake) return;
            ReceiveNativeSleep(_nativeSleepReceiver.Pointer, false);
        }

        internal void ReceiveNativeSleep(nint receiver, bool asleep)
        {
            if (IsDisposed || FailureReason != null) return;
            try
            {
                RequireCurrent();
                if (!IsActive || !_sleepRegistered || _busy || Time.inFixedTimeStep
                    || receiver != _nativeSleepReceiver.Pointer || !_bridge.IsServer || !_bridge.IsGameplayActive)
                    throw new InvalidOperationException("A native sleep transition lost its exact active server actor.");
                int frame = Time.frameCount;
                float multiplier = 1f;
                if (asleep)
                {
                    var environment = _readEnvironment();
                    RequireCurrent();
                    if (environment.Frame != frame || frame != Time.frameCount || !ValidEnvironment(environment)
                        || !float.IsFinite(_stats._comfortRating))
                        throw new InvalidOperationException("The sleeping actor's comfort/environment observation is unavailable.");
                    var range = environment.InteriorSpaceWarmth ? _settings.IndoorRestGainComfortRange
                        : _settings.OutdoorRestGainComfortRange;
                    multiplier = range.x + (range.y - range.x) * Mathf.Clamp01(_stats._comfortRating);
                    if (!float.IsFinite(multiplier)) throw new InvalidOperationException("The sleeping comfort multiplier is invalid.");
                    _sleepAsleepOverrides.SetRuntimeRegenRateMultiplier(_sleepStatNames[6], multiplier);
                    _manager._overrides = _sleepAsleepOverrides;
                }
                else
                {
                    _manager._overrides = _sleepAwakeOverrides;
                    _sleepAsleepOverrides.SetRuntimeRegenRateMultiplier(_sleepStatNames[6], 1f);
                }
                var next = ReadSleepState(checked(_sleepState.ProfileRevision + 1), _sleepState.RateRevision, asleep);
                if (next.HasRestedOverride && next.RestGainMultiplier != multiplier)
                    throw new InvalidOperationException("The exact sleeping rest multiplier did not commit.");
                _sleepState = next;
                if (_dedicatedWake != null) _dedicatedAwaitingWake = asleep;
                RequireSleepBinding();
            }
            catch (Exception error)
            { Fault("Native actor sleep transition failed: " + error.Message, out _); }
        }

        internal void SleepReceiverDestroyed()
        {
            if (!IsDisposed) Fault("The native actor sleep receiver was destroyed before retirement.", out _);
        }

        private SleepRates CaptureSleepRates()
        {
            RequireSleepBinding();
            var profile = _sleepState.IsAsleep ? _sleepAsleepOverrides : _sleepAwakeOverrides;
            Span<PlayerSurvivalRate> rates = stackalloc PlayerSurvivalRate[9];
            uint delegates = _observedFadeDelegates;
            for (int i = 0; i < rates.Length; i++)
            {
                float fade = _observedRates[i].Fade, regeneration = _observedRates[i].Regeneration;
                if (profile is not null && profile.TryGetFadeRate(_sleepStatNames[i], out float overriddenFade))
                {
                    fade = overriddenFade;
                    delegates &= ~(1u << i);
                }
                else if ((delegates & (1u << i)) != 0)
                    fade = PlayerSurvivalSettings.ReadProjectFade(i);
                if (profile is not null && profile.TryGetRegenRate(_sleepStatNames[i], out float overriddenRegeneration))
                    regeneration = overriddenRegeneration;
                if (!float.IsFinite(fade) || !float.IsFinite(regeneration))
                    throw new InvalidOperationException("A native survival rate became nonfinite.");
                rates[i] = new(fade, regeneration);
            }
            var state = ReadSleepState(_sleepState.ProfileRevision, _sleepState.RateRevision, _sleepState.IsAsleep);
            bool changed = _lastSleepRates == null || _lastSleepRates.FadeDelegates != delegates
                || _lastSleepRates.State != state;
            for (int i = 0; i < rates.Length && !changed; i++) changed = rates[i] != _lastSleepRates.Values[i];
            if (changed)
            {
                state = state with { RateRevision = checked(state.RateRevision + 1) };
                _lastSleepRates = new(rates, delegates, state);
            }
            // A missing override keeps the preceding native rate. A fade override permanently
            // clears its delegate (StatsManager.UpdateStats -> Stat.SetFadeRate), even across wake.
            rates.CopyTo(_observedRates);
            _observedFadeDelegates = delegates;
            _sleepState = state;
            return _lastSleepRates;
        }

        private PlayerSurvivalSleepState ReadSleepState(ulong profile, ulong rates, bool asleep)
        {
            var entries = _sleepAsleepOverrides?._stats;
            if (entries == null || entries.WasCollected)
                throw new InvalidOperationException("The actor's private sleeping entries are unavailable.");
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null || entry.WasCollected)
                    throw new InvalidOperationException("An actor sleeping override was removed.");
                if (!string.Equals(entry._assignedType, _sleepStatNames[6], StringComparison.Ordinal)) continue;
                if (!float.IsFinite(entry._regenRateMultiplier))
                    throw new InvalidOperationException("The actor's sleeping multiplier is nonfinite.");
                return new(profile, rates, asleep, true, entry._regenRateMultiplier);
            }
            return new(profile, rates, asleep, false, 1f);
        }

        private void RequireSleepBinding()
        {
            if (!_sleepPrepared) throw new InvalidOperationException("Sleep continuity was not prepared.");
            for (int i = 0; i < _values.Length; i++)
                if (!string.Equals(_values[i]._statName, _sleepStatNames[i], StringComparison.Ordinal))
                    throw new InvalidOperationException("A registered survival stat name changed.");
            var awake = _sleepProfilesInstalled ? _sleepAwakeOverrides : _originalAwakeOverrides;
            var asleep = _sleepProfilesInstalled ? _sleepAsleepOverrides : _originalAsleepOverrides;
            var active = _sleepProfilesInstalled ? (_sleepState.IsAsleep ? asleep : awake) : _originalActiveOverrides;
            if (!SameSleepProfile(_vitals._playerAwakeStatOverrides, awake)
                || !SameSleepProfile(_vitals._playerAsleepStatOverrides, asleep)
                || !SameSleepProfile(_manager._overrides, active))
                throw new InvalidOperationException("The actor's native awake/asleep profile identity changed outside its owner.");
            if (!_sleepProfilesInstalled) return;
            if (!_sleepRegistered || !SleepAlive(_sleepReceiver) || !_sleepReceiver.IsBoundTo(this)
                || _nativeSleepReceiver == null || _nativeSleepReceiver.WasCollected
                || _sleepReceivers == null || _sleepReceivers.WasCollected
                || SleepingManager.Receivers?.Pointer != _sleepReceivers.Pointer)
                throw new InvalidOperationException("The native actor sleep registration was lost.");
            int count = 0;
            for (int i = 0; i < _sleepReceivers.Count; i++)
            {
                var receiver = _sleepReceivers[i];
                if (receiver == null || receiver.WasCollected) continue;
                if (receiver.Pointer == _stats.Pointer)
                    throw new InvalidOperationException("The server actor's mixed LocalPlayer sleep callback was registered again.");
                if (receiver.Pointer == _nativeSleepReceiver.Pointer) count++;
            }
            if (count != 1) throw new InvalidOperationException("The actor sleep receiver is not registered exactly once.");
            if (_dedicatedWake != null)
            {
                if (!GameServerManager.IsDedicatedServer
                    || !SleepingManager.TryGetInstance(out var manager) || !SleepAlive(manager)
                    || manager.Pointer != _dedicatedSleepManager.Pointer
                    || !TimeOfDayHolder.TryGetInstance(out var clock) || !SleepAlive(clock)
                    || clock.Pointer != _dedicatedSleepClock.Pointer)
                    throw new InvalidOperationException("The dedicated actor's native sleep lifecycle was replaced.");
                var callbacks = TimeOfDayHolder.OnTimeJumpComplete;
                if (callbacks == null || callbacks.WasCollected)
                    throw new InvalidOperationException("The dedicated native wake callback was removed.");
                // Native delegates are immutable: scan only when subscriptions change,
                // not once per actor per numeric ownership check.
                if (callbacks.Pointer != _observedWakeCallbacks)
                {
                    var invocationList = callbacks.GetInvocationList();
                    int registered = 0;
                    for (int i = 0; i < invocationList.Length; i++)
                        if (invocationList[i]?.Pointer == _dedicatedWake.Pointer) registered++;
                    if (registered != 1)
                        throw new InvalidOperationException("The dedicated native wake callback is not registered exactly once.");
                    _observedWakeCallbacks = callbacks.Pointer;
                }
            }
        }

        private void ReleaseSleepProfiles()
        {
            if (_dedicatedWake != null)
            {
                var callbacks = TimeOfDayHolder.OnTimeJumpComplete;
                if (callbacks != null)
                    TimeOfDayHolder.OnTimeJumpComplete = Il2CppSystem.Delegate.Remove(callbacks, _dedicatedWake)?.Cast<Il2CppSystem.Action>();
                _dedicatedWake = null;
                _observedWakeCallbacks = IntPtr.Zero;
                _dedicatedAwaitingWake = false;
            }
            if (SleepAlive(_sleepReceiver)) _sleepReceiver.Unbind(this);
            if (_sleepRegistered && _sleepReceivers != null && !_sleepReceivers.WasCollected)
            {
                // Remove only this owned interface, including from a retired native registry.
                for (int i = _sleepReceivers.Count - 1; i >= 0; i--)
                    if (_sleepReceivers[i]?.Pointer == _nativeSleepReceiver?.Pointer) _sleepReceivers.RemoveAt(i);
            }
            _sleepRegistered = false;
            if (_sleepProfilesInstalled && SleepAlive(_vitals) && _manager != null && !_manager.WasCollected)
            {
                if (SameSleepProfile(_manager._overrides, _sleepAwakeOverrides)
                    || SameSleepProfile(_manager._overrides, _sleepAsleepOverrides)) _manager._overrides = _originalActiveOverrides;
                if (SameSleepProfile(_vitals._playerAwakeStatOverrides, _sleepAwakeOverrides))
                    _vitals._playerAwakeStatOverrides = _originalAwakeOverrides;
                if (SameSleepProfile(_vitals._playerAsleepStatOverrides, _sleepAsleepOverrides))
                    _vitals._playerAsleepStatOverrides = _originalAsleepOverrides;
            }
            if (SleepAlive(_sleepReceiver)) UnityEngine.Object.Destroy(_sleepReceiver);
            if (SleepAlive(_sleepAwakeOverrides)) UnityEngine.Object.Destroy(_sleepAwakeOverrides);
            if (SleepAlive(_sleepAsleepOverrides)) UnityEngine.Object.Destroy(_sleepAsleepOverrides);
            _sleepReceiver = null; _nativeSleepReceiver = null; _sleepReceivers = null;
        }

        private static bool SleepAlive(UnityEngine.Object value) => value is not null && !value.WasCollected && value;
        private static bool SameSleepProfile(StatOverrideSet actual, StatOverrideSet expected)
            => expected is null ? actual is null : SleepAlive(actual) && SleepAlive(expected) && actual.Pointer == expected.Pointer;
    }
}
