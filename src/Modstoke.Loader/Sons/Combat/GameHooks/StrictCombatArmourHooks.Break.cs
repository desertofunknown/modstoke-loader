// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using global::Sons.Wearable;
using global::Sons.Wearable.Armour;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatArmourHooks
    {
        private static StrictCombatArmourBarrier _breakOwner;
        private static int _breakSlot;
        private static bool _breakConsumed;

        // This authorizes one exact entry, not nested calls made by its callbacks.
        internal static bool TryBreak(StrictCombatArmourBarrier barrier, int resourceIndex)
        {
            if (!OnOwnerThread || !_prepared || !_hooksInstalled || _historyIncomplete
                || _breakOwner != null || Volatile.Read(ref _mutationDepth) != 0
                || barrier == null || barrier.IsRetired
                || !barrier.Capture.InitialState.TryGetPiece(resourceIndex, out var resource)) return false;
            if (Array.IndexOf(Volatile.Read(ref _owners), barrier) < 0
                || !barrier.TryBeginBreak(resourceIndex)) return false;
            _breakOwner = barrier;
            _breakSlot = resource.Slot;
            _breakConsumed = false;
            try
            {
                bool result = barrier.System.TryUnequipSlot((WearableSlots)resource.Slot, true);
                return result && _breakConsumed && !_historyIncomplete && !barrier.IsRetired
                    && Volatile.Read(ref _mutationDepth) == 0;
            }
            catch (Exception) { barrier.Retire(); return false; }
            finally
            {
                _breakOwner = null;
                _breakConsumed = false;
            }
        }

        private static bool EnterUnequip(PlayerArmourSystem system, WearableSlots slot, bool destroy,
            bool runOriginal, out bool entered)
        {
            entered = false;
            if (!runOriginal) return true;
            var owner = _breakOwner;
            if (OnOwnerThread && owner != null && !_breakConsumed && !_historyIncomplete
                && !owner.IsRetired && system is not null && system.Pointer == owner.SystemPointer
                && destroy && (int)slot == _breakSlot && Volatile.Read(ref _mutationDepth) == 0)
            {
                _breakConsumed = true;
                Interlocked.Increment(ref _mutationDepth);
                entered = true;
                return true;
            }
            return EnterMutation(system, runOriginal, out entered);
        }
    }
}
