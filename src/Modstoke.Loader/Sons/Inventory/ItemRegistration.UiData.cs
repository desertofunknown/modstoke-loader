// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public static partial class ItemRegistration
    {
        private static ItemUiData CloneUiData(ItemUiData source, NormalizedDefinition definition)
        {
            // ItemUiData is an IL2CPP managed object, not a Unity asset. Instantiating
            // ItemData alone does not establish independent nested UI ownership.
            var collections = new Il2CppSystem.Collections.Generic.List<int>();
            var sourceItems = source._collectionItems;
            if (sourceItems != null)
            {
                for (var index = 0; index < sourceItems.Count; index++)
                {
                    var itemId = sourceItems[index];
                    collections.Add(itemId == definition.BaseItemId ? definition.ItemId : itemId);
                }
            }
            return new ItemUiData
            {
                _itemId = definition.ItemId,
                _icon = source._icon,
                _outlineIcon = source._outlineIcon,
                _title = definition.DisplayName,
                _titlePlural = definition.DisplayName,
                _description = source._description,
                _descriptionShowFor = source._descriptionShowFor,
                _translationKey = TranslationKey(definition.ItemId),
                _grammaticalGender = source._grammaticalGender,
                _leftClick = source._leftClick,
                _rightClick = source._rightClick,
                _amountDisplay = source._amountDisplay,
                _collectionTotal = source._collectionTotal,
                _collectionItems = collections,
                _amountFormat = source._amountFormat,
                _showVolumeRecipeNameInDescription = source._showVolumeRecipeNameInDescription,
                _showGotItem = source._showGotItem,
                _showCollectedItem = source._showCollectedItem,
                _showCantCarryMoreItem = source._showCantCarryMoreItem,
                _hideItemNameOnCollection = source._hideItemNameOnCollection,
                _applyCustomActionText = source._applyCustomActionText,
                _leftActionCustomText = source._leftActionCustomText,
                _rightActionCustomText = source._rightActionCustomText,
                _uiColors = source._uiColors,
                _hasDescription = source._hasDescription
            };
        }
    }
}
