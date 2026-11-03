// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons.Combat
{
    public enum StrictCombatArmourReadStatus
    {
        Success, Unavailable, WrongThread, Retired, StateUnavailable, NativeUnavailable,
        InvalidState, LimitReached, StaleSnapshot, AttachmentChanged, IdentityUnavailable, Failed
    }

    /// <summary>A copied native slot observation, not an authoritative resource identity.</summary>
    public readonly struct StrictCombatArmourSlotSnapshot
    {
        internal StrictCombatArmourSlotSnapshot(int slot, int orderIndex, int itemId,
            int pieceInstanceId, int instanceId, bool isEquipped, bool hasInstance, float remainingPoints)
        {
            Slot = slot;
            OrderIndex = orderIndex;
            ItemId = itemId;
            PieceInstanceId = pieceInstanceId;
            InstanceId = instanceId;
            IsEquipped = isEquipped;
            HasInstance = hasInstance;
            RemainingPoints = remainingPoints;
        }

        public int Slot { get; }
        /// <summary>The configured order index, or -1 when this data slot is not in that order.</summary>
        public int OrderIndex { get; }
        public int ItemId { get; }
        public int PieceInstanceId { get; }
        /// <summary>The live spawned instance ID, or zero for a missing or destroyed retained instance.</summary>
        public int InstanceId { get; }
        public bool IsEquipped { get; }
        /// <summary>Whether the spawned instance is Unity-alive; false for a destroyed retained reference.</summary>
        public bool HasInstance { get; }
        /// <summary>The native finite value, including negative or stale unequipped-slot values.</summary>
        public float RemainingPoints { get; }
    }

    /// <summary>
    /// Copied owner armour observations paired with an accepted player snapshot.
    /// Golden multipliers do not establish golden eligibility; native observations do not grant resources.
    /// </summary>
    public sealed class StrictCombatArmourSnapshot
    {
        internal StrictCombatArmourSnapshot(StrictCombatLocalSnapshot player, StrictCombatArmour native)
        {
            Player = player;
            Native = native;
        }

        internal StrictCombatLocalSnapshot Player { get; }
        internal StrictCombatArmour Native { get; }
        public int Frame => Player.Frame;
        public StrictCombatBinding Binding => Player.Binding;
        public StrictCombatSnapshot ServerState => Player.ServerState;
        public int ArmourSystemInstanceId => Native.ArmourSystemInstanceId;
        public int SlotCount => Native.SlotCount;
        public int OrderCount => Native.OrderCount;
        public int NextAvailableOrderIndex => Native.NextAvailableOrderIndex;
        public float GoldenGeneralMultiplier => Native.GoldenGeneralMultiplier;
        public float GoldenDemonicMultiplier => Native.GoldenDemonicMultiplier;
        public bool IsEnabled => Native.IsEnabled;
        public bool ActiveInHierarchy => Native.ActiveInHierarchy;

        public bool TryGetSlot(int slot, out StrictCombatArmourSlotSnapshot snapshot)
            => Native.TryGetSlot(slot, out snapshot);

        public bool TryGetOrderSlot(int index, out int slot)
            => Native.TryGetOrderSlot(index, out slot);
    }
}
