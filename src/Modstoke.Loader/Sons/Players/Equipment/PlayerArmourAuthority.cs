// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Armour numbers and canonical inventory custody for one exact migrated actor. This does
    /// not install ingress, drive source equipment, write health or acknowledge source effects. Only a
    /// consumer retaining the exact accepted hit may use the internal settlement boundary.
    /// </summary>
    public sealed partial class PlayerArmourAuthority : IDisposable
    {
        private static readonly ConditionalWeakTable<PlayerBridge, PlayerArmourAuthority> Owners = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly PlayerInventoryAuthority _inventory;
        private readonly BoltEntity _target;
        private readonly Bolt.Entity _attachment;
        private State _state;
        private Calculation _settlingCalculation;
        private bool _retired;

        public BoltMigrationPlan Plan => _bridge.Plan;
        public ulong ActorNetworkId => _bridge.NetworkId;
        public string AcceptedBaselineFingerprint { get; }
        public string AcceptedInventoryFingerprint { get; }
        public string AcceptanceReason { get; }
        public ulong Revision { get; private set; }
        public ulong LastTransaction { get; private set; }
        public bool IsDisposed { get; private set; }
        internal ArmourSettlement LastSettlement { get; private set; }

        internal bool MatchesPreparedRoute(PlayerBridge bridge, PlayerInventoryAuthority inventory)
            => ReferenceEquals(bridge, _bridge) && ReferenceEquals(inventory, _inventory) && Current();

        private PlayerArmourAuthority(PlayerBridge bridge,
            PlayerInventoryAuthority inventory, BoltEntity target, Snapshot snapshot, string reason)
        {
            _bridge = bridge; _inventory = inventory; _target = target; _attachment = target._entity;
            _state = snapshot.Armour; AcceptedBaselineFingerprint = snapshot.Fingerprint;
            AcceptedInventoryFingerprint = snapshot.InventoryFingerprint; AcceptanceReason = reason;
        }

        /// <summary>
        /// Trusted server/operator decision, not a peer RPC. The independently approved
        /// fingerprint includes the migration, inventory baseline, slot order and multipliers.
        /// Acceptance performs no grants, native writes, hook installation or readiness claim.
        /// </summary>
        public static bool TryAcceptUnverifiedExisting(PlayerBridge bridge,
            PlayerInventoryAuthority inventory, PlayerInventoryBaseline inventoryBaseline,
            Snapshot snapshot, string approvedFingerprint, string operatorReason,
            out PlayerArmourAuthority owner, out string detail)
        {
            owner = null;
            detail = "The exact server bridge, accepted inventory and independently approved armour observation are required.";
            if (!GameBuild.IsRecognized || !PlayerRoles.IsAvailable
                || bridge?.IsServer != true || bridge.IsDisposed || bridge.IsGameplayActive || inventory == null
                || inventory.IsDisposed || inventory.IsFaulted || !inventory.HasAcceptedBaseline
                || !inventory.MatchesArmourBridge(bridge)
                || snapshot == null || snapshot.Plan != bridge.Plan || inventoryBaseline?.Plan != bridge.Plan
                || inventory.AcceptedBaselineFingerprint != inventoryBaseline.Fingerprint
                || !snapshot.MatchesInventory(inventoryBaseline) || snapshot.Fingerprint != approvedFingerprint
                || string.IsNullOrWhiteSpace(operatorReason) || operatorReason.Length > 512
                || Owners.TryGetValue(bridge, out _)) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var target)) return false;
                var candidate = new PlayerArmourAuthority(bridge, inventory, target, snapshot, operatorReason);
                if (!candidate.Current() || !candidate.EquipmentMatches()) return false;
                Owners.Add(bridge, candidate);
                bridge.Retired += candidate.Retire;
                owner = candidate;
                detail = "Approved armour numbers accepted for this actor; source fencing and combat/equipment integration remain separate.";
                return true;
            }
            catch (Exception error) { detail = "Armour acceptance refused: " + error.Message; return false; }
        }

        public bool TryRead(PlayerBridge bridge, out State state)
        {
            state = null;
            if (_settlingCalculation != null || !ReferenceEquals(bridge, _bridge)
                || !Current() || !EquipmentMatches()) return false;
            state = _state;
            return true;
        }

        /// <summary>
        /// Plan the armour stage only, AFTER the consumer has resolved OnNetworkHit's
        /// protection/blocking and Hit's early exits. No state or transaction is consumed.
        /// The consumer, not this helper, retains the exact CombatIngress.AcceptedHit.
        /// </summary>
        internal bool TryCalculateArmour(ulong transaction, float damage, bool ignoreArmour,
            global::ImpactType impactType, bool isDemonic, out Calculation calculation)
        {
            calculation = null;
            if (_settlingCalculation != null || !Current() || !_bridge.IsGameplayActive || !EquipmentMatches()
                || !_inventory.TryReadArmourTransaction(_bridge, out ulong inventoryTransaction)
                || transaction == 0 || transaction <= LastTransaction || transaction <= inventoryTransaction
                || Revision == ulong.MaxValue) return false;
            calculation = Calculate(this, transaction, Revision, inventoryTransaction, _state, damage, ignoreArmour,
                isDemonic || (int)impactType == 4);
            return true;
        }

        /// <summary>
        /// Reserve the complete canonical batch before dispatching source armour effects.
        /// No preview is a completion receipt; settlement requires the separate exact source ACK.
        /// </summary>
        internal bool TryReserveArmour(Calculation calculation, out string detail)
        {
            detail = "The exact current armour calculation is required for inventory reservation.";
            if (_settlingCalculation != null || !MatchesArmourCalculation(_inventory, _bridge, calculation)
                || !EquipmentMatches()) return false;
            return _inventory.TryReserveArmour(calculation, out detail);
        }

        /// <summary>
        /// Commit canonical inventory condition/break accounting and these armour numbers in
        /// one game-thread call, only after the source armour ACK and exact server equipment
        /// publication. The receipt is NOT health/stamina, death or ingress completion. The consumer retains
        /// the accepted hit and serializes those remaining stages; never replay uncertain effects.
        /// </summary>
        internal bool TrySettleArmour(Calculation calculation, out ArmourSettlement settlement)
            => TrySettleArmour(calculation, out settlement, out _);

        internal bool TrySettleArmour(Calculation calculation, out ArmourSettlement settlement, out string detail)
        {
            settlement = null;
            detail = "An exact current armour calculation and idle canonical inventory are required.";
            if (Environment.CurrentManagedThreadId != _thread || _settlingCalculation != null || calculation == null
                || !ReferenceEquals(calculation.Owner, this) || !Current() || !_bridge.IsGameplayActive) return false;
            if (ReferenceEquals(LastSettlement?.Calculation, calculation))
            {
                settlement = LastSettlement;
                detail = "The existing server armour/inventory receipt was returned; no effects were replayed.";
                return true;
            }
            if (calculation.BeforeRevision != Revision
                || !ReferenceEquals(calculation.Before, _state) || calculation.Transaction <= LastTransaction
                || Revision == ulong.MaxValue || !EquipmentMatches()) return false;
            var receipt = new ArmourSettlement(calculation, Revision + 1);
            _settlingCalculation = calculation;
            try
            {
                if (!_inventory.TryCommitArmour(calculation, out detail)) return false;
                // No native call or allocation may intervene between the canonical item
                // commit and these managed assignments on the same game thread.
                _state = calculation.After;
                Revision = receipt.Revision;
                LastTransaction = calculation.Transaction;
                LastSettlement = settlement = receipt;
                return true;
            }
            finally { _settlingCalculation = null; }
        }

        internal bool IsSettlingArmour(PlayerInventoryAuthority inventory,
            PlayerBridge bridge, Calculation calculation)
            => calculation != null && ReferenceEquals(calculation, _settlingCalculation)
                && MatchesArmourCalculation(inventory, bridge, calculation);

        internal bool MatchesArmourCalculation(PlayerInventoryAuthority inventory,
            PlayerBridge bridge, Calculation calculation)
            => calculation != null && ReferenceEquals(calculation.Owner, this)
                && IsCurrentArmourOwner(inventory, bridge)
                && calculation.BeforeRevision == Revision && ReferenceEquals(calculation.Before, _state)
                && calculation.Transaction > LastTransaction && Revision != ulong.MaxValue;

        internal bool IsCurrentArmourOwner(PlayerInventoryAuthority inventory, PlayerBridge bridge)
            => ReferenceEquals(inventory, _inventory) && ReferenceEquals(bridge, _bridge)
                && Current() && _bridge.IsGameplayActive;

        private bool Current()
        {
            if (Environment.CurrentManagedThreadId != _thread || IsDisposed || _retired
                || _load != SaveLifecycle.State.BeforeLoadCount
                || _bridge.IsDisposed || !_bridge.IsServer || !_bridge.Peer.IsCurrent
                || _inventory.IsDisposed || _inventory.IsFaulted
                || _inventory.AcceptedBaselineFingerprint != AcceptedInventoryFingerprint) return false;
            try
            {
                return _bridge.TryReadCurrentTarget(out var target) && Same(target, _target)
                    && _target && _target.isAttached && Same(_target._entity, _attachment)
                    && _attachment.IsOwner && Same(_attachment.UnityObject, _target)
                    && _attachment.NetworkId.PackedValue == ActorNetworkId;
            }
            catch (Exception) { return false; }
        }

        private bool EquipmentMatches()
        {
            try
            {
                var published = _target.GetState<IPlayerState>();
                var armour = published?.PlayerArmourIds;
                var clothing = published?.PlayerClothingIds;
                if (published == null || published.WasCollected || armour == null || armour.WasCollected
                    || clothing == null || clothing.WasCollected || armour.Length is < 1 or > 32
                    || clothing.Length is < 1 or > 32) return false;
                for (int i = 0; i < armour.Length; i++)
                    if (armour[i] != (i < _state.SlotCount ? _state.GetSlot(i).ItemId : 0)) return false;
                for (int i = 0; i < _state.SlotCount; i++)
                {
                    var slot = _state.GetSlot(i);
                    if (!slot.IsEquipped) continue;
                    if (i >= armour.Length || !_inventory.TryReadItem(slot.InventoryKey, out var item)
                        || item.Consumed || item.ItemId != slot.ItemId || item.ArmourSlot != i
                        || item.Quantity != 1 || item.HasNativeInstance || item.HeldSlot >= 0 || item.ClothingEquipped
                        || !SameFloat(item.ArmourPoints, slot.RemainingPoints)) return false;
                }
                var settled = LastSettlement?.Calculation;
                for (int i = 0; settled != null && i < settled.ChangeCount; i++)
                {
                    var change = settled.GetChange(i);
                    if (!change.Broken) continue;
                    if (!_inventory.TryReadItem(change.Before.InventoryKey, out var item)
                        || item.ItemId != change.Before.ItemId || !item.Consumed
                        || item.Quantity != 0 || item.ArmourSlot != -1) return false;
                }
                bool golden = false;
                for (int i = 0; i < clothing.Length; i++) golden |= clothing[i] == 572;
                return golden == _state.WearingGoldenClothing;
            }
            catch (Exception) { return false; }
        }

        private void Retire() { _retired = true; }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Armour authority belongs to its creating game thread.");
            if (IsDisposed) return;
            _bridge.Retired -= Retire;
            IsDisposed = true;
            // The weak-table tombstone prevents reaccepting the original baseline on this
            // bridge. Calculations and the last numeric receipt remain immutable and readable.
        }

        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => left is not null && right is not null && !left.WasCollected && !right.WasCollected
                && left.Pointer != IntPtr.Zero && left.Pointer == right.Pointer;
        private static bool SameFloat(float left, float right)
            => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

        /// <summary>A worn definition and condition, not an ItemInstance. Empty slots retain native stale points.</summary>
        public readonly record struct Slot(int Index, ulong InventoryKey, int ItemId, float RemainingPoints)
        {
            public bool IsEquipped => ItemId != 0;
        }

        public sealed class State
        {
            private readonly Slot[] _slots;
            private readonly int[] _order;
            public int SlotCount => _slots.Length;
            public int OrderCount => _order.Length;
            public int NextAvailableOrderIndex { get; }
            /// <summary>Eligibility captured on the original exact owner, never inferred from a remote armour object.</summary>
            public bool WearingGoldenClothing { get; }
            public float GoldenGeneralMultiplier { get; }
            public float GoldenDemonicMultiplier { get; }

            internal State(Slot[] slots, int[] order, int next, bool golden, float general, float demonic)
            {
                _slots = (Slot[])slots.Clone(); _order = (int[])order.Clone();
                NextAvailableOrderIndex = next; WearingGoldenClothing = golden;
                GoldenGeneralMultiplier = general; GoldenDemonicMultiplier = demonic;
            }
            public Slot GetSlot(int index) => _slots[index];
            public int GetOrderSlot(int index) => _order[index];
            internal Slot[] CopySlots() => (Slot[])_slots.Clone();
            internal int[] CopyOrder() => (int[])_order.Clone();
        }

        public readonly record struct SlotChange(int OrderIndex, Slot Before, Slot After, float Absorbed, bool Broken);

        internal sealed class Calculation
        {
            private readonly SlotChange[] _changes;
            internal PlayerArmourAuthority Owner { get; }
            internal ulong Transaction { get; }
            internal ulong BeforeRevision { get; }
            internal ulong BeforeInventoryTransaction { get; }
            internal State Before { get; }
            internal State After { get; }
            internal float IncomingDamage { get; }
            internal float RemainingDamage { get; }
            internal bool GoldenApplied { get; }
            internal bool IgnoreArmour { get; }
            internal bool Demonic { get; }
            internal int ChangeCount => _changes.Length;
            internal SlotChange GetChange(int index) => _changes[index];

            internal Calculation(PlayerArmourAuthority owner, ulong transaction, ulong revision, ulong inventoryTransaction,
                State before, State after, float incoming, float remaining, bool golden, SlotChange[] changes,
                bool ignoreArmour, bool demonic)
            {
                Owner = owner; Transaction = transaction; BeforeRevision = revision;
                BeforeInventoryTransaction = inventoryTransaction;
                Before = before; After = after; IncomingDamage = incoming; RemainingDamage = remaining;
                GoldenApplied = golden; _changes = (SlotChange[])changes.Clone();
                IgnoreArmour = ignoreArmour; Demonic = demonic;
            }
        }

        internal sealed class ArmourSettlement
        {
            internal Calculation Calculation { get; }
            internal ulong Revision { get; }
            internal ulong InventoryTransaction => Calculation.Transaction;
            internal ArmourSettlement(Calculation calculation, ulong revision)
            { Calculation = calculation; Revision = revision; }
        }
    }
}
