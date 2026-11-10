// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Bolt;
using global::Sons.Inventory;
using TheForest.Items.Inventory;
using TheForest.Utils;

namespace Modstoke.Sons
{
    /// <summary>
    /// Retains and guards the original local inventory through migration. Only exact server-issued
    /// stowed-item removals, prepared grants, qualified held stows and empty-hand equips are executable here. Captured
    /// perishable timers are advanced by the server's native module clock and projected with exact
    /// identity/readback. Plain nutrient consumption uses a separate reserved, acknowledged transaction.
    /// Volume consumers, general equip/drop, crafting, storage, other consumption effects
    /// and death/save reconstruction still need their own transactions. Do not activate
    /// this guard as a full vanilla-ready mode: those ordinary workflows are not authorized here.
    /// A fault or Dispose stops transport, NOT the retained native guard. Never silently resume writers.
    /// </summary>
    public sealed partial class PlayerInventoryEnforcement : IDisposable
    {
        private static readonly List<PlayerInventoryEnforcement> Active = new();
        private static readonly ConditionalWeakTable<PlayerBridge, PlayerInventoryEnforcement> Used = new();
        private static readonly long RetryTicks = Stopwatch.Frequency / 5, TimeoutTicks = Stopwatch.Frequency * 8;
        private static bool _ticking;
        private static PlayerInventoryEnforcement _guard;

        /// <summary>Whether a strict inventory guard is armed in this process.</summary>
        internal static bool StrictInventoryArmed => _guard?.IsArmed == true;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerInventoryBaseline _baseline;
        private readonly List<SourceItem> _sourceItems = new();
        private PlayerInventoryBaseline _currentSnapshot;
        private PlayerInventoryAuthority _authority;
        private PlayerBridge _bridge;
        private PlayerWaterPresentation _sourceWaterTransitions;
        private PlayerInventory _inventory;
        private ItemInstanceManager _manager;
        private BoltEntity _originalEntity;
        private IntPtr _root;
        private PlayerInventoryFrame _pending, _lastReply;
        private bool _hasPending, _busy, _registered, _permit;
        private long _started, _sent;
        private ulong _floor, _lastReceived;
        private int _permitId, _permitQuantity;
        private IntPtr _permitInstance;

        public PlayerInventoryBaseline Baseline => _baseline;
        public bool IsDisposed { get; private set; }
        public bool IsFaulted { get; private set; }
        public bool IsArmed { get; private set; }
        public ulong LastCompletedTransaction { get; private set; }
        /// <summary>Latest exact completion; null before a mutation. Rejection is not an applied grant.</summary>
        public PlayerInventoryReceipt? LastReceipt { get; private set; }
        public string Detail { get; private set; } = "Native inventory writers retained; bridge binding pending.";
        internal bool CanQueue => !IsDisposed && !IsFaulted && !_busy && IsArmed && !_hasPending && !_intakePending
            && !HasPendingArmourSourceCustody && !BlocksNativeWorldMutation && _bridge?.IsGameplayActive == true
            && _authority?.CanQueueNativeMutation != false && AllowsHeldOnlyQueue && Current();
        // Fence only new local intent. Accepted requests and the forced compound's own
        // exact hand-restoration commands must still drain through their normal pumps.
        private bool SourceActionInputHeld => _authority == null
            && (HasPendingArmourSourceCustody || HasPendingSwimmingStow || HeldOnlyRoute?.HoldsForcedTransition == true
                || _sourceWaterTransitions?.HasPendingSwimmingTransition == true
                || PlayerLifecycle.IsSourceInputFenced(_inventory));
        /// <summary>Pending intent, native execution or publication must drain before a new lifecycle fence freezes custody.</summary>
        public bool HasPendingCustody => _busy || _hasPending || _hasIncomingGrant || SourceStowPending
            || SourceEquipPending || _requestingStow || _nativeStowReserved || _receivedStowRequest
            || _requestingEquip || _nativeEquipReserved || _receivedEquipRequest || HasPendingIntake || HasPendingNativeCraftRequest
            || HasNativePowerCustody || HasPendingNativeWorldIntent || HasPendingArmourSourceCustody;

        // Queued requests have no native custody yet. Retained server mutations may
        // validate their exact records while those later requests wait for admission.
        internal bool HasExecutingServerCustody => _authority == null
            || _hasPending || _hasIncomingGrant || SourceStowPending || SourceEquipPending
            || _nativeStowReserved || _nativeEquipReserved || _intakePending
            || (_craftServerPending && _craftServerReserved)
            || _worldIntentTransfer != null || HasNativePowerCustody
            || HasPendingNativeCraftPresentation || _heldOnlyAuthority?.HasPendingCustody == true;

        internal bool TryBindWaterActionOwner(PlayerBridge bridge, PlayerWaterPresentation water)
        {
            if (!OnThread || _sourceWaterTransitions != null || _busy || _authority != null
                || !ReferenceEquals(bridge, _bridge) || bridge.IsGameplayActive || water == null
                || !water.MatchesSourceActionOwner(bridge) || !Current() || !SourceLifetime()) return false;
            _sourceWaterTransitions = water;
            return true;
        }

        private PlayerInventoryEnforcement(PlayerInventoryBaseline baseline)
        {
            _baseline = _currentSnapshot = baseline;
            foreach (var entry in baseline.Entries) _floor = System.Math.Max(_floor, entry.Key);
            _lastReceived = LastCompletedTransaction = _floor;
        }

        /// <summary>
        /// Install hooks at startup, then capture while original local actions are quiesced.
        /// This endpoint itself pauses captured native perishable/volume module writers; callers
        /// need not assume their passive fields remain still. Unknown module kinds are rejected.
        /// Failure never removes items. If a failed handle has IsNativeGuardHeld, explicitly cancel
        /// it or retain it for recovery; Dispose does not release a possibly migrated inventory.
        /// </summary>
        public static bool TryCaptureSource(BoltMigrationPlan plan,
            out PlayerInventoryEnforcement source, out string detail)
        {
            source = null;
            detail = "Installed inventory hooks and a quiesced original local client are required.";
            if (!_hooksInstalled || Environment.CurrentManagedThreadId != _hookThread
                || !PlayerRoles.IsAvailable || _writerDepth != 0) return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer
                    || LocalPlayer.Entity == null || !LocalPlayer.Entity.isOwner
                    || (_guard != null && _guard.SourceLifetime())) return false;
                if (!PlayerInventoryBaseline.TryCaptureLocal(plan, out var baseline, out detail)) return false;
                source = new(baseline) { _inventory = LocalPlayer.Inventory,
                    _manager = LocalPlayer.Inventory._itemInstanceManager, _root = LocalPlayer.GameObject.Pointer,
                    _originalEntity = LocalPlayer.Entity };
                var offsets = new Dictionary<int, int>();
                foreach (var entry in baseline.Entries)
                {
                    ItemInstance instance = null;
                    if (Stowed(entry) && entry.Modules != null)
                    {
                        offsets.TryGetValue(entry.ItemId, out int offset);
                        instance = source._manager._itemsMap[entry.ItemId].UniqueInstances[offset];
                        offsets[entry.ItemId] = offset + 1;
                    }
                    else if (entry.EquipmentSlot >= 0) instance = source._inventory._equipmentSlots[entry.EquipmentSlot];
                    else if (entry.ClothingSlots != 0 && entry.Modules != null)
                    {
                        foreach (var hookup in LocalPlayer.ClothingSystem._clothingHookupData)
                            if ((int)hookup.Slot == entry.ClothingSlots && hookup.ItemInstance?._itemID == entry.ItemId)
                                instance = hookup.ItemInstance;
                    }
                    if (entry.Modules != null && instance == null)
                        throw new InvalidOperationException("Captured module custody cannot be retained.");
                    if (entry.Modules != null && !PlayerInventoryBaseline.MatchesInstance(entry, instance))
                        throw new InvalidOperationException("Captured supplemental module state no longer matches the exact source instance.");
                    source.RetainPausedModules(instance);
                    source._sourceItems.Add(new SourceItem(entry, instance));
                }
                _guard = source;
                if (!source.Unchanged()) throw new InvalidOperationException("Inventory changed while the guard was acquired.");
                Active.Add(source);
                detail = source.Detail;
                return true;
            }
            catch (Exception error)
            {
                detail = "Inventory guard capture failed: " + error.Message;
                source?.Fail(detail);
                return false;
            }
        }

        public bool TryBindSource(PlayerBridge bridge, out string detail)
        {
            detail = "The current client bridge must retain this exact inventory and approved snapshot.";
            if (!OnThread || IsDisposed || IsFaulted || _busy || _writerDepth != 0 || _bridge != null || _inventory == null
                || bridge == null || bridge.IsServer || bridge.Plan != _baseline.Plan) return false;
            try
            {
                if (!bridge.TryReadCurrentTarget(out var target) || LocalPlayer.Entity?.Pointer != target.Pointer
                    || target.isOwner || !Unchanged() || Used.TryGetValue(bridge, out _)) return false;
                Register(bridge);
                detail = Detail = "Inventory guarded; awaiting the server's exact approved-baseline handshake.";
                return true;
            }
            catch (Exception error) { Fail("Inventory binding failed: " + error.Message); detail = Detail; return false; }
        }

        internal static bool TryCreateServer(PlayerBridge bridge,
            PlayerInventoryAuthority authority, PlayerInventoryBaseline baseline,
            out PlayerInventoryEnforcement endpoint, out string detail)
        {
            endpoint = null;
            detail = "One current server endpoint is allowed per exact bridge.";
            if (!_hooksInstalled || Environment.CurrentManagedThreadId != _hookThread
                || !PlayerRoles.IsAvailable || bridge == null || !bridge.IsServer || authority == null
                || baseline == null || baseline.Plan != bridge.Plan || !bridge.TryReadCurrentTarget(out _)
                || Used.TryGetValue(bridge, out _)) return false;
            var candidate = new PlayerInventoryEnforcement(baseline) { _authority = authority };
            try
            {
                candidate.CaptureDetachedUpdater();
                authority.RetainLedgerModules(candidate);
                candidate.Register(bridge);
                Active.Add(candidate);
                endpoint = candidate;
                detail = candidate.Detail = "Server inventory endpoint awaits the exact retained-source handshake.";
                return true;
            }
            catch (Exception error) { candidate.Dispose(); detail = "Inventory endpoint failed: " + error.Message; return false; }
        }

        private void Register(PlayerBridge bridge)
        {
            Used.Add(bridge, this);
            _bridge = bridge;
            _started = Stopwatch.GetTimestamp();
            bridge.Retired += Dispose;
            Datagrams.Received += Receive;
            _registered = true;
        }

        internal void QueueMutation(in PlayerInventoryFrame command)
        {
            _pending = command;
            _hasPending = true;
            _started = Stopwatch.GetTimestamp();
            _sent = 0;
        }

        internal bool CancelUnsentMutation(in PlayerInventoryFrame command)
        {
            if (!OnThread || _busy || !_hasPending || _pending != command || _sent != 0) return false;
            _hasPending = false;
            _pending = default;
            return true;
        }

        internal static void Tick()
        {
            if (_ticking || !PlayerRoles.IsAvailable) return;
            _ticking = true;
            try
            {
                if (_guard != null && _guard._load != SaveLifecycle.State.BeforeLoadCount)
                { _guard.Dispose(); _guard = null; }
                for (int i = 0, count = Active.Count; i < count; i++) Active[i].Pump();
            }
            finally { _ticking = false; Active.RemoveAll(item => item.IsDisposed && !item.HoldsDetachedModules && item._nativeEquip == null); }
        }

        public void Pump()
        {
            if (!OnThread || HasPendingArmourSourceCustody) return;
            if (HeldOnlyRoute != null)
            {
                HeldOnlyRoute.Pump();
                if (HeldOnlyRoute.HasPendingCustody)
                {
                    PumpHeldOnlyEquipment();
                    return;
                }
                if (HeldOnlyRoute.HoldForcedIdlePump) return;
            }
            if (!_busy && (IsDisposed || IsFaulted))
            { RetireNativeWorldIntent(); RetireNativeEquipment(); }
            if (IsDisposed || IsFaulted || _busy || _bridge == null) return;
            if (!_bridge.IsGameplayActive)
            {
                _busy = true;
                try
                {
                    if (!Current()) { Fail("Prepared inventory bridge lost its exact target."); return; }
                    long now = Stopwatch.GetTimestamp();
                    if (_authority == null && !IsArmed && (_sent == 0 || now - _sent >= RetryTicks))
                    {
                        if (!Unchanged()) { Fail("Prepared inventory changed before activation."); return; }
                        Send(Handshake(PlayerInventoryMessage.Ready));
                    }
                    // Activation owns its timeout. Frozen custody must not expire into gameplay.
                    _started = now;
                }
                catch (Exception error) { Fail("Prepared inventory handshake failed: " + error.Message); }
                finally { _busy = false; }
                return;
            }
            try
            {
                PumpNativeWorldIntent();
                if (IsFaulted) return;
                if (_authority != null && IsArmed) _authority.PumpDeathInventory();
                PumpPower();
                if (_authority != null && IsArmed) _authority.PumpCrafting();
                if (!BlocksNativeWorldMutation) ProcessCraftRequest();
                PumpCraftEffects();
                if (_authority != null && IsArmed && !_authority.PumpWorldGrant(out var worldDetail))
                { Fail(worldDetail); return; }
                if (!BlocksNativeWorldMutation && !HasPendingNativeCraftRequest && !HasPendingNativeCraftPresentation)
                {
                    ProcessNativeStowRequest();
                    ProcessNativeEquipRequest();
                    ProcessIntakeRequest();
                    if (_authority != null && IsArmed) _authority.PumpPassiveModules();
                }
            }
            catch (Exception error) { Fail("Inventory progression failed: " + error.Message); return; }
            if (IsFaulted) return;
            _busy = true;
            try
            {
                if (!Current()) { Fail("Inventory bridge or retained native lifetime ended; writers remain guarded."); return; }
                if (_grantReplyQueued)
                {
                    _grantReplyQueued = false;
                    Send(_lastReply);
                }
                if (SourceStowPending) PumpStow();
                if (SourceEquipPending) PumpEquip();
                PumpNativeStowRequest();
                PumpNativeEquipRequest();
                PumpConsumptionPresentation();
                PumpIntake();
                PumpCraftRequest();
                if (IsFaulted) return;
                if (IsArmed && !_hasPending && !_hasIncomingGrant && !SourceStowPending && !SourceEquipPending) return;
                long now = Stopwatch.GetTimestamp();
                if (now - _started >= TimeoutTicks) { Fail("Inventory acknowledgement timed out; no replay or refund is authorized."); return; }
                if (_sent != 0 && now - _sent < RetryTicks) return;
                if (_authority == null && !IsArmed)
                {
                    if (!Unchanged()) { Fail("Retained inventory changed before the approval handshake."); return; }
                    Send(Handshake(PlayerInventoryMessage.Ready));
                }
                else if (_hasPending) Send(_pending);
            }
            catch (Exception error) { Fail("Inventory pump failed: " + error.Message); }
            finally { _busy = false; }
        }

        private PlayerInventoryFrame Handshake(PlayerInventoryMessage kind) => new(_baseline.Plan.Id,
            _baseline.Plan.Generation, _bridge.NetworkId, _baseline.Fingerprint, _floor, 0, 0, 0, 0, kind);

        private void Receive(Datagram message)
        {
            if (!OnThread || HasPendingArmourSourceCustody) return;
            if (ReceiveHeldOnlyEquipment(message)) return;
            if (ReceiveNativeWorldIntent(message)) return;
            if (!SourceStowPending && ReceivePower(message)) return;
            if (ReceiveCraft(message)) return;
            if (ReceiveIntake(message)) return;
            if (!OnThread || IsDisposed || _busy || _bridge == null || message == null
                || (_authority == null && _writerDepth != 0)
                || !ReferenceEquals(message.Peer, _bridge.Peer) || !PlayerInventoryWire.TryRead(message.Payload.Span, out var frame, out var grantState)
                || frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.Fingerprint != _baseline.Fingerprint) return;
            if (!_bridge.IsGameplayActive && frame.Kind is not (PlayerInventoryMessage.Ready or PlayerInventoryMessage.Armed)) return;
            _busy = true;
            try
            {
                if (!Current()) { Fail("Inventory endpoint lost its exact bridge lifetime."); return; }
                if (_authority != null)
                {
                    if (!IsFaulted && frame == Handshake(PlayerInventoryMessage.Ready))
                    {
                        IsArmed = true;
                        Send(Handshake(PlayerInventoryMessage.Armed));
                    }
                    else if (ReceiveServerGrant(frame)) { }
                    else if (ReceiveServerStow(frame)) { }
                    else if (ReceiveServerEquip(frame)) { }
                    else if (ReceiveServerPerishable(frame)) { }
                    else if (ReceiveNativeStowRequest(frame)) { }
                    else if (ReceiveNativeEquipRequest(frame)) { }
                    else if (!IsFaulted && _hasPending && frame == (_pending with { Kind = frame.Kind })
                        && frame.Kind is PlayerInventoryMessage.Applied or PlayerInventoryMessage.Unconfirmed)
                    {
                        if (frame.Kind != PlayerInventoryMessage.Applied)
                        { Fail("Native removal is unconfirmed; no refund or repeat is authorized."); return; }
                        if (!_authority.CompleteEnforcedRemoval(frame, out var detail)) { Fail(detail); return; }
                        LastCompletedTransaction = frame.Transaction;
                        LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, frame.Quantity, false, true);
                        _hasPending = false;
                        Detail = detail;
                    }
                    return;
                }
                if (!IsFaulted && frame == Handshake(PlayerInventoryMessage.Armed))
                {
                    if (IsArmed) return;
                    if (!Unchanged()) { Fail("Retained inventory changed during the approval handshake."); return; }
                    IsArmed = true;
                    Detail = "Approved-baseline server bound; native mutations require exact server commands.";
                    return;
                }
                if (ReceiveSourceGrant(frame, grantState)) return;
                if (ReceiveSourceStow(frame)) return;
                if (ReceiveSourceEquip(frame)) return;
                if (ReceiveSourcePerishable(frame)) return;
                if (ReceiveNativeStowRequest(frame)) return;
                if (ReceiveNativeEquipRequest(frame)) return;
                if (frame.Kind != PlayerInventoryMessage.Remove) return;
                if (_lastReply.Kind is PlayerInventoryMessage.Applied or PlayerInventoryMessage.Unconfirmed
                    && frame == (_lastReply with { Kind = PlayerInventoryMessage.Remove }))
                { Send(_lastReply); return; }
                if (IsFaulted || !IsArmed || _intakePending || _hasIncomingGrant || SourceStowPending || SourceEquipPending || frame.Transaction <= _lastReceived) return;
                if (InventoryFrozen) return; // Keep the server's exact pending command retryable; do not consume its ID.
                // Reserve before any native write. Duplicate commands can only resend this receipt.
                _lastReceived = frame.Transaction;
                _lastReply = frame with { Kind = PlayerInventoryMessage.Unconfirmed };
                if (ApplyRemoval(frame))
                {
                    _lastReply = frame with { Kind = PlayerInventoryMessage.Applied };
                    LastCompletedTransaction = frame.Transaction;
                    LastReceipt = new(frame.Transaction, frame.Key, frame.ItemId, frame.Quantity, false, true);
                }
                else Fail("Server removal could not be reconciled with exact native custody; writers remain guarded.");
                Send(_lastReply);
            }
            catch (Exception error) { Fail("Inventory receive failed: " + error.Message); }
            finally { _permit = false; _busy = false; }
        }

        private bool ApplyRemoval(in PlayerInventoryFrame frame)
        {
            if (_bridge?.IsGameplayActive != true || InventoryFrozen || !Unchanged()) return false;
            if (TryApplyCraftMatRemoval(frame, out bool craftApplied)) return craftApplied;
            SourceItem selected = null;
            foreach (var item in _sourceItems)
            {
                if (item.Quantity == 0) continue;
                if (item.Entry.Key == frame.Key) { selected = item; break; }
            }
            if (selected == null || !Stowed(selected.Entry) || selected.Entry.ItemId != frame.ItemId
                || selected.Quantity != frame.BeforeQuantity || selected.Quantity < frame.Quantity) return false;
            if (selected.Instance != null && (frame.Quantity != 1 || selected.Instance.WasCollected)) return false;
            int index = FindStoredEntry(_currentSnapshot, frame.ItemId, selected.Instance);
            if (index < 0) return false;
            _permitId = frame.ItemId;
            _permitQuantity = frame.Quantity;
            _permitInstance = selected.Instance?.Pointer ?? IntPtr.Zero;
            _permit = true;
            bool removed;
            try
            {
                removed = selected.Instance != null ? _manager.TryRemoveItem(selected.Instance)
                    : _manager.RemoveItems(frame.ItemId, frame.Quantity, false) == frame.Quantity;
                if (_permit) return false; // The native prefix must have consumed this single-use permit.
            }
            finally { _permit = false; }
            if (!removed || !Current()) return false;
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            if (!_currentSnapshot.ConfirmsRemoval(after, index, frame.Quantity, selected.Instance)) return false;
            selected.Quantity -= frame.Quantity;
            _currentSnapshot = after;
            Detail = "Exact server removal applied; all remaining stacks, instances and native module data preserved.";
            return true;
        }

        private void Send(in PlayerInventoryFrame frame, bool trackRetry = true)
        {
            if (!_bridge.IsGameplayActive && frame.Kind is not (PlayerInventoryMessage.Ready or PlayerInventoryMessage.Armed)) return;
            if (_authority != null && frame.Kind == PlayerInventoryMessage.Remove
                && !_authority.PrepareWorldRemovalDispatch(frame, out var detail))
            { Fail(detail); return; }
            Span<byte> bytes = stackalloc byte[PlayerInventoryWire.Length];
            PlayerInventoryModuleState grantState = null;
            if (frame.Kind is PlayerInventoryMessage.GrantPrepare or PlayerInventoryMessage.GrantPoll
                && (_authority == null || !_authority.TryReadEnforcedGrantModuleState(frame, out grantState)))
            { Fail("Grant supplemental custody changed before authenticated preparation."); return; }
            PlayerInventoryWire.Write(frame, bytes, grantState);
            if (trackRetry) _sent = Stopwatch.GetTimestamp();
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result != DatagramSendResult.Queued && result != DatagramSendResult.WindowFull)
                Fail("Inventory transport unavailable (" + result + "); delivery may be uncertain.");
        }

        private bool Current() => _bridge != null && _bridge.TryReadCurrentTarget(out var target)
            && _load == SaveLifecycle.State.BeforeLoadCount
            && (_authority != null ? _bridge.IsServer && BoltNetwork.isServer && target.isOwner
                : !_bridge.IsServer && !target.isOwner && LocalPlayer.Entity?.Pointer == target.Pointer && SourceLifetime());

        private bool SourceLifetime() => _inventory != null && !_inventory.WasCollected && _inventory
            && _manager != null && !_manager.WasCollected && _manager
            && LocalPlayer.Inventory?.Pointer == _inventory.Pointer && LocalPlayer.GameObject?.Pointer == _root
            && _inventory._itemInstanceManager?.Pointer == _manager.Pointer
            && _load == SaveLifecycle.State.BeforeLoadCount;

        private bool Unchanged() => SourceLifetime()
            && _currentSnapshot.LocalMatches(PlayerInventoryBaseline.CaptureRetained(_baseline.Plan));
        private bool OnThread => Environment.CurrentManagedThreadId == _thread;
        // Draining can finish already accepted custody work. Frozen inventories admit no new
        // native write; receipt replay/commit reconciliation remains independent of this veto.
        private bool InventoryFrozen => PlayerLifecycle.IsInventoryIngressFenced(_inventory, out bool frozen) && frozen;
        private static bool Stowed(PlayerInventoryBaseline.Entry entry)
            => entry.EquipmentSlot < 0 && entry.ClothingSlots == 0 && entry.ArmourSlot < 0 && !entry.DefinitionOnly;

        private void Fail(string detail)
        {
            bool newlyFaulted = !IsFaulted;
            if (!IsFaulted) Detail = detail;
            IsFaulted = true;
            IsArmed = false;
            RefreshEquipmentIntent();
            if (newlyFaulted) RetireConsumptionPresentation();
            RetireNativeWorldIntent();
            if (_hasIncomingGrant)
            {
                _lastReply = _incomingGrant with { Kind = PlayerInventoryMessage.GrantUnconfirmed };
                _hasIncomingGrant = false;
            }
            _authority?.FaultEnforcement(Detail);
        }

        /// <summary>Only a cancelled, unchanged original owner may resume native writers.</summary>
        public bool TryCancelSource(out string detail)
            => TryCancelBeforeRetirement(out detail);

        /// <summary>Release only this guard on the unchanged original owner; never recover a retired or uncertain bridge.</summary>
        public bool TryCancelBeforeRetirement(out string detail)
        {
            detail = "Cancellation requires the unchanged original native owner before bridge binding.";
            if (!OnThread || _busy || _writerDepth != 0 || _bridge != null || !ReferenceEquals(_guard, this)) return false;
            try
            {
                var entity = LocalPlayer.Entity;
                if (entity == null || entity.WasCollected || _originalEntity == null || _originalEntity.WasCollected
                    || entity.Pointer != _originalEntity.Pointer || !entity.isAttached || !entity.isOwner
                    || entity.networkId.PackedValue != _baseline.Plan.OldNetworkId || !Unchanged()) return false;
                _guard = null;
                Dispose();
                detail = "Original inventory guard released after exact unchanged-source cancellation.";
                return true;
            }
            catch (Exception error) { detail = "Inventory cancellation failed: " + error.Message; return false; }
        }

        public void Dispose()
        {
            if (!OnThread) throw new InvalidOperationException("Dispose inventory enforcement on its game thread.");
            if (IsDisposed) return;
            _statProjection?.Dispose();
            RetireConsumptionPresentation();
            RetireNativeWorldIntent();
            RetireCraftEffectTransport();
            IsDisposed = true;
            IsArmed = false;
            RefreshEquipmentIntent();
            if (_registered)
            {
                _bridge.Retired -= Dispose;
                Datagrams.Received -= Receive;
                _registered = false;
            }
            ReleaseAllDetachedModules();
            ReleaseEquipListener();
            RetireNativeEquipment();
            // Failed native unregistration is quarantined, never silently resumed or discarded.
            if ((HoldsDetachedModules || _nativeEquip != null) && !Active.Contains(this)) Active.Add(this);
            if (!_ticking && !HoldsDetachedModules && _nativeEquip == null) Active.Remove(this);
            // _guard intentionally retains the exact inventory even after a failed/retired bridge.
        }

        private sealed class SourceItem(PlayerInventoryBaseline.Entry entry, ItemInstance instance)
        {
            internal readonly PlayerInventoryBaseline.Entry Entry = entry;
            internal readonly ItemInstance Instance = instance;
            internal int Quantity = entry.Quantity;
        }
    }

    /// <summary>Completion for one previously server-authorized mutation, not authorization for another.</summary>
    public readonly record struct PlayerInventoryReceipt(ulong Transaction, ulong InstanceKey,
        int ItemId, int Quantity, bool IsGrant, bool Applied)
    {
        public bool IsEquipmentStow { get; init; }
        public bool IsEquipmentEquip { get; init; }
        public bool IsPassiveProgression { get; init; }
        public bool IsConsumption { get; init; }
    }
}
