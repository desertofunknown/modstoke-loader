// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatRebreatherStatus
    {
        Success, Unavailable, WrongThread, AlreadyInWorld, NativeActionObserved,
        UnsupportedContext, AlreadyOwned, CapacityReached, Failed
    }

    internal sealed class StrictCombatRebreatherBarrier
    {
        internal StrictCombatRebreatherBarrier(StrictCombatLocalRebreather capture)
        {
            Capture = capture;
            InventoryPointer = capture.Inventory.Pointer;
            PropsPointer = capture.Props.Pointer;
            ItemPointer = capture.Item.Pointer;
            ObjectPointer = capture.ItemObject.Pointer;
            AnimatorPointer = capture.Life.Stats._animator.Pointer;
            ControllerPointer = capture.Controller.Pointer;
            ModulePointer = capture.Module.Pointer;
            StatsPointer = capture.Life.Stats.Pointer;
            BreathingPointer = capture.Breathing.Pointer;
        }
        internal StrictCombatLocalRebreather Capture { get; }
        internal nint InventoryPointer { get; }
        internal nint PropsPointer { get; }
        internal nint ItemPointer { get; }
        internal nint ObjectPointer { get; }
        internal nint AnimatorPointer { get; }
        internal nint ControllerPointer { get; }
        internal nint ModulePointer { get; }
        internal nint StatsPointer { get; }
        internal nint BreathingPointer { get; }
        internal StrictCombatClient Owner => Capture.Owner;
        private int _retired;
        internal bool IsRetired => Volatile.Read(ref _retired) != 0;
        internal void Retire() => Interlocked.Exchange(ref _retired, 1);
    }

    internal static partial class StrictCombatRebreatherHooks
    {
        private const int MaximumOwners = 32;
        private static HarmonySupportSnapshot _support;
        private static Action<string> _warning;
        private static int _thread, _failureLogged, _acquireFailureReported;
        private static bool _patchAttempted, _hooksInstalled, _prepared;
        private static volatile bool _historyIncomplete;
        private static readonly List<PlayerInventory> Observed = new();
        private static StrictCombatRebreatherBarrier[] _owners = Array.Empty<StrictCombatRebreatherBarrier>();

        internal static void Initialize(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.CompareExchange(ref _thread, Environment.CurrentManagedThreadId, 0) != 0) return;
            _support = support;
            _warning = warning;
        }

        internal static StrictCombatRebreatherStatus PrepareForNextStart()
        {
            if (_thread == 0) return StrictCombatRebreatherStatus.Unavailable;
            if (!OnOwnerThread) return StrictCombatRebreatherStatus.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (GameRuntime.State.IsInWorld || LocalPlayer.IsInWorld || BoltNetwork.isRunning
                    || (socket != null && !socket.WasCollected && socket.State != UdpKit.UdpSocketState.Shutdown))
                    return StrictCombatRebreatherStatus.AlreadyInWorld;
                if (_historyIncomplete) return StrictCombatRebreatherStatus.NativeActionObserved;
                if (!InstallHooks()) return StrictCombatRebreatherStatus.Unavailable;
                _prepared = true;
                return StrictCombatRebreatherStatus.Success;
            }
            catch (Exception) { return StrictCombatRebreatherStatus.Failed; }
        }

        internal static bool TryAcquire(StrictCombatLocalRebreather capture,
            out StrictCombatRebreatherBarrier barrier, out StrictCombatRebreatherStatus status)
        {
            barrier = null;
            status = StrictCombatRebreatherStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatRebreatherStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatRebreatherStatus.UnsupportedContext;
            try
            {
                if (capture == null || !capture.Owner.TryRead(out var binding, out var state)
                    || !capture.IsCurrent(in binding, in state)) return false;
                var candidate = new StrictCombatRebreatherBarrier(capture);
                var owners = Volatile.Read(ref _owners);
                foreach (var existing in owners)
                {
                    if (existing.InventoryPointer != candidate.InventoryPointer
                        && existing.PropsPointer != candidate.PropsPointer
                        && existing.ItemPointer != candidate.ItemPointer
                        && existing.ObjectPointer != candidate.ObjectPointer
                        && existing.AnimatorPointer != candidate.AnimatorPointer
                        && existing.ControllerPointer != candidate.ControllerPointer
                        && existing.ModulePointer != candidate.ModulePointer
                        && existing.StatsPointer != candidate.StatsPointer
                        && existing.BreathingPointer != candidate.BreathingPointer) continue;
                    if (!existing.IsRetired && existing.AnimatorPointer == candidate.AnimatorPointer
                        && existing.Capture.MatchesNativeObjects(capture))
                    {
                        barrier = existing;
                        status = StrictCombatRebreatherStatus.Success;
                        return true;
                    }
                    status = StrictCombatRebreatherStatus.AlreadyOwned;
                    return false;
                }
                if (_historyIncomplete || WasObserved(capture.Inventory))
                { status = StrictCombatRebreatherStatus.NativeActionObserved; return false; }
                if (owners.Length >= MaximumOwners)
                { status = StrictCombatRebreatherStatus.CapacityReached; return false; }
                if (!capture.Owner.TryRead(out binding, out state) || !capture.IsCurrent(in binding, in state)
                    || candidate.AnimatorPointer != capture.Life.Stats._animator.Pointer
                    || _historyIncomplete || WasObserved(capture.Inventory)
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                var next = new StrictCombatRebreatherBarrier[owners.Length + 1];
                Array.Copy(owners, next, owners.Length);
                next[^1] = candidate;
                Volatile.Write(ref _owners, next);
                barrier = candidate;
                status = StrictCombatRebreatherStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                StrictCombatDiagnostics.ReportOnce(ref _acquireFailureReported, "rebreather barrier acquisition", error);
                status = StrictCombatRebreatherStatus.Failed;
                return false;
            }
        }

        private static bool OnOwnerThread => Environment.CurrentManagedThreadId == _thread;
        private static bool IsObserving => _prepared || Volatile.Read(ref _owners).Length != 0;

        // Retired owners keep suppression. Shared native bodies must compare only
        // receiver addresses, without casting unrelated game objects to volume modules.
        private static bool BlocksModulePointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.ModulePointer == pointer) return true;
            return false;
        }

        private static bool BlocksControllerPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.ControllerPointer == pointer) return true;
            return false;
        }

        private static bool BlocksStatsPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.StatsPointer == pointer) return true;
            return false;
        }

        private static bool BlocksBreathingPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.BreathingPointer == pointer) return true;
            return false;
        }

        private static bool OwnsInventory(PlayerInventory inventory)
        {
            if (inventory is null) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.InventoryPointer == inventory.Pointer) return true;
            return false;
        }

        private static bool OwnsProps(InventoryProps props)
        {
            if (props is null) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.PropsPointer == props.Pointer) return true;
            return false;
        }

        private static bool BlocksItemPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.ItemPointer == pointer) return true;
            return false;
        }

        private static bool BlocksObjectPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.ObjectPointer == pointer) return true;
            return false;
        }

        private static bool BlocksAnimatorPointer(nint pointer)
        {
            if (pointer == 0) return false;
            foreach (var owner in Volatile.Read(ref _owners))
                if (owner.AnimatorPointer == pointer) return true;
            return false;
        }

        private static bool WasObserved(PlayerInventory inventory)
        {
            foreach (var previous in Observed)
                if (previous.Pointer == inventory.Pointer) return true;
            return false;
        }

        private static void Remember(PlayerInventory inventory)
        {
            if (!_prepared) return;
            if (!OnOwnerThread || inventory is null || inventory.WasCollected || !inventory)
            { _historyIncomplete = true; return; }
            if (WasObserved(inventory)) return;
            if (Observed.Count >= MaximumOwners) { _historyIncomplete = true; return; }
            Observed.Add(inventory);
        }

        private static bool AllowUnreadableOwner()
        {
            if (IsObserving) _historyIncomplete = true;
            return Volatile.Read(ref _owners).Length == 0;
        }

        private static void OwnershipFailure(Exception error)
        {
            _historyIncomplete = true;
            foreach (var owner in Volatile.Read(ref _owners)) owner.Retire();
            if (Interlocked.Exchange(ref _failureLogged, 1) != 0) return;
            try { _warning?.Invoke("Strict combat rebreather ownership is unavailable: " + error.Message); }
            catch { }
        }
    }
}
