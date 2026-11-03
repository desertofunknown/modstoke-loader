// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using global::Sons.Events;
using global::Sons.Inventory;
using NativeObject = Il2CppSystem.Object;

namespace Modstoke.Sons
{
    /// <summary>Process-local native inventory notifications, independent of legacy SDK initialization.</summary>
    public static class InventoryEvents
    {
        private static readonly List<EventRegistry.SubscriberCallback> NativeCallbacks = new();
        private static int _initializationAttempted;
        private static int _registeredKinds;
        private static int _reportedFailure;

        /// <summary>Native registration calls that returned successfully, not proof of gameplay delivery.</summary>
        public static InventoryActivityKind RegisteredKinds =>
            (InventoryActivityKind)Volatile.Read(ref _registeredKinds);

        /// <summary>Runs synchronously on the native publisher's thread. This is an observation, not a cancellable request.</summary>
        public static event Action<InventoryActivity> Observed;

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref _initializationAttempted, 1) != 0)
                return;

            Register(InventoryActivityKind.PickedUp, () => GameEvent.ItemPickedUp);
            Register(InventoryActivityKind.Crafted, () => GameEvent.ItemCrafted);
            Register(InventoryActivityKind.Consumed, () => GameEvent.ItemConsumed);
            Register(InventoryActivityKind.ArmorEquipped, () => GameEvent.ArmorEquipped);
        }

        private static void Register(InventoryActivityKind kind, Func<NativeObject> eventType)
        {
            try
            {
                EventRegistry.SubscriberCallback callback = (Action<NativeObject>)(value => OnNativeEvent(kind, value));
                // Retain the delegate even if registration throws after modifying the native registry.
                NativeCallbacks.Add(callback);
                EventRegistry.Register(eventType(), callback);
                Interlocked.Or(ref _registeredKinds, (int)kind);
            }
            catch (Exception error)
            {
                ReportFailure(kind, "registration", error);
            }
        }

        private static void OnNativeEvent(InventoryActivityKind kind, NativeObject value)
        {
            var observed = Observed;
            if (observed == null || (RegisteredKinds & kind) == 0)
                return;

            try
            {
                var observation = CopyObservation(kind, value);
                if (observation != null)
                    Modding.Dispatch("OpenACAISonsInventoryEvents.Observed", observed, observation);
            }
            catch (Exception error)
            {
                ReportFailure(kind, "observation", error);
            }
        }

        private static InventoryActivity CopyObservation(InventoryActivityKind kind, NativeObject value)
        {
            if (kind != InventoryActivityKind.ArmorEquipped
                && (value is null || value.WasCollected))
                return null;

            int? itemId = null;
            int? count = null;
            switch (kind)
            {
                case InventoryActivityKind.PickedUp:
                    var pickup = value?.TryCast<ItemInstance.ItemInstanceAndCount>();
                    var pickedUpItem = pickup?.ItemInstance;
                    if (pickedUpItem is null || pickedUpItem.WasCollected)
                        return null;
                    itemId = pickedUpItem._itemID;
                    count = pickup.Count;
                    break;
                case InventoryActivityKind.Crafted:
                    if (value is null || value.WasCollected)
                        return null;
                    itemId = value.Unbox<int>();
                    break;
                case InventoryActivityKind.Consumed:
                    var consumedItem = value?.TryCast<ItemInstance>();
                    if (consumedItem is null || consumedItem.WasCollected)
                        return null;
                    itemId = consumedItem._itemID;
                    break;
            }

            return new InventoryActivity(kind, itemId, count, DateTimeOffset.UtcNow);
        }

        private static void ReportFailure(InventoryActivityKind kind, string operation, Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Inventory event " + operation + " failed for " + kind
                    + " (" + error.GetType().FullName + "). Further inventory event failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    [Flags]
    public enum InventoryActivityKind
    {
        None = 0,
        PickedUp = 1,
        Crafted = 2,
        Consumed = 4,
        ArmorEquipped = 8,
        All = PickedUp | Crafted | Consumed | ArmorEquipped
    }

    public sealed class InventoryActivity
    {
        internal InventoryActivity(InventoryActivityKind kind, int? itemId,
            int? reportedCount, DateTimeOffset observedAt)
        {
            Kind = kind;
            ItemId = itemId;
            ReportedCount = reportedCount;
            ObservedAt = observedAt;
        }

        public InventoryActivityKind Kind { get; }
        /// <summary>Reported item ID for pickup, crafting or consumption; absent for armor notifications.</summary>
        public int? ItemId { get; }
        /// <summary>Raw pickup payload count. Other notifications do not supply a count through this API.</summary>
        public int? ReportedCount { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
