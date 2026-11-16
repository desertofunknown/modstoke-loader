// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Multiplayer.Client;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class WeaponAttachments
    {
        private const float RemoteCheckSeconds = 0.25f;
        private static readonly List<CoopPlayerRemoteSetup> Remotes = new();
        private static readonly List<WeaponMod> Wanted = new();
        private static readonly HashSet<int> WithheldLogged = new();
        private static float _remotesAt;
        private static bool _findRemotes;

        // Remotes belong to the session, and so does the withheld line, which each session's log should show again.
        internal static void ResetForStart()
        {
            Remotes.Clear();
            WithheldLogged.Clear();
        }

        // The game writes a held weapon's mods into the owner's player state only as it goes into the hand
        // (BoltPlayerSetup.AddHeldItem does nothing for an item already held), so a change made in hand is written
        // here. Peers pick it up in ReconcileRemote; the host reads it with ServerWorld.TryReadHeldWeaponMods.
        private static void PublishHeld(RangedWeaponItemInstanceModule module)
        {
            if (!BoltNetwork.isRunning) return;
            try
            {
                var held = LocalPlayer.Inventory.RightHandItem;
                if (held == null || held.WasCollected || !held.TryGetModule<RangedWeaponItemInstanceModule>(out var heldModule)
                    || heldModule == null || heldModule.Pointer != module.Pointer) return;
                // A server-owned player is strict inventory, where TryHostChange already refused the change.
                var entity = LocalPlayer.Entity;
                if (entity == null || !entity || !entity.isAttached || !entity.isOwner) return;
                var state = entity.GetState<IPlayerState>();
                if (state == null || state.WasCollected || !WriteHeldMods(state, held._itemID, module._weaponMods)) return;
                RuntimeEntryPoint.LogSource?.LogInfo("Other players now see item " + held._itemID + " with "
                    + Describe(module._weaponMods) + (ContentAgreement.PeersShareContent || !HasOwn(module._weaponMods) ? "."
                        : "; mod attachments are left out, since not every player in this session is known to have them."));
            }
            catch (Exception error)
            {
                // The weapon in hand has already changed; only other players' view of it is late.
                NetworkFailures.Report("Publishing a held weapon's attachments", error);
            }
        }

        // The native writer copies every hash on the weapon into the state, ours too, which a peer without the mod
        // turns into an empty mod on that player's rifle. Strip them while not every peer is known to have them.
        private static void AfterAddHeldItem(BoltPlayerSetup __instance, ItemInstance __0)
        {
            try
            {
                if (!BoltNetwork.isRunning || ContentAgreement.PeersShareContent || __0 == null || __0.WasCollected
                    || !__0.TryGetModule<RangedWeaponItemInstanceModule>(out var module) || module == null
                    || !HasOwn(module._weaponMods)) return;
                var entity = __instance.entity;
                if (entity == null || !entity || !entity.isOwner) return;
                var state = entity.GetState<IPlayerState>();
                if (state == null || state.WasCollected || !WriteHeldMods(state, __0._itemID, module._weaponMods)) return;
                if (WithheldLogged.Add(__0._itemID))
                    RuntimeEntryPoint.LogSource?.LogInfo("Other players won't see the mod attachments on item " + __0._itemID
                        + ": not every player in this session is known to have them.");
            }
            catch (Exception error)
            {
                NetworkFailures.Report("Leaving mod attachments out of a held weapon's state", error);
            }
        }

        // One weapon's entry, written as the native writer (TryAddHeldItemWeaponMods) does, except that an empty list
        // clears the entry, which the native writer leaves stale, and that our hashes are withheld while not every
        // player is known to have them. Only the owner of the player entity may write it.
        private static bool WriteHeldMods(IPlayerState state, int itemId, Il2CppSystem.Collections.Generic.List<WeaponMod> mods)
        {
            if (!Holds(state, itemId)) return false;
            var entries = state.HeldItemsWeaponMods;
            int slot = -1, free = -1;
            for (var i = 0; i < entries.Length && slot < 0; i++)
            {
                var id = entries[i].ItemId;
                if (id == itemId) slot = i;
                else if (id == 0 && free < 0) free = i;
            }
            var count = mods?.Count ?? 0;
            if (slot < 0)
            {
                if (count == 0 || free < 0) return false;
                slot = free;
            }
            var entry = entries[slot];
            var ids = entry.WeaponModIds;
            var share = ContentAgreement.PeersShareContent;
            for (var i = 0; i < ids.Length; i++)
            {
                var mod = i < count ? mods[i] : null;
                var hash = !Alive(mod) || !share && ByHash.ContainsKey(mod.Id) ? 0 : mod.Id;
                if (ids[i] != hash) ids[i] = hash;
            }
            var owner = count == 0 ? 0 : itemId;
            if (entry.ItemId != owner) entry.ItemId = owner;
            return true;
        }

        /// <summary>The non-zero weapon-mod hashes a player state shows on one held item.</summary>
        internal static int[] ReadHeldModHashes(IPlayerState state, int itemId)
        {
            var entries = state.HeldItemsWeaponMods;
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].ItemId != itemId) continue;
                var ids = entries[i].WeaponModIds;
                var hashes = new List<int>(ids.Length);
                for (var j = 0; j < ids.Length; j++)
                    if (ids[j] != 0) hashes.Add(ids[j]);
                return hashes.ToArray();
            }
            return Array.Empty<int>();
        }

        internal static bool Holds(IPlayerState state, int itemId)
        {
            var held = state.HeldItems;
            for (var i = 0; i < held.Length; i++)
                if (held[i] == itemId) return true;
            return false;
        }

        // A remote rifle is built when the owner's HeldItems change and never again, so the copy here follows the
        // owner's replicated mod list itself: after an attachment change in hand, and after a mod reaches this game's
        // catalog later than the rifle was drawn (the native reader adds nothing usable for a hash it can't find).
        private static void AfterRemoteHeldItems(CoopPlayerRemoteSetup __instance)
        {
            try
            {
                if (!Alive(__instance)) return;
                Track(__instance);
                if (ByHash.Count > 0) ReconcileRemote(__instance);
            }
            catch (Exception error)
            {
                ReportRemoteFailure(error);
            }
        }

        // Rifles drawn before a registration, some perhaps before the hook above went in, are looked up once.
        private static void FindRemotesSoon()
        {
            _findRemotes = true;
            _remotesAt = 0;
        }

        private static void Track(CoopPlayerRemoteSetup setup)
        {
            if (!Remotes.Exists(known => known.Pointer == setup.Pointer)) Remotes.Add(setup);
        }

        private static void TickRemotes()
        {
            if (Time.unscaledTime < _remotesAt) return;
            _remotesAt = Time.unscaledTime + RemoteCheckSeconds;
            try
            {
                if (_findRemotes)
                {
                    _findRemotes = false;
                    if (BoltNetwork.isRunning && !GameBuild.IsDedicatedServer)
                        foreach (var setup in UnityEngine.Object.FindObjectsOfType<CoopPlayerRemoteSetup>()) Track(setup);
                }
                if (Remotes.Count == 0) return;
                Remotes.RemoveAll(setup => !Alive(setup));
                foreach (var setup in Remotes) ReconcileRemote(setup);
            }
            catch (Exception error)
            {
                ReportRemoteFailure(error);
            }
        }

        private static void ReconcileRemote(CoopPlayerRemoteSetup setup)
        {
            var entity = setup.entity;
            var items = setup._equippedItemInstances;
            if (entity == null || !entity || !entity.isAttached || items == null || !TryCatalog(out var catalog)) return;
            var state = entity.GetState<IPlayerState>();
            if (state == null || state.WasCollected) return;
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.WasCollected || !item.TryGetModule<RangedWeaponItemInstanceModule>(out var module)
                    || module == null || module._weaponMods == null || !Alive(module._weaponModsComponent)) continue;
                Wanted.Clear();
                foreach (var hash in ReadHeldModHashes(state, item._itemID))
                {
                    var known = Find(catalog, hash);
                    if (known != null && !Wanted.Exists(mod => mod.Pointer == known.Pointer)) Wanted.Add(known);
                }
                var mods = module._weaponMods;
                if (Shows(mods, Wanted)) continue;
                mods.Clear();
                foreach (var mod in Wanted) mods.Add(mod);
                module.SwitchMods();
                RuntimeEntryPoint.LogSource?.LogInfo("Another player's item " + item._itemID + " now shows " + Describe(mods)
                    + ", as their game reports.");
            }
        }

        private static WeaponMod Find(Il2CppSystem.Collections.Generic.List<WeaponMod> catalog, int hash)
        {
            for (var i = 0; i < catalog.Count; i++)
                if (Alive(catalog[i]) && catalog[i].Id == hash) return catalog[i];
            return null;
        }

        private static bool Shows(Il2CppSystem.Collections.Generic.List<WeaponMod> mods, List<WeaponMod> wanted)
        {
            if (mods.Count != wanted.Count) return false;
            for (var i = 0; i < wanted.Count; i++)
                if (!Alive(mods[i]) || mods[i].Pointer != wanted[i].Pointer) return false;
            return true;
        }

        private static bool HasOwn(Il2CppSystem.Collections.Generic.List<WeaponMod> mods)
        {
            if (mods == null) return false;
            for (var i = 0; i < mods.Count; i++)
                if (Alive(mods[i]) && ByHash.ContainsKey(mods[i].Id)) return true;
            return false;
        }

        private static string Describe(Il2CppSystem.Collections.Generic.List<WeaponMod> mods)
        {
            var names = new List<string>();
            for (var i = 0; i < (mods?.Count ?? 0); i++)
                if (Alive(mods[i])) names.Add(mods[i].name);
            return names.Count == 0 ? "no weapon mods" : "weapon mods " + string.Join(", ", names);
        }

        private static void ReportRemoteFailure(Exception error)
            => NetworkFailures.Report("Bringing other players' weapon attachments up to date", error);
    }
}
