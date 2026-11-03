// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatArmourBindingStatus
    {
        Success, Unavailable, WrongThread, StateUnavailable, Unprovisioned, LifeChanged,
        StaleObservation, NativeStateUnavailable, LayoutMismatch, ResourceMismatch,
        AlreadyBroken, Retired, LimitReached, Failed
    }

    /// <summary>
    /// Retains the native identities associated with accepted owned armour resources for one life.
    /// This binding grants no resources, golden eligibility, or authority to mutate native state.
    /// </summary>
    public sealed class StrictCombatLocalArmourBinding
    {
        private int _retired;
        private uint _completedBreakSlots;

        private StrictCombatLocalArmourBinding(StrictCombatClient owner,
            StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot native)
        {
            Owner = owner;
            InitialState = owned;
            Original = native;
        }

        internal StrictCombatClient Owner { get; }
        internal StrictCombatArmourSnapshot Original { get; }
        internal StrictCombatOwnedArmourState InitialState { get; }
        public bool IsRetired => Volatile.Read(ref _retired) != 0;
        public StrictCombatBinding Binding => Original.Binding;
        public ulong LifeGeneration => Original.ServerState.LifeGeneration;
        public int ResourceCount => InitialState.Count;

        internal static bool TryCreate(StrictCombatClient owner, StrictCombatOwnedArmourState owned,
            StrictCombatArmourSnapshot native, out StrictCombatLocalArmourBinding binding,
            out StrictCombatArmourBindingStatus status)
        {
            binding = null;
            status = StrictCombatArmourBindingStatus.Unavailable;
            if (owner == null) return false;
            if (!ValidateObservation(owner, owned, native, out status)) return false;
            status = StrictCombatArmourBindingStatus.LifeChanged;
            if (native.ServerState.LifeStage != StrictCombatLifeStage.Alive) return false;
            if (!ValidateResources(owned, native, requireUnbroken: true, completedBreakSlots: 0, out status)
                || !ValidateNativeIdentities(native, native, owned, completedBreakSlots: 0, out status)) return false;
            binding = new StrictCombatLocalArmourBinding(owner, owned, native);
            status = StrictCombatArmourBindingStatus.Success;
            return true;
        }

        internal bool Matches(StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot current,
            out StrictCombatArmourBindingStatus status)
            => Matches(owned, current, _completedBreakSlots, out status);

        internal bool CanRetainNativeForNextLife(StrictCombatArmourSnapshot current,
            out StrictCombatArmourBindingStatus status)
        {
            try
            {
                status = StrictCombatArmourBindingStatus.Retired;
                if (IsRetired) return false;
                if (!ValidateObservation(Owner, InitialState, current, out status)) return false;
                status = StrictCombatArmourBindingStatus.LifeChanged;
                if (LifeGeneration == 0 || LifeGeneration == ulong.MaxValue
                    || current.ServerState.LifeStage != StrictCombatLifeStage.Alive
                    || current.ServerState.LifeGeneration != LifeGeneration + 1) return false;
                status = StrictCombatArmourBindingStatus.StaleObservation;
                if (!ReferenceEquals(Original.Player.Player, current.Player.Player)
                    || !Binding.SameBinding(current.Binding)) return false;

                status = StrictCombatArmourBindingStatus.LayoutMismatch;
                if (!StrictCombatOwnedArmourState.SameFloat(Original.GoldenGeneralMultiplier, current.GoldenGeneralMultiplier)
                    || !StrictCombatOwnedArmourState.SameFloat(Original.GoldenDemonicMultiplier, current.GoldenDemonicMultiplier))
                    return false;
                uint completedBreakSlots = _completedBreakSlots;
                // Only original native identities cross the life boundary; accepted resources are supplied separately.
                for (int i = 0; i < ResourceCount; i++)
                {
                    status = StrictCombatArmourBindingStatus.ResourceMismatch;
                    if (!InitialState.TryGetPiece(i, out var resource)
                        || !Original.TryGetSlot(resource.Slot, out var originalSlot)
                        || !current.TryGetSlot(resource.Slot, out var slot)) return false;
                    if ((completedBreakSlots & (1u << resource.Slot)) != 0)
                    {
                        if (slot.IsEquipped || slot.ItemId != 0) return false;
                    }
                    else if (!slot.IsEquipped || !slot.HasInstance || slot.InstanceId == 0
                        || slot.ItemId != resource.ItemId) return false;
                    status = StrictCombatArmourBindingStatus.LayoutMismatch;
                    if (slot.OrderIndex != originalSlot.OrderIndex
                        || !current.TryGetOrderSlot(slot.OrderIndex, out int orderedSlot)
                        || orderedSlot != resource.Slot) return false;
                }
                if (!ValidateNoExtraPieces(InitialState, current, out status)
                    || !ValidateNativeIdentities(Original, current, InitialState, completedBreakSlots, out status))
                    return false;
                status = IsRetired ? StrictCombatArmourBindingStatus.Retired : StrictCombatArmourBindingStatus.Success;
                return status == StrictCombatArmourBindingStatus.Success;
            }
            catch (Exception)
            {
                status = StrictCombatArmourBindingStatus.Failed;
                return false;
            }
        }

        internal bool HasCompletedBreak(int resourceIndex)
            => InitialState.TryGetPiece(resourceIndex, out var resource)
                && (_completedBreakSlots & (1u << resource.Slot)) != 0;

        internal bool TryCompleteBreak(StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot current,
            int resourceIndex, out StrictCombatArmourBindingStatus status)
        {
            status = StrictCombatArmourBindingStatus.Retired;
            if (IsRetired) return false;
            status = StrictCombatArmourBindingStatus.ResourceMismatch;
            if (!InitialState.TryGetPiece(resourceIndex, out var initial)) return false;
            uint slotBit = 1u << initial.Slot;
            if ((_completedBreakSlots & slotBit) != 0)
            {
                status = StrictCombatArmourBindingStatus.AlreadyBroken;
                return false;
            }
            if (owned == null || !owned.TryGetPiece(resourceIndex, out var resource) || !resource.IsBroken)
                return false;
            uint candidateSlots = _completedBreakSlots | slotBit;
            if (!Matches(owned, current, candidateSlots, out status)) return false;
            status = StrictCombatArmourBindingStatus.Retired;
            if (IsRetired) return false;
            _completedBreakSlots = candidateSlots;
            status = StrictCombatArmourBindingStatus.Success;
            return true;
        }

        private bool Matches(StrictCombatOwnedArmourState owned, StrictCombatArmourSnapshot current,
            uint completedBreakSlots, out StrictCombatArmourBindingStatus status)
        {
            status = StrictCombatArmourBindingStatus.Retired;
            if (IsRetired) return false;
            if (!ValidateObservation(Owner, owned, current, out status)) return false;
            status = StrictCombatArmourBindingStatus.LifeChanged;
            if (current.ServerState.LifeGeneration != LifeGeneration) return false;
            status = StrictCombatArmourBindingStatus.StaleObservation;
            if (!ReferenceEquals(Original.Player.Player, current.Player.Player)
                || !Binding.SameBinding(current.Binding)) return false;

            status = StrictCombatArmourBindingStatus.ResourceMismatch;
            if (owned.Count != ResourceCount || owned.Golden != InitialState.Golden
                || !StrictCombatOwnedArmourState.SameFloat(owned.GeneralMultiplier, InitialState.GeneralMultiplier)
                || !StrictCombatOwnedArmourState.SameFloat(owned.DemonicMultiplier, InitialState.DemonicMultiplier))
                return false;
            for (int i = 0; i < ResourceCount; i++)
            {
                if (!InitialState.TryGetPiece(i, out var initial) || !owned.TryGetPiece(i, out var resource)
                    || initial.Slot != resource.Slot || initial.ResourceId != resource.ResourceId
                    || initial.ItemId != resource.ItemId) return false;
            }

            status = StrictCombatArmourBindingStatus.LayoutMismatch;
            if (!StrictCombatOwnedArmourState.SameFloat(Original.GoldenGeneralMultiplier, current.GoldenGeneralMultiplier)
                || !StrictCombatOwnedArmourState.SameFloat(Original.GoldenDemonicMultiplier, current.GoldenDemonicMultiplier))
                return false;
            // Accepted points and broken flags may advance while the original native pieces await projection.
            if (!ValidateResources(owned, current, requireUnbroken: false, completedBreakSlots, out status)
                || !ValidateNativeIdentities(Original, current, owned, completedBreakSlots, out status)) return false;
            status = IsRetired ? StrictCombatArmourBindingStatus.Retired : StrictCombatArmourBindingStatus.Success;
            return status == StrictCombatArmourBindingStatus.Success;
        }

        internal void Retire() => Interlocked.Exchange(ref _retired, 1);

        private static bool ValidateObservation(StrictCombatClient owner, StrictCombatOwnedArmourState owned,
            StrictCombatArmourSnapshot native, out StrictCombatArmourBindingStatus status)
        {
            status = StrictCombatArmourBindingStatus.StateUnavailable;
            if (owned == null) return false;
            status = StrictCombatArmourBindingStatus.Unprovisioned;
            if (!owned.IsProvisioned) return false;
            status = StrictCombatArmourBindingStatus.NativeStateUnavailable;
            if (native == null || native.Player == null || native.Player.Player == null || native.Native == null)
                return false;
            status = StrictCombatArmourBindingStatus.StaleObservation;
            if (!ReferenceEquals(owner, native.Player.Owner)) return false;
            status = StrictCombatArmourBindingStatus.LifeChanged;
            if (native.ServerState.LifeGeneration == 0 || native.ServerState.LifeStage == StrictCombatLifeStage.Uninitialized)
                return false;
            status = StrictCombatArmourBindingStatus.NativeStateUnavailable;
            if (!native.IsEnabled || !native.ActiveInHierarchy) return false;
            status = StrictCombatArmourBindingStatus.Success;
            return true;
        }

        private static bool ValidateResources(StrictCombatOwnedArmourState owned,
            StrictCombatArmourSnapshot native, bool requireUnbroken, uint completedBreakSlots,
            out StrictCombatArmourBindingStatus status)
        {
            int previousOrder = -1;
            for (int i = 0; i < owned.Count; i++)
            {
                status = StrictCombatArmourBindingStatus.ResourceMismatch;
                if (!owned.TryGetPiece(i, out var resource)) return false;
                if (requireUnbroken && resource.IsBroken)
                {
                    status = StrictCombatArmourBindingStatus.AlreadyBroken;
                    return false;
                }
                if (!native.TryGetSlot(resource.Slot, out var slot)) return false;
                if ((completedBreakSlots & (1u << resource.Slot)) != 0)
                {
                    if (!resource.IsBroken || slot.IsEquipped || slot.ItemId != 0) return false;
                }
                else if (!slot.IsEquipped || !slot.HasInstance || slot.InstanceId == 0
                    || slot.ItemId != resource.ItemId) return false;
                status = StrictCombatArmourBindingStatus.LayoutMismatch;
                if (slot.OrderIndex <= previousOrder || slot.OrderIndex >= native.OrderCount
                    || !native.TryGetOrderSlot(slot.OrderIndex, out int orderedSlot) || orderedSlot != resource.Slot)
                    return false;
                previousOrder = slot.OrderIndex;
                status = StrictCombatArmourBindingStatus.ResourceMismatch;
                for (int j = 0; j < i; j++)
                {
                    if (!owned.TryGetPiece(j, out var earlier) || !native.TryGetSlot(earlier.Slot, out var earlierSlot)
                        || (earlierSlot.InstanceId != 0 && slot.InstanceId != 0
                            && earlierSlot.InstanceId == slot.InstanceId)) return false;
                }
            }

            return ValidateNoExtraPieces(owned, native, out status);
        }

        private static bool ValidateNoExtraPieces(StrictCombatOwnedArmourState owned,
            StrictCombatArmourSnapshot native, out StrictCombatArmourBindingStatus status)
        {
            status = StrictCombatArmourBindingStatus.ResourceMismatch;
            for (int i = 0; i < native.SlotCount; i++)
            {
                if (!native.TryGetSlot(i, out var slot)) return false;
                if (!slot.IsEquipped && !slot.HasInstance) continue;
                int matches = 0;
                for (int j = 0; j < owned.Count; j++)
                    if (owned.TryGetPiece(j, out var resource) && resource.Slot == slot.Slot) matches++;
                if (matches != 1) return false;
            }
            status = StrictCombatArmourBindingStatus.Success;
            return true;
        }

        private static bool ValidateNativeIdentities(StrictCombatArmourSnapshot original,
            StrictCombatArmourSnapshot current, StrictCombatOwnedArmourState owned,
            uint completedBreakSlots, out StrictCombatArmourBindingStatus status)
        {
            // Only native identity comparisons can cross into retained native objects.
            try
            {
                status = StrictCombatArmourBindingStatus.LayoutMismatch;
                if (!original.Native.HasSameLayout(current.Native)) return false;
                status = StrictCombatArmourBindingStatus.ResourceMismatch;
                for (int i = 0; i < owned.Count; i++)
                {
                    if (!owned.TryGetPiece(i, out var resource)) return false;
                    bool completed = (completedBreakSlots & (1u << resource.Slot)) != 0;
                    if (completed ? !original.Native.HasSameBrokenPiece(current.Native, resource.Slot)
                        : !original.Native.HasSamePiece(current.Native, resource.Slot)) return false;
                }
                status = StrictCombatArmourBindingStatus.Success;
                return true;
            }
            catch (Exception)
            {
                status = StrictCombatArmourBindingStatus.Failed;
                return false;
            }
        }
    }
}
