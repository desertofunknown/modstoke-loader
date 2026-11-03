// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using Bolt;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private enum ScalarKind { Integer, Float, Boolean }
        private sealed record Scalar(string Name, ScalarKind Kind, Func<global::PlayerState, int> Read,
            Action<global::PlayerState, int> Write);

        // Wire order is part of version 1. Null setters use the separately qualified animator path,
        // never a fabricated Bolt setter. Their generated wrapper schema remains getter-only.
        private static readonly Scalar[] Scalars =
        {
            F("CharacterScale", s => s.CharacterScale, (s, v) => s.CharacterScale = v),
            F("MecanimSpeed", s => s.MecanimSpeed, (s, v) => s.MecanimSpeed = v),
            I("PlayerRace", s => s.PlayerRace, (s, v) => s.PlayerRace = v),
            I("PlayerVariationHair", s => s.PlayerVariationHair, (s, v) => s.PlayerVariationHair = v),
            F("hSpeed", s => s.hSpeed, (s, v) => s.hSpeed = v),
            I("itemAtFeet", s => s.itemAtFeet, (s, v) => s.itemAtFeet = v),
            F("vSpeed", s => s.vSpeed, (s, v) => s.vSpeed = v),
            F("overallSpeed", s => s.overallSpeed), F("weaponHit", s => s.weaponHit),
            F("normCamX", s => s.normCamX), F("normCamY", s => s.normCamY),
            F("spineBlendX", s => s.spineBlendX, (s, v) => s.spineBlendX = v),
            F("tiredFloat", s => s.tiredFloat), F("aimAtTarget", s => s.aimAtTarget),
            F("crouch", s => s.crouch), F("groundHeight", s => s.groundHeight),
            B("logHeld", s => s.logHeld),
            I("HeldOnlyItemId", s => s.HeldOnlyItemId, (s, v) => s.HeldOnlyItemId = v),
            I("HeldOnlyItemCount", s => s.HeldOnlyItemCount, (s, v) => s.HeldOnlyItemCount = v),
            F("coldFloat", s => s.coldFloat), F("inWaterBlend", s => s.inWaterBlend),
            B("bodyHeld", s => s.bodyHeld),
            I("CurrentView", s => s.CurrentView, (s, v) => s.CurrentView = v),
            B("FX_Fire", s => s.FX_Fire, (s, v) => s.FX_Fire = v),
            B("Bloody", s => s.Bloody, (s, v) => s.Bloody = v),
            B("Muddy", s => s.Muddy, (s, v) => s.Muddy = v),
            B("Cold", s => s.Cold, (s, v) => s.Cold = v),
            B("ShowLoadedAmmo", s => s.ShowLoadedAmmo, (s, v) => s.ShowLoadedAmmo = v),
            F("TreeDensity", s => s.TreeDensity, (s, v) => s.TreeDensity = v),
            B("MolotovFire", s => s.MolotovFire, (s, v) => s.MolotovFire = v),
            B("DynamiteFire", s => s.DynamiteFire, (s, v) => s.DynamiteFire = v),
            B("isTargetting", s => s.isTargetting, (s, v) => s.isTargetting = v),
            F("axeBlend1", s => s.axeBlend1), F("terrainAngle", s => s.terrainAngle),
            F("singleArmBlock", s => s.singleArmBlock),
            B("inWater", s => s.inWater, (s, v) => s.inWater = v),
            B("onRaft", s => s.onRaft, (s, v) => s.onRaft = v),
            B("IsWaitingToSleep", s => s.IsWaitingToSleep, (s, v) => s.IsWaitingToSleep = v),
            F("climbSpeed", s => s.climbSpeed),
            I("UnderfootSurface", s => s.UnderfootSurface, (s, v) => s.UnderfootSurface = v),
            B("UnderfootGore", s => s.UnderfootGore, (s, v) => s.UnderfootGore = v),
            B("zipLineAttach", s => s.zipLineAttach),
            I("LoopingEventPath", s => s.LoopingEventPath, (s, v) => s.LoopingEventPath = v),
            F("shellBlend", s => s.shellBlend),
            B("arrowFire", s => s.arrowFire, (s, v) => s.arrowFire = v),
            B("clampSpine", s => s.clampSpine),
            I("SpearedFishType", s => s.SpearedFishType, (s, v) => s.SpearedFishType = v),
            F("bowSpeed", s => s.bowSpeed),
            B("RightHandItemIsLit", s => s.RightHandItemIsLit, (s, v) => s.RightHandItemIsLit = v),
            B("LeftHandItemIsInactive", s => s.LeftHandItemIsInactive, (s, v) => s.LeftHandItemIsInactive = v),
            B("VREnabled", s => s.VREnabled, (s, v) => s.VREnabled = v),
            I("RightHandHeldItemId", s => s.RightHandHeldItemId),
            F("RightHandHeldItemIdFloat", s => s.RightHandHeldItemIdFloat),
            B("rightHandActive", s => s.rightHandActive, (s, v) => s.rightHandActive = v),
            B("HeadIsUnderwater", s => s.HeadIsUnderwater, (s, v) => s.HeadIsUnderwater = v),
            I("caveMask", s => s.caveMask, (s, v) => s.caveMask = v),
            I("Dying", s => s.Dying, (s, v) => s.Dying = v),
            I("StimuliFlags", s => s.StimuliFlags, (s, v) => s.StimuliFlags = v),
            F("VisibilityRangeMult", s => s.VisibilityRangeMult, (s, v) => s.VisibilityRangeMult = v),
            I("TiedUpState", s => s.TiedUpState, (s, v) => s.TiedUpState = v),
            I("MarkerHash", s => s.MarkerHash, (s, v) => s.MarkerHash = v),
            B("IsInMidAction", s => s.IsInMidAction, (s, v) => s.IsInMidAction = v),
            B("HasBackpack", s => s.HasBackpack, (s, v) => s.HasBackpack = v),
            I("RightHandVisualVariantNum", s => s.RightHandVisualVariantNum, (s, v) => s.RightHandVisualVariantNum = v),
            I("RightHandItemPlatingState", s => s.RightHandItemPlatingState, (s, v) => s.RightHandItemPlatingState = v),
            B("IsGoldArmorPlated", s => s.IsGoldArmorPlated, (s, v) => s.IsGoldArmorPlated = v),
            B("TactiPenHeld", s => s.TactiPenHeld, (s, v) => s.TactiPenHeld = v),
            B("TactiPadHeld", s => s.TactiPadHeld, (s, v) => s.TactiPadHeld = v),
            I("Expression", s => s.Expression, (s, v) => s.Expression = v),
            B("DisableBodyColliders", s => s.DisableBodyColliders, (s, v) => s.DisableBodyColliders = v),
            I("StandUpProps", s => s.StandUpProps, (s, v) => s.StandUpProps = v),
            F("treeHeightBlend", s => s.treeHeightBlend),
            I("HeldScrewStructureID", s => s.HeldScrewStructureID, (s, v) => s.HeldScrewStructureID = v),
            F("RideLeanRightLeft", s => s.RideLeanRightLeft), F("RideLeanForwardBack", s => s.RideLeanForwardBack),
            B("DrivingGolfCart", s => s.DrivingGolfCart), B("PassengerGolfCart", s => s.PassengerGolfCart),
            F("PassengerGolfCartIndex", s => s.PassengerGolfCartIndex),
            B("RadioIsOn", s => s.RadioIsOn, (s, v) => s.RadioIsOn = v),
            I("RadioTrackHash", s => s.RadioTrackHash, (s, v) => s.RadioTrackHash = v),
            B("VirginiaLikesPlayer", s => s.VirginiaLikesPlayer, (s, v) => s.VirginiaLikesPlayer = v),
            I("LeftGatherItem", s => s.LeftGatherItem, (s, v) => s.LeftGatherItem = v),
            I("RightGatherItem", s => s.RightGatherItem, (s, v) => s.RightGatherItem = v),
            B("InMenus", s => s.InMenus, (s, v) => s.InMenus = v),
            I("SpearedFishCount", s => s.SpearedFishCount, (s, v) => s.SpearedFishCount = v),
            B("IsRobbyDead", s => s.IsRobbyDead, (s, v) => s.IsRobbyDead = v)
        };

        private static Scalar I(string name, Func<global::PlayerState, int> read, Action<global::PlayerState, int> write = null)
            => new(name, ScalarKind.Integer, read, write);
        private static Scalar F(string name, Func<global::PlayerState, float> read, Action<global::PlayerState, float> write = null)
            => new(name, ScalarKind.Float, s => BitConverter.SingleToInt32Bits(read(s)),
                write == null ? null : (s, v) => write(s, BitConverter.Int32BitsToSingle(v)));
        private static Scalar B(string name, Func<global::PlayerState, bool> read, Action<global::PlayerState, bool> write = null)
            => new(name, ScalarKind.Boolean, s => read(s) ? 1 : 0,
                write == null ? null : (s, v) => write(s, v != 0));

        private static readonly (string Name, Func<global::PlayerState, NetworkArray_Integer> Read)[] IntegerArrays =
        {
            ("HeldItems", s => s.HeldItems), ("PlayerClothingIds", s => s.PlayerClothingIds),
            ("PlayerArmourIds", s => s.PlayerArmourIds), ("MapPieces", s => s.MapPieces),
            ("ClothWeapons", s => s.ClothWeapons), ("WeaponUpgrades", s => s.WeaponUpgrades),
            ("QuickSelectItemIds", s => s.QuickSelectItemIds), ("MecanimLayerHashes", s => s.MecanimLayerHashes)
        };
        private static readonly (string Name, Func<global::PlayerState, NetworkArray_Float> Read)[] FloatArrays =
        {
            ("MecanimLayerWeights", s => s.MecanimLayerWeights), ("MecanimLayerTimes", s => s.MecanimLayerTimes)
        };
        private static readonly (string Name, Func<global::PlayerState, NetworkTransform> Read)[] Transforms =
        {
            ("Transform", s => s.Transform), ("RaftTransform", s => s.RaftTransform),
            ("HeadVR", s => s.HeadVR), ("RightHandVR", s => s.RightHandVR),
            ("LeftHandVR", s => s.LeftHandVR), ("ConnectedTransform", s => s.ConnectedTransform)
        };

        // Check generated wrapper shape once. No reflection is used to read or write state values.
        private static readonly Lazy<string> SchemaError = new(CheckSchema);
        private static string CheckSchema()
        {
            var expected = new Dictionary<string, (Type Type, bool Setter)>(StringComparer.Ordinal);
            foreach (var f in Scalars)
                expected.Add(f.Name, (f.Kind == ScalarKind.Float ? typeof(float)
                    : f.Kind == ScalarKind.Boolean ? typeof(bool) : typeof(int), f.Write != null));
            foreach (var a in IntegerArrays) expected.Add(a.Name, (typeof(NetworkArray_Integer), false));
            foreach (var a in FloatArrays) expected.Add(a.Name, (typeof(NetworkArray_Float), false));
            foreach (var t in Transforms) expected.Add(t.Name, (typeof(NetworkTransform), false));
            expected.Add("name", (typeof(string), true));
            expected.Add("PlayerColor", (typeof(Color), true));
            expected.Add("LoopingEventPosition", (typeof(Vector3), true));
            expected.Add("RaftEntity", (typeof(BoltEntity), true));
            expected.Add("OnknockBackTrigger", (typeof(Il2CppSystem.Action), true));
            expected.Add("LoadedAmmoVisualInstance", (typeof(IProtocolToken), true));
            expected.Add("SharableStorage", (typeof(NetworkArray_Objects<ItemInfo>), false));
            expected.Add("HeldItemsWeaponMods", (typeof(NetworkArray_Objects<HeldItemWeaponMods>), false));
            expected.Add("HeldRightHandItemPerishableState", (typeof(PerishableItemInstanceModuleBoltData), false));
            expected.Add("HeldRightHandItemDismemberState", (typeof(DismemberItemInstanceModuleBoltData), false));
            expected.Add("StoringItems", (typeof(StoredItemsData), false));
            expected.Add("HeldItemEquipSlotTransfer", (typeof(HeldItemSlotTransfer), false));
            string error = CheckProperties(typeof(global::PlayerState), expected);
            if (error != null) return error;
            foreach (string inherited in new[] { "CharacterScale", "MecanimSpeed", "MecanimLayerHashes", "MecanimLayerWeights", "MecanimLayerTimes" })
                expected.Remove(inherited);
            error = CheckProperties(typeof(IPlayerState), expected);
            if (error != null) return error;
            return CheckProperties(typeof(ItemInfo), Fields(("Id", typeof(int)), ("Bonus", typeof(int)),
                    ("BonusValue", typeof(float)), ("IntVal1", typeof(int)), ("FloatVal1", typeof(float))))
                ?? CheckProperties(typeof(HeldItemWeaponMods), Fields(("ItemId", typeof(int)), ("WeaponModIds", typeof(NetworkArray_Integer))))
                ?? CheckProperties(typeof(StoredItemsData), Fields(("ItemId", typeof(int)), ("Count", typeof(int)),
                    ("AreUnique", typeof(bool)), ("ItemInstances", typeof(NetworkArray_ProtocolToken))))
                ?? CheckProperties(typeof(HeldItemSlotTransfer), Fields(("ItemId", typeof(int)), ("FromSlot", typeof(int)), ("ToSlot", typeof(int))))
                ?? CheckProperties(typeof(PerishableItemInstanceModuleBoltData), Fields(("CurrentState", typeof(int)), ("TimeRemainingInState", typeof(float))))
                ?? CheckProperties(typeof(DismemberItemInstanceModuleBoltData), Fields(("ActorTypeId", typeof(int)), ("ActorSeed", typeof(int)), ("IsBloody", typeof(bool))));
        }

        private static Dictionary<string, (Type, bool)> Fields(params (string Name, Type Type)[] fields)
        {
            var result = new Dictionary<string, (Type, bool)>(StringComparer.Ordinal);
            foreach (var f in fields) result.Add(f.Name, (f.Type,
                f.Type != typeof(NetworkArray_Integer) && f.Type != typeof(NetworkArray_ProtocolToken)));
            return result;
        }

        private static string CheckProperties(Type type, Dictionary<string, (Type Type, bool Setter)> expected)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (properties.Length != expected.Count) return type.Name + " property count changed.";
            foreach (var p in properties)
                if (!expected.TryGetValue(p.Name, out var e) || p.PropertyType != e.Type
                    || p.GetMethod == null || (p.SetMethod != null) != e.Setter)
                    return type.Name + "." + p.Name + " schema changed.";
            return null;
        }
    }
}
