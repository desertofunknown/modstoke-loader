// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Weapon;
using GameInput = global::Sons.Input.InputSystem;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatWeaponInputStatus
    {
        Unavailable, Ready, Buffered, Queued, Busy, Expired, ContextChanged, WrongThread, BarrierRequired
    }

    public sealed partial class StrictCombatClient
    {
        private const long WeaponInputLifetimeMs = 250;
        private StrictCombatWeaponBarrier _nativeWeaponBarrier;
        private bool _readingWeaponInput, _hasWeaponInput;
        private StrictCombatWeaponBarrier _weaponInputOwner;
        private StrictCombatBinding _weaponInputBinding;
        private ulong _weaponInputLife;
        private StrictCombatAction _weaponInputAction;
        private UnityEngine.Vector3 _weaponInputDirection;
        private long _weaponInputStarted;
        private int _lastFireInputFrame = -1, _lastReloadInputFrame = -1;
        private StrictCombatWeaponInputStatus _weaponInputStatus;
        private static int _weaponAcquireFailureReported, _weaponInputFailureReported;

        /// <summary>Last local input result. Queued does not acknowledge a server action.</summary>
        public StrictCombatWeaponInputStatus WeaponInputStatus => _weaponInputStatus;

        /// <summary>Prepares local pistol interception from the menu before world/network startup.</summary>
        public static StrictCombatWeaponStatus PrepareNativeWeaponBarrierForNextStart()
            => StrictCombatWeaponHooks.PrepareForNextStart();

        /// <summary>
        /// Acquires an already equipped idle pistol for the exact owned local life. Does not
        /// equip an item, copy native ammo into the server ledger, or enable a combat mode.
        /// </summary>
        public bool TryAcquireNativeWeaponBarrier(out StrictCombatWeaponStatus status)
        {
            status = StrictCombatWeaponStatus.WrongThread;
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return false;
            status = StrictCombatWeaponStatus.UnsupportedContext;
            if (_readingWeaponInput || _projectingHealth || _projectingLife || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment) return false;
            _readingWeaponInput = true;
            try
            {
                var life = _nativeLifeBarrier;
                if (life == null || life.IsRetired || !ReferenceEquals(life.Owner, this)
                    || !ReferenceEquals(life.Player, _localPlayer)
                    || !TryRead(out var binding, out var state)
                    || state.LifeGeneration != life.LifeGeneration
                    || !StrictCombatLocalWeapon.TryCapture(this, life, in binding, in state, out var capture))
                    return false;
                if (!StrictCombatWeaponHooks.TryAcquire(capture, out var barrier, out status)) return false;
                if (!ReferenceEquals(barrier, _nativeWeaponBarrier))
                {
                    _nativeWeaponBarrier?.Retire();
                    ClearWeaponInput();
                    ClearWeaponShots();
                    _nativeWeaponBarrier = barrier;
                }
                _weaponInputStatus = StrictCombatWeaponInputStatus.Ready;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _weaponAcquireFailureReported, "weapon acquisition", error);
                status = StrictCombatWeaponStatus.Failed;
                return false;
            }
            finally { _readingWeaponInput = false; }
        }

        /// <summary>
        /// Discards captured fire/reload input that has not been queued. Call on the game
        /// thread when setup or another local wait ends. Does not cancel a protocol request,
        /// release native suppression or change server ammunition.
        /// </summary>
        public bool DiscardLocalWeaponInput()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _readingWeaponInput
                || _projectingHealth || _projectingLife || _placingRespawn || _presentingWeapon
                || _projectingArmour || _projectingAmmunition || _updatingEquipment) return false;
            ClearWeaponInput();
            _weaponInputStatus = _nativeWeaponBarrier == null || _nativeWeaponBarrier.IsRetired
                ? StrictCombatWeaponInputStatus.Unavailable : StrictCombatWeaponInputStatus.Ready;
            return true;
        }

        // Called only by the two native input hooks. Mutation callbacks never create intents.
        internal void ObserveWeaponInput(StrictCombatWeaponBarrier barrier,
            RangedWeaponController controller, StrictCombatAction action)
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _readingWeaponInput
                || _projectingHealth || _projectingLife || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment
                || !ReferenceEquals(barrier, _nativeWeaponBarrier) || barrier.IsRetired
                || controller == null || controller.Pointer != barrier.Capture.Controller.Pointer
                || (action != StrictCombatAction.Fire && action != StrictCombatAction.Reload)) return;
            _readingWeaponInput = true;
            try
            {
                if (!IsCurrent(out long now) || !TryRead(out var binding, out var state)) return;
                int frame = UnityEngine.Time.frameCount;
                if ((action == StrictCombatAction.Fire ? _lastFireInputFrame : _lastReloadInputFrame) == frame) return;
                bool pressed = action == StrictCombatAction.Fire
                    ? GameInput.GetButtonDown(GameInput.Actions.PrimaryAction)
                    : GameInput.GetButtonDown(GameInput.Actions.Reload);
                if (!pressed) return;
                if (action == StrictCombatAction.Fire) _lastFireInputFrame = frame;
                else _lastReloadInputFrame = frame;
                if (!barrier.Capture.CanReadInput(action, in binding, in state)) return;
                if (_hasWeaponInput && !WeaponInputMatches(in binding, in state, now)) ClearWeaponInput();
                if (_hasWeaponInput) return; // Preserve the first pending action; never extend its deadline.
                UnityEngine.Vector3 direction = default;
                if (action == StrictCombatAction.Fire
                    && !barrier.Capture.TryReadFireDirection(in binding, in state, out direction)) return;
                if (frame != UnityEngine.Time.frameCount || barrier.IsRetired
                    || !barrier.Capture.CanReadInput(action, in binding, in state)) return;
                _weaponInputOwner = barrier;
                _weaponInputBinding = binding;
                _weaponInputLife = state.LifeGeneration;
                _weaponInputAction = action;
                _weaponInputDirection = direction;
                _weaponInputStarted = now;
                _hasWeaponInput = true;
                _weaponInputStatus = StrictCombatWeaponInputStatus.Buffered;
            }
            catch (Exception)
            {
                ClearWeaponInput();
                barrier.Retire();
                _weaponInputStatus = StrictCombatWeaponInputStatus.Unavailable;
            }
            finally { _readingWeaponInput = false; }
        }

        /// <summary>
        /// Queues one captured fire/reload input through the ordinary life-bound protocol.
        /// Call on the game thread before Update. Waiting for another receipt is bounded to
        /// 250 ms; input is never retargeted into another life or held weapon.
        /// </summary>
        public StrictCombatWeaponInputStatus UpdateLocalWeaponInput()
        {
            if (Environment.CurrentManagedThreadId != _ownerThreadId) return StrictCombatWeaponInputStatus.WrongThread;
            if (_readingWeaponInput || _projectingHealth || _projectingLife || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment)
                return StrictCombatWeaponInputStatus.Busy;
            _readingWeaponInput = true;
            try
            {
                if (!IsCurrent(out long now) || !TryRead(out var binding, out var state))
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.Unavailable; }
                var barrier = _nativeWeaponBarrier;
                if (barrier == null || barrier.IsRetired)
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.BarrierRequired; }
                if (!barrier.Capture.IsHeldCurrent(in binding, in state))
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.ContextChanged; }
                if (!_hasWeaponInput) return _weaponInputStatus = StrictCombatWeaponInputStatus.Ready;
                if (now < _weaponInputStarted || now - _weaponInputStarted >= WeaponInputLifetimeMs)
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.Expired; }
                if (!WeaponInputMatches(in binding, in state, now)
                    || !barrier.Capture.CanReadInput(_weaponInputAction, in binding, in state))
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.ContextChanged; }
                var direction = _weaponInputDirection;
                // Keep the final check and queue entirely in managed state. A second native
                // context refresh here could replace the life after validating the copied input.
                if (!_state.HasBinding || !_state.HasSnapshot
                    || !WeaponInputMatches(_state.Binding, _state.Snapshot, now))
                { ClearWeaponInput(); return _weaponInputStatus = StrictCombatWeaponInputStatus.ContextChanged; }
                if (!_state.TryBegin(_weaponInputAction, now, direction.x, direction.y, direction.z))
                    return _weaponInputStatus = StrictCombatWeaponInputStatus.Busy;
                RememberWeaponShot(barrier, in binding, _state.PendingIntent, _weaponInputStarted);
                ClearWeaponInput();
                return _weaponInputStatus = StrictCombatWeaponInputStatus.Queued;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _weaponInputFailureReported, "weapon input", error);
                ClearWeaponInput();
                _nativeWeaponBarrier?.Retire();
                return _weaponInputStatus = StrictCombatWeaponInputStatus.Unavailable;
            }
            finally { _readingWeaponInput = false; }
        }

        private bool WeaponInputMatches(in StrictCombatBinding binding, in StrictCombatSnapshot state, long now)
            => _hasWeaponInput && _weaponInputOwner != null
                && ReferenceEquals(_weaponInputOwner, _nativeWeaponBarrier) && !_weaponInputOwner.IsRetired
                && now >= _weaponInputStarted && now - _weaponInputStarted < WeaponInputLifetimeMs
                && binding.SameBinding(in _weaponInputBinding)
                && state.LifeGeneration == _weaponInputLife && state.LifeStage == StrictCombatLifeStage.Alive
                && state.OwnsPistol && state.Equipped;

        private void ClearWeaponInput()
        {
            _hasWeaponInput = false;
            _weaponInputOwner = null;
            _weaponInputBinding = default;
            _weaponInputLife = 0;
            _weaponInputAction = default;
            _weaponInputDirection = default;
            _weaponInputStarted = 0;
        }

        internal bool TryReadWeaponForStash(StrictCombatLifeBarrier life,
            TheForest.Items.Inventory.PlayerInventory inventory, global::Sons.Inventory.ItemInstance item,
            out StrictCombatLocalWeapon capture)
        {
            capture = null;
            if (Environment.CurrentManagedThreadId != _ownerThreadId || !_projectingLife
                || _readingWeaponInput || _placingRespawn || _presentingWeapon || _projectingArmour || _projectingAmmunition || _updatingEquipment) return false;
            var barrier = _nativeWeaponBarrier;
            if (barrier == null || barrier.IsRetired || !ReferenceEquals(life, _nativeLifeBarrier)
                || !ReferenceEquals(life, barrier.Capture.Life)
                || inventory is null || item is null
                || inventory.Pointer != barrier.Capture.Inventory.Pointer
                || item.Pointer != barrier.Capture.Item.Pointer
                || !TryRead(out var binding, out var state)
                || !barrier.Capture.IsHeldForStash(in binding, in state)) return false;
            capture = barrier.Capture;
            return true;
        }

        private void ReleaseLocalWeapon()
        {
            _equipmentOperation?.RequestCancellation();
            _nativeWeaponBarrier?.Retire();
            ClearWeaponInput();
            ClearWeaponShots();
            _weaponInputStatus = StrictCombatWeaponInputStatus.Unavailable;
        }
    }
}
