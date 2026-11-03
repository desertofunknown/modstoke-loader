// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatRebreatherHooks
    {
        internal static bool TryHandoff(StrictCombatClient owner, StrictCombatRebreatherBarrier previous,
            StrictCombatLocalRebreather capture, StrictCombatOwnedRebreatherState owned,
            in StrictCombatBinding binding, in StrictCombatSnapshot state, int frame,
            out StrictCombatRebreatherBarrier barrier, out StrictCombatRebreatherStatus status)
        {
            barrier = null;
            status = StrictCombatRebreatherStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatRebreatherStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatRebreatherStatus.NativeActionObserved;
            if (_historyIncomplete) return false;
            bool published = false;
            try
            {
                status = StrictCombatRebreatherStatus.UnsupportedContext;
                if (owner == null || !owner.IsCurrentRebreatherHandoffContext(previous, capture, owned,
                    in binding, in state, frame)) return false;
                var owners = Volatile.Read(ref _owners);
                int index = Array.IndexOf(owners, previous);
                if (index < 0) return false;
                barrier = new StrictCombatRebreatherBarrier(capture);
                if (barrier.InventoryPointer != previous.InventoryPointer
                    || barrier.PropsPointer != previous.PropsPointer
                    || barrier.ItemPointer != previous.ItemPointer
                    || barrier.ObjectPointer != previous.ObjectPointer
                    || barrier.AnimatorPointer != previous.AnimatorPointer
                    || barrier.ControllerPointer != previous.ControllerPointer
                    || barrier.ModulePointer != previous.ModulePointer
                    || barrier.StatsPointer != previous.StatsPointer
                    || barrier.BreathingPointer != previous.BreathingPointer) return false;
                var next = (StrictCombatRebreatherBarrier[])owners.Clone();
                next[index] = barrier;
                if (!owner.IsCurrentRebreatherHandoffContext(previous, capture, owned,
                    in binding, in state, frame)
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                status = StrictCombatRebreatherStatus.NativeActionObserved;
                if (_historyIncomplete) return false;

                // Replace one entry without a native call or an unowned interval. All
                // retained receivers keep suppression while the old token retires.
                Volatile.Write(ref _owners, next);
                published = true;
                previous.Retire();
                if (_historyIncomplete || barrier.IsRetired)
                {
                    barrier.Retire();
                    status = StrictCombatRebreatherStatus.NativeActionObserved;
                    return false;
                }
                status = StrictCombatRebreatherStatus.Success;
                return true;
            }
            catch (Exception)
            {
                if (published) { barrier?.Retire(); previous?.Retire(); }
                status = StrictCombatRebreatherStatus.Failed;
                return false;
            }
        }
    }
}
