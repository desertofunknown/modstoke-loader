// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;

namespace Modstoke.Sons
{
    /// <summary>Where a mod item id can travel.</summary>
    public enum ItemIdBand
    {
        /// <summary>1000-2047: fits the game's own item events, so other players and the server can see it.</summary>
        Networked = 0,
        /// <summary>2048 and above: the native events cannot carry it, so the item stays on the local machine.</summary>
        ClientLocal = 1
    }

    public enum ItemIdReserveStatus
    {
        Reserved = 0,
        /// <summary>The same mod already holds exactly this range; nothing changed.</summary>
        AlreadyReserved = 1,
        InvalidModId = 2,
        InvalidRange = 3,
        /// <summary>The range is outside the band it was reserved for.</summary>
        WrongBand = 4,
        Overlaps = 5,
        TooManyRanges = 6
    }

    public sealed class ItemIdReservation
    {
        internal ItemIdReservation(string modId, int first, int last, ItemIdBand band, bool implicitReservation)
        {
            ModId = modId;
            First = first;
            Last = last;
            Band = band;
            Implicit = implicitReservation;
        }

        public string ModId { get; }
        public int First { get; }
        public int Last { get; }
        public ItemIdBand Band { get; }
        /// <summary>Made by item registration for a mod that registered an id without reserving it.</summary>
        public bool Implicit { get; }
        public int Count => Last - First + 1;
        public bool Contains(int itemId) => itemId >= First && itemId <= Last;

        public override string ToString() => ModId + " " + First + "-" + Last + " (" + Band + ")";
    }

    /// <summary>
    /// Item id ranges owned by mods. A mod reserves its range before registering items so two mods can never
    /// hand out the same id, and so the server and every client agree on who owns an id.
    /// </summary>
    public static class ItemIds
    {
        /// <summary>Largest id the game's item events carry (PlayerAddItem and the item frames it checks against).</summary>
        public const int NativeEventMaxItemId = ServerInventory.MaxItemId;
        public const int FirstModItemId = 1000;
        public const int FirstClientLocalItemId = NativeEventMaxItemId + 1;
        public const int MaxRangesPerMod = 16;
        public const int MaxRanges = 1024;

        private static readonly object SyncRoot = new();
        private static readonly List<ItemIdReservation> Ranges = new();
        private static IReadOnlyList<ItemIdReservation> _published = Array.Empty<ItemIdReservation>();

        /// <summary>Every reservation, ordered by first id.</summary>
        public static IReadOnlyList<ItemIdReservation> Reservations
        {
            get { lock (SyncRoot) return _published; }
        }

        public static bool FitsNativeEvent(int itemId) => itemId is > 0 and <= NativeEventMaxItemId;

        public static ItemIdBand BandOf(int itemId) => itemId > NativeEventMaxItemId ? ItemIdBand.ClientLocal : ItemIdBand.Networked;

        /// <summary>
        /// Reserves first..last for a mod. Reserving the same range again is allowed; overlapping another range,
        /// including another range of the same mod, is refused.
        /// </summary>
        public static ItemIdReserveStatus TryReserve(string modId, int first, int last, ItemIdBand band)
        {
            var id = SaveData.NormalizeModId(modId);
            if (string.IsNullOrWhiteSpace(modId) || string.IsNullOrEmpty(id))
                return ItemIdReserveStatus.InvalidModId;
            if (first < FirstModItemId || last < first)
                return ItemIdReserveStatus.InvalidRange;
            if (band == ItemIdBand.Networked ? last > NativeEventMaxItemId : first < FirstClientLocalItemId)
                return ItemIdReserveStatus.WrongBand;

            lock (SyncRoot)
                return Add(new ItemIdReservation(id, first, last, band, false));
        }

        /// <summary>The reservation that contains an id, if any.</summary>
        public static bool TryGetOwner(int itemId, out ItemIdReservation reservation)
        {
            lock (SyncRoot)
            {
                reservation = Find(itemId);
                return reservation != null;
            }
        }

        public static IReadOnlyList<ItemIdReservation> GetReservations(string modId)
        {
            var id = SaveData.NormalizeModId(modId);
            lock (SyncRoot)
                return _published.Where(range => string.Equals(range.ModId, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>
        /// Checks that a mod may register an item id. An id inside another mod's range is refused. An id outside every
        /// range gets a one-id reservation, so mods written before reservations existed keep working. Called once
        /// the rest of the registration has been accepted, so a refused registration never claims an id.
        /// </summary>
        internal static bool TryClaimForRegistration(string modId, int itemId, out string reason, out string detail)
        {
            reason = "";
            detail = "";
            var id = SaveData.NormalizeModId(modId);
            lock (SyncRoot)
            {
                var owner = Find(itemId);
                if (owner != null)
                {
                    if (string.Equals(owner.ModId, id, StringComparison.OrdinalIgnoreCase))
                        return true;
                    reason = "item-id-reserved";
                    detail = "Item id " + itemId + " is inside the range " + owner.First + "-" + owner.Last + " reserved by "
                        + owner.ModId + ".";
                    return false;
                }
                // Ids below FirstModItemId are left to older mods that already use them; the vanilla collision check
                // in item registration still guards them.
                var status = Add(new ItemIdReservation(id, itemId, itemId, BandOf(itemId), true));
                if (status is ItemIdReserveStatus.Reserved or ItemIdReserveStatus.AlreadyReserved)
                    return true;
                reason = "item-id-reservation-full";
                detail = "Item id " + itemId + " could not be reserved for " + id + ": " + status + ".";
                return false;
            }
        }

        private static ItemIdReserveStatus Add(ItemIdReservation range)
        {
            var mine = 0;
            foreach (var existing in Ranges)
            {
                var sameMod = string.Equals(existing.ModId, range.ModId, StringComparison.OrdinalIgnoreCase);
                if (sameMod && existing.First == range.First && existing.Last == range.Last && existing.Band == range.Band)
                    return ItemIdReserveStatus.AlreadyReserved;
                if (existing.First <= range.Last && range.First <= existing.Last)
                    return ItemIdReserveStatus.Overlaps;
                if (sameMod)
                    mine++;
            }
            if (mine >= MaxRangesPerMod || Ranges.Count >= MaxRanges)
                return ItemIdReserveStatus.TooManyRanges;

            Ranges.Add(range);
            Ranges.Sort((a, b) => a.First.CompareTo(b.First));
            _published = Ranges.ToArray();
            return ItemIdReserveStatus.Reserved;
        }

        private static ItemIdReservation Find(int itemId)
        {
            int low = 0, high = Ranges.Count - 1;
            while (low <= high)
            {
                var middle = (low + high) >> 1;
                var range = Ranges[middle];
                if (itemId < range.First) high = middle - 1;
                else if (itemId > range.Last) low = middle + 1;
                else return range;
            }
            return null;
        }
    }
}
