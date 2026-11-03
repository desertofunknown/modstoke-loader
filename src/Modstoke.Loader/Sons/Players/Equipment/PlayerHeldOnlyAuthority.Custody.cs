// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerHeldOnlyAuthority
    {
        public enum MutationKind { Lift, PutDown, Settings, Remove, DropAll, KnockDownDrop, SwimmingDrop }

        internal static bool IsForcedDrop(MutationKind kind)
            => kind is MutationKind.KnockDownDrop or MutationKind.SwimmingDrop;

        /// <summary>Server-created, single-use reservation; neither a client request nor a native-success receipt.</summary>
        public sealed class Mutation
        {
            internal readonly PlayerHeldOnlyAuthority Owner;
            internal readonly ItemInstance Representative;
            internal readonly RepresentativeIdentity Identity;
            internal readonly PlayerInventoryBaseline.Entry Instance;
            public ulong Transaction { get; }
            public MutationKind Kind { get; }
            public HeldState Before { get; }
            public HeldState After { get; }
            public bool Drop { get; }
            public bool Dispatched { get; internal set; }
            internal NativeCommand Command;
            internal NativeReceipt Receipt;
            internal PlayerInventoryBaseline.Entry[] Outgoing;
            public string NativeModuleSaveData => Instance?.Modules;
            public PlayerInventoryModuleState ModuleState => Instance?.ModuleState;

            internal Mutation(PlayerHeldOnlyAuthority owner, ulong transaction, MutationKind kind,
                HeldState after, bool drop, ItemInstance representative, PlayerInventoryBaseline.Entry instance)
            {
                Owner = owner; Transaction = transaction; Kind = kind; Before = owner._state;
                After = after; Drop = drop; Representative = representative; Instance = instance;
                Identity = ReferenceEquals(representative, owner._representative) ? owner._representativeIdentity
                    : representative == null ? null : new RepresentativeIdentity(representative);
            }
        }

        /// <summary>
        /// Trusted server decision after world/storage custody is reserved. item must be the exact
        /// authorized server instance, not one constructed from a client item ID. Copies its full
        /// native state into this owner's detached representative; never mutates the donor.
        /// Native Lift's gameplay guards, strength gain, equipment and animation still need the
        /// aggregate's approved action path; this reserves the resulting custody, not those effects.
        /// </summary>
        public bool TryReserveLift(ulong transaction, ItemInstance item, out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A fresh server-authorized lift, exact donor instance and idle held-only account are required.";
            if (!CanReserve(transaction) || item == null || item.WasCollected) return false;
            _busy = true;
            try
            {
                int id = item._itemID;
                int capacity = Capacity(_catalog, id);
                if (capacity == 0 || (_state.Count != 0 && _state.ItemId != id)
                    || (_state.Count >= capacity && !_state.InfiniteHack)
                    || !Alive(item.Data) || ItemDatabaseManager.ItemById(id)?.Pointer != item.Data.Pointer) return false;
                var entry = new PlayerInventoryBaseline.Entry { ItemId = id,
                    Modules = PlayerInventoryBaseline.CaptureModules(item),
                    ModuleState = PlayerInventoryBaseline.CaptureModuleState(item) };
                var representative = PlayerInventoryBaseline.RestoreInstance(entry);
                Require(PlayerInventoryBaseline.MatchesInstance(entry, item)
                    && IdentityCurrent() && RepresentativeStateCurrent(),
                    "Lift donor or held-only account changed during reservation.");
                // Native Lift replaces its sole representative and clamps count even with InfiniteHack.
                int count = Math.Clamp(_state.Count + 1, 1, capacity);
                var after = new HeldState(id, count, EffectiveAmount(_catalog, id, count, _state.PlaceFullLoadHack),
                    _state.InfiniteHack, _state.PlaceFullLoadHack);
                mutation = Reserve(transaction, MutationKind.Lift, after, false, representative, entry);
                detail = "Lift custody reserved; donor remains reserved until exact settled effect completion.";
                return true;
            }
            catch (Exception error) { detail = "Held-only lift reservation rejected: " + error.Message; return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Reserves real PutDown(fake:false). drop=true also requires actual world destination
        /// custody. An infinite last non-drop item remains physically held; no synthetic debit.
        /// </summary>
        public bool TryReservePutDown(ulong transaction, bool drop, out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A fresh approved put-down and positive settled held-only custody are required.";
            if (!CanReserve(transaction) || _state.Count <= 0) return false;
            int count = _state.Count - (drop || !_state.InfiniteHack || _state.Count > 1 ? 1 : 0);
            var after = _state with { Count = count,
                Amount = EffectiveAmount(_catalog, _state.ItemId, count, _state.PlaceFullLoadHack) };
            ItemInstance representative = _representative;
            var entry = _instance;
            // SpawnDropItemDelayed.MoveNext phase zero moves the old representative into
            // the iterator, then replaces it with a DEFAULT instance, or null at zero.
            if (drop)
            {
                try { CreateDefaultRepresentative(_state.ItemId, count, out representative, out entry); }
                catch (Exception error) { detail = "Drop representative unavailable: " + error.Message; return false; }
            }
            mutation = Reserve(transaction, MutationKind.PutDown, after, drop, representative, entry);
            mutation.Outgoing = new[] { _instance };
            detail = "Put-down custody reserved; native callbacks, renderer delay and destination effects remain pending.";
            return true;
        }

        public bool TryReserveRemove(ulong transaction, bool drop, out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A fresh removal and positive settled held-only custody are required.";
            if (!CanReserve(transaction) || _state.Count <= 0) return false;
            int count = _state.Count - (drop || !_state.InfiniteHack || _state.Count > 1 ? 1 : 0);
            var after = _state with { Count = count,
                Amount = EffectiveAmount(_catalog, _state.ItemId, count, _state.PlaceFullLoadHack) };
            // Remove alone does not run the spawn iterator and retains its representative.
            mutation = Reserve(transaction, MutationKind.Remove, after, drop, _representative, _instance);
            mutation.Outgoing = new[] { _instance };
            detail = "Native removal reserved; destination retains its own custody reservation.";
            return true;
        }

        public bool TryReserveDropAll(ulong transaction, out Mutation mutation, out string detail)
            => TryReserveWholeLoad(transaction, MutationKind.DropAll, out mutation, out detail);

        internal bool TryReserveForcedDrop(ulong transaction, MutationKind kind, out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A concrete knockdown or swimming transition must own this forced drop.";
            return IsForcedDrop(kind) && TryReserveWholeLoad(transaction, kind, out mutation, out detail);
        }

        private bool TryReserveWholeLoad(ulong transaction, MutationKind kind, out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A fresh drop-all and positive settled held-only custody are required.";
            if (!CanReserve(transaction) || _state.Count <= 0) return false;
            try
            {
                var outgoing = new PlayerInventoryBaseline.Entry[_state.Count];
                outgoing[0] = _instance;
                if (outgoing.Length > 1)
                {
                    CreateDefaultRepresentative(_state.ItemId, 1, out _, out var entry);
                    for (int i = 1; i < outgoing.Length; i++) outgoing[i] = entry;
                }
                // Ordinary DropAll temporarily clears InfiniteHack. The shared forced body
                // leaves it unchanged and removes with (equipPrevious:true, drop:true).
                var after = _state with { Count = 0,
                    Amount = EffectiveAmount(_catalog, _state.ItemId, 0, _state.PlaceFullLoadHack) };
                mutation = Reserve(transaction, kind, after, true, null, null);
                mutation.Outgoing = outgoing;
                detail = "Entire raw held load reserved; each delayed outgoing effect must be captured.";
                return true;
            }
            catch (Exception error) { detail = "Drop-all reservation rejected: " + error.Message; return false; }
        }

        private static void CreateDefaultRepresentative(int id, int count, out ItemInstance item,
            out PlayerInventoryBaseline.Entry entry)
        {
            item = count == 0 ? null : new ItemInstance(id);
            entry = item == null ? null : new PlayerInventoryBaseline.Entry
            { ItemId = id, Modules = PlayerInventoryBaseline.CaptureModules(item),
                ModuleState = PlayerInventoryBaseline.CaptureModuleState(item) };
        }

        /// <summary>Trusted server settings decision; peer observation of changed hacks is not approval.</summary>
        public bool TryReserveSettings(ulong transaction, bool infiniteHack, bool placeFullLoadHack,
            out Mutation mutation, out string detail)
        {
            mutation = null;
            detail = "A fresh trusted settings decision and idle held-only account are required.";
            if (!CanReserve(transaction)) return false;
            var after = _state with { InfiniteHack = infiniteHack, PlaceFullLoadHack = placeFullLoadHack,
                Amount = EffectiveAmount(_catalog, _state.ItemId, _state.Count, placeFullLoadHack) };
            mutation = Reserve(transaction, MutationKind.Settings, after, false, _representative, _instance);
            detail = "Exact held-only settings reserved; not applied to native source yet.";
            return true;
        }

        private bool CanReserve(ulong transaction)
        {
            if (!Current() || !_bridge.IsGameplayActive || _pending != null
                || transaction == 0 || transaction <= LastTransaction) return false;
            try
            {
                if (RepresentativeStateCurrent()) return true;
                FailureReason = "Held-only representative module state changed outside its custody owner.";
            }
            catch (Exception error) { FailureReason = "Held-only module state is unreadable: " + error.GetType().Name + "."; }
            return false;
        }

        private Mutation Reserve(ulong transaction, MutationKind kind, HeldState after, bool drop,
            ItemInstance representative, PlayerInventoryBaseline.Entry entry)
        {
            var mutation = new Mutation(this, transaction, kind, after, drop, representative, entry);
            LastTransaction = transaction;
            return _pending = mutation;
        }

        /// <summary>Mark BEFORE the single native send/execute attempt; uncertainty is not cancellable.</summary>
        internal bool TryMarkDispatched(Mutation mutation)
        {
            if (!Current() || !Owns(mutation) || mutation.Dispatched) return false;
            try
            {
                if (!RepresentativeStateCurrent() || (mutation.Instance != null
                    && (mutation.Identity == null || !mutation.Identity.Matches(mutation.Representative)
                        || !PlayerInventoryBaseline.MatchesInstance(mutation.Instance, mutation.Representative))))
                { HoldUnconfirmed(mutation, "Reserved held-only representative changed before dispatch."); return false; }
            }
            catch (Exception error)
            { HoldUnconfirmed(mutation, "Reserved held-only representative is unreadable: " + error.GetType().Name + "."); return false; }
            mutation.Dispatched = true;
            return true;
        }

        internal bool TryCancelUndispatched(Mutation mutation)
        {
            if (!Current() || !Owns(mutation) || mutation.Dispatched) return false;
            _pending = null;
            return true;
        }

        /// <summary>
        /// Integration-only completion. Invoke only after the exact authenticated transaction's
        /// native action AND all effects/coroutines/destination custody have settled. A count
        /// notification, native bool return or matching observation alone is NOT that evidence.
        /// No mutation is authorized by this observation; it must match the server reservation.
        /// </summary>
        internal bool TryCompleteSettled(Mutation mutation, Snapshot observation, out string detail)
        {
            detail = "Completion requires the exact dispatched reservation and authenticated settled observation.";
            if (!Current() || !Owns(mutation) || !mutation.Dispatched || mutation.Receipt == null || observation == null
                || observation.Fingerprint != mutation.Receipt.After.Fingerprint
                || observation.Plan != Plan || observation.DropProfile != DropProfile) return false;
            _busy = true;
            try
            {
                Require(observation.Matches(_catalog, mutation.After, mutation.Instance)
                    && (mutation.Instance == null ? mutation.Representative == null
                        : mutation.Identity != null && mutation.Identity.Matches(mutation.Representative)
                            && PlayerInventoryBaseline.MatchesInstance(mutation.Instance, mutation.Representative))
                    && IdentityCurrent() && RepresentativeStateCurrent(),
                    "Native held-only completion disagrees with the reserved exact custody.");
                _state = mutation.After; _representative = mutation.Representative; _instance = mutation.Instance;
                _representativeIdentity = mutation.Identity;
                LastCommittedTransaction = mutation.Transaction;
                _pending = null;
                detail = "Exact server held-only custody committed once.";
                return true;
            }
            catch (Exception error)
            {
                FailureReason = detail = "Held-only completion uncertain; custody held, no refund/replay: " + error.Message;
                return false;
            }
            finally { _busy = false; }
        }

        internal void HoldUnconfirmed(Mutation mutation, string reason)
        {
            if (Environment.CurrentManagedThreadId != _thread || !Owns(mutation)) return;
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "Held-only effect completion is unconfirmed." : reason;
        }

        private bool Owns(Mutation mutation) => mutation != null && ReferenceEquals(mutation.Owner, this)
            && ReferenceEquals(_pending, mutation) && !IsDisposed;
    }
}
