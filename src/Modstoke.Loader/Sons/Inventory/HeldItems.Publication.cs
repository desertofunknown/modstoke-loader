// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class HeldItems
    {
        // Bolt sends every id in a player's HeldItems to all peers, and a peer that can't resolve one stops drawing
        // that player's hand items. Held mod ids therefore stay out of it unless every peer is known to have them;
        // the player still sees the item in their own hand.
        private static bool _publicationGated;

        private static void GatePublication(HarmonyLib.Harmony harmony)
        {
            // Nothing on a dedicated server holds an item, and its GameAssembly isn't the build these addresses come from.
            if (GameBuild.IsDedicatedServer) return;
            try
            {
                // Both bodies were read at these addresses: AddHeldItem writes the item's id into the first empty
                // entry of the replicated array, RemoveHeldItem clears it. A game update that moves them leaves the
                // gate off, and SessionRefusal then keeps held mod items out of the hand in sessions without that
                // guarantee.
                var add = PlayerLifecycle.QualifyNative(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.AddHeldItem), 0x32C1440,
                    false, typeof(void), typeof(ItemInstance));
                var remove = PlayerLifecycle.QualifyNative(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.RemoveHeldItem), 0x32C23E0,
                    false, typeof(void), typeof(int));
                harmony.Patch(add, prefix: new HarmonyMethod(typeof(HeldItems), nameof(BeforeAddHeldItem)));
                harmony.Patch(remove, prefix: new HarmonyMethod(typeof(HeldItems), nameof(BeforeRemoveHeldItem)));
                _publicationGated = true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Held mod items can't be hidden from players who may lack them, so in "
                    + "multiplayer they are only held where every player is known to have them: " + error.Message);
            }
        }

        // Removals take the same check. It only turns from false to true within a session (a client learns the
        // server's policy at join), and the native removal tolerates an id it never added.
        private static bool Publishes(int itemId)
            => !Profiles.ContainsKey(itemId) || !BoltNetwork.isRunning || ContentAgreement.PeersShareContent;

        private static bool BeforeAddHeldItem(ItemInstance __0, bool __runOriginal)
        {
            if (!__runOriginal || __0 == null || __0.WasCollected || Publishes(__0._itemID)) return __runOriginal;
            LogOnce("withheld " + __0._itemID, "Other players won't see item " + __0._itemID
                + " in the hand: not every player in this session is known to have it.");
            return false;
        }

        private static bool BeforeRemoveHeldItem(int __0, bool __runOriginal) => __runOriginal && Publishes(__0);
    }
}
