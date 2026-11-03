// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public static partial class ItemCatalog
    {
        public const int MaxPublishedCompatibleAmmoPerItem = 64;

        /// <summary>
        /// Copies stored weapon/ammunition definitions for one item on the
        /// initialized Unity lifecycle thread. Reads only the existing item
        /// cache; does not initialize the database, refresh the catalog, load
        /// prefabs, or inspect a live weapon's ammunition or reload state.
        /// </summary>
        public static ItemAmmunitionSnapshot CaptureAmmunition(int itemId)
        {
            if (itemId <= 0)
                return ItemAmmunitionSnapshot.CreateUnavailable(
                    itemId, "A positive Sons item id is required.");
            if (!CanReadGameDatabase())
                return ItemAmmunitionSnapshot.CreateUnavailable(
                    itemId, "Item ammunition capture requires the initialized Unity lifecycle thread.");

            try
            {
                var cache = ItemDatabaseManager._itemsCache;
                if (cache is null || cache.WasCollected)
                    return ItemAmmunitionSnapshot.CreateUnavailable(
                        itemId, "The item database cache is not available; no initialization was requested.");
                if (!cache.TryGetValue(itemId, out var item))
                    return ItemAmmunitionSnapshot.CreateUnavailable(
                        itemId, "The requested item is not present in the current item database cache.");
                if (!HasUnityObject(item) || item._id != itemId)
                    return ItemAmmunitionSnapshot.CreateUnavailable(
                        itemId, "The cached item definition is no longer live or does not match the requested id.");

                return CaptureAmmunitionDefinition(itemId, item);
            }
            catch (Exception ex)
            {
                return ItemAmmunitionSnapshot.CreateUnavailable(
                    itemId, "Item ammunition metadata read failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static ItemAmmunitionSnapshot CaptureAmmunitionDefinition(int itemId, ItemData item)
        {
            var details = new List<string>();
            var rawWeaponType = item._weaponType;
            ItemWeaponType? weaponType = rawWeaponType switch
            {
                ItemData.WeaponType.Melee => ItemWeaponType.Melee,
                ItemData.WeaponType.Thrown => ItemWeaponType.Thrown,
                ItemData.WeaponType.AmmoBased => ItemWeaponType.AmmoBased,
                ItemData.WeaponType.MeleeAndThrown => ItemWeaponType.MeleeAndThrown,
                _ => null
            };
            if (!weaponType.HasValue)
                details.Add("Unrecognized weapon type " + (int)rawWeaponType + ".");

            const RangedStyle knownStyles = RangedStyle.Forward | RangedStyle.Bell
                | RangedStyle.Instantaneous | RangedStyle.Charged | RangedStyle.Shoot;
            var rawRangedStyle = item._rangedStyle;
            ItemRangedStyle? rangedStyle = (rawRangedStyle & ~knownStyles) == 0
                ? (ItemRangedStyle)(int)rawRangedStyle
                : null;
            if (!rangedStyle.HasValue)
                details.Add("Unrecognized ranged-style flags " + (int)rawRangedStyle + ".");

            var rawMultiplier = item.AmmoDamageMult;
            float? multiplier = float.IsFinite(rawMultiplier) ? rawMultiplier : null;
            if (!multiplier.HasValue)
                details.Add("The stored ammunition damage multiplier is not finite.");

            int? compatibleAmmoCount = null;
            var compatibleAmmoIds = Array.Empty<int?>();
            var unreadableEntries = 0;
            var source = item._compatibleAmmo;
            if (source is null || source.WasCollected)
            {
                details.Add("The compatible-ammunition list is unavailable; an empty list was not established.");
            }
            else
            {
                var count = source.Count;
                compatibleAmmoCount = count;
                compatibleAmmoIds = new int?[Math.Min(count, MaxPublishedCompatibleAmmoPerItem)];
                for (var index = 0; index < compatibleAmmoIds.Length; index++)
                {
                    try
                    {
                        var entry = source[index];
                        if (entry is not null && !entry.WasCollected)
                            compatibleAmmoIds[index] = entry._itemId;
                    }
                    catch (Exception)
                    {
                        // Preserve this list position as unknown without losing
                        // other readable relationships in the bounded prefix.
                    }
                    if (!compatibleAmmoIds[index].HasValue)
                        unreadableEntries++;
                }
                if (unreadableEntries != 0)
                    details.Add(unreadableEntries + " captured compatible-ammunition entries are unreadable.");
                if (source.Count != count)
                    details.Add("The compatible-ammunition list changed during capture; the recorded count is from its start.");
            }

            var truncated = compatibleAmmoCount.HasValue && compatibleAmmoCount.Value > compatibleAmmoIds.Length;
            var status = details.Count > 0 ? "UNCERTAIN" : truncated ? "TRUNCATED" : "READY";
            if (truncated)
                details.Add("Only the first " + MaxPublishedCompatibleAmmoPerItem + " compatible-ammunition entries were captured.");

            return new ItemAmmunitionSnapshot(
                itemId,
                status,
                details.Count == 0 ? "Stored item ammunition metadata is available." : string.Join(" ", details),
                (int)rawWeaponType,
                weaponType,
                (int)rawRangedStyle,
                rangedStyle,
                multiplier,
                compatibleAmmoCount,
                compatibleAmmoIds,
                truncated);
        }
    }

    public enum ItemWeaponType
    {
        Melee = 0,
        Thrown = 1,
        AmmoBased = 2,
        MeleeAndThrown = 3
    }

    [Flags]
    public enum ItemRangedStyle
    {
        Forward = 1,
        Bell = 2,
        Instantaneous = 4,
        Charged = 8,
        Shoot = 16
    }

    /// <summary>
    /// Detached definition metadata, not an ammunition compatibility check,
    /// current clip/load observation, or permission to fire or reload. Nullable
    /// values remain unknown when unavailable. READY means complete capture;
    /// TRUNCATED reports a bounded prefix, UNCERTAIN reports unreadable or
    /// unrecognized data, and UNAVAILABLE means no definition was captured.
    /// </summary>
    public sealed class ItemAmmunitionSnapshot
    {
        private readonly IReadOnlyList<int?> _compatibleAmmoItemIds;

        internal ItemAmmunitionSnapshot(
            int itemId,
            string status,
            string detail,
            int? rawWeaponType,
            ItemWeaponType? weaponType,
            int? rawRangedStyle,
            ItemRangedStyle? rangedStyle,
            float? ammoDamageMultiplier,
            int? compatibleAmmoCount,
            int?[] compatibleAmmoItemIds,
            bool truncated)
        {
            ItemId = itemId;
            CapturedUtc = DateTimeOffset.UtcNow.ToString("O");
            Status = status;
            Detail = detail;
            RawWeaponType = rawWeaponType;
            WeaponType = weaponType;
            RawRangedStyle = rawRangedStyle;
            RangedStyle = rangedStyle;
            AmmoDamageMultiplier = ammoDamageMultiplier;
            CompatibleAmmoCount = compatibleAmmoCount;
            _compatibleAmmoItemIds = Array.AsReadOnly((int?[])compatibleAmmoItemIds.Clone());
            Truncated = truncated;
        }

        public int ItemId { get; }
        public string CapturedUtc { get; }
        public bool Ready => Status == "READY";
        public string Status { get; }
        public string Detail { get; }

        /// <summary>Stored numeric code, retained even when WeaponType is unrecognized; null when unavailable.</summary>
        public int? RawWeaponType { get; }

        /// <summary>Nominal definition value; does not establish that this item is a weapon.</summary>
        public ItemWeaponType? WeaponType { get; }

        /// <summary>Stored numeric flags, including unrecognized bits; null when unavailable.</summary>
        public int? RawRangedStyle { get; }

        /// <summary>Stored flags only. Zero means no flags are set, not a verified firing mode.</summary>
        public ItemRangedStyle? RangedStyle { get; }

        /// <summary>Finite stored multiplier, not computed damage or a guarantee it is used.</summary>
        public float? AmmoDamageMultiplier { get; }

        /// <summary>Native list count at capture start, including unreadable entries; null when unavailable.</summary>
        public int? CompatibleAmmoCount { get; }

        /// <summary>
        /// Bounded prefix copied in native list order, retaining duplicates and
        /// raw IDs without resolving them. Null entries are unreadable, not ID
        /// zero. An empty prefix establishes an empty stored list only when the
        /// count is zero and capture is READY. No reciprocal weapon lookup is inferred.
        /// </summary>
        public IReadOnlyList<int?> CompatibleAmmoItemIds => _compatibleAmmoItemIds;

        public int PublishedCompatibleAmmoCount => _compatibleAmmoItemIds.Count;
        public bool Truncated { get; }
        public int MaxPublishedCompatibleAmmo => ItemCatalog.MaxPublishedCompatibleAmmoPerItem;

        internal static ItemAmmunitionSnapshot CreateUnavailable(int itemId, string detail)
        {
            return new ItemAmmunitionSnapshot(
                itemId, "UNAVAILABLE", detail, null, null, null, null, null, null, Array.Empty<int?>(), false);
        }
    }
}
