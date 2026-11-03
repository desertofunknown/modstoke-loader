// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Wearable.Armour;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // The caller owns game-thread, frame, local-player and accepted-binding validation.
    internal sealed class StrictCombatArmour
    {
        // Bounds the WearableSlots enum domain, not the configured live capacity.
        private const int MaximumSlots = 12;
        private readonly RetainedObject _system, _gameObject, _orderList, _dataList;
        private readonly RetainedObject[] _data, _pieces, _instances;
        private readonly int[] _order;
        private readonly StrictCombatArmourSlotSnapshot[] _slots;

        private StrictCombatArmour(PlayerArmourSystem system, RetainedObject retainedSystem,
            RetainedObject gameObject, RetainedObject orderList, RetainedObject dataList,
            RetainedObject[] data, RetainedObject[] pieces, RetainedObject[] instances,
            int[] order, StrictCombatArmourSlotSnapshot[] slots, int nextAvailableOrderIndex,
            float goldenGeneralMultiplier, float goldenDemonicMultiplier,
            bool isEnabled, bool activeInHierarchy)
        {
            System = system;
            _system = retainedSystem;
            _gameObject = gameObject;
            _orderList = orderList;
            _dataList = dataList;
            _data = data;
            _pieces = pieces;
            _instances = instances;
            _order = order;
            _slots = slots;
            NextAvailableOrderIndex = nextAvailableOrderIndex;
            GoldenGeneralMultiplier = goldenGeneralMultiplier;
            GoldenDemonicMultiplier = goldenDemonicMultiplier;
            IsEnabled = isEnabled;
            ActiveInHierarchy = activeInHierarchy;
        }

        internal PlayerArmourSystem System { get; }
        internal int ArmourSystemInstanceId => _system.InstanceId;
        internal int SlotCount => _slots.Length;
        internal int OrderCount => _order.Length;
        internal int NextAvailableOrderIndex { get; }
        internal float GoldenGeneralMultiplier { get; }
        internal float GoldenDemonicMultiplier { get; }
        internal bool IsEnabled { get; }
        internal bool ActiveInHierarchy { get; }

        internal bool TryGetSlot(int slot, out StrictCombatArmourSlotSnapshot snapshot)
        {
            snapshot = default;
            if (slot < 0 || slot >= _slots.Length) return false;
            snapshot = _slots[slot];
            return true;
        }

        internal bool TryGetOrderSlot(int index, out int slot)
        {
            slot = -1;
            if (index < 0 || index >= _order.Length) return false;
            slot = _order[index];
            return true;
        }

        internal PlayerArmourSystem.ArmourSlotData GetRetainedSlotData(int slot)
        {
            try
            {
                if (slot < 0 || slot >= _data.Length || !_data[slot].Matches(_data[slot]))
                    return null;
                // Return only the retained record; the caller owns freshness and authority.
                return _data[slot].Value as PlayerArmourSystem.ArmourSlotData;
            }
            catch (Exception) { return null; }
        }

        internal static bool TryCapture(PlayerArmourSystem system, out StrictCombatArmour capture,
            out StrictCombatArmourReadStatus status)
        {
            capture = null;
            status = StrictCombatArmourReadStatus.NativeUnavailable;
            try
            {
                if (!Live(system)) return false;
                var gameObject = system.gameObject;
                var orderList = system._armourSlotOrder;
                var dataList = system._armourSlotData;
                if (!Live(gameObject) || !Live(orderList) || !Live(dataList)) return false;
                var retainedSystem = new RetainedObject(system);
                var retainedGameObject = new RetainedObject(gameObject);
                var retainedOrderList = new RetainedObject(orderList);
                var retainedDataList = new RetainedObject(dataList);

                int orderCount = orderList.Count;
                int slotCount = dataList.Count;
                if (orderCount < 0 || orderCount > MaximumSlots || slotCount < 0 || slotCount > MaximumSlots)
                {
                    status = StrictCombatArmourReadStatus.LimitReached;
                    return false;
                }
                status = StrictCombatArmourReadStatus.InvalidState;
                float general = system._goldenArmourGeneralDamageMultiplier;
                float demonic = system._goldenArmourDemonicDamageMultiplier;
                if (!float.IsFinite(general) || !float.IsFinite(demonic)) return false;

                var order = new int[orderCount];
                var orderIndices = new int[slotCount];
                Array.Fill(orderIndices, -1);
                for (int i = 0; i < orderCount; i++)
                {
                    int slot = (int)orderList[i];
                    if (slot < 0 || slot >= slotCount || orderIndices[slot] != -1) return false;
                    order[i] = slot;
                    orderIndices[slot] = i;
                }

                var data = new RetainedObject[slotCount];
                var pieces = new RetainedObject[slotCount];
                var instances = new RetainedObject[slotCount];
                var slots = new StrictCombatArmourSlotSnapshot[slotCount];
                for (int i = 0; i < slotCount; i++)
                {
                    status = StrictCombatArmourReadStatus.IdentityUnavailable;
                    var slotData = dataList[i];
                    if (!Live(slotData)) return false;
                    data[i] = new RetainedObject(slotData);
                    status = StrictCombatArmourReadStatus.InvalidState;
                    if ((int)slotData.Slot != i) return false;
                    float points = slotData.RemainingArmourpoints;
                    if (!float.IsFinite(points)) return false;
                    var piece = slotData.ArmourPiece;
                    var instance = slotData.ArmourInstance;
                    status = StrictCombatArmourReadStatus.IdentityUnavailable;
                    if ((piece is not null && !Live(piece))
                        || (instance is not null && !Live((Il2CppObjectBase)instance)))
                        return false;
                    pieces[i] = new RetainedObject(piece);
                    instances[i] = new RetainedObject(instance);
                    // Native unequip leaves the destroyed instance reference and stale points behind.
                    if (piece is not null && instance is not null && !instances[i].UnityAlive)
                        return false;
                    int itemId = piece is null ? 0 : piece._itemId;
                    status = StrictCombatArmourReadStatus.InvalidState;
                    if (piece is not null && itemId <= 0) return false;
                    slots[i] = new StrictCombatArmourSlotSnapshot(i, orderIndices[i], itemId,
                        pieces[i].InstanceId, instances[i].InstanceId, piece is not null,
                        instances[i].UnityAlive, points);
                }

                // Keep the native cursor as observed; no capacity or cursor semantics are inferred.
                capture = new StrictCombatArmour(system, retainedSystem, retainedGameObject,
                    retainedOrderList, retainedDataList, data, pieces, instances, order, slots,
                    system._nextAvailableArmourSlotIndex, general, demonic, system.enabled,
                    gameObject.activeInHierarchy);
                status = StrictCombatArmourReadStatus.Success;
                return true;
            }
            catch (Exception)
            {
                capture = null;
                status = StrictCombatArmourReadStatus.Failed;
                return false;
            }
        }

        internal bool Matches(StrictCombatArmour other)
        {
            try
            {
                if (!HasSameLayout(other) || NextAvailableOrderIndex != other.NextAvailableOrderIndex
                    || !SameFloat(GoldenGeneralMultiplier, other.GoldenGeneralMultiplier)
                    || !SameFloat(GoldenDemonicMultiplier, other.GoldenDemonicMultiplier)
                    || IsEnabled != other.IsEnabled || ActiveInHierarchy != other.ActiveInHierarchy)
                    return false;
                for (int i = 0; i < _slots.Length; i++)
                {
                    var before = _slots[i];
                    var after = other._slots[i];
                    if (!_data[i].Matches(other._data[i]) || !_pieces[i].Matches(other._pieces[i])
                        || !_instances[i].Matches(other._instances[i])
                        || before.Slot != after.Slot || before.OrderIndex != after.OrderIndex
                        || before.ItemId != after.ItemId || before.PieceInstanceId != after.PieceInstanceId
                        || before.InstanceId != after.InstanceId || before.IsEquipped != after.IsEquipped
                        || before.HasInstance != after.HasInstance
                        || !SameFloat(before.RemainingPoints, after.RemainingPoints)) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool HasSamePiece(StrictCombatArmour other, int slot)
        {
            try
            {
                // Historical points and enablement may change. A definition or slot alone
                // cannot distinguish replacement, so a retained live render instance is required.
                return HasSameLayout(other) && slot >= 0 && slot < _slots.Length
                    && _pieces[slot].UnityAlive && other._pieces[slot].UnityAlive
                    && _instances[slot].UnityAlive && other._instances[slot].UnityAlive
                    && _data[slot].Matches(other._data[slot])
                    && _pieces[slot].Matches(other._pieces[slot])
                    && _instances[slot].Matches(other._instances[slot]);
            }
            catch (Exception) { return false; }
        }

        internal bool HasSameLayout(StrictCombatArmour other)
        {
            if (other is null || _slots.Length != other._slots.Length || _order.Length != other._order.Length
                || !_system.Matches(other._system) || !_gameObject.Matches(other._gameObject)
                || !_orderList.Matches(other._orderList) || !_dataList.Matches(other._dataList)) return false;
            for (int i = 0; i < _order.Length; i++)
                if (_order[i] != other._order[i]) return false;
            return true;
        }

        internal bool HasSameBrokenPiece(StrictCombatArmour current, int slot)
        {
            try
            {
                // Native unequip clears the definition but retains the instance reference
                // through deferred destruction. A destroyed definition is not an empty slot.
                return HasSameLayout(current) && slot >= 0 && slot < _slots.Length
                    && _data[slot].Matches(current._data[slot])
                    && _pieces[slot].UnityAlive && _pieces[slot].Matches(_pieces[slot])
                    && current._pieces[slot].Value is null
                    && _instances[slot].MatchesAfterDestroy(current._instances[slot]);
            }
            catch (Exception) { return false; }
        }

        private static bool SameFloat(float before, float after)
            => BitConverter.SingleToInt32Bits(before) == BitConverter.SingleToInt32Bits(after);

        private static bool Live(Il2CppObjectBase value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value;

        private readonly struct RetainedObject
        {
            internal RetainedObject(Il2CppObjectBase value)
            {
                Value = value;
                Pointer = value?.Pointer ?? IntPtr.Zero;
                UnityObject = value as UnityEngine.Object;
                UnityAlive = Live(UnityObject);
                InstanceId = UnityAlive ? UnityObject.GetInstanceID() : 0;
            }

            internal Il2CppObjectBase Value { get; }
            internal int InstanceId { get; }
            internal bool UnityAlive { get; }
            private IntPtr Pointer { get; }
            private UnityEngine.Object UnityObject { get; }

            internal bool MatchesAfterDestroy(RetainedObject current)
            {
                if (!UnityAlive || UnityObject is null || current.UnityObject is null
                    || !Live(Value) || !Live(current.Value) || Value.Pointer != Pointer
                    || current.Value.Pointer != current.Pointer || Pointer != current.Pointer)
                    return false;
                bool currentAlive = Live(current.UnityObject);
                if (currentAlive != current.UnityAlive || Live(UnityObject) != currentAlive)
                    return false;
                // GetInstanceID must not be called on a Unity-destroyed retained reference.
                if (!currentAlive) return true;
                return InstanceId == current.InstanceId
                    && UnityObject.GetInstanceID() == InstanceId
                    && current.UnityObject.GetInstanceID() == current.InstanceId;
            }

            internal bool Matches(RetainedObject other)
            {
                if (Value is null || other.Value is null) return Value is null && other.Value is null;
                if (!Live(Value) || !Live(other.Value) || Value.Pointer != Pointer
                    || other.Value.Pointer != other.Pointer || Pointer != other.Pointer) return false;
                if (UnityObject is null || other.UnityObject is null)
                    return UnityObject is null && other.UnityObject is null;
                if (UnityAlive != other.UnityAlive || Live(UnityObject) != UnityAlive
                    || Live(other.UnityObject) != other.UnityAlive) return false;
                // Retained dead references can establish empty-slot freshness only.
                if (!UnityAlive) return true;
                return InstanceId == other.InstanceId
                    && UnityObject.GetInstanceID() == InstanceId
                    && other.UnityObject.GetInstanceID() == other.InstanceId;
            }
        }
    }
}
