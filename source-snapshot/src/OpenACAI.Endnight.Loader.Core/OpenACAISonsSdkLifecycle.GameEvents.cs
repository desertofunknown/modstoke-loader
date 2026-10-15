// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Sons.Events;
using Sons.Inventory;
using SonsSdk;
using NativeObject = Il2CppSystem.Object;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSdkLifecycle
    {
        public void RegisterGameEvents()
        {
            EventRegistry.Register(GameEvent.ItemPickedUp, (EventRegistry.SubscriberCallback)OnItemPickedUp);
            EventRegistry.Register(GameEvent.LoadedMainScene, (EventRegistry.SubscriberCallback)OnNativeGameStart);
            EventRegistry.Register(GameEvent.ItemCrafted, (EventRegistry.SubscriberCallback)OnItemCrafted);
            EventRegistry.Register(GameEvent.ItemConsumed, (EventRegistry.SubscriberCallback)OnItemConsumed);
            EventRegistry.Register(GameEvent.ArmorEquipped, (EventRegistry.SubscriberCallback)OnArmorEquipped);
        }

        private static void OnItemPickedUp(NativeObject value)
        {
            var item = value.TryCast<ItemInstance.ItemInstanceAndCount>();
            if (item == null)
                return;

            SdkEvents.OnItemPickup.Invoke(item);
        }

        private static void OnItemConsumed(NativeObject value)
        {
            var item = value.TryCast<ItemInstance>();
            if (item == null)
                return;

            SdkEvents.OnItemConsumed.Invoke(item);
        }

        private static void OnItemCrafted(NativeObject value)
        {
            var itemId = value.Unbox<int>();
            SdkEvents.OnItemCrafted.Invoke(itemId);
        }

        private static void OnArmorEquipped(NativeObject value)
        {
            SdkEvents.OnArmorEquipped.Invoke();
        }

        private void OnNativeGameStart(NativeObject value)
        {
            OnGameStart();
        }
    }
}
