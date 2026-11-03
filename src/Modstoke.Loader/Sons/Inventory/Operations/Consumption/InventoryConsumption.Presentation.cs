// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Common;
using global::Sons.Inventory;
using global::Sons.Player;
using TheForest.Items.Inventory;
using TheForest.Player.Actions;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class InventoryConsumption
    {
        // Native animation owns the temporary prop, animator, camera and callbacks. It never
        // authorizes custody or stats; the endpoint admits its exact receipt-bound calls.
        internal sealed class Presentation
        {
            internal readonly PlayerInventory Inventory;
            internal readonly InventoryEdibleItem Edible;
            internal readonly ItemInstance Item;
            internal readonly PlayerConsumeItemAction Action;
            internal readonly FirstPersonCharacter Character;
            internal readonly Animator Animator;
            internal readonly PlayerConsumeItemAction.OnConsumeComplete Callback;
            internal readonly bool Animate;
            internal readonly VolumeContainerItemInstanceModule Volume;
            private readonly GameObject _specialActions, _hidden, _cursor;
            private readonly PlayerAnimatorControl _animControl;
            private readonly global::Sons.Cutscenes.InventoryCutscene _cutscene;
            private readonly InventoryCameraController _camera;
            private readonly InventoryProps _props;
            private readonly Il2CppSystem.Collections.Generic.Dictionary<int, GameObject> _propInstances;
            private readonly InventoryProps.PropDefinition _definition;
            private readonly Transform _propLocator;
            private readonly Il2CppSystem.Action<GameObject, InventoryProps.PropDefinition, ItemInstance> _initialized;
            private readonly int _itemId, _uses;
            private readonly bool _reusable;
            private GameObject _prop;
            private bool _observing;
            internal bool PropInitialized { get; private set; }
            internal bool CallbackIdentityFault { get; private set; }

            private Presentation(PlayerInventory inventory, InventoryEdibleItem edible, ItemInstance item,
                PlayerConsumeItemAction action, IntPtr callbackMethod)
            {
                Inventory = inventory; Edible = edible; Item = item; Action = action;
                Character = LocalPlayer.FpCharacter; Animator = LocalPlayer.Animator;
                _specialActions = LocalPlayer.SpecialActions; _animControl = LocalPlayer.AnimControl;
                _cutscene = inventory._inventoryCutscene; _camera = _cutscene._inventoryCameraController;
                _props = inventory.InventoryProps; _hidden = edible._objectToHideDuringConsumeAnimation;
                _propInstances = _props._gameObjectInstances;
                _cursor = action._cursorState; Animate = edible._shouldAnimate;
                _itemId = item._itemID; _uses = edible._numberOfUses; _reusable = edible._isReusable;
                Volume = _reusable ? item._modules[0].TryCast<VolumeContainerItemInstanceModule>() : null;
                _definition = Animate ? _props.GetPropDefinitionForItem(_itemId) : null;
                _propLocator = _definition?._heldLocator;
                // This is the native delegate constructor used by TryPerformAction, not an
                // arbitrary managed completion callback claiming native animation finished.
                Callback = new PlayerConsumeItemAction.OnConsumeComplete(edible, callbackMethod);
                _initialized = (System.Action<GameObject, InventoryProps.PropDefinition, ItemInstance>)ObserveInitialized;
            }

            internal static bool TryCapture(PlayerInventory inventory, InventoryEdibleItem edible, ItemInstance item,
                out Presentation presentation)
            {
                presentation = null;
                if (!Live(inventory) || !Live(edible) || item == null || item.WasCollected
                    || !Live(LocalPlayer.SpecialActions) || !Live(inventory._inventoryCutscene)
                    || !Live(inventory._inventoryCutscene._inventoryCameraController) || !Live(inventory.InventoryProps)
                    || item._modules == null || item._modules.WasCollected || item._modules.Count > 1
                    || (edible._isReusable && (item._modules.Count != 1 || edible._numberOfUses <= 0))) return false;
                var action = LocalPlayer.SpecialActions.GetComponentInChildren<PlayerConsumeItemAction>();
                if (!Live(action)) return false;
                var method = typeof(InventoryEdibleItem).GetMethod(nameof(InventoryEdibleItem.OnConsumeComplete), Type.EmptyTypes);
                var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                if (field?.GetValue(null) is not IntPtr info || info == IntPtr.Zero) return false;
                var candidate = new Presentation(inventory, edible, item, action, info);
                if (!candidate.CanStart()) return false;
                presentation = candidate;
                return true;
            }

            internal bool ContextCurrent()
                => Live(Inventory) && Live(Edible) && Live(Action) && Live(Character) && Live(Animator)
                    && Live(_specialActions) && Live(_animControl) && Live(_cutscene) && Live(_camera) && Live(_props)
                    && LocalPlayer.Inventory?.Pointer == Inventory.Pointer
                    && LocalPlayer.SpecialActions?.Pointer == _specialActions.Pointer
                    && _specialActions.GetComponentInChildren<PlayerConsumeItemAction>()?.Pointer == Action.Pointer
                    && LocalPlayer.FpCharacter?.Pointer == Character.Pointer && LocalPlayer.Animator?.Pointer == Animator.Pointer
                    && LocalPlayer.AnimControl?.Pointer == _animControl.Pointer
                    && Inventory.InventoryProps?.Pointer == _props.Pointer && Inventory._inventoryCutscene?.Pointer == _cutscene.Pointer
                    && _cutscene._inventoryCameraController?.Pointer == _camera.Pointer
                    && Inventory._currentView == PlayerInventory.PlayerViews.Inventory
                    && _cutscene.isActiveAndEnabled && _cutscene._initialized && _cutscene._isPlayingLocally
                    && !_cutscene._isShuttingDown && !_cutscene._isCleaningUp && !_cutscene._inQuickSelectMode
                    && !_cutscene._inConstructionMaterialMode && _cutscene._playerAnimator?.Pointer == Animator.Pointer
                    && _propInstances != null && !_propInstances.WasCollected
                    && _props._gameObjectInstances?.Pointer == _propInstances.Pointer
                    && Edible._shouldAnimate == Animate && Edible._numberOfUses == _uses && Edible._isReusable == _reusable
                    && SameOptional(Edible._objectToHideDuringConsumeAnimation, _hidden) && SameOptional(Action._cursorState, _cursor)
                    && Item != null && !Item.WasCollected && Item._itemID == _itemId
                    && (!Animate || (_definition != null && !_definition.WasCollected && _definition._itemId == _itemId
                        && Live(_propLocator) && _definition._heldLocator?.Pointer == _propLocator.Pointer
                        && _props.GetPropDefinitionForItem(_itemId)?.Pointer == _definition.Pointer))
                    && (!Animate || (Action.isActiveAndEnabled && Animator.enabled && float.IsFinite(Animator.speed)
                        && Animator.speed > 0 && Animator.layerCount > 9 && Live(_cursor) && !_animControl._isSwimming
                        && float.IsFinite(Action._inventoryUpperBodyBlendTime) && Action._inventoryUpperBodyBlendTime >= 0))
                    && !CallbackIdentityFault;

            internal bool CanStart()
                => ContextCurrent() && !Action._isConsuming && !Action._updateHeldAnimator
                    && !Live(Action._heldInstance) && !Live(Action._heldAnimator)
                    && !Character._isInMidAction && !LocalPlayer.IsInMidAction
                    && !Inventory._suppressRightHandFirstLookAts && !Inventory._suppressLeftHandFirstLookAts
                    && !_camera._followingPlayerCamera && (!Animate || !_cursor.activeSelf)
                    && (!Live(_hidden) || _hidden.activeSelf)
                    && _props._leftHandWideBlendCoroutine is null
                    && !_propInstances.ContainsKey(_itemId)
                    && (!_reusable || (Volume != null && !Volume.WasCollected && Volume._isInstantiated));

            internal void Invoke()
            {
                if (!CanStart()) throw new InvalidOperationException("Native consumption presentation is no longer idle.");
                if (Animate)
                {
                    _observing = true;
                    _props.add_OnHeldItemInitialized(_initialized);
                    _camera._followingPlayerCamera = true;
                }
                Edible._volumeModule = Volume;
                Action.ConsumeItem(Item, Animate, Callback);
                // TryPerformAction hides after entry, including synchronous nonanimated use.
                if (Live(_hidden)) _hidden.SetActive(false);
            }

            private void ObserveInitialized(GameObject held, InventoryProps.PropDefinition definition, ItemInstance item)
            {
                try
                {
                    if (item?.Pointer != Item.Pointer) return;
                    if (!_observing || PropInitialized || !ContextCurrent() || !Live(held) || definition?.Pointer != _definition.Pointer)
                    { CallbackIdentityFault = true; return; }
                    _prop = held;
                    PropInitialized = true;
                }
                catch { CallbackIdentityFault = true; }
            }

            internal bool NativeIdentityCurrent()
                => ContextCurrent() && Action._itemInstance?.Pointer == Item.Pointer
                    && Action._onConsumeComplete?.Pointer == Callback.Pointer
                    && (Volume == null ? Edible._volumeModule == null : Edible._volumeModule?.Pointer == Volume.Pointer);

            internal bool IsVisuallySettled()
                => NativeIdentityCurrent() && !Action._isConsuming && !Action._updateHeldAnimator
                    && !Live(Action._heldInstance) && !Live(Action._heldAnimator) && !Character._isInMidAction
                    && !_camera._followingPlayerCamera && (!Live(_hidden) || _hidden.activeSelf == Animate)
                    && !Inventory._suppressRightHandFirstLookAts && !Inventory._suppressLeftHandFirstLookAts
                    && (!Animate || (PropInitialized && !Live(_prop) && !_cursor.activeSelf && Animator.GetLayerWeight(9) == 0))
                    && _props._leftHandWideBlendCoroutine is null
                    && !_propInstances.ContainsKey(_itemId);

            internal void Retire(bool stopPending)
            {
                // Stop only the captured action. Never run cleanup against a replacement
                // LocalPlayer or synthesize the missing native completion callback.
                try { if (stopPending && Live(Action)) Action.StopAllCoroutines(); }
                finally { ReleaseObserver(); }
            }

            internal void ReleaseObserver()
            {
                if (!_observing) return;
                if (!Live(_props)) throw new InvalidOperationException("Consumption prop receiver disappeared before unsubscription.");
                _props.remove_OnHeldItemInitialized(_initialized);
                _observing = false;
            }

            private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
            private static bool SameOptional(UnityEngine.Object left, UnityEngine.Object right)
                => right is null ? left is null : Live(left) && Live(right) && left.Pointer == right.Pointer;
        }
    }
}
