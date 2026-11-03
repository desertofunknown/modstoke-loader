// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Player.Stats;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    // The original source actor executes life presentation. The server, stat projector
    // and equipment transaction owner retain their respective numeric/custody authority.
    internal sealed partial class PlayerLifePresentation : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventoryEnforcement _source;
        private readonly PlayerStatProjection _projection;
        private readonly PlayerStats _stats;
        private readonly Vitals _vitals;
        private readonly PlayerInventory _inventory;
        private readonly HeldOnlyItemController _held;
        private readonly FirstPersonCharacter _character;
        private readonly PlayerAnimatorControl _animation;
        private readonly Animator _animator;
        private readonly camFollowHead _camera;
        private readonly SimpleMouseRotator _bodyRotation, _cameraRotation;
        private readonly playerScriptSetup _setup;
        private readonly GameObject _root;
        private readonly UnityEngine.Object[] _identities;
        private readonly int[] _instanceIds;
        private readonly int _scene;
        private readonly List<string> _calls = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly List<DelayedCall> _delayed = new();
        private PlayerLifeFrame _ticket;
        private PlayerLifeValuesFrame _values;
        private ulong _lifeGeneration, _epoch;
        private bool _pending, _projected, _nativeStarted, _pumping, _downed;
        private bool _resetFinished, _finalized, _priorHitDetection;
        private string _entry;
        private int _lastPumpFrame = -1;
        private int _healthSetters, _dyingWrites, _midActionCalls;

        internal ulong CompletedSequence { get; private set; }
        internal bool BlocksMovement => IsDisposed || FailureReason != null || _pending || _downed;
        internal bool IsDisposed { get; private set; }
        internal string FailureReason { get; private set; }
        internal bool NativeCallbackActive => _calls.Count != 0;
        internal PlayerInventoryEnforcement ForcedInventory => _source;
        private string Call => _calls.Count == 0 ? null : _calls[^1];

        private PlayerLifePresentation(PlayerBridge bridge,
            PlayerInventoryEnforcement source, PlayerStatProjection projection, PlayerStats stats)
        {
            _bridge = bridge; _source = source; _projection = projection; _stats = stats;
            _vitals = stats._vitals; _inventory = LocalPlayer.Inventory; _character = LocalPlayer.FpCharacter;
            _held = _inventory.HeldOnlyItemController?.TryCast<HeldOnlyItemController>();
            _animation = LocalPlayer.AnimControl; _animator = LocalPlayer.Animator; _camera = LocalPlayer.CamFollowHead;
            _bodyRotation = LocalPlayer.MainRotator; _cameraRotation = LocalPlayer.CamRotator;
            _setup = LocalPlayer.ScriptSetup; _root = LocalPlayer.GameObject; _scene = _root.scene.handle;
            _identities = new UnityEngine.Object[] { _stats, _vitals, _inventory, _character, _animation,
                _animator, _camera, _bodyRotation, _cameraRotation, _setup, _root, _held, LocalPlayer.PlayerStimuli };
            _instanceIds = new int[_identities.Length];
            for (int i = 0; i < _identities.Length; i++)
            {
                if (!Live(_identities[i])) throw new InvalidOperationException("Native life component is unavailable.");
                _instanceIds[i] = _identities[i].GetInstanceID();
            }
            RequireCurrent();
        }

        internal static bool TryPrepare(PlayerBridge bridge,
            PlayerInventoryEnforcement sourceInventory, PlayerStatProjection statProjection,
            out PlayerLifePresentation owner, out string detail)
        {
            owner = null;
            detail = "Source life requires installed native gates and the inactive original actor binding.";
            if (!_installed || Environment.CurrentManagedThreadId != _hookThread || bridge == null
                || bridge.IsServer || bridge.IsDisposed || bridge.IsGameplayActive || sourceInventory == null
                || statProjection == null || statProjection.IsDisposed || statProjection.FailureReason != null
                || !sourceInventory.TryReadStatProjectionActor(bridge, out var stats)) return false;
            try
            {
                foreach (var existing in Owners)
                    if (!existing.IsDisposed || Same(existing._stats, stats)) return false;
                var candidate = new PlayerLifePresentation(bridge, sourceInventory, statProjection, stats);
                candidate.RequireAlive();
                if (!candidate.HeldCustodySettled())
                    throw new InvalidOperationException("Life preparation requires the exact settled held-only custody and bound route.");
                foreach (var name in DeathInvokes)
                    if (stats.IsInvoking(name)) throw new InvalidOperationException("An earlier native life invoke is still active: " + name);
                if (!statProjection.BindLife(bridge, candidate)) return false;
                Owners.Add(candidate);
                bridge.Retired += candidate.Dispose;
                owner = candidate;
                detail = "Original native life actor, callback gates and stat projector bound without starting gameplay.";
                return true;
            }
            catch (Exception error) { detail = "Source life preparation refused: " + error.Message; return false; }
        }

        // A true return accepts the exact command, not its completion. Repeated datagrams
        // must be deduplicated by the ordered route; this entry never repeats callbacks.
        internal bool TryBegin(in PlayerLifeFrame ticket, in PlayerLifeValuesFrame values, out string detail)
        {
            detail = FailureReason ?? "Source life requires the next exact supported command.";
            if (_projection.FailureReason != null)
                return Fail(_projection.FailureReason, out detail);
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _pending || _pumping || NativeCallbackActive || !_bridge.IsGameplayActive || !ticket.IsValid
                || ticket.Kind != PlayerLifeMessage.Command || ticket.MigrationId != _bridge.Plan.Id
                || ticket.Generation != _bridge.Plan.Generation || ticket.NetworkId != _bridge.NetworkId
                || !values.MatchesCommand(in ticket)
                || ticket.Sequence != CompletedSequence + 1 || CompletedSequence != _projection.CompletedLifeSequence
                || (_lifeGeneration != 0 && ticket.LifeGeneration != _lifeGeneration)) return false;
            if (ticket.Action == PlayerLifeSourceAction.Respawn)
                return Fail("Native respawn needs authoritative death-bag, destination, marker and new-actor lifetime ownership.", out detail);
            try
            {
                RequireCurrent();
                if (_source.HasPendingCustody)
                { detail = "Life awaits already accepted inventory and intake custody."; return false; }
                if (!HeldCustodyEmpty())
                { detail = "Life awaits settled and actually empty held-only custody; forced death drops are not ordinary drop-all."; return false; }
                if (ticket.Action == PlayerLifeSourceAction.StartDeath)
                {
                    if (!_source.TryReadHeldOnlyForcedDropSettlement(
                        PlayerHeldOnlyAuthority.MutationKind.KnockDownDrop, ticket.Sequence, out detail)) return false;
                    RequireAlive();
                    RequireBurningStart(in ticket, in values);
                    if (HasDamagingLanding())
                    { detail = "Life awaits the accepted damaging landing before starting native death."; return false; }
                    if (ticket.DeathType != DeathType.Unknown || ticket.Health > Mathf.Epsilon || ticket.TargetHealth > Mathf.Epsilon
                        || _character._isAnimControllerSwimming || _animation._knockdownTime > 0
                        || _stats._debugInstantRespawn
                        || (!PlayerStats._blockFinalDeath && _stats._playerDeathCount >= _stats._maxDeathCount))
                        throw new InvalidOperationException("This death requires drowning/final-death, knockdown or outstanding fall-damage ownership.");
                    CaptureHands();
                    _priorHitDetection = _setup.HitDetection;
                }
                else
                {
                    if (ticket.Action != PlayerLifeSourceAction.ResetFromHeal || !_downed
                        || !DownedMatches())
                        throw new InvalidOperationException("Native same-life heal does not match this downed actor or its authoritative numbers.");
                    RequireEmptyHands();
                }
                if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                    throw new InvalidOperationException("Accepted life work could not yield the source body hold: " + detail);
                // Every scheduled invoke and iterator belongs to an action, not just Stats.
                CancelDeathInvokes();
                _epoch = checked(_epoch + 1);
                _ticket = ticket; _values = values; _lifeGeneration = ticket.LifeGeneration;
                _pending = true; _projected = false; _nativeStarted = false;
                _resetFinished = false; _finalized = false;
                _layerFinished = false;
                _healthSetters = 0; _dyingWrites = 0; _midActionCalls = 0;
                _seen.Clear();
                BeginBurningPresentation();
                detail = "Exact native life command retained; equipment and numeric boundaries are pending.";
                return true;
            }
            catch (Exception error) { return Fail(error.Message, out detail); }
        }

        internal bool TryPump(out string detail)
        {
            detail = FailureReason ?? "Native life has no pending transition.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _pumping || NativeCallbackActive || Time.inFixedTimeStep || !_bridge.IsGameplayActive) return false;
            if (_lastPumpFrame == Time.frameCount) return true;
            _lastPumpFrame = Time.frameCount;
            _pumping = true;
            try
            {
                RequireCurrent();
                if ((_pending || _burnFxPending)
                    && !PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(_bridge, out detail))
                    throw new InvalidOperationException("Pending life work could not yield the source body hold: " + detail);
                PumpBurningTail();
                if (!_pending) return true;
                if (_source.IsFaulted) throw new InvalidOperationException(_source.Detail);
                if (!_projected)
                {
                    if (!HeldCustodyEmpty())
                    { detail = "Life numeric projection awaits settled and actually empty held-only custody."; return true; }
                    if (!_projection.TryBeginLife(this, in _ticket, in _values, out detail))
                    {
                        if (_projection.FailureReason != null) throw new InvalidOperationException(_projection.FailureReason);
                        if (!_source.StatProjectionCanApply || _source.HasPendingCustody) return true;
                        throw new InvalidOperationException("Exact life projection was refused at a settled numeric boundary: " + detail);
                    }
                    _projected = true;
                }
                if (!_nativeStarted)
                {
                    if (_ticket.Action == PlayerLifeSourceAction.StartDeath)
                    {
                        if (!PumpHands(true, out detail)) return FailureReason == null;
                        RequireEmptyHands();
                        RequireAlive();
                        if (_stats._isOnFire != _burnRequired || !HeldCustodyEmpty() || HasDamagingLanding())
                            throw new InvalidOperationException("Native death lost its settled held-only or landing boundary.");
                        _nativeDeathStartedAt = Time.time;
                        _nativeStarted = true;
                        InvokeNative(nameof(PlayerStats.CheckDeath), () => _stats.CheckDeath(_ticket.DeathType));
                        if (!_seen.Contains(nameof(PlayerStats.StartDeathRoutine))
                            || !_seen.Contains(nameof(PlayerStats.DoBasicDeath)) || _audioWorker?.Epoch != _epoch
                            || !_delayed.Exists(call => call.Method == nameof(PlayerStats.CheckFirstDeathRoutine)))
                            throw new InvalidOperationException("Native CheckDeath did not retain the ordinary death graph, audio and scheduled prompt.");
                    }
                    else
                    {
                        if (!MatchesNativeHealNumbers(in _ticket))
                            throw new InvalidOperationException("Projected life commit does not match native same-life heal numbers.");
                        _nativeStarted = true;
                        InvokeNative(nameof(PlayerStats.HealedMp), () => _stats.HealedMp(false));
                        if (_resetWorker == null || _healthSetters != 2 || _dyingWrites != 1)
                            throw new InvalidOperationException("HealedMp did not produce its exact native reset iterator and numeric boundaries.");
                    }
                }
                PumpBurningExpiry();
                PumpDelayed();
                RequireCurrent();
                if (_ticket.Action == PlayerLifeSourceAction.StartDeath)
                {
                    if (_delayed.Count != 0 || !_seen.Contains(nameof(PlayerStats.KillPlayerWithRespawnPrompt))) return true;
                    if (!DownedMatches() || _dyingWrites != 1 || _midActionCalls != 2
                        || !_seen.Contains(nameof(PlayerStats.StopBurning)) || (_burnRequired && !_burnExecuted))
                        throw new InvalidOperationException("Native death did not settle its actual downed controls and state.");
                    _downed = true;
                }
                else
                {
                    if (!_resetFinished || !_finalized || !_layerFinished) return true;
                    if (!PumpHands(false, out detail)) return FailureReason == null;
                    if (!AliveMatches() || _midActionCalls != 2 || _setup.HitDetection != _priorHitDetection)
                        throw new InvalidOperationException("Native get-up did not restore its retained controls or prior hit-detection mode.");
                    _downed = false;
                }
                if (!_projection.TryCompleteLife(this, in _ticket, out detail))
                    throw new InvalidOperationException(_projection.FailureReason ?? detail);
                CompletedSequence = _ticket.Sequence;
                _pending = false;
                detail = "Native callbacks, continuation and exact equipment receipts completed for this life sequence.";
                return true;
            }
            catch (Exception error) { return Fail(error.Message, out detail); }
            finally { _pumping = false; }
        }

        private void InvokeNative(string name, Action invoke)
        {
            RequireCurrent();
            if (_entry != null || NativeCallbackActive) throw new InvalidOperationException("Native life entry is reentrant.");
            _entry = name;
            try { invoke(); }
            finally { _entry = null; }
            RequireCurrent();
            if (!_seen.Contains(name)) throw new InvalidOperationException("Native life callback was suppressed: " + name);
        }

        private void RequireCurrent()
        {
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || _bridge.IsDisposed || !_bridge.TryReadCurrentTarget(out _) || _projection.IsDisposed
                || _projection.FailureReason != null || !_source.TryReadStatProjectionActor(_bridge, out var stats)
                || !Same(_stats, stats) || !Same(_stats, LocalPlayer.Stats) || !Same(_inventory, LocalPlayer.Inventory)
                || !Same(_inventory, _stats._inventory) || !Same(_vitals, _stats._vitals)
                || !Same(_character, LocalPlayer.FpCharacter) || !Same(_animation, LocalPlayer.AnimControl)
                || !Same(_animator, LocalPlayer.Animator) || !Same(_animator, _stats._animator)
                || !Same(_animator, _animation._animator) || !Same(_camera, LocalPlayer.CamFollowHead)
                || !Same(_bodyRotation, LocalPlayer.MainRotator) || !Same(_cameraRotation, LocalPlayer.CamRotator)
                || !Same(_setup, LocalPlayer.ScriptSetup) || !Same(_root, LocalPlayer.GameObject)
                || !Same(_held, _inventory.HeldOnlyItemController?.TryCast<HeldOnlyItemController>())
                || !Same(_held._player, _inventory)
                || !Same(_identities[^1], LocalPlayer.PlayerStimuli) || _root.scene.handle != _scene)
                throw new InvalidOperationException("Native life lost its original actor, scene, bridge or numeric owner.");
            for (int i = 0; i < _identities.Length; i++)
                if (!Live(_identities[i]) || _identities[i].GetInstanceID() != _instanceIds[i])
                    throw new InvalidOperationException("A native life component was replaced.");
            if ((_downed || (_pending && _nativeStarted)) && !HeldCustodyEmpty())
                throw new InvalidOperationException("Held-only custody changed during the retained native life action.");
        }

        // Movement still validates its entire topology and physics profile. This only
        // permits the control state produced by this exact original actor's life graph.
        internal bool TryValidateRig(PlayerBridge bridge, FirstPersonCharacter character, out string detail)
        {
            detail = FailureReason ?? "Movement does not match the retained native life control phase.";
            if (!ReferenceEquals(bridge, _bridge) || !Same(character, _character)
                || IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || NativeCallbackActive) return false;
            try
            {
                RequireCurrent();
                bool valid;
                if (!_pending) valid = _downed ? DownedMatches() : AliveMatches();
                else if (!_nativeStarted)
                    valid = _ticket.Action == PlayerLifeSourceAction.StartDeath ? AliveMatches() : DownedMatches();
                else if (_ticket.Action == PlayerLifeSourceAction.StartDeath)
                    valid = _seen.Contains(nameof(PlayerStats.StartDeathRoutine))
                        && DownControlsMatch(!_seen.Contains(nameof(PlayerStats.KillPlayerWithRespawnPrompt)))
                        && _vitals._hasDied && _stats._playerIsDowned && !_stats._healing
                        && _stats._dyingState == (_seen.Contains(nameof(PlayerStats.KillPlayerWithRespawnPrompt))
                            ? PlayerStats.DyingState.Dying : PlayerStats.DyingState.Alive);
                else valid = _finalized ? AliveMatches()
                    : _seen.Contains(nameof(PlayerStats.StartResetFromHeal)) && DownControlsMatch(true)
                        && _stats._dyingState == PlayerStats.DyingState.Alive && !_vitals._hasDied
                        && !_stats._playerIsDowned && _stats._healing;
                if (!valid) return false;
                detail = "Exact source rig and observed native life control phase remain current.";
                return true;
            }
            catch (Exception error) { detail = error.Message; return false; }
        }

        private bool HeldCustodySettled()
        {
            var retained = _source.RetainedHeldOnlySource;
            return !_source.HasPendingHeldOnlyCustody && _source.HeldOnlyRoute != null
                && retained != null && retained.Lifetime(true, false)
                && PlayerHeldOnlyAuthority.Snapshot.TryCaptureRetained(_bridge, _inventory,
                    _held, out var snapshot, out _) && retained.MatchesSettledCustody(snapshot);
        }

        private bool HeldCustodyEmpty() => !_source.HasPendingHeldOnlyCustody && _held._heldCount == 0;

        internal bool CanPrepareForcedDrop(ulong lifeGeneration, PlayerHeldOnlyAuthority.MutationKind kind,
            ulong transition)
        {
            if (IsDisposed || FailureReason != null || _pending || _downed || NativeCallbackActive
                || lifeGeneration != (_lifeGeneration == 0 ? 1UL : _lifeGeneration)
                || !PlayerHeldOnlyAuthority.IsForcedDrop(kind)
                || kind == PlayerHeldOnlyAuthority.MutationKind.KnockDownDrop && transition != CompletedSequence + 1)
                return false;
            RequireCurrent();
            RequireAlive();
            return true;
        }

        private bool HasDamagingLanding()
        {
            float landing = _character._landingVelocityY, threshold = _character._fallDamageVelocity;
            if (!float.IsFinite(landing) || !float.IsFinite(threshold))
                throw new InvalidOperationException("Native landing velocity or threshold is invalid.");
            // DoFallDamage (2F89C30) exits when threshold >= landing or this
            // transient timer latch is clear. It is not a global damage-enable flag.
            return _character._allowFallDamage && landing > threshold;
        }

        private bool DownControlsMatch(bool midAction) => !_inventory.enabled && !_character.enabled
            && !_bodyRotation.enabled && !_cameraRotation.enabled && _character._isInMidAction == midAction
            && !_stats._isHealing && !_stats._respawning && !_stats._doFinalDeath;

        private void RequireAlive()
        {
            if (_stats._dyingState != PlayerStats.DyingState.Alive || _vitals._hasDied || _stats._playerIsDowned
                || _stats._healing || _stats._isHealing || _stats._respawning || _stats._doFinalDeath
                || _animator.layerCount <= 6 || _stats._getupLayerBehaviourActivators == null
                || (Live(_stats._mpRespawnTimerGui) && _stats._mpRespawnTimerGui.gameObject.activeInHierarchy))
                throw new InvalidOperationException("Original actor is not at an idle alive life boundary.");
        }

        private bool DownedMatches() => _stats._dyingState == PlayerStats.DyingState.Dying && _vitals._hasDied
            && _stats._playerIsDowned && !_stats._healing && !_stats._isHealing && !_stats._respawning
            && !_stats._doFinalDeath && DownControlsMatch(false);

        private bool AliveMatches() => _stats._dyingState == PlayerStats.DyingState.Alive && !_vitals._hasDied
            && !_stats._playerIsDowned && !_stats._healing && !_stats._isHealing && !_stats._respawning
            && !_stats._doFinalDeath && _inventory.enabled && _character.enabled
            && _bodyRotation.enabled && _cameraRotation.enabled && !_character._isInMidAction;

        private bool MatchesNativeHealNumbers(in PlayerLifeFrame ticket)
        {
            float target = MathF.Min(_vitals._lowHealthThreshold + 1f,
                _vitals._health._max - _vitals._sickness._currentValue);
            float health = Mathf.Clamp(target, _vitals._health._min, _vitals._health._max);
            return ticket.Health == health && ticket.TargetHealth == target;
        }

        private bool Fail(string detail, out string result)
        {
            FailureReason ??= detail;
            _delayed.Clear();
            result = FailureReason;
            return false;
        }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Retire native life on its game thread.");
            if (IsDisposed) return;
            // Tombstones remain registered while Stats lives; old Unity continuations must
            // never gain access to a replacement LocalPlayer after this owner retires.
            IsDisposed = true;
            _delayed.Clear(); _epoch++;
            _bridge.Retired -= Dispose;
            try
            {
                // StopDyingMpAudio only stops this receiver's stored coroutine/FMOD event.
                // It does not consult LocalPlayer and cannot restore a replacement actor.
                if (Live(_stats) && _stats.GetInstanceID() == _instanceIds[0])
                {
                    foreach (string name in DeathInvokes) _stats.CancelInvoke(name);
                    if (_audioWorker != null) _stats.StopDyingMpAudio();
                    if (_resetWorker != null) _stats.StopCoroutine(nameof(PlayerStats.ResetPlayerFromHeal));
                }
            }
            catch (Exception error) { FailureReason ??= "Native life retirement could not stop its retained worker: " + error.Message; }
        }

        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Live(left) && Live(right) && left.Pointer == right.Pointer;
    }
}
