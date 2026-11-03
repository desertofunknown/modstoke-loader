// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatArmourHooks
    {
        internal static bool TryHandoff(StrictCombatClient owner, StrictCombatArmourBarrier previous,
            StrictCombatLocalArmourBinding capture, StrictCombatLifeBarrier life,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot native,
            out StrictCombatArmourBarrier barrier, out StrictCombatArmourStatus status)
        {
            barrier = null;
            status = StrictCombatArmourStatus.WrongThread;
            if (!OnOwnerThread) return false;
            status = StrictCombatArmourStatus.Unavailable;
            if (!_prepared || !_hooksInstalled) return false;
            status = StrictCombatArmourStatus.NativeHistoryIncomplete;
            if (_historyIncomplete) return false;
            status = StrictCombatArmourStatus.Busy;
            if (_breakOwner != null || Volatile.Read(ref _mutationDepth) != 0) return false;
            bool published = false;
            try
            {
                status = StrictCombatArmourStatus.StaleObservation;
                if (owner == null || !owner.IsCurrentArmourHandoffContext(previous, capture, life, owned, native))
                    return false;
                var owners = Volatile.Read(ref _owners);
                int index = Array.IndexOf(owners, previous);
                if (index < 0) return false;
                if (previous.SystemPointer != capture.Original.Native.System.Pointer
                    || previous.StatsPointer != life.Stats.Pointer) return false;
                barrier = new StrictCombatArmourBarrier(capture, life);
                var next = (StrictCombatArmourBarrier[])owners.Clone();
                next[index] = barrier;
                if (!owner.IsCurrentArmourHandoffContext(previous, capture, life, owned, native)
                    || !ReferenceEquals(owners, Volatile.Read(ref _owners))) return false;
                status = StrictCombatArmourStatus.NativeHistoryIncomplete;
                if (_historyIncomplete) return false;
                status = StrictCombatArmourStatus.Busy;
                if (_breakOwner != null || Volatile.Read(ref _mutationDepth) != 0) return false;

                // Replace one registry entry without a native call or an unowned interval.
                // Old tokens retire; callbacks still meet suppression on the same receiver.
                Volatile.Write(ref _owners, next);
                published = true;
                previous.Retire();
                if (_historyIncomplete || barrier.IsRetired)
                {
                    barrier.Retire();
                    status = StrictCombatArmourStatus.NativeHistoryIncomplete;
                    return false;
                }
                status = StrictCombatArmourStatus.Success;
                return true;
            }
            catch (Exception)
            {
                if (published) { barrier?.Retire(); previous?.Retire(); }
                status = StrictCombatArmourStatus.Failed;
                return false;
            }
        }
    }
}
