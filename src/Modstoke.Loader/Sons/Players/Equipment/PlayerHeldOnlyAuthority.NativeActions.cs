// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Security.Cryptography;
using System.Text;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerHeldOnlyAuthority
    {
        /// <summary>Carry these exact fields through the authenticated parent action envelope.</summary>
        public sealed partial class NativeCommand
        {
            public ulong Transaction { get; }
            public MutationKind Kind { get; }
            public bool Drop { get; }
            public Snapshot Before { get; }
            public Snapshot After { get; }
            public string Fingerprint { get; }
            public int DropCount => Kind == MutationKind.DropAll || IsForcedDrop(Kind) ? Before.State.Count
                : Kind == MutationKind.PutDown && Drop ? 1 : 0;

            internal NativeCommand(ulong transaction, MutationKind kind, bool drop, Snapshot before, Snapshot after)
            {
                Transaction = transaction; Kind = kind; Drop = drop; Before = before; After = after;
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(
                    transaction.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + (int)kind
                    + ":" + (drop ? "1" : "0") + ":" + before.Fingerprint + ":" + after.Fingerprint)));
            }

            public static bool TryRead(BoltMigrationPlan plan, ulong transaction, MutationKind kind,
                bool drop, byte[] before, byte[] after, out NativeCommand command, out string detail)
            {
                command = null;
                detail = "A complete authenticated held-only command is required.";
                if (transaction == 0 || !Enum.IsDefined(kind)
                    || !Snapshot.TryReadObservation(plan, before, out var first, out detail)
                    || !Snapshot.TryReadObservation(plan, after, out var last, out detail)) return false;
                var candidate = new NativeCommand(transaction, kind, drop, first, last);
                if (!candidate.ShapeValid()) { detail = "Held-only command violates native count/settings semantics."; return false; }
                command = candidate;
                return true;
            }

            internal bool ShapeValid()
            {
                var catalog = Before.GetCatalog();
                if (Before.Plan != After.Plan || Before.DropProfile != After.DropProfile
                    || !catalog.AsSpan().SequenceEqual(After.GetCatalog())) return false;
                var before = Before.State;
                var after = After.State;
                if (Kind == MutationKind.Settings)
                    return !Drop && before.Count == after.Count && before.ItemId == after.ItemId
                        && SameModules(Before, After);
                if (before.InfiniteHack != after.InfiniteHack || before.PlaceFullLoadHack != after.PlaceFullLoadHack) return false;
                if (Kind == MutationKind.Lift)
                {
                    int capacity = Capacity(catalog, after.ItemId);
                    return !Drop && capacity > 0 && (before.Count == 0 || before.ItemId == after.ItemId)
                        && (before.Count < capacity || before.InfiniteHack)
                        && after.Count == Math.Clamp(before.Count + 1, 1, capacity) && After.NativeModuleSaveData != null;
                }
                if (before.Count <= 0 || before.ItemId != after.ItemId) return false;
                if (Kind == MutationKind.DropAll || IsForcedDrop(Kind))
                    return Drop && after.Count == 0 && After.NativeModuleSaveData == null;
                if (after.Count != before.Count - (Drop || !before.InfiniteHack || before.Count > 1 ? 1 : 0)) return false;
                return (Kind == MutationKind.Remove || !Drop) ? SameModules(Before, After)
                    : after.Count == 0 ? After.NativeModuleSaveData == null : After.NativeModuleSaveData != null;
            }

            private static bool SameModules(Snapshot first, Snapshot second)
                => first.ModuleState == second.ModuleState && (first.NativeModuleSaveData == null
                    ? second.NativeModuleSaveData == null : second.NativeModuleSaveData != null
                        && PlayerInventoryBaseline.JsonEqual(first.NativeModuleSaveData, second.NativeModuleSaveData));
        }

        /// <summary>Captured at the delayed native spawn boundary, NOT an accepted world object.</summary>
        public sealed class NativeDropEffect
        {
            public int Index { get; }
            public int ItemId { get; }
            public int PrefabId { get; }
            public Vector3 Position { get; }
            public Quaternion Rotation { get; }
            public Vector3 Velocity { get; }
            public Vector3 Torque { get; }
            public string NativeModuleSaveData { get; }
            public PlayerInventoryModuleState ModuleState { get; }
            internal readonly ItemInstance Item;
            internal readonly RepresentativeIdentity Identity;
            internal Transform AudioTransform;
            internal NativeDropEffect(int index, ItemInstance item, int prefabId, Vector3 position,
                Quaternion rotation, Vector3 velocity, Vector3 torque)
            {
                Index = index; Item = item; ItemId = item._itemID; PrefabId = prefabId;
                Position = position; Rotation = rotation; Velocity = velocity; Torque = torque;
                NativeModuleSaveData = PlayerInventoryBaseline.CaptureModules(item);
                ModuleState = PlayerInventoryBaseline.CaptureModuleState(item);
                Identity = new RepresentativeIdentity(item);
            }

            internal NativeDropEffect(int index, int itemId, int prefabId, Vector3 position,
                Quaternion rotation, Vector3 velocity, Vector3 torque, string modules, PlayerInventoryModuleState moduleState)
            {
                Index = index; ItemId = itemId; PrefabId = prefabId; Position = position; Rotation = rotation;
                Velocity = velocity; Torque = torque; NativeModuleSaveData = modules; ModuleState = moduleState;
            }

            internal bool Current() => Identity?.Matches(Item) == true
                && PlayerInventoryBaseline.MatchesInstance(new PlayerInventoryBaseline.Entry
                { ItemId = ItemId, Modules = NativeModuleSaveData, ModuleState = ModuleState }, Item);
        }

        /// <summary>
        /// Local native completion, not donor/destination/stat settlement. The parent must keep
        /// those reservations until its real receipts complete; this type cannot approve them.
        /// </summary>
        public sealed partial class NativeReceipt
        {
            public NativeCommand Command { get; }
            public Snapshot After { get; }
            public float StrengthGain { get; }
            public int BoltRendererCount { get; }
            public string Fingerprint => Convert.ToHexString(SHA256.HashData(Export()));
            private readonly NativeDropEffect[] _drops;
            public NativeDropEffect[] GetDrops() => (NativeDropEffect[])_drops.Clone();
            internal NativeReceipt(NativeCommand command, Snapshot after, float strengthGain,
                int rendererCount, NativeDropEffect[] drops)
            { Command = command; After = after; StrengthGain = strengthGain; BoltRendererCount = rendererCount; _drops = drops; }
        }

        public bool TryDispatchNative(Mutation mutation, out NativeCommand command, out string detail)
        {
            command = null;
            detail = "The exact fresh server reservation must be marked before one delivery attempt.";
            if (!Current() || !Owns(mutation) || mutation.Dispatched) return false;
            try
            {
                var before = MakeSnapshot(_state, _instance);
                var after = MakeSnapshot(mutation.After, mutation.Instance);
                var candidate = new NativeCommand(mutation.Transaction, mutation.Kind, mutation.Drop, before, after);
                if (!candidate.ShapeValid() || !TryMarkDispatched(mutation)) return false;
                mutation.Command = command = candidate;
                detail = "Native command dispatched once; retransmit this command, never repeat the reservation.";
                return true;
            }
            catch (Exception error) { detail = "Held-only command unavailable: " + error.Message; return false; }
        }

        private Snapshot MakeSnapshot(HeldState state, PlayerInventoryBaseline.Entry entry)
            => new(new Document { Plan = Plan, Catalog = (CatalogItem[])_catalog.Clone(), State = state,
                DropProfile = DropProfile,
                Modules = entry?.Modules, ModuleState = entry?.ModuleState });

        /// <summary>
        /// Parent calls only after authenticating the receipt's source and reconstructing its
        /// exact native evidence. This records source completion, never transfers world custody.
        /// </summary>
        internal bool TryRecordNativeReceipt(Mutation mutation, NativeReceipt receipt, out string detail)
        {
            detail = "Exact dispatched held-only native receipt is required.";
            if (!Current() || !Owns(mutation) || mutation.Command == null || !mutation.Dispatched
                || receipt == null || receipt.Command.Fingerprint != mutation.Command.Fingerprint
                || receipt.After.Plan != Plan || receipt.After.DropProfile != DropProfile
                || !receipt.After.Matches(_catalog, mutation.After, mutation.Instance)
                || !float.IsFinite(receipt.StrengthGain) || receipt.StrengthGain < 0
                || mutation.Kind != MutationKind.Lift && receipt.StrengthGain != 0
                || receipt.BoltRendererCount < 0 || receipt.BoltRendererCount > mutation.After.Count) return false;
            var drops = receipt.GetDrops();
            if (drops.Length != mutation.Command.DropCount) return false;
            for (int i = 0; i < drops.Length; i++)
            {
                var expected = mutation.Outgoing[i];
                var actual = drops[i];
                if (actual == null || actual.Index != i || actual.ItemId != expected.ItemId
                    || actual.ModuleState != expected.ModuleState
                    || !PlayerInventoryBaseline.JsonEqual(actual.NativeModuleSaveData, expected.Modules)) return false;
            }
            if (mutation.Receipt != null) return mutation.Receipt.Fingerprint == receipt.Fingerprint;
            mutation.Receipt = receipt;
            detail = "Native source completion retained; donor/destination, equipment and stat receipts remain separate.";
            return true;
        }
    }
}
