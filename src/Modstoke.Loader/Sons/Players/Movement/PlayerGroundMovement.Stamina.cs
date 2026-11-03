// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using Modstoke.Loader;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerGroundMovement
    {
        private PlayerStats _staminaStats;
        private Vitals _staminaVitals;
        private StaminaStat _staminaStat;
        private VitalityStat _staminaVitality;
        private int _staminaStatsId, _staminaVitalsId, _staminaFrame = -1;
        private PlayerStaminaProfile _staminaProfile;
        private Func<PlayerStaminaConditions> _readStaminaConditions;
        private Coroutine _staminaCoroutine;
        private bool _insideStaminaCoroutine, _staminaRunning, _staminaForceStop;
        private float _staminaRecovery, _staminaExhaustedUntil = float.NaN;

        public bool StaminaAuthorityEnabled => !_disposed && _staminaVitals != null;

        /// <summary>
        /// Binds the exact migrated carrier's paused PlayerStats/Vitals. Their native Update
        /// routines must remain quiescent: those routines access LocalPlayer. No native stat
        /// value is seeded, copied from the host or reset here. The adapter owns other survival
        /// progression and supplies actor-local run permission, animation speed and penalties.
        /// Recovery runs once per rendered frame; run drain runs once per simulated fixed step.
        /// </summary>
        public bool TryEnableStaminaAuthority(PlayerStats serverStats, PlayerStaminaProfile profile,
            Func<PlayerStaminaConditions> readServerConditions, out string detail)
        {
            detail = "Exact paused carrier PlayerStats/Vitals and authoritative stamina conditions are required.";
            if (_disposed || Environment.CurrentManagedThreadId != _thread || _insideMovementStep
                || Time.inFixedTimeStep || _staminaVitals != null || readServerConditions == null
                || !GameBuild.IsRecognized || !Range(profile.RunCostPerSecond, 0, 10000)
                || !Range(profile.TimeToRecover, 0, 600) || !Range(profile.RecoveryRemaining, -600, 600)
                || !Range(profile.ExhaustionWaitRemaining, 0, 0.9f)) return false;
            try
            {
                if (!Current() || !SwimAlive(serverStats) || serverStats.enabled
                    || !serverStats.transform.IsChildOf(_transform)) return false;
                var vitals = serverStats._vitals;
                if (!SwimAlive(vitals) || vitals.enabled || !vitals.transform.IsChildOf(_transform)
                    || vitals._stamina == null || vitals._vitality == null) return false;
                var conditions = readServerConditions();
                if (!Current() || !ValidStaminaConditions(conditions)) return false;
                _staminaStats = serverStats;
                _staminaVitals = vitals;
                _staminaStat = vitals._stamina;
                _staminaVitality = vitals._vitality;
                _staminaStatsId = serverStats.GetInstanceID();
                _staminaVitalsId = vitals.GetInstanceID();
                _staminaProfile = profile;
                _staminaRecovery = profile.RecoveryRemaining;
                _staminaForceStop = profile.ExhaustionWaitRemaining > 0;
                _staminaExhaustedUntil = float.NaN;
                _readStaminaConditions = readServerConditions;
                if (!StaminaCurrent() || (_activated && !TryStartStaminaObservation(out detail)))
                {
                    DisposeStamina();
                    return false;
                }
                detail = _activated ? "Actor-local run/jump stamina and recovery bound."
                    : "Actor-local stamina bound; recovery and exhaustion timers remain held until activation.";
                return true;
            }
            catch (Exception error)
            {
                DisposeStamina();
                detail = "Stamina enrollment failed (" + error.GetType().Name + ").";
                return false;
            }
        }

        public bool TryReadStaminaState(out PlayerStaminaState state)
        {
            state = default;
            if (Environment.CurrentManagedThreadId != _thread
                || !(_nativeModeSuspended ? NativeModeIdentityCurrent() : Current()) || !StaminaCurrent()) return false;
            state = new(_staminaStat._currentValue, _staminaVitality._currentValue, _staminaRunning,
                _staminaForceStop, _staminaRecovery, _staminaStats._staminaActionTime);
            return true;
        }

        private bool StaminaCurrent()
            => !_disposed && SwimAlive(_staminaStats) && SwimAlive(_staminaVitals)
                && _staminaStats.GetInstanceID() == _staminaStatsId && _staminaVitals.GetInstanceID() == _staminaVitalsId
                && !_staminaStats.enabled && !_staminaVitals.enabled
                && _staminaStats.transform.IsChildOf(_transform) && _staminaVitals.transform.IsChildOf(_transform)
                && _staminaStats._vitals?.Pointer == _staminaVitals.Pointer
                && _staminaStat != null && !_staminaStat.WasCollected
                && _staminaVitality != null && !_staminaVitality.WasCollected
                && _staminaVitals._stamina?.Pointer == _staminaStat.Pointer
                && _staminaVitals._vitality?.Pointer == _staminaVitality.Pointer
                && Range(_staminaStat._currentValue, _staminaStat._min, _staminaStat._max)
                && Range(_staminaVitality._currentValue, _staminaVitality._min, _staminaVitality._max)
                && float.IsFinite(_staminaStat._min) && float.IsFinite(_staminaStat._max)
                && float.IsFinite(_staminaVitality._min) && float.IsFinite(_staminaVitality._max)
                && Range(_staminaStats._jumpStaminaCost, 0, 10000)
                && Range(_staminaStats._staminaRecoverRate, 0, 10000)
                && float.IsFinite(_staminaStats._staminaActionTime);

        private PlayerStaminaConditions ReadStaminaConditions()
        {
            if (!StaminaCurrent()) throw new InvalidOperationException("Carrier stamina ownership changed.");
            var conditions = _readStaminaConditions();
            if (!Current() || !StaminaCurrent() || !ValidStaminaConditions(conditions))
                throw new InvalidOperationException("Authoritative stamina conditions changed or are invalid.");
            return conditions;
        }

        private bool ApplyRunningStamina(bool sprint, bool canRun, float overallSpeed, float threshold, float dt)
        {
            var conditions = ReadStaminaConditions();
            // FixedUpdate 182F80A40: the threshold is zero stamina, not _staminaMinNeededToRun.
            _staminaRunning = sprint && canRun && conditions.CanRun
                && _staminaStat._currentValue > 0 && overallSpeed >= threshold;
            if (_staminaRunning)
            {
                _staminaVitals.AdjustStamina(-_staminaProfile.RunCostPerSecond * dt);
                if (!StaminaCurrent()) throw new InvalidOperationException("Run stamina debit lost its actor.");
                if (_staminaStat._currentValue <= 0 && !_staminaForceStop)
                {
                    _staminaForceStop = true;
                    _staminaExhaustedUntil = Time.time + 0.9f;
                }
            }
            return _staminaRunning;
        }

        private bool ApplyRunningStamina(bool sprint, float dt)
        {
            var c = ReadStaminaConditions();
            return ApplyRunningStamina(sprint, c.CanRun, c.OverallSpeed, c.WalkToRunThreshold, dt);
        }

        private void ChargeOrdinaryJump()
        {
            if (_staminaVitals == null) return;
            if (!StaminaCurrent()) throw new InvalidOperationException("Jump stamina lost its actor.");
            // PlayerStats.OnJump 1831110B0 is actor-local; keep its action-time write too.
            _staminaStats.OnJump();
            if (!StaminaCurrent()) throw new InvalidOperationException("Jump stamina debit lost its actor.");
        }

        private IEnumerator ObserveStamina()
        {
            while (!_disposed && _staminaVitals is not null)
            {
                yield return null;
                if (_disposed || _staminaVitals is null) yield break;
                if (!_activated || _nativeModeSuspended || !_bridge.IsGameplayActive) continue;
                _insideStaminaCoroutine = true;
                try { RecoverStamina(); }
                catch (Exception error) { Stop("Stamina recovery failed (" + error.GetType().Name + ")."); }
                finally { _insideStaminaCoroutine = false; }
            }
        }

        private bool TryStartStaminaObservation(out string detail)
        {
            detail = "Stamina observation requires an active gameplay bridge and an exact bound actor.";
            if (!_bridge.IsGameplayActive || !StaminaCurrent() || _staminaCoroutine != null) return false;
            _staminaExhaustedUntil = _staminaForceStop
                ? Time.time + _staminaProfile.ExhaustionWaitRemaining : float.NaN;
            if (!global::Modstoke.Loader.Coroutines.TryStartObserved(ObserveStamina(), StaminaCoroutineCompleted,
                out var token, out detail)) return false;
            _staminaCoroutine = token;
            return true;
        }

        private void RecoverStamina()
        {
            if (_staminaFrame == Time.frameCount) return;
            var conditions = ReadStaminaConditions();
            float dt = Time.deltaTime;
            float now = Time.time;
            if (!Range(dt, 0, 1) || !float.IsFinite(now))
                throw new InvalidOperationException("Invalid stamina recovery time.");
            _staminaFrame = Time.frameCount;
            if (_staminaForceStop && now >= _staminaExhaustedUntil)
            {
                _staminaForceStop = false;
                _staminaExhaustedUntil = float.NaN;
                _staminaRecovery = _staminaProfile.TimeToRecover;
            }
            // UpdateStamina 1831168F0: no recovery/countdown when already at vitality.
            if (_staminaStat._currentValue >= _staminaVitality._currentValue) return;
            if (_staminaRunning || _staminaRecovery > 0 || now - _staminaStats._staminaActionTime < 0.5f)
            {
                if (_staminaRecovery > 0) _staminaRecovery -= dt;
                return;
            }
            _staminaVitals.AdjustStamina(_staminaStats._staminaRecoverRate * dt * conditions.RecoveryMultiplier);
            if (!StaminaCurrent()) throw new InvalidOperationException("Stamina recovery lost its actor.");
        }

        private void StaminaCoroutineCompleted(bool completed, Exception error)
        {
            _staminaCoroutine = null;
            if (!_disposed && _staminaVitals is not null)
                Stop("Stamina recovery observation ended" + (error == null ? "." : " (" + error.GetType().Name + ")."));
        }

        private void DisposeStamina()
        {
            _staminaStats = null;
            _staminaVitals = null;
            _staminaStat = null;
            _staminaVitality = null;
            _readStaminaConditions = null;
            _staminaRunning = false;
            var token = _staminaCoroutine;
            _staminaCoroutine = null;
            if (token != null && !_insideStaminaCoroutine)
                try { global::Modstoke.Loader.Coroutines.StopOwned(token); }
                catch (Exception error) { StopReason ??= "Stamina observation cleanup unconfirmed (" + error.GetType().Name + ")."; }
        }

        private static bool ValidStaminaConditions(PlayerStaminaConditions c)
            => Range(c.OverallSpeed, 0, 1000) && Range(c.WalkToRunThreshold, 0, 1000)
                && Range(c.RecoveryMultiplier, 0, 1000);
    }
}
