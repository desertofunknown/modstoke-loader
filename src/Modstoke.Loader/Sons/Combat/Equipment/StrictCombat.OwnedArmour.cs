// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons.Combat
{
    public readonly struct StrictCombatArmourResource
    {
        public StrictCombatArmourResource(int slot, ulong resourceId, int itemId, float remainingPoints, bool isBroken)
        {
            Slot = slot;
            ResourceId = resourceId;
            ItemId = itemId;
            RemainingPoints = remainingPoints;
            IsBroken = isBroken;
        }

        public int Slot { get; }
        public ulong ResourceId { get; }
        public int ItemId { get; }
        public float RemainingPoints { get; }
        public bool IsBroken { get; }
    }

    /// <summary>Immutable owned resources, independent of native armour observations.</summary>
    public sealed class StrictCombatOwnedArmourState
    {
        private readonly StrictCombatArmourResource[] _pieces;

        private StrictCombatOwnedArmourState(bool isProvisioned, ulong revision, bool golden,
            float generalMultiplier, float demonicMultiplier, StrictCombatArmourResource[] pieces)
        {
            IsProvisioned = isProvisioned;
            Revision = revision;
            Golden = golden;
            GeneralMultiplier = generalMultiplier;
            DemonicMultiplier = demonicMultiplier;
            _pieces = pieces;
        }

        public static StrictCombatOwnedArmourState Unprovisioned { get; } = new StrictCombatOwnedArmourState(
            false, 0, false, 0f, 0f, Array.Empty<StrictCombatArmourResource>());

        public bool IsProvisioned { get; }
        public ulong Revision { get; }
        public bool Golden { get; }
        public float GeneralMultiplier { get; }
        public float DemonicMultiplier { get; }
        public int Count => _pieces.Length;

        public bool TryGetPiece(int index, out StrictCombatArmourResource piece)
        {
            piece = default;
            if ((uint)index >= (uint)_pieces.Length) return false;
            piece = _pieces[index];
            return true;
        }

        public static bool TryCreate(ulong revision, bool golden, float generalMultiplier, float demonicMultiplier,
            ReadOnlySpan<StrictCombatArmourResource> pieces, out StrictCombatOwnedArmourState state)
        {
            state = Unprovisioned;
            if (pieces.Length > StrictCombatArmourProtocol.MaximumPieces
                || !IsValidMultiplier(generalMultiplier) || !IsValidMultiplier(demonicMultiplier)
                || !AreValidPieces(pieces)) return false;
            state = new StrictCombatOwnedArmourState(true, revision, golden,
                generalMultiplier, demonicMultiplier, pieces.ToArray());
            return true;
        }

        public bool SameContents(StrictCombatOwnedArmourState other)
        {
            if (other == null || IsProvisioned != other.IsProvisioned || Revision != other.Revision
                || Golden != other.Golden || !SameFloat(GeneralMultiplier, other.GeneralMultiplier)
                || !SameFloat(DemonicMultiplier, other.DemonicMultiplier) || Count != other.Count) return false;
            for (int i = 0; i < Count; i++)
            {
                StrictCombatArmourResource first = _pieces[i], second = other._pieces[i];
                if (first.Slot != second.Slot || first.ResourceId != second.ResourceId || first.ItemId != second.ItemId
                    || first.IsBroken != second.IsBroken || !SameFloat(first.RemainingPoints, second.RemainingPoints))
                    return false;
            }
            return true;
        }

        internal static bool SameFloat(float first, float second)
            => BitConverter.SingleToInt32Bits(first) == BitConverter.SingleToInt32Bits(second);

        internal static bool IsValidMultiplier(float value) => float.IsFinite(value) && value >= 0f && value <= 1f;

        internal static bool AreValidPieces(ReadOnlySpan<StrictCombatArmourResource> pieces)
        {
            for (int i = 0; i < pieces.Length; i++)
            {
                StrictCombatArmourResource piece = pieces[i];
                if ((uint)piece.Slot >= StrictCombatArmourProtocol.MaximumPieces || piece.ResourceId == 0
                    || piece.ItemId <= 0 || !float.IsFinite(piece.RemainingPoints) || piece.RemainingPoints < 0f
                    || (piece.IsBroken && BitConverter.SingleToInt32Bits(piece.RemainingPoints) != 0)) return false;
                for (int j = 0; j < i; j++)
                    if (pieces[j].Slot == piece.Slot || pieces[j].ResourceId == piece.ResourceId) return false;
            }
            return true;
        }
    }

    /// <summary>One immutable fragment of an owned-armour receipt.</summary>
    public sealed class StrictCombatArmourReceiptPage
    {
        private readonly StrictCombatArmourResource[] _pieces;

        internal StrictCombatArmourReceiptPage(StrictCombatReceipt receipt, ulong revision, bool isProvisioned,
            bool golden, float generalMultiplier, float demonicMultiplier, int totalCount, int pageIndex,
            StrictCombatArmourResource[] pieces)
        {
            Receipt = receipt;
            Revision = revision;
            IsProvisioned = isProvisioned;
            Golden = golden;
            GeneralMultiplier = generalMultiplier;
            DemonicMultiplier = demonicMultiplier;
            TotalCount = totalCount;
            PageIndex = pageIndex;
            _pieces = pieces;
        }

        public StrictCombatReceipt Receipt { get; }
        public ulong Revision { get; }
        public bool IsProvisioned { get; }
        public bool Golden { get; }
        public float GeneralMultiplier { get; }
        public float DemonicMultiplier { get; }
        public int TotalCount { get; }
        public int PageIndex { get; }
        public int PieceCount => _pieces.Length;

        public bool TryGetPiece(int index, out StrictCombatArmourResource piece)
        {
            piece = default;
            if ((uint)index >= (uint)_pieces.Length) return false;
            piece = _pieces[index];
            return true;
        }

        public bool SameEnvelope(StrictCombatArmourReceiptPage other)
            => other != null && Revision == other.Revision && IsProvisioned == other.IsProvisioned
                && Golden == other.Golden && TotalCount == other.TotalCount
                && StrictCombatOwnedArmourState.SameFloat(GeneralMultiplier, other.GeneralMultiplier)
                && StrictCombatOwnedArmourState.SameFloat(DemonicMultiplier, other.DemonicMultiplier)
                && StrictCombatArmourProtocol.SameReceipt(Receipt, other.Receipt);
    }
}
