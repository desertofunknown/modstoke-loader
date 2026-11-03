// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Multiplayer;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifePresentation
    {
        private static readonly string[] DeathInvokes = { nameof(PlayerStats.resetInjuredBool), nameof(PlayerStats.disablePlayerControl),
            nameof(PlayerStats.ResetHit), nameof(PlayerStats.CheckFirstDeathRoutine), nameof(PlayerStats.CheckFirstDeathRoutineNoPrompt),
            nameof(PlayerStats.KillPlayer), nameof(PlayerStats.EndDeathRoutine), nameof(PlayerStats.EndSwimmingDeathRoutine) };
        private readonly record struct DelayedCall(string Method, float Due, ulong Epoch, PlayerLifeFrame Ticket);
        private sealed record NativeWorker(Il2CppObjectBase Routine, ulong Epoch, PlayerLifeFrame Ticket);
        private NativeWorker _resetWorker, _audioWorker;
        private NativeWorker _layerWorker;
        private readonly System.Collections.Generic.List<NativeWorker> _layerWorkers = new();
        private bool _layerFinished;
        private MultiplayerRespawnTimer _timer;

        private void CancelDeathInvokes()
        {
            RequireCurrent();
            foreach (string name in DeathInvokes) _stats.CancelInvoke(name);
            _delayed.Clear();
        }

        private static bool BeforeLifeInvoke(MonoBehaviour __0, string __1, float __2, float __3)
        {
            PlayerLifePresentation owner = null;
            foreach (var candidate in Owners)
                if (__0 != null && !__0.WasCollected && __0.Pointer == candidate._stats.Pointer) { owner = candidate; break; }
            if (owner == null || Array.IndexOf(DeathInvokes, __1) < 0) return true;
            try
            {
                owner.RequireCurrent();
                bool allowed = owner.Call switch
                {
                    nameof(PlayerStats.StartDeathRoutine) => __1 is nameof(PlayerStats.resetInjuredBool)
                        or nameof(PlayerStats.disablePlayerControl) or nameof(PlayerStats.ResetHit),
                    nameof(PlayerStats.DoBasicDeath) => __1 == nameof(PlayerStats.CheckFirstDeathRoutine),
                    nameof(PlayerStats.CheckFirstDeathOption) => __1 == nameof(PlayerStats.KillPlayer),
                    _ => false
                };
                if (!owner._pending || !allowed || !float.IsFinite(__2) || __2 < 0 || __2 > 10 || __3 != 0)
                    throw new InvalidOperationException("Unowned native life scheduling request: " + __1);
                if ((__1 == nameof(PlayerStats.CheckFirstDeathRoutine) && __2 != 2f)
                    || (__1 == nameof(PlayerStats.KillPlayer) && __2 != 1f))
                    throw new InvalidOperationException("Native death scheduling changed its qualified two-stage delay.");
                foreach (var pending in owner._delayed)
                    if (pending.Method == __1) throw new InvalidOperationException("Duplicate native life invoke: " + __1);
                owner._delayed.Add(new(__1, Time.time + __2, owner._epoch, owner._ticket));
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            // Unity named Invoke has no generation token. Execute this same native callback
            // once from the owner's pump at the observed game-time deadline instead.
            return false;
        }

        private void PumpDelayed()
        {
            while (_delayed.Count != 0)
            {
                int earliest = 0;
                for (int i = 1; i < _delayed.Count; i++)
                    if (_delayed[i].Due < _delayed[earliest].Due) earliest = i;
                var call = _delayed[earliest];
                if (Time.time < call.Due) return;
                if (call.Method == nameof(PlayerStats.KillPlayer) && !BurningPromptReady()) return;
                _delayed.RemoveAt(earliest);
                RequireCurrent();
                if (call.Epoch != _epoch || call.Ticket != _ticket || !_pending)
                    throw new InvalidOperationException("Native life invoke outlived its source ticket.");
                InvokeNative(call.Method, () => StatsMethods[call.Method].Invoke(_stats, null));
            }
        }

        private static bool BeforeLifeCoroutine(MonoBehaviour __instance, Il2CppSystem.Collections.IEnumerator __0)
        {
            foreach (var owner in Owners)
            {
                if (__instance == null || __instance.WasCollected || __instance.Pointer != owner._stats.Pointer
                    || owner.Call != nameof(PlayerStats.StartDeathRoutine)) continue;
                try
                {
                    owner.RequireCurrent();
                    var audio = __0?.TryCast<PlayerStats._DyingMpAudio_d__248>();
                    if (audio == null || audio.WasCollected || audio.__1__state != 0
                        || !Same(audio.__4__this, owner._stats) || owner._audioWorker?.Epoch == owner._epoch)
                        throw new InvalidOperationException("Death did not start its exact native audio iterator.");
                    owner._audioWorker = new(audio, owner._epoch, owner._ticket);
                    return true;
                }
                catch (Exception error) { owner.Fail(error.Message, out _); return false; }
            }
            return true;
        }

        private static bool BeforeResetFactory(PlayerStats __instance, out HookState __state)
        {
            __state = default;
            var owner = FindStats(__instance);
            if (owner == null) return true;
            try
            {
                owner.RequireCurrent();
                if (!Same(owner._stats, __instance) || owner.Call != nameof(PlayerStats.HealedMp)
                    || owner._resetWorker?.Epoch == owner._epoch) return false;
                __state = owner.Enter("ResetFactory");
                return true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static Exception AfterResetFactory(Il2CppSystem.Collections.IEnumerator __result,
            bool __runOriginal, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireCurrent();
                var worker = __result?.TryCast<PlayerStats._ResetPlayerFromHeal_d__264>();
                if (!__runOriginal || __exception != null || worker == null || worker.WasCollected
                    || worker.__1__state != 0 || !Same(worker.__4__this, owner._stats))
                    throw new InvalidOperationException("HealedMp did not create its actual native reset worker.");
                owner._resetWorker = new(worker, owner._epoch, owner._ticket);
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }

        private static bool BeforeResetWorker(PlayerStats._ResetPlayerFromHeal_d__264 __instance,
            bool __runOriginal, ref bool __result, out HookState __state)
        {
            __state = default;
            try { return EnterWorker(__instance, __instance.__4__this, false, __runOriginal, ref __result, out __state); }
            catch (Exception) { __result = false; return false; }
        }

        private static bool BeforeAudioWorker(PlayerStats._DyingMpAudio_d__248 __instance,
            bool __runOriginal, ref bool __result, out HookState __state)
        {
            __state = default;
            try { return EnterWorker(__instance, __instance.__4__this, true, __runOriginal, ref __result, out __state); }
            catch (Exception) { __result = false; return false; }
        }

        private static bool EnterWorker(Il2CppObjectBase routine, PlayerStats stats, bool audio,
            bool runOriginal, ref bool result, out HookState state)
        {
            state = default;
            var owner = FindStats(stats);
            if (owner == null) return true;
            result = false;
            try
            {
                owner.RequireCurrent();
                var retained = audio ? owner._audioWorker : owner._resetWorker;
                if (!runOriginal || !Same(owner._stats, stats) || routine == null || routine.WasCollected
                    || retained == null || retained.Routine.WasCollected || retained.Routine.Pointer != routine.Pointer
                    || retained.Epoch != owner._epoch || retained.Ticket != owner._ticket
                    || (audio ? owner._ticket.Action != PlayerLifeSourceAction.StartDeath
                        : !owner._pending || owner._resetFinished || owner._ticket.Action != PlayerLifeSourceAction.ResetFromHeal))
                    return false;
                if (owner.Call != null && owner.Call != (audio ? nameof(PlayerStats.StartDeathRoutine) : nameof(PlayerStats.HealedMp)))
                    throw new InvalidOperationException("Native life worker reentered an unrelated callback.");
                state = owner.Enter(audio ? "AudioWorker" : "ResetWorker");
                return true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static Exception AfterLifeWorker(bool __runOriginal, bool __result, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireCurrent();
                if (!__runOriginal || __exception != null || !owner._projection.SourceNumbersCurrent())
                    throw new InvalidOperationException("Native life worker failed or changed unowned numbers.");
                if (__state.Call == "ResetWorker" && !__result)
                {
                    if (!owner._finalized || !owner._seen.Contains(nameof(PlayerStats.StartResetFromHeal)))
                        throw new InvalidOperationException("Reset iterator ended without native start/reset finalization.");
                    owner._resetFinished = true;
                }
                if (__state.Call == "LayerWorker" && !__result)
                {
                    if (owner._animator.GetLayerWeight(6) != 1f)
                        throw new InvalidOperationException("Native get-up layer did not reach its completed weight.");
                    owner._layerFinished = true;
                }
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }

        private static bool BeforeLayerFactory(PlayerAnimatorControl __instance, int __0, out HookState __state)
        {
            __state = default;
            foreach (var owner in Owners)
            {
                if (!Same(owner._animation, __instance) || owner.Call != nameof(PlayerStats.FinalizeResetFromHeal)) continue;
                try
                {
                    owner.RequireCurrent();
                    if (__0 != 6 || owner._layerWorker?.Epoch == owner._epoch)
                        throw new InvalidOperationException("Unexpected native get-up layer factory.");
                    __state = owner.Enter("LayerFactory");
                    owner._layerFinished = false;
                    return true;
                }
                catch (Exception error) { owner.Fail(error.Message, out _); return false; }
            }
            return true;
        }

        private static Exception AfterLayerFactory(Il2CppSystem.Collections.IEnumerator __result,
            bool __runOriginal, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireCurrent();
                var worker = __result?.TryCast<PlayerAnimatorControl._smoothEnableLayerNew_d__286>();
                if (!__runOriginal || __exception != null || worker == null || worker.WasCollected
                    || worker.__1__state != 0 || worker.l != 6 || !Same(worker.__4__this, owner._animation))
                    throw new InvalidOperationException("Native finalization did not create its exact layer worker.");
                owner._layerWorker = new(worker, owner._epoch, owner._ticket);
                owner._layerWorkers.Add(owner._layerWorker);
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }

        private static bool BeforeLayerWorker(PlayerAnimatorControl._smoothEnableLayerNew_d__286 __instance,
            bool __runOriginal, ref bool __result, out HookState __state)
        {
            __state = default;
            foreach (var owner in Owners)
            {
                NativeWorker worker = null;
                if (__instance != null && !__instance.WasCollected)
                {
                    for (int i = 0; i < owner._layerWorkers.Count; i++)
                    {
                        var candidate = owner._layerWorkers[i];
                        if (candidate.Routine.Pointer != __instance.Pointer) continue;
                        worker = candidate;
                        break;
                    }
                }
                // Other gameplay blends are not life workers. Only this actual factory
                // result is held, including its delayed resumes after retirement.
                if (__instance == null || __instance.WasCollected || worker?.Routine.Pointer != __instance.Pointer) continue;
                __result = false;
                try
                {
                    owner.RequireCurrent();
                    if (!__runOriginal || worker.Epoch != owner._epoch || worker.Ticket != owner._ticket
                        || owner._layerFinished || __instance.l != 6 || !Same(__instance.__4__this, owner._animation)) return false;
                    if (owner.Call != null && owner.Call != nameof(PlayerStats.FinalizeResetFromHeal))
                        throw new InvalidOperationException("Native get-up layer reentered an unrelated callback.");
                    __state = owner.Enter("LayerWorker");
                    return true;
                }
                catch (Exception error) { owner.Fail(error.Message, out _); return false; }
            }
            return true;
        }

        private static bool BeforeRespawnWorker(PlayerStats._MultiplayerRespawnWorker_d__260 __instance, ref bool __result)
        {
            try
            {
                var owner = FindStats(__instance.__4__this);
                if (owner == null) return true;
                __result = false;
                owner.Fail("A native respawn worker has no authoritative new-life ticket.", out _);
                return false;
            }
            catch (Exception) { __result = false; return false; }
        }

        private static bool BeforeLifeRespawnTimer(MultiplayerRespawnTimer __instance)
        {
            foreach (var owner in Owners)
            {
                if (__instance == null || __instance.WasCollected) continue;
                if ((owner._timer != null && owner._timer.Pointer == __instance.Pointer)
                    || (Live(owner._stats) && owner._stats._mpRespawnTimerGui?.Pointer == __instance.Pointer))
                {
                    owner.Fail("Native respawn input/timeout requires authoritative final-death and new-actor ownership.", out _);
                    return false;
                }
            }
            return true;
        }
    }
}
