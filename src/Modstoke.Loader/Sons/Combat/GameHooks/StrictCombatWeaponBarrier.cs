// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Weapon;
using TheForest.Items.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatWeaponStatus
    {
        Success, Unavailable, WrongThread, AlreadyInWorld, NativeActionObserved,
        UnsupportedContext, AlreadyOwned, CapacityReached, Failed
    }

    // Retirement removes input authority, but never releases native mutation suppression.
    internal sealed class StrictCombatWeaponBarrier
    {
        internal StrictCombatWeaponBarrier(StrictCombatLocalWeapon capture) => Capture = capture;
        internal StrictCombatLocalWeapon Capture { get; }
        internal StrictCombatClient Owner => Capture.Owner;
        private int _retired;
        internal bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal void Retire() => Interlocked.Exchange(ref _retired, 1);
    }

    internal static partial class StrictCombatWeaponHooks
    {
        private const int MaximumOwners = 32;
        private static HarmonySupportSnapshot _support;
        private static Action<string> _warning;
        private static int _thread, _acquireFailureReported;
        private static bool _patchAttempted, _hooksInstalled, _prepared;
        private static volatile bool _historyIncomplete;
        private static readonly List<PlayerInventory> Observed = new();
        private static StrictCombatWeaponBarrier[] _owners = Array.Empty<StrictCombatWeaponBarrier>();

        internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _thread, Environment.CurrentManagedThreadId, 0) != 0) return;
            _support = support;
            _warning = warning;
        }

        internal static StrictCombatWeaponStatus PrepareForNextStart()
        {
            if (_thread == 0) return StrictCombatWeaponStatus.Unavailable;
            if (!OnOwnerThread) return StrictCombatWeaponStatus.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (GameRuntime.State.IsInWorld || LocalPlayer.IsInWorld || BoltNetwork.isRunning
                    || (socket != null && !socket.WasCollected && socket.State != UdpKit.UdpSocketState.Shutdown))
                    return StrictCombatWeaponStatus.AlreadyInWorld;
                if (_historyIncomplete) return StrictCombatWeaponStatus.NativeActionObserved;
                if (!InstallHooks()) return StrictCombatWeaponStatus.Unavailable;
                _prepared = true;
                return StrictCombatWeaponStatus.Success;
            }
            catch (Exception) { return StrictCombatWeaponStatus.Failed; }
        }

        internal static bool TryAcquire(StrictCombatLocalWeapon capture,
            out StrictCombatWeaponBarrier barrier, out StrictCombatWeaponStatus status)
        {
            barrier = null;
            status = StrictCombatWeaponStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatWeaponStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatWeaponStatus.UnsupportedContext;
            try
            {
                if (capture == null || !capture.Owner.TryRead(out var binding, out var state)
                    || !capture.IsHeldCurrent(in binding, in state)) return false;
                var owners = Volatile.Read(ref _owners);
                foreach (var existing in owners)
                {
                    if (!Same(existing.Capture.Controller, capture.Controller)) continue;
                    if (!existing.IsRetired && ReferenceEquals(existing.Owner, capture.Owner)
                        && existing.Capture.MatchesNativeObjects(capture))
                    {
                        barrier = existing;
                        status = StrictCombatWeaponStatus.Success;
                        return true;
                    }
                    status = StrictCombatWeaponStatus.AlreadyOwned;
                    return false;
                }
                if (_historyIncomplete || WasObserved(capture.Inventory))
                { status = StrictCombatWeaponStatus.NativeActionObserved; return false; }
                var prepared = FindPreparedController(capture.Controller);
                if (prepared != null && !prepared.CanAdopt(capture)) return false;
                if (owners.Length + PendingEquipmentReservations() - (prepared == null ? 0 : 1) >= MaximumOwners)
                { status = StrictCombatWeaponStatus.CapacityReached; return false; }
                if (!capture.Owner.TryRead(out binding, out state)
                    || !capture.IsHeldCurrent(in binding, in state)
                    || (prepared != null && !prepared.CanAdopt(capture))
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                barrier = new StrictCombatWeaponBarrier(capture);
                var next = new StrictCombatWeaponBarrier[owners.Length + 1];
                Array.Copy(owners, next, owners.Length);
                next[^1] = barrier;
                Volatile.Write(ref _owners, next);
                prepared?.Adopt(barrier);
                if (barrier.IsRetired) return false;
                status = StrictCombatWeaponStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _acquireFailureReported, "weapon barrier acquisition", error);
                status = StrictCombatWeaponStatus.Failed;
                return false;
            }
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _thread;
        private static bool IsObserving => _prepared || Volatile.Read(ref _owners).Length != 0;

        // Pointer identity is safe without reading native fields, including on a retired capture.
        private static bool Same(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase first,
            Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase second)
            => first is not null && second is not null && first.Pointer == second.Pointer;

        private static StrictCombatWeaponBarrier FindController(RangedWeaponController controller)
        {
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.Controller, controller)) return owner;
            return null;
        }

        private static bool OwnsInventory(PlayerInventory inventory)
        {
            foreach (var owner in Volatile.Read(ref _owners))
                if (Same(owner.Capture.Inventory, inventory)) return true;
            return false;
        }

        private static bool IsLocalPistol(RangedWeaponController controller)
            => controller.ItemId == 355 && controller.IsLocalPlayer();

        private static bool IsLocalPistol(RangedWeapon weapon)
            => weapon._weaponItemId == 355 && weapon._isLocalPlayer && !weapon._controlledByAI;

        private static bool AllowUnreadableOwner()
        {
            if (IsObserving) _historyIncomplete = true;
            return Volatile.Read(ref _owners).Length == 0;
        }

        private static bool WasObserved(PlayerInventory inventory)
        {
            foreach (var previous in Observed) if (Same(previous, inventory)) return true;
            return false;
        }

        private static void Remember(PlayerInventory inventory)
        {
            if (!_prepared) return;
            if (!OnOwnerThread || inventory is null || inventory.WasCollected || !inventory)
            { _historyIncomplete = true; return; }
            if (WasObserved(inventory)) return;
            if (Observed.Count == MaximumOwners) { _historyIncomplete = true; return; }
            Observed.Add(inventory);
        }

        private static bool AllowController(RangedWeaponController controller, bool runOriginal, bool observeFire = false)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                if (FindController(controller) != null || FindPreparedController(controller) != null) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (controller is null || controller.WasCollected || !controller) return AllowUnreadableOwner();
                if (!IsLocalPistol(controller)) return true;
                // Unknown local pistol receivers can still mutate the currently owned inventory.
                var inventory = LocalPlayer.Inventory;
                if (inventory is null || inventory.WasCollected || !inventory) return AllowUnreadableOwner();
                if (OwnsInventory(inventory)) return false;
                if (observeFire) Remember(inventory);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowBallisticsWeapon(Ballistics.Weapon weapon, bool runOriginal)
        {
            if (!runOriginal || !IsObserving || weapon is null) return true;
            return !IsRetainedWeapon(weapon.Pointer);
        }

        private static bool IsRetainedWeapon(nint pointer)
        {
            if (pointer == 0) return false;
            // The base ingress is shared by other weapons; suppress only retained identities.
            if (IsPreparedWeapon(pointer)) return true;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.Capture.Weapon is not null && owner.Capture.Weapon.Pointer == pointer) return true;
            return false;
        }

        private static bool AllowWeapon(RangedWeapon weapon, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                if (weapon is not null && IsPreparedWeapon(weapon.Pointer)) return false;
                foreach (var owner in Volatile.Read(ref _owners))
                    if (Same(owner.Capture.Weapon, weapon)) return false;
                if (!OnOwnerThread) return AllowUnreadableOwner();
                if (weapon is null || weapon.WasCollected || !weapon) return AllowUnreadableOwner();
                if (!IsLocalPistol(weapon)) return true;
                var inventory = LocalPlayer.Inventory;
                if (inventory is null || inventory.WasCollected || !inventory) return AllowUnreadableOwner();
                if (OwnsInventory(inventory)) return false;
                Remember(inventory);
                return true;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }

        private static bool AllowInput(RangedWeaponController controller, StrictCombatAction action, bool runOriginal)
        {
            if (!runOriginal || !IsObserving) return true;
            try
            {
                var owner = FindController(controller);
                if (owner == null) return AllowController(controller, true);
                if (OnOwnerThread && !owner.IsRetired)
                    owner.Owner.ObserveWeaponInput(owner, controller, action);
                return false;
            }
            catch (Exception) { return AllowUnreadableOwner(); }
        }
    }
}
