// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using global::Sons.Weapon;
using UnityEngine;
using NativeItems = Il2CppSystem.Collections.Generic.List<global::Sons.Items.Core.ItemData>;
using NativeItemCache = Il2CppSystem.Collections.Generic.Dictionary<int, global::Sons.Items.Core.ItemData>;
using Object = UnityEngine.Object;
using Types = global::Sons.Items.Core.Types;

namespace Modstoke.Sons
{
    public static partial class ItemRegistration
    {
        private const int FeatherItemId = 479;
        private const Types DefaultModelTypes = Types.CraftingMaterial | Types.Droppable;
        // Families that need held props, equipment slots or ammunition handling, which modeled items do not author.
        private const Types UnsupportedModelTypes = Types.Equippable | Types.Clothing | Types.Ammo | Types.Projectile
            | Types.StructureBlueprint | Types.RangedWeapon | Types.UniqueItem | Types.Weapon | Types.Armor | Types.Axe;

        /// <summary>
        /// Authors a counted item from the mod's own model: item data, an inventory mat layout that follows stock
        /// and, when the definition is spawnable, a world pickup. Ids, keys and reservations follow the same rules
        /// as the other overloads; nothing is replaced or hot-unregistered.
        /// </summary>
        /// <remarks>
        /// The definition must be a <see cref="ItemRegistrationKind.CustomItem"/> without hotkeys or asset
        /// references. Its item type defaults to "CraftingMaterial, Droppable"; equipment, weapon and ammunition
        /// types are refused. A positive BaseItemId picks the item whose remaining data (stack sounds, UI colours,
        /// grammar) is copied; otherwise the feather is used.
        /// </remarks>
        public static ItemRegistrationResult ExecuteItemRegistration(CustomItemDefinition definition, ItemModel model)
            => model == null
                ? Result(false, false, false, "missing-item-model", "An item model is required.")
                : ExecuteRegistration(definition, null, false, 0, model);

        private sealed class ModelSources
        {
            internal ItemData Data;
            internal PickUp Pickup;
            internal Types Type;
        }

        private static bool TryPrepareModel(NormalizedDefinition definition, ItemModel model, ItemData baseItem,
            NativeItemCache cache, NativeItems items, out ModelSources sources, out string reason, out string detail)
        {
            sources = null;
            reason = "unsupported-item-model";
            if (definition.Kind != ItemRegistrationKind.CustomItem || definition.CanBeHotKeyed
                || definition.AssetReferenceKind != "none" || definition.AssetReference.Length != 0)
            {
                detail = "Modeled items are custom items without hotkeys or asset references; the model supplies the assets.";
                return false;
            }
            if (model.MaxAmount < 1 || !(model.MatScale > 0) || float.IsInfinity(model.MatScale)
                || !Finite(model.MatOffset) || !Finite(model.MatRotation))
            {
                detail = "The model needs a positive stack size and a finite mat placement and scale.";
                return false;
            }

            var type = DefaultModelTypes;
            if (definition.ItemType.Length != 0)
            {
                if (!Enum.TryParse(definition.ItemType, true, out type)
                    || !string.Equals(type.ToString(), definition.ItemType, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "invalid-item-type";
                    detail = "Item types use the native flag names in value order, for example \"CraftingMaterial, Droppable\".";
                    return false;
                }
            }
            if ((type & UnsupportedModelTypes) != 0)
            {
                detail = "Equipment, weapon and ammunition types need held models, which modeled items do not author yet.";
                return false;
            }
            if (definition.CanBeSpawned && (type & Types.Droppable) == 0)
            {
                reason = "invalid-item-type";
                detail = "Spawnable items must include the Droppable type.";
                return false;
            }

            var data = Live(baseItem) ? baseItem : cache.TryGetValue(FeatherItemId, out var feather) ? feather : null;
            if (!Live(data) || data._uiData == null || data._eachInstanceIsUnique)
            {
                reason = "model-template-unavailable";
                detail = "The base item, or the feather when none is given, must be a live counted item with UI data.";
                return false;
            }

            PickUp pickup = null;
            if (definition.CanBeSpawned)
            {
                // Native item order is stable for a game build, so every peer settles on the same donor.
                for (var index = 0; index < items.Count && pickup == null; index++)
                    pickup = PlainPickup(items[index]);
                if (pickup == null)
                {
                    reason = "model-pickup-unavailable";
                    detail = "No native item has a plain single-item pickup to build the world pickup from.";
                    return false;
                }
            }
            if (!TranslationKeysFree(definition.ItemId, out reason, out detail))
                return false;

            sources = new ModelSources { Data = data, Pickup = pickup, Type = type };
            reason = "ready";
            detail = "";
            return true;
        }

        // A pickup whose only job is to add one item: no weapon state, extra rewards, spawns or bundles.
        private static PickUp PlainPickup(ItemData item)
        {
            if (!Live(item) || item._eachInstanceIsUnique || !Live(item._pickupPrefab) || !IsPrefabSource(item._pickupPrefab))
                return null;
            var prefab = item._pickupPrefab;
            var pickup = prefab.GetComponent<PickUp>();
            if (!Live(pickup) || IL2CPP.il2cpp_object_get_class(pickup.Pointer) != Il2CppClassPointerStore<PickUp>.NativeClassPtr
                || pickup._itemId != item._id || pickup._infinite || pickup.ItemInstance != null
                || prefab.GetComponentsInChildren<PickUp>(true).Length != 1 || !Live(prefab.GetComponent<Rigidbody>()))
                return null;
            var amount = pickup._amount;
            if (amount == null || amount._min != 1 || amount._max != 1
                || pickup._spawnSpiders || Live(pickup._spawnOnPickup) || Live(pickup._spawnAfterPickupPrefab)
                || Live(pickup._spawnAfterPickupPrefab2) || Live(pickup._spawnAfterPickupTr)
                || pickup._additionalItems != null && pickup._additionalItems.Count != 0)
                return null;
            return Live(prefab.GetComponentInChildren<RangedWeapon>(true)) || Live(prefab.GetComponentInChildren<WeaponMods>(true))
                ? null : pickup;
        }

        private static void CreateModeledItem(NormalizedDefinition definition, ItemModel model, ModelSources sources,
            RuntimeRegistration registration)
        {
            var item = registration.Item = Object.Instantiate(sources.Data);
            item.name = definition.ModId + ":" + definition.ItemKey;
            item._id = definition.ItemId;
            item._name = item._editorName = definition.DisplayName;
            item._type = sources.Type;
            item._maxAmount = model.MaxAmount;
            item._eachInstanceIsUnique = false;
            item._canBeHotKeyed = item._allowQuickSelect = false;
            item._canBeSpawned = definition.CanBeSpawned;
            item._excludeFromSpawnAll = true;
            item._canNotBeStored = false;
            item._preventEquipFromInventoryView = true;
            item._alwaysDropOnUnequip = false;
            item._hasFirstLook = false;
            item._isPerishable = item._isVolumeContainer = item._isDismemberedLimb = false;
            item._isGPSLocator = item._canBePlated = item._isUpgradeableArrow = false;
            item._hasBloodReveal = item._hasVisualVariant = item._isStructureElement = false;
            item._pickupPrefab = item._pickupBundlePrefab = item._heldPrefab = item._propPrefab = null;
            item._pickupCanBeBundled = false;
            item._ownedIfOtherItemIsOwned = new();
            // Select allocation by length, not the native-pointer constructor.
            item._ownedStatEffect = new(size: 0);
            item._equippedStatEffect = new(size: 0);
            item._usedStatEffect = new(size: 0);
            item._tags = new();
            foreach (var tag in definition.Tags) item._tags.Add(tag);
            item._aliases = new();

            var ui = item._uiData = CloneUiData(sources.Data._uiData, definition);
            ui._collectionItems = new();
            ui._description = model.Description ?? "";
            ui._hasDescription = ui._description.Length != 0;
            if (Live(model.Icon)) ui._icon = ui._outlineIcon = model.Icon;
            item.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            if (definition.CanBeSpawned)
                item._pickupPrefab = CreateModelPickup(definition, model, sources.Pickup, registration);
            AddItemTranslations(definition, registration);
        }

        private static Transform CreateModelPickup(NormalizedDefinition definition, ItemModel model, PickUp donor,
            RuntimeRegistration registration)
        {
            // Built below an inactive root so no pickup Awake runs before the new identity is bound.
            var root = registration.PrefabRoot = new GameObject(registration.Item.name + " prefabs");
            root.SetActive(false);
            Object.DontDestroyOnLoad(root);
            var prefab = Object.Instantiate(donor.gameObject, root.transform, false);
            prefab.name = registration.Item.name + " pickup";
            var pickup = prefab.GetComponent<PickUp>();
            // The donor's renderable child is placed and scaled for the donor's model, so the mod model sits on the
            // root and only borrows the layer the donor renders on.
            var layer = (Live(pickup._itemRenderable) ? pickup._itemRenderable.gameObject : prefab).layer;

            // The donor's look: lazily loaded renderables, LOD groups, renderers and solid colliders.
            foreach (var renderable in prefab.GetComponentsInChildren<ItemRenderable>(true))
                Object.DestroyImmediate(renderable);
            foreach (var lod in prefab.GetComponentsInChildren<LODGroup>(true))
                Object.DestroyImmediate(lod);
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                Object.DestroyImmediate(renderer);
            foreach (var collider in prefab.GetComponentsInChildren<Collider>(true))
                if (!collider.isTrigger) Object.DestroyImmediate(collider);
            pickup._itemRenderable = null;
            pickup._rigidBodyCache = null;

            // One solid box on the root keeps physics simple and stays on the layer the game's interaction rays hit.
            var visual = ItemModelCopies.Create(model.PickupModel ?? model.Model, prefab.transform, layer);
            var bounds = ItemModelCopies.LocalBounds(prefab.transform, visual);
            var box = prefab.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = bounds.size;

            pickup._itemId = definition.ItemId;
            pickup._itemDataCached = registration.Item;
            pickup.ItemInstance = null;
            foreach (var tag in prefab.GetComponentsInChildren<ItemLocatorTag>(true))
            {
                tag._itemId = definition.ItemId;
                tag._name = definition.DisplayName;
            }
            prefab.SetActive(true);

            // Keep the donor's pickup entity and state, under a prefab id every peer derives from the item id,
            // so hosts can network the pickup and clients resolve it to this template.
            var entity = prefab.GetComponent<BoltEntity>();
            if (Live(entity) && definition.ItemId >= ItemIds.FirstModItemId && ItemIds.IsNetworked(definition.ItemId))
            {
                entity._prefabId = PickupPrefabId(definition.ItemId);
                registration.NetworkEntity = entity;
                NetworkPrefabs.Instance.RegisterPrefab(entity, null);
                RuntimeEntryPoint.LogSource?.LogInfo("Item " + definition.ItemId + " pickup is Bolt prefab " + entity._prefabId
                    + ", built from item " + donor._itemId + "'s pickup.");
            }
            else
            {
                // Client-local items never travel, so their pickups stay plain local objects.
                foreach (var behaviour in prefab.GetComponentsInChildren<EntityBehaviour>(true))
                    Object.DestroyImmediate(behaviour);
                foreach (var stale in prefab.GetComponentsInChildren<BoltEntity>(true))
                    Object.DestroyImmediate(stale);
            }
            return prefab.transform;
        }

        // Above the native prefab range and below 65536, one id per networked mod item id.
        private const int FirstPickupPrefabId = 33000;

        private static int PickupPrefabId(int itemId) => FirstPickupPrefabId + itemId - ItemIds.FirstModItemId;

        private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static bool Finite(Quaternion value) => float.IsFinite(value.x) && float.IsFinite(value.y)
            && float.IsFinite(value.z) && float.IsFinite(value.w) && value.x * value.x + value.y * value.y
            + value.z * value.z + value.w * value.w > 0.5f;
    }

    /// <summary>Inert copies of mod-supplied item models for SDK-owned mat and world objects.</summary>
    internal static class ItemModelCopies
    {
        internal static GameObject Create(Func<GameObject> factory, Transform parent, int layer)
        {
            var template = factory();
            if (template is null || template.WasCollected || !template)
                throw new InvalidOperationException("The item model factory returned no live object.");
            // Parents are inactive here, so the template's scripts never wake on the copy.
            var copy = Object.Instantiate(template, parent, false);
            copy.SetActive(true);
            foreach (var joint in copy.GetComponentsInChildren<Joint>(true))
                Object.DestroyImmediate(joint);
            foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
                Object.DestroyImmediate(behaviour);
            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
                Object.DestroyImmediate(body);
            foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
                Object.DestroyImmediate(collider);
            foreach (var transform in copy.GetComponentsInChildren<Transform>(true))
                transform.gameObject.layer = layer;
            return copy;
        }

        /// <summary>Mesh bounds of a model in another transform's local space, without needing active renderers.</summary>
        internal static Bounds LocalBounds(Transform space, GameObject model)
        {
            var found = false;
            var bounds = new Bounds();
            void Add(Transform owner, Bounds local)
            {
                var min = local.min;
                var max = local.max;
                for (var corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3((corner & 1) == 0 ? min.x : max.x, (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z);
                    point = space.InverseTransformPoint(owner.TransformPoint(point));
                    if (found) bounds.Encapsulate(point);
                    else
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        found = true;
                    }
                }
            }
            foreach (var filter in model.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh is { } mesh && mesh) Add(filter.transform, mesh.bounds);
            foreach (var skinned in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                Add(skinned.transform, skinned.localBounds);
            if (!found) return new Bounds(Vector3.zero, Vector3.one * 0.1f);
            // Keep thin parts grabbable.
            bounds.size = Vector3.Max(bounds.size, Vector3.one * 0.02f);
            return bounds;
        }
    }
}
