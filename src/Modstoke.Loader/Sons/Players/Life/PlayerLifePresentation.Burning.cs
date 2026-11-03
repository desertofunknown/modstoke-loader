// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerLifePresentation
    {
        private PlayerLifeBurnFrame _burnReceipt;
        private bool _burnRequired, _burnReceived, _burnExecuted;
        private int _burnStopCalls, _burnStateCalls, _burnPublications, _burnFxCalls;
        private float _nativeDeathStartedAt, _burnTimeBefore;
        private PlayerEffects _burnEffects;
        private GameObject _burnObject;
        private int _burnEffectsId, _burnObjectId;
        private bool _burnFxPending, _burnFxEntry;
        private float _burnFxDue;
        private PlayerLifeFrame _burnFxTicket;
        internal bool HasPendingForcedWork { get; set; }
        internal bool HasPendingNativeWork => _pending || _burnFxPending || HasPendingForcedWork;

        private void RequireBurningStart(in PlayerLifeFrame ticket, in PlayerLifeValuesFrame values)
        {
            bool burning = (values.Values.Flags & PlayerStatValues.Burning) != 0;
            if (_stats._isOnFire != burning || !float.IsFinite(ticket.BurnTime))
                throw new InvalidOperationException("Death burning does not match the retained source effect.");
            if (!burning) return;
            if (_burnFxPending)
                throw new InvalidOperationException("A new fire cannot replace an unfinished native fire tail.");
            _burnEffects = _stats._playerEffects;
            _burnObject = _burnEffects?._onFireEffect;
            if (!Live(_burnEffects)
                || _burnEffects.IsInvoking(nameof(PlayerEffects.DisableFireObject)))
                throw new InvalidOperationException("Burning death requires its actual source effect receiver without an older disable invoke.");
            _burnEffectsId = _burnEffects.GetInstanceID(); _burnObjectId = Live(_burnObject) ? _burnObject.GetInstanceID() : 0;
        }

        private void BeginBurningPresentation()
        {
            _burnRequired = _ticket.Action == PlayerLifeSourceAction.StartDeath
                && (_values.Values.Flags & PlayerStatValues.Burning) != 0;
            _burnReceipt = default; _burnReceived = false; _burnExecuted = false;
            _burnStopCalls = _burnStateCalls = _burnPublications = _burnFxCalls = 0;
            // A completed StopBurning's two-second native visual tail belongs to its
            // own ticket and survives a later same-life get-up command.
        }

        internal bool TryAcceptBurning(in PlayerLifeBurnFrame receipt, out string detail)
        {
            detail = "Burning delivery requires the exact pending native death action.";
            if (IsDisposed || FailureReason != null || Environment.CurrentManagedThreadId != _thread
                || !_pending || !_burnRequired || receipt.Kind != PlayerLifeBurnMessage.Commit
                || !receipt.MatchesCommand(_ticket)) return false;
            if (_burnReceived && _burnReceipt != receipt) return Fail("A retained burning commit changed.", out detail);
            _burnReceipt = receipt; _burnReceived = true;
            detail = "Exact server burning boundary retained; no native completion inferred.";
            return true;
        }

        internal bool TryReadBurningCompletion(out PlayerLifeBurnFrame receipt)
        {
            receipt = default;
            if (!_burnRequired || !_burnExecuted || CompletedSequence != _ticket.Sequence) return false;
            receipt = _burnReceipt with { Kind = PlayerLifeBurnMessage.Acknowledgement };
            return true;
        }

        private bool BurningPromptReady() => !_burnRequired || (_burnReceived
            && (_burnExecuted || (_burnReceipt.Cause == PlayerLifeBurnCause.DeathPrompt
                && Time.time - _nativeDeathStartedAt >= _burnReceipt.Elapsed)));

        private void PumpBurningExpiry()
        {
            if (!_burnRequired || !_nativeStarted || !_burnReceived || _burnExecuted
                || _burnReceipt.Cause != PlayerLifeBurnCause.Expired
                || Time.time - _nativeDeathStartedAt < _burnReceipt.Elapsed) return;
            InvokeNative(nameof(PlayerStats.StopBurning), _stats.StopBurning);
        }

        private void RequireBurningEffects()
        {
            RequireCurrent();
            if (!Same(_burnEffects, _stats._playerEffects) || _burnEffects.GetInstanceID() != _burnEffectsId
                || (_burnObjectId == 0 ? Live(_burnEffects._onFireEffect)
                    : !Same(_burnObject, _burnEffects._onFireEffect) || _burnObject.GetInstanceID() != _burnObjectId))
                throw new InvalidOperationException("Native burning lost its retained source effect identity.");
        }

        private void BeforeNativeBurning()
        {
            if (!_projection.SourceNumbersCurrent())
                throw new InvalidOperationException("Native burning lost its exact projected numeric boundary.");
            _burnStopCalls++;
            _burnTimeBefore = _stats._burnTime;
            if (!_burnRequired || _burnExecuted)
            {
                if (_stats._isOnFire) throw new InvalidOperationException("An unowned ignition reached native death cleanup.");
                return;
            }
            RequireBurningEffects();
            bool expiryEntry = _calls.Count == 1 && _entry == nameof(PlayerStats.StopBurning);
            if (!_burnReceived || !_stats._isOnFire || _stats._burnTime != _ticket.BurnTime
                || _burnStopCalls != 1 || (expiryEntry != (_burnReceipt.Cause == PlayerLifeBurnCause.Expired))
                || Time.time - _nativeDeathStartedAt < _burnReceipt.Elapsed)
                throw new InvalidOperationException("Native burning does not match its exact server-timed stop receipt.");
            // Only this timer belongs to the receipt. All other numeric storage must
            // still equal the immutable life snapshot throughout the native graph.
            _stats._burnTime = _burnReceipt.BurnTimeBefore;
        }

        private void AfterNativeBurning()
        {
            if (!_burnRequired || _burnExecuted)
            {
                if (_stats._isOnFire || _stats._burnTime != _burnTimeBefore)
                    throw new InvalidOperationException("An inactive native fire cleanup changed numeric storage.");
                return;
            }
            RequireBurningEffects();
            if (_stats._isOnFire || _stats._burnTime != 0 || _burnStateCalls != 1
                || _burnPublications != 1 || _burnFxCalls != 1 || _burnFxPending != (_burnObjectId != 0)
                || _stats.IsInvoking(nameof(PlayerStats.HitFire))
                || !_projection.TryCompleteLifeBurning(this, _ticket, _burnReceipt))
                throw new InvalidOperationException("Native StopBurning did not complete its exact state, effect and numeric receipt.");
            _burnExecuted = true;
        }

        private static PlayerLifePresentation FindBurningEffects(PlayerEffects effects)
        {
            if (effects == null || effects.WasCollected) return null;
            foreach (var owner in Owners)
                if (owner._burnEffects?.Pointer == effects.Pointer) return owner;
            return null;
        }

        private static bool BeforeBurningFx(PlayerEffects __instance, bool __0, bool __runOriginal, out HookState __state)
        {
            __state = default;
            var owner = FindBurningEffects(__instance);
            if (owner == null) return true;
            if (owner.Call != nameof(PlayerStats.SetOnFireState)) return true;
            try
            {
                owner.RequireBurningEffects();
                if (!__runOriginal || __0 || owner.Call != nameof(PlayerStats.SetOnFireState)
                    || !owner._burnRequired || !owner._burnReceived || owner._burnExecuted || ++owner._burnFxCalls != 1)
                    throw new InvalidOperationException("Unowned native fire effect callback during retained life.");
                __state = owner.Enter("BurningFx");
                return true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static Exception AfterBurningFx(bool __runOriginal, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireBurningEffects();
                if (!__runOriginal || __exception != null || owner._burnFxPending
                    || owner._burnEffects.IsInvoking(nameof(PlayerEffects.DisableFireObject)) != (owner._burnObjectId != 0))
                    throw new InvalidOperationException("Native burning did not retain its actual delayed effect shutdown.");
                if (owner._burnObjectId == 0) return __exception;
                // ToggleBurningFx invokes the Unity icall directly. Replace only this
                // named continuation with the same native callback and an exact ticket.
                owner._burnEffects.CancelInvoke(nameof(PlayerEffects.DisableFireObject));
                if (owner._burnEffects.IsInvoking(nameof(PlayerEffects.DisableFireObject)))
                    throw new InvalidOperationException("The native fire tail could not be retained exclusively.");
                owner._burnFxTicket = owner._ticket;
                owner._burnFxDue = Time.time + 2f; owner._burnFxPending = true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }

        private void PumpBurningTail()
        {
            if (!_burnFxPending || Time.time < _burnFxDue) return;
            RequireBurningEffects();
            _burnFxEntry = true;
            try { _burnEffects.DisableFireObject(); }
            finally { _burnFxEntry = false; }
            if (_burnFxPending) throw new InvalidOperationException("The retained native fire tail did not complete.");
        }

        private static bool BeforeDisableFire(PlayerEffects __instance, bool __runOriginal, out HookState __state)
        {
            __state = default;
            var owner = FindBurningEffects(__instance);
            if (owner == null) return true;
            if (!owner._burnFxPending && !owner.IsDisposed) return true;
            try
            {
                owner.RequireBurningEffects();
                if (!__runOriginal || !owner._burnFxEntry || !owner._burnFxPending || owner.NativeCallbackActive
                    || owner._burnFxTicket.MigrationId != owner._bridge.Plan.Id
                    || owner._burnFxTicket.LifeGeneration != owner._lifeGeneration
                    || owner._burnFxTicket.Sequence > owner._ticket.Sequence || Time.time < owner._burnFxDue)
                    return false;
                __state = owner.Enter("DisableFire");
                return true;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); return false; }
        }

        private static Exception AfterDisableFire(bool __runOriginal, Exception __exception, HookState __state)
        {
            var owner = __state.Owner;
            if (owner == null) return __exception;
            try
            {
                owner.RequireBurningEffects();
                if (!__runOriginal || __exception != null || owner._burnObject.activeSelf
                    || !owner._projection.SourceNumbersCurrent())
                    throw new InvalidOperationException("Native fire tail failed its retained effect/numeric readback.");
                owner._burnFxPending = false;
            }
            catch (Exception error) { owner.Fail(error.Message, out _); }
            finally { owner.Leave(__state); }
            return __exception;
        }
    }
}
