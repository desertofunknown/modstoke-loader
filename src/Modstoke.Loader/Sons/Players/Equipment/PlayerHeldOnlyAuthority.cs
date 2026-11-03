// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    /// <summary>
    /// Exclusive server held-only ledger for one exact actor bridge. The single representative
    /// native instance survives bare removal at count zero; a delayed drop replaces it with
    /// a default instance or clears it at zero. The count is aggregate custody, not a claim
    /// that native stores a distinct instance for every log.
    /// This owner never invokes the host's held-only controller. Renderer publication requires
    /// the exact native receipt and restored server carrier.
    /// Source writer fencing, authenticated action delivery and settled effect receipts must be
    /// integrated by the migration owner before gameplay; creating this ledger is not that gate.
    /// </summary>
    public sealed partial class PlayerHeldOnlyAuthority : IDisposable
    {
        private static readonly Dictionary<PlayerBridge, PlayerHeldOnlyAuthority> Owners = new();
        private static readonly HashSet<Guid> AcceptedMigrations = new();
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly IntPtr _target;
        private readonly CatalogItem[] _catalog;
        private readonly ItemData[] _definitions;
        private HeldState _state;
        private ItemInstance _representative;
        private RepresentativeIdentity _representativeIdentity;
        private PlayerInventoryBaseline.Entry _instance;
        private Mutation _pending;
        private bool _busy;

        public bool IsDisposed { get; private set; }
        public string FailureReason { get; private set; }
        public bool HasPendingCustody => _pending != null;
        public ulong LastTransaction { get; private set; }
        internal ulong LastCommittedTransaction { get; private set; }
        public string AcceptedBaselineFingerprint { get; }
        public string AcceptanceReason { get; }
        public BoltMigrationPlan Plan => _bridge.Plan;
        public DropConfiguration DropProfile { get; }

        private PlayerHeldOnlyAuthority(PlayerBridge bridge, IntPtr target,
            Snapshot snapshot, string reason, ItemData[] definitions, ItemInstance representative)
        {
            _bridge = bridge; _target = target; _catalog = snapshot.GetCatalog(); _definitions = definitions;
            _state = snapshot.State; _representative = representative; _instance = snapshot.InstanceEntry();
            DropProfile = snapshot.DropProfile;
            _representativeIdentity = representative == null ? null : new RepresentativeIdentity(representative);
            AcceptedBaselineFingerprint = snapshot.Fingerprint; AcceptanceReason = reason;
        }

        /// <summary>
        /// Trusted server/operator approval, exactly like the inventory baseline decision.
        /// The fingerprint must be independently approved, not automatically echoed from a peer.
        /// It approves existing quantities, controller catalog capacities, drop forces and both hack settings;
        /// it does not prove prior acquisition. Admission is all-or-nothing and performs no grants.
        /// </summary>
        public static bool TryAcceptUnverifiedExisting(PlayerBridge bridge, Snapshot snapshot,
            string approvedFingerprint, string operatorReason,
            out PlayerHeldOnlyAuthority owner, out string detail)
        {
            owner = null;
            detail = "Exact current server bridge and independent held-only baseline approval are required.";
            if (!PlayerRoles.IsAvailable || !GameBuild.IsRecognized
                || bridge?.IsServer != true || snapshot == null || snapshot.Plan != bridge.Plan
                || snapshot.Fingerprint != approvedFingerprint || string.IsNullOrWhiteSpace(operatorReason)
                || operatorReason.Length > 512 || !bridge.TryReadCurrentTarget(out var target)
                || Owners.ContainsKey(bridge) || AcceptedMigrations.Contains(bridge.Plan.Id)) return false;
            try
            {
                var catalog = snapshot.GetCatalog();
                var definitions = new ItemData[catalog.Length];
                for (int i = 0; i < catalog.Length; i++)
                {
                    var data = ItemDatabaseManager.ItemById(catalog[i].ItemId);
                    Require(Alive(data) && data.Id == catalog[i].ItemId
                        && data._eachInstanceIsUnique == catalog[i].EachInstanceIsUnique,
                        "Accepted held-only catalog does not match this server's native item database.");
                    definitions[i] = data;
                }
                var entry = snapshot.InstanceEntry();
                var representative = entry == null ? null : PlayerInventoryBaseline.RestoreInstance(entry);
                var candidate = new PlayerHeldOnlyAuthority(bridge, target.Pointer,
                    snapshot, operatorReason, definitions, representative);
                Require(candidate.IdentityCurrent(), "Player or item identity changed during held-only admission.");
                Owners.Add(bridge, candidate);
                AcceptedMigrations.Add(bridge.Plan.Id);
                bridge.Retired += candidate.Dispose;
                SaveLifecycle.Lifecycle += candidate.OnSaveLifecycle;
                owner = candidate;
                detail = "Explicitly approved held-only custody accepted; native action/receipt integration is still required.";
                return true;
            }
            catch (Exception error) { detail = "Held-only admission rejected: " + error.Message; return false; }
        }

        /// <summary>
        /// Settled custody read for action admission. Pending native work is not settled
        /// custody and cannot authorize another action.
        /// </summary>
        public bool TryRead(PlayerBridge bridge, out HeldState state, out string detail)
        {
            state = default;
            detail = "Held-only custody must be current, exclusively owned and settled for this exact actor.";
            if (!ReferenceEquals(bridge, _bridge) || !Current() || _pending != null) return false;
            state = _state;
            detail = "Accepted server custody and native Amount semantics are current.";
            return true;
        }

        /// <summary>
        /// Survival observes the committed server amount, including while the next action
        /// is reserved. A reservation and early source animation do not change that amount;
        /// TryCompleteSettled is its sole commit boundary. Retained frames keep this value,
        /// never the later amount read when their numeric updates finally run.
        /// </summary>
        internal bool TryReadSurvivalState(PlayerBridge bridge, out HeldState state,
            out ulong committedTransaction, out string detail)
        {
            state = default; committedTransaction = 0;
            detail = "Survival requires this exact actor's current committed held-only custody.";
            if (!ReferenceEquals(bridge, _bridge) || !Current()) return false;
            state = _state; committedTransaction = LastCommittedTransaction;
            detail = "Committed server amount captured; any reserved native action remains pending.";
            return true;
        }

        private bool Current()
        {
            if (Environment.CurrentManagedThreadId != _thread || IsDisposed || FailureReason != null || _busy) return false;
            try
            {
                if (Owners.TryGetValue(_bridge, out var owner) && ReferenceEquals(owner, this) && IdentityCurrent()) return true;
                FailureReason = "Held-only actor/world/catalog or representative instance identity changed.";
            }
            catch (Exception error) { FailureReason = "Held-only identity is unreadable: " + error.GetType().Name + "."; }
            return false;
        }

        private bool IdentityCurrent()
        {
            if (IsDisposed || _load != SaveLifecycle.State.BeforeLoadCount
                || !_bridge.Peer.IsCurrent || !_bridge.IsServer
                || !_bridge.TryReadCurrentTarget(out var target) || target.Pointer != _target) return false;
            for (int i = 0; i < _catalog.Length; i++)
            {
                var data = _definitions[i];
                if (!Alive(data) || data.Id != _catalog[i].ItemId
                    || ItemDatabaseManager.ItemById(data.Id)?.Pointer != data.Pointer
                    || data._eachInstanceIsUnique != _catalog[i].EachInstanceIsUnique) return false;
            }
            return _instance == null ? _representative == null && _representativeIdentity == null
                : _representativeIdentity != null && _representativeIdentity.Matches(_representative)
                    && _representative._itemID == _instance.ItemId;
        }

        private bool RepresentativeStateCurrent() => _instance == null ? _representative == null
            : PlayerInventoryBaseline.MatchesInstance(_instance, _representative);

        // Per-frame amount reads need identity, not native module save allocation. Full module
        // values are compared at action boundaries, where custody can actually change.
        internal sealed class RepresentativeIdentity
        {
            private readonly ItemInstance _item;
            private readonly ItemData _definition;
            private readonly IntPtr _moduleList;
            private readonly ItemInstanceModule[] _modules;
            private readonly int _id;

            internal RepresentativeIdentity(ItemInstance item)
            {
                Require(item != null && !item.WasCollected && Alive(item.Data)
                    && item._modules != null && !item._modules.WasCollected
                    && item._modules.Count is >= 0 and <= 32, "Held-only native representative identity is unavailable.");
                _item = item; _definition = item.Data; _id = item._itemID; _moduleList = item._modules.Pointer;
                _modules = new ItemInstanceModule[item._modules.Count];
                var identities = new HashSet<IntPtr>();
                for (int i = 0; i < _modules.Length; i++)
                {
                    var module = item._modules[i];
                    Require(module != null && !module.WasCollected && identities.Add(module.Pointer),
                        "Held-only native module identity is unavailable or duplicated.");
                    _modules[i] = module;
                }
                Require(Matches(item), "Held-only representative changed during identity capture.");
            }

            internal bool Matches(ItemInstance item)
            {
                if (item == null || item.WasCollected || _item.WasCollected || item.Pointer != _item.Pointer
                    || item._itemID != _id || !Alive(_definition) || _definition.Id != _id
                    || item.Data?.Pointer != _definition.Pointer
                    || ItemDatabaseManager.ItemById(_id)?.Pointer != _definition.Pointer) return false;
                var modules = item._modules;
                if (modules == null || modules.WasCollected || modules.Pointer != _moduleList
                    || modules.Count != _modules.Length) return false;
                for (int i = 0; i < _modules.Length; i++)
                    if (_modules[i].WasCollected || modules[i] == null || modules[i].WasCollected
                        || modules[i].Pointer != _modules[i].Pointer) return false;
                return true;
            }
        }

        private void OnSaveLifecycle(SaveLifecycleEvent change)
        { if (_load != SaveLifecycle.State.BeforeLoadCount || !_bridge.Peer.IsCurrent) Dispose(); }

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("Dispose held-only authority on its game thread.");
            if (IsDisposed) return;
            IsDisposed = true;
            if (_pending != null) FailureReason ??= "Retired with unresolved held-only custody; no refund or replay is authorized.";
            if (Owners.TryGetValue(_bridge, out var owner) && ReferenceEquals(owner, this)) Owners.Remove(_bridge);
            _bridge.Retired -= Dispose;
            SaveLifecycle.Lifecycle -= OnSaveLifecycle;
            // Retirement is not settlement. Keep unresolved reservations and their exact
            // representative rooted for the aggregate owner; never silently refund/replay.
            if (_pending == null) { _instance = null; _representative = null; _representativeIdentity = null; }
        }
    }
}
