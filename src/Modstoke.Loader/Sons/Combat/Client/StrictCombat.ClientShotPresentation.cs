// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatShotPresentationStatus
    {
        Unavailable, NoShot, Presented, Expired, ContextChanged, UnsupportedContext,
        MutationUncertain, Busy, WrongThread
    }

    public sealed partial class StrictCombatClient
    {
        private const long ShotPresentationLifetimeMs = 1000;
        private StrictCombatLocalShot _pendingWeaponShot, _readyWeaponShot, _presentingShot;
        private bool _presentingWeapon;

        private void RememberWeaponShot(StrictCombatWeaponBarrier owner,
            in StrictCombatBinding binding, in StrictCombatIntent intent, long started)
        {
            if (intent.Action != StrictCombatAction.Fire) return;
            _pendingWeaponShot = new StrictCombatLocalShot(owner, in binding, in intent, started);
        }

        // Only called after the current transport peer's receipt passed request correlation.
        // A public receipt value cannot authorize local effects.
        private void ObserveWeaponReceipt(in StrictCombatReceipt receipt)
        {
            var shot = _pendingWeaponShot;
            if (shot == null || !shot.Matches(in receipt)) return;
            if (receipt.Result == StrictCombatResult.ShotPending) return;
            _pendingWeaponShot = null;
            if (receipt.Result != StrictCombatResult.Accepted || _state.IsRetired) return;
            // Presentation is bounded to the latest accepted shot if its owner stops pumping it.
            // Replacing an unplayed effect never changes a server action or request receipt.
            _readyWeaponShot = shot;
        }

        /// <summary>
        /// Presents the latest accepted shot captured by UpdateLocalWeaponInput, at most once.
        /// Call on the game thread. Does not fire a native projectile, spend ammunition or
        /// forward a native weapon event. Old or changed-context shots are discarded.
        /// </summary>
        public StrictCombatShotPresentationStatus UpdateLocalShotPresentation()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
                return StrictCombatShotPresentationStatus.WrongThread;
            if (_projectingArmour || _projectingAmmunition || _updatingEquipment || _presentingWeapon || _readingWeaponInput || _projectingHealth || _projectingLife || _placingRespawn)
                return StrictCombatShotPresentationStatus.Busy;
            _presentingWeapon = true;
            try
            {
                if (!IsCurrent(out _) || !TryRead(out var binding, out var state))
                { ClearWeaponShots(); return StrictCombatShotPresentationStatus.Unavailable; }
                var shot = _readyWeaponShot;
                if (shot == null) return StrictCombatShotPresentationStatus.NoShot;
                // Consume before native reads or writes. Failure must never replay a partial effect.
                _readyWeaponShot = null;
                long now = Environment.TickCount64;
                if (now < shot.Started || now - shot.Started >= ShotPresentationLifetimeMs)
                    return StrictCombatShotPresentationStatus.Expired;
                if (!IsCurrentShot(shot, in binding, in state))
                    return StrictCombatShotPresentationStatus.ContextChanged;
                _presentingShot = shot;
                int frame = UnityEngine.Time.frameCount;
                return shot.Owner.Capture.PresentShot(this, shot, in binding, in state, frame);
            }
            catch (Exception)
            {
                _presentingShot?.Owner.Retire();
                return StrictCombatShotPresentationStatus.MutationUncertain;
            }
            finally { _presentingShot = null; _presentingWeapon = false; }
        }

        internal bool IsCurrentShotPresentation(StrictCombatLocalShot shot,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, int frame)
        {
            return _presentingWeapon && ReferenceEquals(shot, _presentingShot)
                && Environment.CurrentManagedThreadId == _ownerThreadId
                && IsCurrent(out _)
                && _state.HasBinding && _state.HasSnapshot
                && IsCurrentShot(shot, _state.Binding, _state.Snapshot)
                && binding.SameBinding(_state.Binding)
                && state.Revision == _state.Snapshot.Revision
                && UnityEngine.Time.frameCount == frame
                && shot.Owner.Capture.IsHeldCurrent(in binding, in state, requireIdle: false);
        }

        private bool IsCurrentShot(StrictCombatLocalShot shot,
            in StrictCombatBinding binding, in StrictCombatSnapshot state)
            => ReferenceEquals(shot.Owner, _nativeWeaponBarrier) && !shot.Owner.IsRetired
                && shot.Binding.SameBinding(in binding)
                && shot.Intent.LifeGeneration == state.LifeGeneration
                && state.LifeStage == StrictCombatLifeStage.Alive && state.OwnsPistol && state.Equipped;

        private void ClearWeaponShots()
        {
            _pendingWeaponShot = null;
            _readyWeaponShot = null;
        }
    }

    internal sealed class StrictCombatLocalShot
    {
        internal StrictCombatLocalShot(StrictCombatWeaponBarrier owner,
            in StrictCombatBinding binding, in StrictCombatIntent intent, long started)
        {
            Owner = owner;
            Binding = binding;
            Intent = intent;
            Started = started;
        }

        internal StrictCombatWeaponBarrier Owner { get; }
        internal StrictCombatBinding Binding { get; }
        internal StrictCombatIntent Intent { get; }
        internal long Started { get; }

        internal bool Matches(in StrictCombatReceipt receipt)
            => receipt.Epoch == Binding.Epoch && receipt.Sequence == Intent.Sequence
                && receipt.Action == StrictCombatAction.Fire
                && receipt.RequestLifeGeneration == Intent.LifeGeneration;
    }
}
