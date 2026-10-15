// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using Sons.Events;
using Sons.Inventory;
using NativeObject = Il2CppSystem.Object;

namespace OpenACAI.Sons
{
    /// <summary>Process-local native inventory notifications, independent of legacy SDK initialization.</summary>
    public static class OpenACAISonsInventoryEvents
    {
        private static readonly List<EventRegistry.SubscriberCallback> NativeCallbacks = new();
        private static int _initializationAttempted;
        private static int _registeredKinds;
        private static int _reportedFailure;

        /// <summary>Native registration calls that returned successfully, not proof of gameplay delivery.</summary>
        public static OpenACAISonsInventoryActivityKind RegisteredKinds =>
            (OpenACAISonsInventoryActivityKind)Volatile.Read(ref _registeredKinds);

        /// <summary>Runs synchronously on the native publisher's thread. This is an observation, not a cancellable request.</summary>
        public static event Action<OpenACAISonsInventoryActivity> Observed;

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref _initializationAttempted, 1) != 0)
                return;

            Register(OpenACAISonsInventoryActivityKind.PickedUp, () => GameEvent.ItemPickedUp);
            Register(OpenACAISonsInventoryActivityKind.Crafted, () => GameEvent.ItemCrafted);
            Register(OpenACAISonsInventoryActivityKind.Consumed, () => GameEvent.ItemConsumed);
            Register(OpenACAISonsInventoryActivityKind.ArmorEquipped, () => GameEvent.ArmorEquipped);
        }

        private static void Register(OpenACAISonsInventoryActivityKind kind, Func<NativeObject> eventType)
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

        private static void OnNativeEvent(OpenACAISonsInventoryActivityKind kind, NativeObject value)
        {
            if ((RegisteredKinds & kind) == 0)
                return;

            try
            {
                var observation = CopyObservation(kind, value);
                if (observation != null)
                    OpenACAIModding.Dispatch("OpenACAISonsInventoryEvents.Observed", Observed, observation);
            }
            catch (Exception error)
            {
                ReportFailure(kind, "observation", error);
            }
        }

        private static OpenACAISonsInventoryActivity CopyObservation(OpenACAISonsInventoryActivityKind kind, NativeObject value)
        {
            int? itemId = null;
            int? count = null;
            switch (kind)
            {
                case OpenACAISonsInventoryActivityKind.PickedUp:
                    var pickup = value?.TryCast<ItemInstance.ItemInstanceAndCount>();
                    var pickedUpItem = pickup?.ItemInstance;
                    if (pickedUpItem is null || pickedUpItem.WasCollected)
                        return null;
                    itemId = pickedUpItem._itemID;
                    count = pickup.Count;
                    break;
                case OpenACAISonsInventoryActivityKind.Crafted:
                    if (value is null || value.WasCollected)
                        return null;
                    itemId = value.Unbox<int>();
                    break;
                case OpenACAISonsInventoryActivityKind.Consumed:
                    var consumedItem = value?.TryCast<ItemInstance>();
                    if (consumedItem is null || consumedItem.WasCollected)
                        return null;
                    itemId = consumedItem._itemID;
                    break;
            }

            return new OpenACAISonsInventoryActivity(kind, itemId, count, DateTimeOffset.UtcNow);
        }

        private static void ReportFailure(OpenACAISonsInventoryActivityKind kind, string operation, Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0)
                return;
            try
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Inventory event " + operation + " failed for " + kind
                    + " (" + error.GetType().FullName + "). Further inventory event failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    [Flags]
    public enum OpenACAISonsInventoryActivityKind
    {
        None = 0,
        PickedUp = 1,
        Crafted = 2,
        Consumed = 4,
        ArmorEquipped = 8,
        All = PickedUp | Crafted | Consumed | ArmorEquipped
    }

    public sealed class OpenACAISonsInventoryActivity
    {
        internal OpenACAISonsInventoryActivity(OpenACAISonsInventoryActivityKind kind, int? itemId,
            int? reportedCount, DateTimeOffset observedAt)
        {
            Kind = kind;
            ItemId = itemId;
            ReportedCount = reportedCount;
            ObservedAt = observedAt;
        }

        public OpenACAISonsInventoryActivityKind Kind { get; }
        /// <summary>Reported item ID for pickup, crafting or consumption; absent for armor notifications.</summary>
        public int? ItemId { get; }
        /// <summary>Raw pickup payload count. Other notifications do not supply a count through this API.</summary>
        public int? ReportedCount { get; }
        public DateTimeOffset ObservedAt { get; }
    }
}
