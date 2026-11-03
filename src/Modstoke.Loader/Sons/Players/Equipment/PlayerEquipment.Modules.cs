// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerEquipment
    {
        private PreparedHeldModules _preparedHeldModules;
        internal bool HasPreparedHeldModules => _preparedHeldModules != null;

        /// <summary>
        /// Inventory-authority integration only: supply the exact retained ledger instance after
        /// admission or committed module mutation, with its approved complete module observation.
        /// Preparation is read-only and is permitted on a prepared (inactive) bridge.
        /// </summary>
        internal bool TryPrepareHeldModuleRefresh(ItemInstance authoritativeItem, string expectedSave,
            PlayerInventoryModuleState expectedState, out string detail)
        {
            detail = "Held module preparation requires an idle, accepted server inventory and exact instance.";
            if (!OnThread || _busy || !_server || IsFaulted || _preparedHeldModules != null
                || _inventoryAuthority?.HasAcceptedBaseline != true || !Current(out var target)) return false;
            _busy = true;
            try
            {
                var state = target.GetState<IPlayerState>();
                if (authoritativeItem == null || authoritativeItem.WasCollected || state == null || state.WasCollected
                    || !ValidHeldState(state) || !ValidIds(state.HeldItems)
                    || !ValidIds(state.PlayerClothingIds) || !ValidIds(state.PlayerArmourIds)
                    || !Contains(state.HeldItems, authoritativeItem._itemID)
                    || expectedState == null || expectedSave == null
                    || expectedState != PlayerInventoryBaseline.CaptureModuleState(authoritativeItem)
                    || !PlayerInventoryBaseline.JsonEqual(expectedSave,
                        PlayerInventoryBaseline.CaptureModules(authoritativeItem))) return false;
                var before = new HeldModuleState(state);
                var held = CopyIds(state.HeldItems);
                var clothing = CopyIds(state.PlayerClothingIds);
                var armour = CopyIds(state.PlayerArmourIds);
                if (!TryPrepareHeldModules(state, authoritativeItem, authoritativeItem._itemID,
                    out var publication, out detail, refresh: true)) return false;
                var prepared = new PreparedHeldModules(target.Pointer, state.Pointer, before, publication, held, clothing, armour);
                if (!Current(out var current) || current.Pointer != target.Pointer
                    || current.GetState<IPlayerState>()?.Pointer != state.Pointer || !prepared.Matches(state))
                {
                    detail = "Held instance or carrier changed during module preparation.";
                    return false;
                }
                _preparedHeldModules = prepared;
                detail = "Exact held module publication prepared without writes; activation is still required.";
                return true;
            }
            catch (Exception error) { detail = "Held module preparation failed: " + error.Message; return false; }
            finally { _busy = false; }
        }

        /// <summary>Publish once after activation; a stale preparation must be discarded and prepared again.</summary>
        internal bool TryPublishPreparedHeldModules(out string detail)
        {
            detail = "Held module publication requires the active, unchanged prepared server carrier.";
            if (!OnThread || _busy || !_server || IsFaulted || _bridge?.IsGameplayActive != true
                || _preparedHeldModules == null || !Current(out var target)) return false;
            _busy = true;
            bool attempted = false;
            try
            {
                var prepared = _preparedHeldModules;
                var state = target.GetState<IPlayerState>();
                if (target.Pointer != prepared.Target || state == null || state.WasCollected
                    || state.Pointer != prepared.State || !prepared.Matches(state)) return false;
                // Consume before the first setter. A partial publication must never be replayed.
                _preparedHeldModules = null;
                attempted = true;
                prepared.Publication.Expected.PublishModules(state);
                if (!Current(out var current) || current.Pointer != prepared.Target
                    || current.GetState<IPlayerState>()?.Pointer != prepared.State
                    || !prepared.EquipmentMatches(state) || !prepared.Publication.Confirms(state))
                {
                    Fail("Held module publication could not be confirmed; explicit recovery is required.");
                    detail = Detail;
                    return false;
                }
                detail = "Typed held modules published and read back without changing inventory or equipment custody.";
                return true;
            }
            catch (Exception error)
            {
                detail = "Held module publication failed: " + error.Message;
                if (attempted) Fail(detail);
                return false;
            }
            finally { _busy = false; }
        }

        internal void DiscardPreparedHeldModules()
        {
            if (!OnThread || _busy) throw new InvalidOperationException("Discard prepared modules on their idle game thread.");
            _preparedHeldModules = null;
        }

        private static int[] CopyIds(Bolt.NetworkArray_Integer ids)
        {
            var result = new int[ids.Length];
            for (int i = 0; i < result.Length; i++) result[i] = ids[i];
            return result;
        }

        private sealed class PreparedHeldModules(IntPtr target, IntPtr state, HeldModuleState before,
            HeldModulePublication publication, int[] held, int[] clothing, int[] armour)
        {
            internal readonly IntPtr Target = target, State = state;
            internal readonly HeldModulePublication Publication = publication;
            internal bool Matches(IPlayerState current)
                => before.Matches(current) && EquipmentMatches(current) && Publication.ConfirmsItem();
            internal bool EquipmentMatches(IPlayerState current)
                => MatchesIds(current.HeldItems, held) && MatchesIds(current.PlayerClothingIds, clothing)
                    && MatchesIds(current.PlayerArmourIds, armour);
            private static bool MatchesIds(Bolt.NetworkArray_Integer values, int[] expected)
            {
                if (!ValidIds(values) || values.Length != expected.Length) return false;
                for (int i = 0; i < expected.Length; i++) if (values[i] != expected[i]) return false;
                return true;
            }
        }

        private static bool TryPrepareHeldModules(IPlayerState state, ItemInstance item, int id,
            out HeldModulePublication publication, out string detail, bool refresh = false)
        {
            publication = null;
            detail = "The server must resolve the exact authorized held instance and bounded module data.";
            if (item == null || item.WasCollected || item._itemID != id) return false;
            var data = item.Data;
            var modules = item._modules;
            if (!Live(data) || data.Id != id || modules == null || modules.WasCollected
                || modules.Count is < 0 or > MaxItemModules) return false;
            for (int i = 0; i < modules.Count; i++)
            {
                var module = modules[i];
                if (module == null || module.WasCollected) return false;
                string kind = module.GetIl2CppType().FullName;
                if (!KnownHeldModule(kind))
                {
                    detail = "No native held-publication mapping is established for " + kind + ".";
                    return false;
                }
            }

            var result = new HeldModulePublication(state, item);
            if (item.TryGetModule<RangedWeaponItemInstanceModule>(out var ranged))
            {
                if (!result.PrepareWeaponMods(ranged, id, out detail, refresh)) return false;
            }
            else if (Array.IndexOf(result.Expected.WeaponItems, id) >= 0)
            {
                detail = "A stale native weapon-mod entry would attach mods absent from the authorized instance.";
                return false;
            }

            if (item.TryGetModule<PerishableItemInstanceModule>(out var perishable))
            {
                if (perishable == null || perishable.WasCollected || !Enum.IsDefined(perishable._currentState)
                    || perishable.CurrentStateData == null || perishable.CurrentStateData.WasCollected
                    || perishable.CurrentStateData.State != perishable._currentState
                    || !float.IsFinite(perishable.TimeRemainingInState)
                    || perishable.TimeRemainingInState is < -1f or > 31536000f) return false;
            }
            if (item.TryGetModule<DismemberedLimbItemInstanceModule>(out var limb)
                && (limb == null || limb.WasCollected || !Enum.IsDefined(limb.ActorType))) return false;

            // BoltPlayerSetup gates these shared fields on the definition's slot, not on a
            // client-supplied slot. The retained local instance and server custody stay separate.
            if (data._equipmentSlot == EquipmentSlot.RightHand)
            {
                result.Expected.PerishableState = data.IsPerishable && perishable != null
                    ? (int)perishable.CurrentStateData.State : 0;
                result.Expected.PerishableTime = data.IsPerishable && perishable != null
                    ? perishable.TimeRemainingInState : 0;
                if (limb != null)
                {
                    result.Expected.ActorType = (int)limb.ActorType;
                    result.Expected.ActorSeed = limb.ActorSeed;
                    result.Expected.Bloody = limb.IsBloody;
                }
                else result.Expected.Bloody = false;
                result.RestorePerishable = data.IsPerishable && perishable != null && limb == null;
            }

            // HeldControllerBase.Initialize calls UpdateCoopPlatingState (RVA 34D8E60)
            // and UpdateCoopVisualVariantState (34D9080) before the held-initialized event.
            // Their remote branches read these very fields. Do not call their local writers:
            // those resolve LocalPlayer.Entity instead of this authorized server carrier.
            if (data.CanBePlated && item.TryGetModule<ItemPlatingItemInstanceModule>(out var plating))
            {
                if (plating == null || plating.WasCollected) return false;
                result.PublishPlating = true;
                result.Expected.Plating = plating.IsPlated ? 1 : 0;
            }
            if (data.HasVisualVariant && item.TryGetModule<VisualVariantItemInstanceModule>(out var variant))
            {
                if (variant == null || variant.WasCollected) return false;
                result.PublishVariant = true;
                // Native publishes the signed value. ToggleVariant (31F18B0) displays
                // negative values as zero; the remote initializer likewise keeps its default.
                result.Expected.Variant = variant.GetVariantNum();
            }
            publication = result;
            detail = "All native module saves retained; native held fields prepared from the authorized instance.";
            return true;
        }

        private static bool KnownHeldModule(string type) => type switch
        {
            "Sons.Inventory.RangedWeaponItemInstanceModule" => true,
            "Sons.Inventory.PerishableItemInstanceModule" => true,
            "Sons.Inventory.DismemberedLimbItemInstanceModule" => true,
            "Sons.Inventory.ItemPlatingItemInstanceModule" => true,
            "Sons.Inventory.VisualVariantItemInstanceModule" => true,

            // These have native save/token data but no additional HeldItems state mapping.
            // CoopPlayerRemoteSetup.AddHeldItem (RVA 3191820) restores only weapon mods,
            // perishable and limb data; HeldControllerBase adds plating/visual variant above.
            // Preserve the complete authoritative saves, not a filtered or default instance.
            // ArrowUpgrade: Refresh 31CC1E0 -> ModuleRefreshed; visual updater 3300B80.
            // BloodReveal: Refresh 31CC570 -> BloodOnItem.ForceBloodAmounts, no state write.
            // GPS: Refresh 31CCF50 -> ModuleRefreshed; locator/controller 347D220/34D5AD0.
            // StructureElement: Refresh 31EF6E0 -> SetElementId; visual controller 2C9A580.
            // VolumeContainer: Refresh 31F2240 -> ModuleRefreshed; cooking pot 2CA1200.
            // Their PushToUdpPacket methods serve item tokens, not a new player-state field.
            // LoadedAmmoVisualInstance, pickups and placed locators have separate lifetimes;
            // initializing a held item must not invent their tokens or action state.
            "Sons.Inventory.ArrowUpgradeItemInstanceModule" => true,
            "Sons.Inventory.BloodRevealItemInstanceModule" => true,
            "Sons.Inventory.GPSLocatorItemInstanceModule" => true,
            "Sons.Inventory.StructureElementItemInstanceModule" => true,
            "Sons.Inventory.VolumeContainerItemInstanceModule" => true,
            _ => false
        };

        private sealed class HeldModulePublication
        {
            internal readonly HeldModuleState Expected;
            internal bool PublishPlating, PublishVariant, RestorePerishable;
            private readonly ItemInstance _item;
            private readonly IntPtr _itemPointer, _dataPointer, _moduleListPointer;
            private readonly IntPtr[] _modulePointers;
            private readonly string _save;
            private readonly PlayerInventoryModuleState _moduleState;
            private readonly int _id;
            private readonly DismemberedLimbItemInstanceModule _limb;
            private readonly bool _bloody;
            private readonly VolumeContainerItemInstanceModule _volume;
            private readonly float _currentVolume;

            internal HeldModulePublication(IPlayerState state, ItemInstance item = null)
            {
                Expected = new HeldModuleState(state);
                _item = item;
                if (item == null) return;
                _itemPointer = item.Pointer;
                _id = item._itemID;
                _dataPointer = item.Data.Pointer;
                _moduleListPointer = item._modules.Pointer;
                _modulePointers = new IntPtr[item._modules.Count];
                for (int i = 0; i < _modulePointers.Length; i++) _modulePointers[i] = item._modules[i].Pointer;
                // GetModuleSaveData (RVA 31E3EB0) selects every module's native GetSaveData.
                // Reuse the baseline's bounded serializer and structural comparison, including
                // ammo/count/upgrade, decay pause, blood weights, GPS options and ingredients.
                _save = PlayerInventoryBaseline.CaptureModules(item);
                _moduleState = PlayerInventoryBaseline.CaptureModuleState(item);
                if (item.TryGetModule<DismemberedLimbItemInstanceModule>(out var limb))
                { _limb = limb; _bloody = limb.IsBloody; }
                if (item.TryGetModule<VolumeContainerItemInstanceModule>(out var volume))
                { _volume = volume; _currentVolume = volume.CurrentVolume; }
            }

            internal bool PrepareWeaponMods(RangedWeaponItemInstanceModule ranged, int id, out string detail, bool refresh)
            {
                detail = "Native weapon-mod state cannot represent the complete source list.";
                if (ranged == null || ranged.WasCollected || ranged._weaponMods == null
                    || ranged._weaponMods.WasCollected || ranged._weaponMods.Count is < 0 or > MaxWeaponMods) return false;
                var mods = ranged._weaponMods;
                int slot = Array.IndexOf(Expected.WeaponItems, id);
                // Native skips a zero-mod list; an old entry would otherwise equip stale mods.
                if (mods.Count == 0)
                {
                    if (slot < 0) return true;
                    if (!refresh) return false;
                    Expected.WeaponItems[slot] = 0;
                    Array.Clear(Expected.WeaponMods[slot]);
                    return true;
                }
                if (slot < 0) slot = Array.IndexOf(Expected.WeaponItems, 0);
                if (slot < 0 || mods.Count > Expected.WeaponMods[slot].Length) return false;
                var catalog = ItemDatabaseManager.WeaponMods;
                if (catalog == null || catalog.WasCollected) return false;
                Array.Clear(Expected.WeaponMods[slot]);
                for (int i = 0; i < mods.Count; i++)
                {
                    var mod = mods[i];
                    if (!Live(mod) || mod.Id == 0 || mod.ModItemId <= 0
                        || !Live(ItemDatabaseManager.ItemById(mod.ModItemId))) return false;
                    // Native writer 32C16E0 stores WeaponMod.Id (signed hash at +0x2c).
                    // Remote reader 3191960 resolves that hash in ItemDatabaseManager.WeaponMods.
                    // TryGetActiveModItemIds instead returns inventory IDs, a different domain.
                    int matches = 0;
                    for (int j = 0; j < catalog.Count; j++)
                    {
                        var candidate = catalog[j];
                        if (!Live(candidate) || candidate.Id != mod.Id) continue;
                        if (candidate.Pointer != mod.Pointer) return false;
                        matches++;
                    }
                    if (matches != 1) return false;
                    for (int j = 0; j < i; j++) if (Expected.WeaponMods[slot][j] == mod.Id) return false;
                    Expected.WeaponMods[slot][i] = mod.Id;
                }
                Expected.WeaponItems[slot] = id;
                return true;
            }

            internal static HeldModulePublication Removal(IPlayerState state, ItemData data)
            {
                var result = new HeldModulePublication(state);
                int slot = Array.IndexOf(result.Expected.WeaponItems, data.Id);
                if (slot >= 0)
                {
                    result.Expected.WeaponItems[slot] = 0;
                    Array.Clear(result.Expected.WeaponMods[slot]);
                }
                if (data._equipmentSlot == EquipmentSlot.RightHand)
                {
                    // Native removal retains ActorTypeId/ActorSeed and the appearance scalars.
                    result.Expected.PerishableState = 0;
                    result.Expected.PerishableTime = 0;
                    result.Expected.Bloody = false;
                }
                if (result.Expected.TransferId == data.Id) result.Expected.TransferId = 0;
                return result;
            }

            internal void PublishAppearance(IPlayerState state)
            {
                if (PublishPlating) state.RightHandItemPlatingState = Expected.Plating;
                if (PublishVariant) state.RightHandVisualVariantNum = Expected.Variant;
            }

            internal bool Confirms(IPlayerState state)
            {
                if (!Expected.Matches(state)) return false;
                return ConfirmsItem();
            }

            internal bool ConfirmsItem()
            {
                if (_item == null) return true;
                if (_item.WasCollected || _item.Pointer != _itemPointer || _item._itemID != _id
                    || !Live(_item.Data) || _item.Data.Pointer != _dataPointer
                    || _item._modules == null || _item._modules.WasCollected
                    || _item._modules.Pointer != _moduleListPointer || _item._modules.Count != _modulePointers.Length)
                    return false;
                for (int i = 0; i < _modulePointers.Length; i++)
                {
                    var module = _item._modules[i];
                    if (module == null || module.WasCollected || module.Pointer != _modulePointers[i]) return false;
                }
                // Limb freshness is not saved; volume saves truncate floats to integers.
                if ((_limb != null && (_limb.WasCollected || _limb.IsBloody != _bloody))
                    || (_volume != null && (_volume.WasCollected || !_volume.CurrentVolume.Equals(_currentVolume))))
                    return false;
                return _moduleState == PlayerInventoryBaseline.CaptureModuleState(_item)
                    && PlayerInventoryBaseline.JsonEqual(_save,
                    PlayerInventoryBaseline.CaptureModules(_item));
            }
        }
    }
}
