// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using TheForest.Utils;
using UnityEngine;
using NativeSlot = global::Sons.Items.Core.WeaponMod.Slot;
using Object = UnityEngine.Object;

namespace Modstoke.Sons
{
    /// <summary>Weapon mod slots, in the game's numbering.</summary>
    public enum WeaponAttachmentSlot
    {
        Rail = 1,
        Muzzle = 2,
        OnTopRail = 3,
        OnBottomRail = 4,
        AmmoClip = 5,
        Grip = 6
    }

    /// <summary>One mod item that attaches to one weapon item, through the game's own weapon mods.</summary>
    public sealed class WeaponAttachmentSpec
    {
        public WeaponAttachmentSpec(string modId, string key, int attachmentItemId, int weaponItemId, Func<GameObject> model)
        {
            ModId = modId ?? "";
            Key = key ?? "";
            AttachmentItemId = attachmentItemId;
            WeaponItemId = weaponItemId;
            Model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public string ModId { get; }
        public string Key { get; }
        public int AttachmentItemId { get; }
        public int WeaponItemId { get; }

        /// <summary>A template the mod keeps alive; the game instantiates copies on every weapon that carries it.</summary>
        public Func<GameObject> Model { get; }

        public WeaponAttachmentSlot Slot { get; init; } = WeaponAttachmentSlot.OnTopRail;

        /// <summary>Placement relative to the slot's locator on the weapon.</summary>
        public Vector3 Offset { get; init; }

        public Quaternion Rotation { get; init; } = Quaternion.identity;

        /// <summary>
        /// Parts of the weapon hidden while attached, matched as substrings of object names, for example
        /// "RifleScopeModel" for the hunting rifle's built-in red dot. They show again when it comes off.
        /// </summary>
        public IReadOnlyList<string> Hides { get; init; } = Array.Empty<string>();

        /// <summary>Runs on the Unity thread whenever a weapon object shows this attachment, local or remote.</summary>
        public Action<WeaponAttachmentContext> OnAttached { get; init; }

        /// <summary>Runs when it comes off, and when the weapon object carrying it is destroyed (unequip, drop, unload).</summary>
        public Action<WeaponAttachmentContext> OnDetached { get; init; }
    }

    public sealed class WeaponAttachmentContext
    {
        internal WeaponAttachmentContext(WeaponAttachmentSpec spec, GameObject weapon, GameObject attached, bool local)
        {
            Spec = spec;
            Weapon = weapon;
            Attached = attached;
            IsLocalPlayer = local;
        }

        public WeaponAttachmentSpec Spec { get; }

        /// <summary>The weapon object carrying the attachment. May already be destroyed in OnDetached.</summary>
        public GameObject Weapon { get; }

        /// <summary>The game's copy of the attachment model on this weapon. May already be destroyed in OnDetached.</summary>
        public GameObject Attached { get; }

        /// <summary>Whether the weapon belongs to the local player's own rig.</summary>
        public bool IsLocalPlayer { get; }

        /// <summary>Free for the mod's own per-attachment state.</summary>
        public object State { get; set; }
    }

    /// <summary>
    /// Registers mod items as native weapon mods and attaches them to weapons in the local inventory. Attached mods
    /// live in the weapon's own module, so saves and held-weapon replication follow the game. Every peer must
    /// register the same specs. Attaching and detaching are host and singleplayer only, and refuse while strict
    /// inventory is armed, since those changes are not yet an authorised custody operation.
    /// </summary>
    public static class WeaponAttachments
    {
        private const string HashPrefix = "modstoke.weaponmod/1\n";
        private static readonly Dictionary<int, Registration> ByHash = new();
        private static readonly List<WeaponAttachmentContext> Live = new();
        private static GameObject _templates;
        private static IntPtr _catalogSeen;
        private static bool _hooksAttempted;

        private sealed class Registration
        {
            internal WeaponAttachmentSpec Spec;
            internal WeaponMod Mod;
        }

        /// <summary>The native mod hash for a spec: the first four bytes, little-endian, of a SHA-256 of its identity.</summary>
        public static int HashOf(string modId, string key)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(HashPrefix + SaveData.NormalizeModId(modId) + "\n" + key));
            return BitConverter.ToInt32(bytes, 0);
        }

        /// <summary>
        /// Adds the spec to the game's weapon mod catalog. Both items must already be registered. Repeating an
        /// identical spec is accepted; a hash collision with another mod is refused, never bumped.
        /// </summary>
        public static bool TryRegister(WeaponAttachmentSpec spec, out string status)
        {
            status = "";
            try
            {
                if (spec == null || spec.ModId.Length == 0 || spec.Key.Length == 0 || !Enum.IsDefined(spec.Slot))
                {
                    status = "A spec with a mod id, key and known slot is required.";
                    return false;
                }
                var hash = HashOf(spec.ModId, spec.Key);
                if (ByHash.TryGetValue(hash, out var existing))
                {
                    var same = existing.Spec.AttachmentItemId == spec.AttachmentItemId && existing.Spec.WeaponItemId == spec.WeaponItemId
                        && string.Equals(SaveData.NormalizeModId(existing.Spec.ModId), SaveData.NormalizeModId(spec.ModId), StringComparison.Ordinal)
                        && existing.Spec.Key == spec.Key;
                    status = same ? "" : "Hash " + hash + " already belongs to " + existing.Spec.ModId + "/" + existing.Spec.Key + ".";
                    return same;
                }
                if (hash == 0 || !ItemDatabaseManager.TryFindItemById(spec.AttachmentItemId, out var item) || !Alive(item)
                    || !ItemDatabaseManager.TryFindItemById(spec.WeaponItemId, out var weapon) || !Alive(weapon))
                {
                    status = "Both the attachment and the weapon item must be registered first.";
                    return false;
                }
                if (!TryCatalog(out var catalog))
                {
                    status = "The game's weapon mod catalog is not ready.";
                    return false;
                }
                for (var i = 0; i < catalog.Count; i++)
                    if (Alive(catalog[i]) && catalog[i].Id == hash)
                    {
                        status = "Hash " + hash + " collides with native weapon mod " + catalog[i].name + ".";
                        return false;
                    }
                EnsureHooks();
                if (!Alive(_templates))
                {
                    _templates = new GameObject("Modstoke weapon attachments");
                    _templates.SetActive(false);
                    Object.DontDestroyOnLoad(_templates);
                }
                // Active under an inactive root, so the game's copies come out active and the template never draws.
                var holder = new GameObject(spec.ModId + ":" + spec.Key);
                holder.transform.SetParent(_templates.transform, false);
                var model = ItemModelCopies.Create(spec.Model, holder.transform, 0);
                model.transform.localPosition = spec.Offset;
                model.transform.localRotation = spec.Rotation;
                var mod = ScriptableObject.CreateInstance(Il2CppType.Of<WeaponMod>()).Cast<WeaponMod>();
                mod.name = holder.name;
                mod.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                mod.ModPrefab = holder;
                mod.ModItemId = spec.AttachmentItemId;
                mod.AttachesToSlot = (NativeSlot)(int)spec.Slot;
                mod.RequiredSlot = NativeSlot.None;
                mod._hashId = hash;
                catalog.Add(mod);
                _catalogSeen = catalog.Pointer;
                ByHash.Add(hash, new Registration { Spec = spec, Mod = mod });
                RuntimeEntryPoint.LogSource?.LogInfo("Item " + spec.AttachmentItemId + " attaches to item " + spec.WeaponItemId
                    + " as weapon mod " + hash + " on " + spec.Slot + ".");
                return true;
            }
            catch (Exception error)
            {
                status = "Could not register the attachment: " + error.Message;
                return false;
            }
        }

        public static bool IsAttachment(int itemId) => Find(itemId) != null;

        /// <summary>
        /// Moves one attachment item from the inventory onto a weapon it fits: the held one first, otherwise the
        /// first carried. An attachment already in that slot comes back to the inventory.
        /// </summary>
        public static bool TryAttach(int attachmentItemId, out string status)
        {
            if (!TryHostChange(out status)) return false;
            var registration = Find(attachmentItemId);
            if (registration == null)
            {
                status = "Item " + attachmentItemId + " is not a registered attachment.";
                return false;
            }
            if (!TryWeapon(registration.Spec.WeaponItemId, null, out var module, out status)) return false;
            var mods = module._weaponMods;
            if (mods.Contains(registration.Mod))
            {
                status = "It is already attached.";
                return false;
            }
            if (!Inventory.TryGetItemCount(attachmentItemId, out var count, out status) || count < 1)
            {
                status = "There is no item " + attachmentItemId + " in the inventory.";
                return false;
            }
            WeaponMod previous = null;
            foreach (var mod in mods)
                if (Alive(mod) && mod.AttachesToSlot == registration.Mod.AttachesToSlot) previous = mod;
            if (!Inventory.TryRemoveItem(attachmentItemId, 1, out var removal))
            {
                status = "Could not take the attachment from the inventory: " + removal.Detail;
                return false;
            }
            if (previous != null)
            {
                mods.Remove(previous);
                if (!Inventory.TryAddItem(previous.ModItemId, 1, out var returned))
                    RuntimeEntryPoint.LogSource?.LogWarning("Weapon mod item " + previous.ModItemId + " came off but could not return to the inventory: "
                        + returned.Detail);
            }
            mods.Add(registration.Mod);
            Refresh(module);
            status = "";
            return true;
        }

        /// <summary>Takes the attachment off the weapon carrying it and returns it to the inventory.</summary>
        public static bool TryDetach(int attachmentItemId, out string status)
        {
            if (!TryHostChange(out status)) return false;
            var registration = Find(attachmentItemId);
            if (registration == null)
            {
                status = "Item " + attachmentItemId + " is not a registered attachment.";
                return false;
            }
            if (!TryWeapon(registration.Spec.WeaponItemId, registration.Mod, out var module, out status)) return false;
            if (!Inventory.TryAddItem(attachmentItemId, 1, out var added))
            {
                status = "There is no room for it in the inventory: " + added.Detail;
                return false;
            }
            module._weaponMods.Remove(registration.Mod);
            Refresh(module);
            status = "";
            return true;
        }

        /// <summary>Whether a weapon the local player carries has this attachment on.</summary>
        public static bool IsAttached(int attachmentItemId)
        {
            var registration = Find(attachmentItemId);
            return registration != null && TryWeapon(registration.Spec.WeaponItemId, registration.Mod, out _, out _);
        }

        internal static void Tick()
        {
            if (ByHash.Count == 0) return;
            try
            {
                // A rebuilt item database brings a new catalog without our entries.
                if (TryCatalog(out var catalog) && catalog.Pointer != _catalogSeen)
                {
                    foreach (var registration in ByHash.Values)
                        if (Alive(registration.Mod) && !catalog.Contains(registration.Mod)) catalog.Add(registration.Mod);
                    _catalogSeen = catalog.Pointer;
                }
                for (var i = Live.Count - 1; i >= 0; i--)
                {
                    var context = Live[i];
                    if (Alive(context.Attached)) continue;
                    Live.RemoveAt(i);
                    Notify(context.Spec.OnDetached, context);
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Weapon attachment upkeep failed: " + error.Message);
            }
        }

        private static Registration Find(int itemId)
        {
            foreach (var registration in ByHash.Values)
                if (registration.Spec.AttachmentItemId == itemId) return registration;
            return null;
        }

        private static bool TryHostChange(out string status)
        {
            status = BoltNetwork.isRunning && !BoltNetwork.isServer
                ? "Attachments can only be changed by the host for now."
                : PlayerInventoryEnforcement.StrictInventoryArmed
                    ? "Strict inventory is armed; attachment changes are not an authorised custody operation yet."
                    : "";
            return status.Length == 0;
        }

        // The held weapon first, then any carried copy; with a mod, the copy that has it.
        private static bool TryWeapon(int weaponItemId, WeaponMod having, out RangedWeaponItemInstanceModule module, out string status)
        {
            module = null;
            var inventory = LocalPlayer.Inventory;
            if (!Alive(inventory) || !Alive(inventory._itemInstanceManager))
            {
                status = "The local inventory is not available.";
                return false;
            }
            var held = inventory.RightHandItem;
            if (held != null && !held.WasCollected && held._itemID == weaponItemId && Fits(held, having, out module))
            {
                status = "";
                return true;
            }
            foreach (var pair in inventory._itemInstanceManager._itemsMap)
            {
                var stack = pair.Value;
                if (stack == null || stack.WasCollected || stack.UniqueInstances == null) continue;
                foreach (var instance in stack.UniqueInstances)
                    if (instance != null && !instance.WasCollected && instance._itemID == weaponItemId && Fits(instance, having, out module))
                    {
                        status = "";
                        return true;
                    }
            }
            status = having == null ? "You are not carrying item " + weaponItemId + "."
                : "No carried item " + weaponItemId + " has it attached.";
            return false;
        }

        private static bool Fits(ItemInstance instance, WeaponMod having, out RangedWeaponItemInstanceModule module)
            => instance.TryGetModule<RangedWeaponItemInstanceModule>(out module) && module != null && !module.WasCollected
                && module._weaponMods != null && (having == null || module._weaponMods.Contains(having));

        // A weapon in the hand shows its new mods now; a stored one shows them when next equipped.
        private static void Refresh(RangedWeaponItemInstanceModule module)
        {
            if (Alive(module._weaponModsComponent)) module.SwitchMods();
        }

        private static bool TryCatalog(out Il2CppSystem.Collections.Generic.List<WeaponMod> catalog)
        {
            catalog = Alive(ItemDatabaseManager._instance) ? ItemDatabaseManager.WeaponMods : null;
            return catalog != null && !catalog.WasCollected;
        }

        private static void EnsureHooks()
        {
            if (_hooksAttempted) return;
            _hooksAttempted = true;
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-weapon-attachments");
                harmony.Patch(AccessTools.Method(typeof(WeaponMods), nameof(WeaponMods.ApplyMod), new[] { typeof(WeaponMod) }),
                    postfix: new HarmonyMethod(typeof(WeaponAttachments), nameof(AfterApplyMod)));
                harmony.Patch(AccessTools.Method(typeof(WeaponMods), nameof(WeaponMods.RemoveMod), new[] { typeof(WeaponMod) }),
                    prefix: new HarmonyMethod(typeof(WeaponAttachments), nameof(BeforeRemoveMod)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Weapon attachment hooks are unavailable; attachments still apply, "
                    + "but OnAttached/OnDetached and part hiding will not run: " + error.Message);
            }
        }

        private static void AfterApplyMod(WeaponMods __instance, WeaponMod __0)
        {
            try
            {
                if (__0 == null || !ByHash.TryGetValue(__0.Id, out var registration) || !Alive(__instance)) return;
                var attached = __instance.GetAttachedModObject(__0);
                if (!Alive(attached)) return;
                // The copy takes the layer of the weapon it sits on (first-person and world weapons render apart).
                var layer = attached.transform.parent != null ? attached.transform.parent.gameObject.layer : __instance.gameObject.layer;
                foreach (var part in attached.GetComponentsInChildren<Transform>(true)) part.gameObject.layer = layer;
                SetHidden(__instance.gameObject, attached, registration.Spec.Hides, true);
                foreach (var existing in Live)
                    if (existing.Attached.Pointer == attached.Pointer) return;
                var root = LocalPlayer.Transform;
                var context = new WeaponAttachmentContext(registration.Spec, __instance.gameObject, attached,
                    root != null && root && __instance.transform.IsChildOf(root));
                Live.Add(context);
                Notify(registration.Spec.OnAttached, context);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Weapon attachment apply hook failed: " + error.Message);
            }
        }

        private static void BeforeRemoveMod(WeaponMods __instance, WeaponMod __0)
        {
            try
            {
                if (__0 == null || !ByHash.TryGetValue(__0.Id, out var registration) || !Alive(__instance)) return;
                var attached = __instance.GetAttachedModObject(__0);
                SetHidden(__instance.gameObject, attached, registration.Spec.Hides, false);
                for (var i = Live.Count - 1; i >= 0; i--)
                    if (Alive(attached) && Live[i].Attached is { } known && known.Pointer == attached.Pointer)
                    {
                        var context = Live[i];
                        Live.RemoveAt(i);
                        Notify(registration.Spec.OnDetached, context);
                    }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Weapon attachment remove hook failed: " + error.Message);
            }
        }

        private static void SetHidden(GameObject weapon, GameObject attached, IReadOnlyList<string> names, bool hidden)
        {
            if (names.Count == 0 || !Alive(weapon)) return;
            foreach (var renderer in weapon.GetComponentsInChildren<Renderer>(true))
            {
                if (Alive(attached) && renderer.transform.IsChildOf(attached.transform)) continue;
                foreach (var name in names)
                    if (name.Length != 0 && renderer.name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        renderer.forceRenderingOff = hidden;
                        break;
                    }
            }
        }

        private static void Notify(Action<WeaponAttachmentContext> callback, WeaponAttachmentContext context)
        {
            if (callback == null) return;
            try { callback(context); }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Attachment callback for " + context.Spec.ModId + "/" + context.Spec.Key
                    + " failed: " + error);
            }
        }

        private static bool Alive(Object value) => value is not null && !value.WasCollected && value;

        private static bool Alive(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase value) => value is not null && !value.WasCollected;
    }
}
