// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Items.Core;
using global::Sons.Player;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Preparation owns these objects even when a mutation cannot be confirmed.
    // An inactive prepared clone is not an equipped or initialized weapon.
    internal sealed class StrictCombatEquipmentPrefab
    {
        private readonly ItemData _data;
        private readonly InventoryProps.PropDefinition _definition;
        private readonly Transform _parent, _locator;
        private readonly UnityEngine.Object _prefab;
        private readonly GameObject _prefabObject;
        private readonly Action _requireOperation;
        private bool _ready;

        private StrictCombatEquipmentPrefab(ItemData data, InventoryProps.PropDefinition definition,
            Transform parent, Action requireOperation)
        {
            _data = data;
            _definition = definition;
            _parent = parent;
            _requireOperation = requireOperation;
            _prefab = data._heldPrefab;
            _prefabObject = data._heldPrefab.gameObject;
            _locator = definition._heldLocator;
        }

        internal GameObject Root { get; private set; }
        internal GameObject Clone { get; private set; }
        internal bool RootCreationAttempted { get; private set; }
        internal bool CloneCreationAttempted { get; private set; }
        internal bool IsUncertain { get; private set; }

        internal static bool TryCreate(ItemData data, InventoryProps.PropDefinition definition,
            Transform parent, Action requireOperation, out StrictCombatEquipmentPrefab stage)
        {
            stage = null;
            try
            {
                if (requireOperation is null) return false;
                requireOperation();
                if (!Live(data) || data._id != 355 || !Live(data._heldPrefab)
                    || definition is null || definition.WasCollected || definition._itemId != 355
                    || !Live(definition._heldLocator) || !Live(parent)) return false;
                stage = new StrictCombatEquipmentPrefab(data, definition, parent, requireOperation);
                stage.RequireSources();
                stage.RootCreationAttempted = true;
                stage.Root = new GameObject("Modstoke weapon preparation");
                stage.RequireSources();
                if (!Live(stage.Root)) throw new InvalidOperationException("The preparation root is unavailable.");

                stage.RequireSources();
                stage.Root.SetActive(false);
                stage.RequireSources();
                stage.RequireInactiveRoot(false);

                stage.RequireSources();
                stage.Root.transform.SetParent(parent, false);
                stage.RequireSources();
                stage.RequireInactiveRoot(true);

                stage.RequireSources();
                stage.RequireInactiveRoot(true);
                stage.CloneCreationAttempted = true;
                stage.Clone = UnityEngine.Object.Instantiate<GameObject>(
                    stage._prefabObject, stage.Root.transform, false);
                stage.RequireSources();
                stage.RequireInactiveClone(false);

                stage.RequireSources();
                stage.RequireInactiveClone(false);
                stage.Clone.SetActive(false);
                stage.RequireSources();
                stage.RequireInactiveClone(true);

                stage.RequireSources();
                stage.RequireInactiveClone(true);
                InventoryProps.MatchLocalTransforms(stage.Clone.transform, stage._locator);
                stage.RequireSources();
                stage.RequireInactiveClone(true);
                stage._ready = true;
                return true;
            }
            catch (Exception)
            {
                if (stage is not null && stage.RootCreationAttempted) stage.IsUncertain = true;
                return false;
            }
        }

        internal bool IsCurrent()
        {
            if (!_ready || IsUncertain) return false;
            try
            {
                RequireSources();
                RequireInactiveClone(true);
                _requireOperation();
                return true;
            }
            catch (Exception) { return false; }
        }

        private void RequireSources()
        {
            _requireOperation();
            if (!Live(_data) || _data._id != 355
                || _definition is null || _definition.WasCollected || _definition._itemId != 355
                || !Live(_parent) || !Live(_locator) || !Same(_definition._heldLocator, _locator)
                || !Live(_prefab) || !Same(_data._heldPrefab, _prefab)
                || !Live(_prefabObject) || !Same(_data._heldPrefab.gameObject, _prefabObject))
                throw new InvalidOperationException("The equipment preparation sources changed.");
        }

        private void RequireInactiveRoot(bool parented)
        {
            if (!Live(Root) || !Live(Root.transform) || Root.activeSelf || Root.activeInHierarchy
                || (parented && !Same(Root.transform.parent, _parent)))
                throw new InvalidOperationException("The equipment preparation root changed.");
        }

        private void RequireInactiveClone(bool inactiveSelf)
        {
            RequireInactiveRoot(true);
            if (!Live(Clone) || Same(Clone, _prefabObject) || !Live(Clone.transform)
                || !Same(Clone.transform.parent, Root.transform) || Clone.activeInHierarchy
                || (inactiveSelf && Clone.activeSelf))
                throw new InvalidOperationException("The equipment preparation clone changed.");
        }

        private static bool Live(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value;

        private static bool Same(UnityEngine.Object first, UnityEngine.Object second)
            => first is not null && second is not null && first.Pointer == second.Pointer;
    }
}
