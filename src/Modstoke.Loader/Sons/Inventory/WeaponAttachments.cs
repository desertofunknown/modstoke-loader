// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using global::Sons.Crafting;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using TheForest.Utils;
using UnityEngine;
using NativeIngredient = global::Sons.Crafting.CraftingIngredient;
using NativeSlot = global::Sons.Items.Core.WeaponMod.Slot;
using Object = UnityEngine.Object;
using Types = global::Sons.Items.Core.Types;

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
    /// Registers mod items as native weapon mods. Attached mods live in the weapon's own module, so saves and
    /// held-weapon replication follow the game. Every peer must register the same specs. Each registration also
    /// gets the game's two crafting recipes: the weapon and the item combine on the crafting mat, and the weapon
    /// alone dismantles there and hands its mods back. As with the game's own, the weapon stays on the mat after
    /// either, and a mod taken off lies beside it. TryAttach and TryDetach change the local weapon directly,
    /// on hosts and clients alike; they refuse while strict inventory is armed, since those changes are not yet
    /// an authorised custody operation.
    /// </summary>
    public static class WeaponAttachments
    {
        private const string HashPrefix = "modstoke.weaponmod/1\n";
        private static readonly Dictionary<int, Registration> ByHash = new();
        private static readonly List<WeaponAttachmentContext> Live = new();
        private static readonly HashSet<int> MatWeapons = new();
        private static GameObject _templates;
        private static IntPtr _catalogSeen, _matSeen;
        private static bool _hooksAttempted, _weaponsOnMat, _listing, _listFailureLogged;
        private static float _recipesAt;

        private sealed class Registration
        {
            internal WeaponAttachmentSpec Spec;
            internal WeaponMod Mod;
            internal CraftingRecipe Apply, Listed;
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
                _weaponsOnMat = false;
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
            var inventory = LocalPlayer.Inventory;
            if (inventory.AmountOf(attachmentItemId, true, false) < 1)
            {
                status = "There is no item " + attachmentItemId + " in the inventory.";
                return false;
            }
            WeaponMod previous = null;
            foreach (var mod in mods)
                if (Alive(mod) && mod.AttachesToSlot == registration.Mod.AttachesToSlot) previous = mod;
            // The native calls also work with the inventory open, where native crafting applies weapon mods too.
            if (!inventory.RemoveItem(attachmentItemId, 1, false, true, true, null, false))
            {
                status = "Could not take the attachment from the inventory.";
                return false;
            }
            if (previous != null)
            {
                mods.Remove(previous);
                if (!inventory.AddItem(previous.ModItemId, 1, true, false, null))
                    RuntimeEntryPoint.LogSource?.LogWarning("Weapon mod item " + previous.ModItemId + " came off but could not return to the inventory.");
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
            if (!LocalPlayer.Inventory.AddItem(attachmentItemId, 1, true, false, null))
            {
                status = "There is no room for it in the inventory.";
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
                    _weaponsOnMat = false;
                }
                for (var i = Live.Count - 1; i >= 0; i--)
                {
                    var context = Live[i];
                    if (Alive(context.Attached)) continue;
                    Live.RemoveAt(i);
                    Notify(context.Spec.OnDetached, context);
                }
                if (Time.unscaledTime >= _recipesAt)
                {
                    _recipesAt = Time.unscaledTime + 1;
                    EnsureRecipes();
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Weapon attachment upkeep failed: " + error.Message);
            }
        }

        // The game's weapon mods go on by combining the weapon and the mod on the crafting mat and come off
        // by dismantling the weapon there. Every attachment gets the same two recipes, appended so the game's
        // own keep their places in the list. They carry no craft animation: the game has none for a weapon it
        // never modded, and a recipe without one completes at once, as its chainsaw recharge does. A new
        // recipe database (a new player or item database) is filled again.
        private static void EnsureRecipes()
        {
            var system = CraftingSystem._Instance_k__BackingField;
            if (!Alive(system) || !system._isInitialized || !Alive(system._recipeDatabase)) return;
            var recipes = system._recipeDatabase._recipes;
            if (recipes == null || recipes.WasCollected) return;
            if (system.Pointer != _matSeen || !_weaponsOnMat)
            {
                _matSeen = system.Pointer;
                _weaponsOnMat = PutWeaponsOnMat(system);
            }
            CraftingRecipe apply = null, dismantle = null;
            foreach (var recipe in recipes)
            {
                if (!Alive(recipe)) continue;
                if (recipe._recipeType == CraftingRecipe.Type.DismantleWeaponMods) dismantle ??= recipe;
                else if (recipe._recipeType == CraftingRecipe.Type.ApplyWeaponMod && Alive(recipe._weaponMod)
                    && !ByHash.ContainsKey(recipe._weaponMod.Id)) apply ??= recipe;
            }
            if (apply == null) return;
            var added = 0;
            foreach (var registration in ByHash.Values)
            {
                if (Alive(registration.Apply) && recipes.Contains(registration.Apply)) continue;
                if (!Alive(registration.Apply)) registration.Apply = ApplyRecipe(apply, registration);
                recipes.Add(registration.Apply);
                ReturnOnDismantle(recipes, dismantle, registration.Spec);
                added++;
            }
            if (added == 0) return;
            system.RefreshAvailableRecipes(false);
            RuntimeEntryPoint.LogSource?.LogInfo(added + " weapon attachment recipe(s) added to the crafting mat; " + recipes.Count
                + " recipes in all.");
        }

        // The game moves an item to the crafting mat only if it is a crafting material, and prompts for it only
        // if its right click says so. A weapon comes back from the mat as the same instance only if its row
        // there keeps instances; otherwise a new one is made, without the ammunition and mods of the old. The
        // weapons the game mods itself are set up this way, and a weapon it never modded is given the same. One
        // with no row on the mat is left alone: the game has nowhere to lay it.
        private static bool PutWeaponsOnMat(CraftingSystem system)
        {
            var groups = system._ingredientLayoutItemGroups;
            if (groups == null || groups.WasCollected) return false;
            MatWeapons.Clear();
            foreach (var registration in ByHash.Values) MatWeapons.Add(registration.Spec.WeaponItemId);
            var settled = true;
            foreach (var group in groups)
            {
                if (!Alive(group) || !MatWeapons.Remove(group._itemId)) continue;
                var id = group._itemId;
                if (!ItemDatabaseManager.TryFindItemById(id, out var weapon) || !Alive(weapon))
                {
                    settled = false;
                    continue;
                }
                if (!group._isOneToOneCollection)
                {
                    // Not while it lies there: its row took it in without the instance.
                    if (system.GetIngredientCount(id) != 0)
                    {
                        settled = false;
                        continue;
                    }
                    group._isOneToOneCollection = true;
                }
                if ((weapon._type & Types.CraftingMaterial) != 0) continue;
                weapon._type |= Types.CraftingMaterial;
                var ui = weapon._uiData;
                if (ui != null && ui._rightClick == ItemUiData.RightClickCommands.None)
                    ui._rightClick = ItemUiData.RightClickCommands.Combine;
                RuntimeEntryPoint.LogSource?.LogInfo("Item " + id + " can go on the crafting mat to take its attachments.");
            }
            if (!settled) return false;
            foreach (var id in MatWeapons)
                RuntimeEntryPoint.LogSource?.LogWarning("Item " + id + " has no place on the crafting mat, so its attachments cannot be combined there.");
            return true;
        }

        // The list of recipes under the cog leaves weapon mods out. An attachment is listed through a stand-in,
        // an ordinary recipe with the same two ingredients that is never in the database. The list names a
        // recipe after its first result and shows it while any result still fits the inventory, so the stand-in
        // gives the attachment first and the weapon second: whichever of the two lies on the mat fits.
        private static bool BeforeRecipeList(HudGui __instance, bool __0, Il2CppSystem.Collections.Generic.IEnumerable<CraftingRecipe> __1)
        {
            if (_listing || !__0 || __1 == null || ByHash.Count == 0) return true;
            try
            {
                var list = __1.TryCast<Il2CppSystem.Collections.Generic.List<CraftingRecipe>>();
                if (list == null) return true;
                Il2CppSystem.Collections.Generic.List<CraftingRecipe> shown = null;
                for (var i = 0; i < list.Count; i++)
                {
                    var recipe = list[i];
                    var listed = Listed(recipe);
                    if (listed == null)
                    {
                        shown?.Add(recipe);
                        continue;
                    }
                    if (shown == null)
                    {
                        shown = new();
                        for (var before = 0; before < i; before++) shown.Add(list[before]);
                    }
                    shown.Add(listed);
                }
                if (shown == null) return true;
                _listing = true;
                try
                {
                    __instance.ShowValidCraftingRecipes(true, shown.Cast<Il2CppSystem.Collections.Generic.IEnumerable<CraftingRecipe>>());
                }
                finally
                {
                    _listing = false;
                }
                return false;
            }
            catch (Exception error)
            {
                if (!_listFailureLogged)
                {
                    _listFailureLogged = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("Attachment recipes could not be listed under the cog: " + error.Message);
                }
                return true;
            }
        }

        private static CraftingRecipe Listed(CraftingRecipe recipe)
        {
            if (!Alive(recipe) || recipe._recipeType != CraftingRecipe.Type.ApplyWeaponMod || !Alive(recipe._weaponMod)
                || !ByHash.TryGetValue(recipe._weaponMod.Id, out var registration) || !Takes(registration)) return null;
            if (!Alive(registration.Listed))
            {
                var spec = registration.Spec;
                var listed = Clone(recipe, spec.ModId + ":" + spec.Key + " listed", CraftingRecipe.Type.CraftNewItem, spec.WeaponItemId);
                listed._ingredients.Add(new NativeIngredient { ItemId = spec.AttachmentItemId, Count = 1, IsReusable = false });
                listed._resultingItems.Insert(0, new CraftingRecipe.ResultingItem { Id = spec.AttachmentItemId });
                listed._weaponMod = null;
                registration.Listed = listed;
            }
            return registration.Listed;
        }

        // A weapon on the mat with the slot already taken cannot take another attachment there.
        private static bool Takes(Registration registration)
        {
            var system = CraftingSystem._Instance_k__BackingField;
            var ingredients = Alive(system) ? system._CurrentIngredients_k__BackingField : null;
            if (ingredients == null || ingredients.WasCollected) return true;
            foreach (var ingredient in ingredients)
                if (ingredient != null && ingredient.ItemId == registration.Spec.WeaponItemId)
                    return ingredient.ItemInstance is not { } weapon
                        || !weapon.TryGetModule<RangedWeaponItemInstanceModule>(out var module) || module == null
                        || module.IsSlotAvailable(registration.Mod.AttachesToSlot);
            return true;
        }

        private static CraftingRecipe ApplyRecipe(CraftingRecipe template, Registration registration)
        {
            var spec = registration.Spec;
            var recipe = Clone(template, spec.ModId + ":" + spec.Key + " apply", CraftingRecipe.Type.ApplyWeaponMod, spec.WeaponItemId);
            recipe._ingredients.Add(new NativeIngredient { ItemId = spec.AttachmentItemId, Count = 1, IsReusable = false });
            recipe._weaponMod = registration.Mod;
            return recipe;
        }

        // The weapon's dismantle recipe lists every item it can hand back. The game's weapons have one; a weapon
        // it never modded gets its own.
        private static void ReturnOnDismantle(Il2CppSystem.Collections.Generic.List<CraftingRecipe> recipes, CraftingRecipe template,
            WeaponAttachmentSpec spec)
        {
            CraftingRecipe dismantle = null;
            foreach (var recipe in recipes)
                if (Alive(recipe) && recipe._recipeType == CraftingRecipe.Type.DismantleWeaponMods && recipe._resultingItems is { Count: > 0 } returned
                    && returned[0] != null && returned[0].Id == spec.WeaponItemId)
                {
                    dismantle = recipe;
                    break;
                }
            if (dismantle == null)
            {
                if (template == null) return;
                dismantle = Clone(template, "Modstoke dismantle " + spec.WeaponItemId, CraftingRecipe.Type.DismantleWeaponMods, spec.WeaponItemId);
                dismantle._weaponMod = null;
                recipes.Add(dismantle);
            }
            foreach (var result in dismantle._resultingItems)
                if (result != null && result.Id == spec.AttachmentItemId) return;
            dismantle._resultingItems.Add(new CraftingRecipe.ResultingItem { Id = spec.AttachmentItemId });
        }

        // A copy of one of the game's recipes, for its audio and settings, that takes the weapon and gives it back.
        private static CraftingRecipe Clone(CraftingRecipe template, string name, CraftingRecipe.Type type, int weaponItemId)
        {
            var recipe = Object.Instantiate(template);
            recipe.name = name;
            recipe.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            recipe._recipeType = type;
            recipe._animationStateName = "";
            recipe._hasCustomName = false;
            recipe._useContainerDataForResultingItems = false;
            recipe._containerItemData = null;
            recipe._forceNewItemInstance = false;
            recipe._ingredients = new();
            recipe._ingredients.Add(new NativeIngredient { ItemId = weaponItemId, Count = 1, IsReusable = false });
            recipe._resultingItems = new();
            recipe._resultingItems.Add(new CraftingRecipe.ResultingItem { Id = weaponItemId });
            recipe._generatedResultingItems = new();
            return recipe;
        }

        private static Registration Find(int itemId)
        {
            foreach (var registration in ByHash.Values)
                if (registration.Spec.AttachmentItemId == itemId) return registration;
            return null;
        }

        // Like native weapon-mod crafting, the change is local; servers learn it from the held weapon's state.
        // Under strict inventory a local module edit would break the authorised baseline, so it is refused.
        private static bool TryHostChange(out string status)
        {
            status = !Alive(LocalPlayer.Inventory)
                ? "The local inventory is not available."
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
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-weapon-attachment-recipes");
                harmony.Patch(AccessTools.Method(typeof(HudGui), nameof(HudGui.ShowValidCraftingRecipes),
                        new[] { typeof(bool), typeof(Il2CppSystem.Collections.Generic.IEnumerable<CraftingRecipe>) }),
                    prefix: new HarmonyMethod(typeof(WeaponAttachments), nameof(BeforeRecipeList)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Attachment recipes will not be listed under the crafting cog: " + error.Message);
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
