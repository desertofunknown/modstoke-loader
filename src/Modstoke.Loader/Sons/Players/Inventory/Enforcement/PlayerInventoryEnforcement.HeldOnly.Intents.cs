// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using TheForest.Items.Special;
using UnityEngine;
using HeldAuthority = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        /// <summary>Original native control intent, captured BEFORE mutation. Not server approval.</summary>
        public sealed class HeldOnlyIntent
        {
            public ulong Sequence { get; }
            public HeldAuthority.MutationKind Kind { get; }
            public int ItemId { get; }
            public bool Drop { get; }
            public bool Fake { get; }
            public bool EquipPrevious { get; }
            public float Delay { get; }
            public bool InfiniteHack { get; }
            public bool PlaceFullLoadHack { get; }
            public bool RequiresLifeOwner { get; }
            internal readonly ItemInstance Donor;
            internal readonly GameObject PreSpawned;
            internal readonly HeldAuthority.RepresentativeIdentity Identity;
            internal HeldOnlyIntent(ulong sequence, HeldAuthority.MutationKind kind, int id, ItemInstance donor,
                bool drop, bool fake, bool equipPrevious, GameObject preSpawned, float delay, bool infinite, bool full,
                bool requiresLifeOwner)
            {
                Sequence = sequence; Kind = kind; ItemId = id; Donor = donor; Drop = drop; Fake = fake;
                EquipPrevious = equipPrevious; PreSpawned = preSpawned; Delay = delay;
                InfiniteHack = infinite; PlaceFullLoadHack = full;
                RequiresLifeOwner = requiresLifeOwner;
                if (donor != null) Identity = new HeldAuthority.RepresentativeIdentity(donor);
            }

            public bool TryReadExactLiftDonor(out ItemInstance donor)
            {
                donor = null;
                if (Identity?.Matches(Donor) != true) return false;
                donor = Donor;
                return true;
            }
        }

        private static HeldOnlyIntent _heldIntent;
        private static ulong _heldIntentSequence;

        internal static bool TryAllocateHeldOnlySequence(out ulong sequence)
        {
            sequence = 0;
            if (Environment.CurrentManagedThreadId != _heldHookThread || _heldIntentSequence == ulong.MaxValue) return false;
            sequence = ++_heldIntentSequence;
            return true;
        }

        private static void CaptureHeldIntent(HeldOnlyItemController controller, HeldAuthority.MutationKind kind, int id,
            ItemInstance donor, bool drop, bool fake, bool equipPrevious, GameObject preSpawned, float delay,
            bool? infinite = null, bool? full = null, bool requiresLifeOwner = false)
        {
            var source = _heldOnlySource;
            if (source == null || source.Controller?.Pointer != controller?.Pointer || !source.Lifetime(true)
                || source.Command != null || _heldCall != null || _heldIntent != null || _heldIntentSequence == ulong.MaxValue
                || source.InventoryOwner?.SourceActionInputHeld == true
                || PlayerLifecycle.IsInventoryIngressFenced(source.Inventory, out _)) return;
            try
            {
                _heldIntent = new HeldOnlyIntent(++_heldIntentSequence, kind, id, donor, drop, fake, equipPrevious, preSpawned, delay,
                    infinite ?? controller.InfiniteHack, full ?? controller.PlaceFullLoadHack, requiresLifeOwner);
            }
            catch (Exception error) { source.Fault("Native intent could not retain its exact donor: " + error.Message); }
        }

        /// <summary>Parent routes this observation to the concrete donor/destination action owner.</summary>
        public static bool TryTakeHeldOnlyIntent(HeldOnlySource source, out HeldOnlyIntent intent)
        {
            intent = null;
            if (source == null || !ReferenceEquals(source, _heldOnlySource) || !source.Lifetime(true)
                || source.Command != null || _heldIntent == null) return false;
            intent = _heldIntent; _heldIntent = null;
            return true;
        }
    }
}
