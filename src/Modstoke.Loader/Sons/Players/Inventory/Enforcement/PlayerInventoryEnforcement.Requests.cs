// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using global::Sons.Inventory;
using global::Sons.Items.Core;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerInventoryFrame _nativeStowRequest, _serverStowRequest, _lastStowRequestReply;
        private bool _requestingStow, _receivedStowRequest, _nativeStowReserved;
        private ulong _nextStowRequest, _lastStowRequest;
        private long _stowRequestStarted, _stowRequestSent;
        private PlayerInventoryFrame _nativeEquipRequest, _serverEquipRequest, _lastEquipRequestReply;
        private bool _requestingEquip, _receivedEquipRequest, _nativeEquipReserved;
        private ulong _nextEquipRequest, _lastEquipRequest;
        private long _equipRequestStarted, _equipRequestSent;
        private PlayerConsumptionFrame _serverIntakePrepared;

        // Native input still selects the local slot. It can request, never authorize, custody.
        // Do not run this from callbacks inside another native inventory writer.
        private void RequestNativeStow(int slot)
        {
            if (!OnThread || _writerDepth != 1 || _busy || IsDisposed || IsFaulted || !IsArmed || _bridge?.IsGameplayActive != true
                || _authority != null || SourceActionInputHeld || HasPendingIntake || HasPendingNativeCraftRequest || _requestingStow || _nativeStowReserved || _hasIncomingGrant || SourceStowPending
                || _requestingEquip || _nativeEquipReserved || SourceEquipPending || HasNativePowerCustody || HasPendingNativeCraftPresentation
                || !NativeHandSlot(slot) || _nextStowRequest == ulong.MaxValue) return;
            try
            {
                if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _))
                { Detail = "Native stow request declined while inventory lifecycle ingress is fenced; retry after resume."; return; }
                if (!Current() || !Unchanged()) return;
                SourceItem selected = null;
                foreach (var item in _sourceItems)
                    if (item.Quantity == 1 && item.Entry.EquipmentSlot == slot) { selected = item; break; }
                if (selected?.Instance == null || selected.Instance.WasCollected
                    || !SupportsHeldStow(selected.Instance.Data)
                    || _inventory._equipmentSlots[slot]?.Pointer != selected.Instance.Pointer) return;
                StartNativeStowRequest(selected, slot);
            }
            catch (Exception error) { Fail("Native stow request could not preserve custody: " + error.Message); }
        }

        // Admission belongs to the caller: either fresh native input above, or one
        // retained swimming callback. Both use the same request and receipt ledger.
        private void StartNativeStowRequest(SourceItem selected, int slot)
        {
            RefreshEquipmentIntent();
            _nativeStowRequest = new PlayerInventoryFrame(_baseline.Plan.Id, _baseline.Plan.Generation,
                _bridge.NetworkId, _baseline.Fingerprint, ++_nextStowRequest, selected.Entry.Key,
                selected.Entry.ItemId, 1, slot, PlayerInventoryMessage.StowRequest,
                PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(selected.Instance)));
            _requestingStow = true;
            _stowRequestStarted = Stopwatch.GetTimestamp();
            _stowRequestSent = 0;
            Detail = "Native hand stow requested; exact server custody authorization is pending.";
        }

        private void PumpNativeStowRequest()
        {
            if (!_requestingStow || _authority != null) return;
            long now = Stopwatch.GetTimestamp();
            if (now - _stowRequestStarted >= TimeoutTicks)
            {
                // A request timeout cannot authorize retrying a different native action. The
                // server may still have reserved it, so retain the guard until reconciliation.
                Fail("Native stow authorization timed out; no local action or replacement request is authorized.");
                return;
            }
            if (_stowRequestSent != 0 && now - _stowRequestSent < RetryTicks) return;
            _stowRequestSent = now;
            Send(_nativeStowRequest, false);
        }

        private bool ReceiveNativeStowRequest(in PlayerInventoryFrame frame)
        {
            if (_authority == null)
            {
                if (frame.Kind is not (PlayerInventoryMessage.StowRequestAccepted or PlayerInventoryMessage.StowRequestRejected)) return false;
                if (IsFaulted || !_requestingStow || frame != (_nativeStowRequest with { Kind = frame.Kind })) return true;
                _requestingStow = false;
                if (frame.Kind == PlayerInventoryMessage.StowRequestAccepted)
                {
                    _nativeStowReserved = SourceStowPending;
                    foreach (var item in _sourceItems)
                        if (item.Entry.Key == frame.Key && item.Quantity == 1 && item.Entry.EquipmentSlot == frame.BeforeQuantity)
                            _nativeStowReserved = true;
                }
                Detail = frame.Kind == PlayerInventoryMessage.StowRequestAccepted
                    ? "Server reserved the native stow; its separate exact-custody completion receipt is pending."
                    : "Server declined this stow request without changing native custody.";
                RefreshEquipmentIntent();
                return true;
            }
            if (frame.Kind != PlayerInventoryMessage.StowRequest) return false;
            if (_lastStowRequestReply.Kind is PlayerInventoryMessage.StowRequestAccepted or PlayerInventoryMessage.StowRequestRejected
                && frame == (_lastStowRequestReply with { Kind = PlayerInventoryMessage.StowRequest }))
            { Send(_lastStowRequestReply, false); return true; }
            if (IsFaulted || !IsArmed || _receivedStowRequest || frame.Transaction <= _lastStowRequest) return true;
            _serverStowRequest = frame;
            _lastStowRequest = frame.Transaction;
            _receivedStowRequest = true;
            return true;
        }

        private void ProcessNativeStowRequest()
        {
            if (_authority == null || !_receivedStowRequest || !CanQueue) return;
            // Run outside Receive's busy scope; the authority's existing server transaction
            // preflight is the decision, not the client's requested item/slot/hash.
            var request = _serverStowRequest;
            if (!_authority.TryAuthorizeNativeStow(request, out bool settled, out var detail))
            {
                if (!settled) return;
                _lastStowRequestReply = request with { Kind = PlayerInventoryMessage.StowRequestRejected };
            }
            else _lastStowRequestReply = request with { Kind = PlayerInventoryMessage.StowRequestAccepted };
            _receivedStowRequest = false;
            Detail = detail;
            Send(_lastStowRequestReply, false);
        }

        private void RequestNativeEquip(ItemInstance instance, int itemId, int slot,
            bool memorizePrevious, bool forceFirstLook)
        {
            if (!OnThread || _writerDepth != 1 || _busy || IsDisposed || IsFaulted || !IsArmed || _bridge?.IsGameplayActive != true
                || _authority != null || SourceActionInputHeld || _requestingStow || _nativeStowReserved || _requestingEquip
                || HasPendingIntake || HasPendingNativeCraftRequest || _nativeEquipReserved || _hasIncomingGrant || SourceStowPending || SourceEquipPending
                || HasNativePowerCustody || HasPendingNativeCraftPresentation || _nextEquipRequest == ulong.MaxValue) return;
            try
            {
                if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _)
                    || _inventory._currentView != TheForest.Items.Inventory.PlayerInventory.PlayerViews.World
                    || !Current() || !Unchanged()) return;
                SourceItem selected = null;
                foreach (var item in _sourceItems)
                {
                    if (item.Quantity != 1 || !Stowed(item.Entry) || item.Entry.ItemId != itemId
                        || item.Instance == null || item.Instance.WasCollected
                        || (instance != null && item.Instance.Pointer != instance.Pointer)) continue;
                    // ID-only vanilla input must not pick a different copy with the same ID.
                    if (selected != null) return;
                    selected = item;
                }
                if (selected == null || !SupportsHeldStow(selected.Instance.Data)) return;
                if (!TryReadNativeEquipSlot(selected.Instance, slot, out int nativeSlot)) return;
                RefreshEquipmentIntent();
                _nativeEquipRequest = new PlayerInventoryFrame(_baseline.Plan.Id, _baseline.Plan.Generation,
                    _bridge.NetworkId, _baseline.Fingerprint, ++_nextEquipRequest, selected.Entry.Key,
                    selected.Entry.ItemId, 1, nativeSlot, PlayerInventoryMessage.EquipRequest,
                    PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(selected.Instance)),
                    EquipmentOptions: (byte)((memorizePrevious ? 1 : 0) | (forceFirstLook ? 2 : 0)));
                _requestingEquip = true;
                _equipRequestStarted = Stopwatch.GetTimestamp();
                _equipRequestSent = 0;
                Detail = "Exact native equip intent captured; no custody or presentation write is authorized yet.";
            }
            catch (Exception error) { Fail("Native equip request could not preserve custody: " + error.Message); }
        }

        private void PumpNativeEquipRequest()
        {
            if (!_requestingEquip || _authority != null) return;
            long now = Stopwatch.GetTimestamp();
            if (now - _equipRequestStarted >= TimeoutTicks)
            { Fail("Native equip authorization timed out; its exact request remains uncertain and cannot be replaced."); return; }
            if (_equipRequestSent != 0 && now - _equipRequestSent < RetryTicks) return;
            _equipRequestSent = now;
            Send(_nativeEquipRequest, false);
        }

        private bool ReceiveNativeEquipRequest(in PlayerInventoryFrame frame)
        {
            if (_authority == null)
            {
                if (frame.Kind is not (PlayerInventoryMessage.EquipRequestAccepted or PlayerInventoryMessage.EquipRequestRejected)) return false;
                if (IsFaulted || !_requestingEquip || frame != (_nativeEquipRequest with { Kind = frame.Kind })) return true;
                _requestingEquip = false;
                _nativeEquipReserved = frame.Kind == PlayerInventoryMessage.EquipRequestAccepted;
                Detail = _nativeEquipReserved
                    ? "Native equip reserved by the server; exact native terminal receipt is pending."
                    : "Server declined equip intent without changing custody.";
                RefreshEquipmentIntent();
                return true;
            }
            if (frame.Kind != PlayerInventoryMessage.EquipRequest) return false;
            if (_lastEquipRequestReply.Kind is PlayerInventoryMessage.EquipRequestAccepted or PlayerInventoryMessage.EquipRequestRejected
                && frame == (_lastEquipRequestReply with { Kind = PlayerInventoryMessage.EquipRequest }))
            { Send(_lastEquipRequestReply, false); return true; }
            if (IsFaulted || !IsArmed || _receivedEquipRequest || frame.Transaction <= _lastEquipRequest) return true;
            _serverEquipRequest = frame;
            _lastEquipRequest = frame.Transaction;
            _receivedEquipRequest = true;
            return true;
        }

        private void ProcessNativeEquipRequest()
        {
            if (_authority == null || !_receivedEquipRequest || !CanQueue) return;
            var request = _serverEquipRequest;
            if (!_authority.TryAuthorizeNativeEquip(request, out bool settled, out var detail))
            {
                if (!settled) return;
                _lastEquipRequestReply = request with { Kind = PlayerInventoryMessage.EquipRequestRejected };
            }
            else _lastEquipRequestReply = request with { Kind = PlayerInventoryMessage.EquipRequestAccepted };
            _receivedEquipRequest = false;
            Detail = detail;
            Send(_lastEquipRequestReply, false);
        }
    }

    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerConsumptionFrame _intakeRequest, _intakeCommand, _intakeReply, _serverIntakeRequest, _serverIntakeReply;
        private bool _requestingIntake, _intakePending, _receivedIntakeRequest;
        private ulong _nextIntakeRequest, _lastIntakeRequest;
        private long _intakeStarted, _intakeSent;
        private PlayerStats _intakeStats;
        private global::Vitals _intakeVitals;
        private global::Sons.StatSystem.Stat _intakeHydration, _intakeFullness, _intakeRest;
        private global::Sons.StatSystem.Stat _intakeHealth, _intakeSickness, _intakeStamina, _intakeVitality;
        private global::Sons.Player.PlayerState _intakeCounters;
        private ItemInstance _intakeItem;
        private InventoryEdibleItem _intakeEdible;
        private IntPtr _intakeData, _intakeModuleList, _intakeModule;
        private string _intakeModules;
        private PlayerInventoryModuleState _intakeModuleState;
        private int _intakeUses;
        private bool _intakeReusable, _intakeVolumePermit;
        private int _intakeCount;
        private bool HasPendingIntake => _requestingIntake || _intakePending || _receivedIntakeRequest;

        private bool RequestPlainIntake(global::Sons.Inventory.InventoryEdibleItem edible, ItemInstance native, bool unique)
        {
            if (!OnThread || _writerDepth != 1 || _busy || IsDisposed || IsFaulted || !IsArmed
                || _bridge?.IsGameplayActive != true || _authority != null || SourceActionInputHeld || HasPendingCustody || HasPendingNativeCraftPresentation
                || edible == null || edible.WasCollected || !edible
                || native == null || native.WasCollected || native._modules == null || native._modules.WasCollected
                || native._modules.Count is < 0 or > 1
                || _nextIntakeRequest == ulong.MaxValue) return false;
            try
            {
                if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _) || !Current() || !Unchanged()) return false;
                var data = native.Data;
                if (data == null || data.WasCollected || !data || unique != data._eachInstanceIsUnique
                    || (data._type & global::Sons.Items.Core.Types.Edible) == 0) return false;
                bool volume = data._isVolumeContainer;
                if (edible._isReusable != volume || (volume && edible._numberOfUses <= 0)) return false;
                if (!unique && !InventoryConsumption.SupportsPlainIntake(data)) return false;
                if (native._modules.Count == 1 && (volume
                    ? native._modules[0]?.TryCast<VolumeContainerItemInstanceModule>() == null
                    : native._modules[0]?.TryCast<PerishableItemInstanceModule>() == null)) return false;
                SourceItem selected = null;
                foreach (var item in _sourceItems)
                    if (item.Quantity > 0 && item.Entry.ItemId == native._itemID
                        && (Stowed(item.Entry) || (volume && NativeHandSlot(item.Entry.EquipmentSlot)))
                        && (unique ? item.Instance != null && !item.Instance.WasCollected && item.Instance.Pointer == native.Pointer
                            && PlayerInventoryBaseline.MatchesInstance(item.Entry, native) : item.Instance == null))
                    { selected = item; break; }
                if (selected == null) return false;
                var stats = TheForest.Utils.LocalPlayer.Stats;
                if (!InventoryConsumption.TryReadIntake(stats, out var before)
                    || !global::Sons.Player.PlayerState.TryGetInstance(out var counters) || counters == null || counters.WasCollected || !counters)
                    return false;
                int count = counters.GetConsumed(native._itemID);
                if (count < 0 || count == int.MaxValue) return false;
                if (!PrepareConsumptionPresentation(edible, native, selected)) return false;
                _intakeStats = stats; _intakeVitals = stats._vitals;
                _intakeHydration = _intakeVitals._hydration; _intakeFullness = _intakeVitals._fullness; _intakeRest = _intakeVitals._rested;
                _intakeHealth = _intakeVitals._health; _intakeSickness = _intakeVitals._sickness;
                _intakeStamina = _intakeVitals._stamina; _intakeVitality = _intakeVitals._vitality;
                _intakeCounters = counters; _intakeCount = count; _intakeItem = native;
                _intakeEdible = edible; _intakeUses = edible._numberOfUses; _intakeReusable = edible._isReusable;
                _intakeData = data.Pointer; _intakeModuleList = native._modules.Pointer;
                _intakeModule = native._modules.Count == 0 ? IntPtr.Zero : native._modules[0].Pointer;
                _intakeModules = PlayerInventoryBaseline.CaptureModules(native);
                _intakeModuleState = PlayerInventoryBaseline.CaptureModuleState(native);
                ulong request = ++_nextIntakeRequest;
                _intakeRequest = new(new(_baseline.Plan.Id, _baseline.Plan.Generation, _bridge.NetworkId,
                    _baseline.Fingerprint, request, selected.Entry.Key, selected.Entry.ItemId, 1,
                    selected.Quantity, PlayerInventoryMessage.ConsumeRequest), request, before, default);
                _requestingIntake = true;
                _intakeStarted = Stopwatch.GetTimestamp(); _intakeSent = 0;
                Detail = "Exact consumption intent captured before native removal, effects or animation.";
                return true;
            }
            catch (Exception error) { Detail = "Native intake refused before native entry: " + error.Message; return false; }
        }

        private bool IntakeSourceCurrent(ConsumptionNumbers expected)
            => Current() && _intakeStats != null && !_intakeStats.WasCollected
                && TheForest.Utils.LocalPlayer.Stats?.Pointer == _intakeStats.Pointer
                && _intakeStats._vitals?.Pointer == _intakeVitals?.Pointer
                && _intakeVitals?._hydration?.Pointer == _intakeHydration?.Pointer
                && _intakeVitals?._fullness?.Pointer == _intakeFullness?.Pointer
                && _intakeVitals?._rested?.Pointer == _intakeRest?.Pointer
                && _intakeVitals?._health?.Pointer == _intakeHealth?.Pointer
                && _intakeVitals?._sickness?.Pointer == _intakeSickness?.Pointer
                && _intakeVitals?._stamina?.Pointer == _intakeStamina?.Pointer
                && _intakeVitals?._vitality?.Pointer == _intakeVitality?.Pointer
                && _intakeItem != null && !_intakeItem.WasCollected && _intakeItem._itemID == _intakeRequest.Action.ItemId
                && _intakeItem.Data?.Pointer == _intakeData && _intakeItem._modules?.Pointer == _intakeModuleList
                && (_intakeModule == IntPtr.Zero ? _intakeItem._modules.Count == 0
                    : _intakeItem._modules.Count == 1 && _intakeItem._modules[0]?.Pointer == _intakeModule)
                && PlayerInventoryBaseline.JsonEqual(_intakeModules, PlayerInventoryBaseline.CaptureModules(_intakeItem))
                && _intakeModuleState == PlayerInventoryBaseline.CaptureModuleState(_intakeItem)
                && _intakeEdible != null && !_intakeEdible.WasCollected && _intakeEdible
                && _intakeEdible._numberOfUses == _intakeUses && _intakeEdible._isReusable == _intakeReusable
                && global::Sons.Player.PlayerState.TryGetInstance(out var counters) && counters != null
                && !counters.WasCollected && counters.Pointer == _intakeCounters?.Pointer
                && InventoryConsumption.TryReadIntake(_intakeStats, out var current) && current == expected;

        private bool CanApplyIntakeDebit(in PlayerConsumptionFrame frame)
        {
            SourceItem selected = null;
            foreach (var item in _sourceItems)
                if (item.Entry.Key == frame.Action.Key && item.Quantity > 0) { selected = item; break; }
            var debit = frame.Debit;
            if (selected == null || !debit.IsValid || selected.Quantity != debit.BeforeQuantity
                || selected.Entry.EquipmentSlot != debit.HeldSlot || selected.Entry.ClothingSlots != 0
                || selected.Entry.ArmourSlot >= 0 || selected.Entry.DefinitionOnly
                || selected.Entry.ItemId != frame.Action.ItemId) return false;
            if (selected.Instance == null)
                return !debit.UsesVolume && Stowed(selected.Entry) && debit.BeforeModuleFingerprint == null
                    && debit.PerishableState == -1 && !_intakeReusable && _intakeModule == IntPtr.Zero;
            if (selected.Instance.Pointer != _intakeItem.Pointer
                || !PlayerInventoryBaseline.MatchesInstance(selected.Entry, selected.Instance)
                || PlayerInventoryWire.HashModules(selected.Entry.Modules) != debit.BeforeModuleFingerprint) return false;
            if (debit.UsesVolume)
                return _intakeReusable && (Stowed(selected.Entry) || NativeHandSlot(debit.HeldSlot))
                    && _currentSnapshot.ReplicatedEquipmentMatches()
                    && InventoryConsumption.CanDebitVolume(selected.Instance, debit);
            var perishable = FindPerishable(selected.Instance);
            return !_intakeReusable && Stowed(selected.Entry)
                && (perishable == null ? debit.PerishableState == -1 : (int)perishable._currentState == debit.PerishableState);
        }

        private bool ApplyIntakeDebit(in PlayerConsumptionFrame frame)
        {
            if (!CanApplyIntakeDebit(frame)) return false;
            if (!frame.Debit.UsesVolume) return ApplyRemoval(frame.Action with { Kind = PlayerInventoryMessage.Remove });
            SourceItem selected = null;
            foreach (var item in _sourceItems)
                if (item.Entry.Key == frame.Action.Key) { selected = item; break; }
            if (selected == null || _writerDepth != 0 || InventoryFrozen) return false;
            int index = Stowed(selected.Entry) ? FindStoredEntry(_currentSnapshot, selected.Entry.ItemId, selected.Instance) : -1;
            for (int i = 0; index < 0 && i < _currentSnapshot.Entries.Count; i++)
            {
                var candidate = _currentSnapshot.Entries[i];
                if (selected.Entry.EquipmentSlot >= 0 && candidate.ItemId == selected.Entry.ItemId
                    && candidate.EquipmentSlot == selected.Entry.EquipmentSlot
                    && _inventory._equipmentSlots[candidate.EquipmentSlot]?.Pointer == selected.Instance.Pointer)
                { index = i; break; }
            }
            if (index < 0 || selected.Entry.ModuleState == null) return false;
            _intakeVolumePermit = true;
            try
            {
                if (!InventoryConsumption.TryDebitVolume(selected.Instance, frame.Debit)
                    || _intakeVolumePermit || !Current()) return false;
            }
            finally { _intakeVolumePermit = false; }
            string modules = PlayerInventoryBaseline.CaptureModules(selected.Instance);
            var moduleState = selected.Entry.ModuleState with { ContainerVolume = frame.Debit.AfterVolume };
            var after = PlayerInventoryBaseline.CaptureRetained(_baseline.Plan);
            if (!_currentSnapshot.ConfirmsModuleChange(after, index, modules, moduleState)
                || !after.ReplicatedEquipmentMatches()) return false;
            var entry = selected.Entry;
            _sourceItems[_sourceItems.IndexOf(selected)] = new SourceItem(new PlayerInventoryBaseline.Entry
            {
                Key = entry.Key, ItemId = entry.ItemId, Quantity = selected.Quantity, Modules = modules, ModuleState = moduleState,
                EquipmentSlot = entry.EquipmentSlot, ClothingSlots = entry.ClothingSlots,
                ArmourSlot = entry.ArmourSlot, ArmourPoints = entry.ArmourPoints, DefinitionOnly = entry.DefinitionOnly
            }, selected.Instance);
            _currentSnapshot = after;
            _intakeModules = modules;
            _intakeModuleState = moduleState;
            return true;
        }

        private void ProcessIntakeRequest()
        {
            if (_authority == null || !_receivedIntakeRequest || !CanQueue) return;
            var request = _serverIntakeRequest;
            if (_authority.TryAuthorizeNativeIntake(request, out var prepared, out var detail))
            {
                _intakeCommand = _serverIntakePrepared = prepared; _intakePending = true;
                _intakeStarted = Stopwatch.GetTimestamp(); _intakeSent = 0;
                _serverIntakeReply = prepared;
            }
            else _serverIntakeReply = request.WithKind(PlayerInventoryMessage.ConsumeRejected);
            _receivedIntakeRequest = false;
            Detail = detail;
            SendIntake(_serverIntakeReply);
        }

        private void PumpIntake()
        {
            if (!_requestingIntake && !_intakePending) return;
            long now = Stopwatch.GetTimestamp();
            if (now - _intakeStarted >= TimeoutTicks)
            { Fail("Consumption acknowledgement timed out; reserved custody/effects are not replayable or refundable."); return; }
            if (_intakeSent != 0 && now - _intakeSent < RetryTicks) return;
            SendIntake(_requestingIntake ? _intakeRequest : _authority != null ? _intakeCommand : _intakeReply);
        }

        private void SendIntake(in PlayerConsumptionFrame frame)
        {
            if (_bridge?.IsGameplayActive != true) return;
            _intakeSent = Stopwatch.GetTimestamp();
            SendIntakeFragments(frame);
        }

        private bool ReceiveIntake(Datagram message)
        {
            if (message == null || !IsIntakeFragment(message.Payload.Span)) return false;
            if (!OnThread || IsDisposed || _busy || _bridge?.IsGameplayActive != true
                || (_authority == null && _writerDepth != 0) || !ReferenceEquals(message.Peer, _bridge.Peer)
                || !TryAssembleIntake(message.Payload.Span, out var frame)
                || frame.Action.MigrationId != _baseline.Plan.Id || frame.Action.Generation != _baseline.Plan.Generation
                || frame.Action.NetworkId != _bridge.NetworkId || frame.Action.Fingerprint != _baseline.Fingerprint) return true;
            _busy = true;
            try
            {
                if (!Current()) { Fail("Consumption lost its exact player lifetime."); return true; }
                if (_authority != null) ReceiveServerIntake(frame);
                else ReceiveSourceIntake(frame);
            }
            catch (Exception error)
            {
                Fail("Consumption custody/projection is uncertain: " + error.Message);
                if (_authority == null && _intakePending
                    && _intakeReply.Action.Kind == PlayerInventoryMessage.ConsumeUnconfirmed) SendIntake(_intakeReply);
            }
            finally { _permit = false; _intakeVolumePermit = false; _busy = false; }
            return true;
        }

        private void ReceiveServerIntake(in PlayerConsumptionFrame frame)
        {
            if (frame.Action.Kind == PlayerInventoryMessage.ConsumeRequest)
            {
                if (frame == _serverIntakeRequest && _serverIntakeReply.Request == frame.Request)
                {
                    if (!IsFaulted || _serverIntakeReply.Action.Kind == PlayerInventoryMessage.ConsumeSettled)
                        SendIntake(_serverIntakeReply);
                    return;
                }
                if (IsFaulted || !IsArmed || _receivedIntakeRequest || _intakePending || frame.Debit != default
                    || HasPendingNativeCraftRequest || HasNativePowerCustody || frame.Request <= _lastIntakeRequest) return;
                _serverIntakeRequest = frame; _lastIntakeRequest = frame.Request; _receivedIntakeRequest = true;
                return;
            }
            if (!_intakePending)
            {
                if (_serverIntakeReply.Action.Kind == PlayerInventoryMessage.ConsumeSettled
                    && frame == _serverIntakeReply.WithKind(PlayerInventoryMessage.ConsumeApplied)) SendIntake(_serverIntakeReply);
                return;
            }
            if (IsFaulted) return;
            if (frame.Action.Kind == PlayerInventoryMessage.ConsumeRemoved)
            {
                // A source awaiting Project retries the original debit receipt, not the
                // rebased numeric result it has not received yet.
                if (frame != _serverIntakePrepared.WithKind(PlayerInventoryMessage.ConsumeRemoved)) return;
                if (_intakeCommand.Action.Kind == PlayerInventoryMessage.ConsumePrepare)
                {
                    if (!_authority.CommitConsumptionDebit(frame, frame.Debit, out var projection, out var detail))
                    { Fail(detail); return; }
                    _intakeCommand = projection;
                    Detail = detail; _serverIntakeReply = projection;
                }
                SendIntake(_intakeCommand); return;
            }
            if (frame.Action.Kind == PlayerInventoryMessage.ConsumeUnconfirmed)
            {
                if (frame == _intakeCommand.WithKind(frame.Action.Kind)
                    || frame == _serverIntakePrepared.WithKind(frame.Action.Kind))
                    Fail("Source consumption custody or numeric projection is unconfirmed.");
                return;
            }
            if (frame != _intakeCommand.WithKind(frame.Action.Kind)) return;
            if (frame.Action.Kind == PlayerInventoryMessage.ConsumeRejected && _intakeCommand.Action.Kind == PlayerInventoryMessage.ConsumePrepare)
            {
                if (!_authority.RejectPlainConsumption(frame)) { Fail("Consumption rejection did not match unwritten custody."); return; }
                _intakePending = false; _serverIntakeReply = frame; return;
            }
            if (frame.Action.Kind != PlayerInventoryMessage.ConsumeApplied || _intakeCommand.Action.Kind != PlayerInventoryMessage.ConsumeProject) return;
            if (!_authority.AcknowledgeConsumptionProjection(frame, frame.Debit, out var completion)) { Fail(completion); return; }
            _intakePending = false;
            LastCompletedTransaction = frame.Action.Transaction;
            LastReceipt = new(frame.Action.Transaction, frame.Action.Key, frame.Action.ItemId, 1, false, true) { IsConsumption = true };
            _serverIntakeReply = frame.WithKind(PlayerInventoryMessage.ConsumeSettled);
            Detail = completion;
            SendIntake(_serverIntakeReply);
        }

        private void ReceiveSourceIntake(in PlayerConsumptionFrame frame)
        {
            var kind = frame.Action.Kind;
            if (_requestingIntake && kind == PlayerInventoryMessage.ConsumeRejected
                && frame == _intakeRequest.WithKind(kind))
            {
                ReleaseConsumptionPresentation();
                _requestingIntake = false;
                Detail = "Consumption declined before native custody or effects changed.";
                return;
            }
            if (kind == PlayerInventoryMessage.ConsumeSettled && _intakePending
                && _intakeReply.Action.Kind == PlayerInventoryMessage.ConsumeApplied && frame == _intakeReply.WithKind(kind))
            {
                ReleaseConsumptionPresentation();
                _intakePending = false;
                LastCompletedTransaction = frame.Action.Transaction;
                LastReceipt = new(frame.Action.Transaction, frame.Action.Key, frame.Action.ItemId, 1, false, true) { IsConsumption = true };
                _intakeItem = null; _intakeEdible = null; _intakeStats = null; _intakeVitals = null; _intakeCounters = null;
                return;
            }
            if (kind == PlayerInventoryMessage.ConsumePrepare)
            {
                if (_intakeReply.Request != 0 && frame == _intakeReply.WithKind(kind)) { SendIntake(_intakeReply); return; }
                if (IsFaulted || !IsArmed || !_requestingIntake || _intakePending || frame.Request != _intakeRequest.Request
                    || frame.Action.Key != _intakeRequest.Action.Key || frame.Action.ItemId != _intakeRequest.Action.ItemId
                    || frame.Action.BeforeQuantity != _intakeRequest.Action.BeforeQuantity || frame.Before != _intakeRequest.Before
                    || !frame.Debit.IsValid || frame.Debit.BeforeQuantity != frame.Action.BeforeQuantity
                    || frame.Action.Transaction <= _lastReceived) return;
                _requestingIntake = false;
                _lastReceived = frame.Action.Transaction;
                _intakeCommand = frame;
                _intakeReply = frame.WithKind(PlayerInventoryMessage.ConsumeRejected);
                if (InventoryFrozen || _hasIncomingGrant || SourceStowPending || SourceEquipPending
                    || !IntakeSourceCurrent(frame.Before) || !Unchanged() || !CanApplyIntakeDebit(frame)
                    || !ConsumptionPresentationReady(frame)
                    || !InventoryConsumption.CanProjectIntake(_intakeStats, frame.Before, frame.After))
                { ReleaseConsumptionPresentation(); SendIntake(_intakeReply); return; }
                _intakePending = true;
                _intakeReply = frame.WithKind(PlayerInventoryMessage.ConsumeUnconfirmed);
                if (!ApplyIntakeDebit(frame) || !IntakeSourceCurrent(frame.Before))
                { Fail("Exact consumption source removal is unconfirmed."); SendIntake(_intakeReply); return; }
                _intakeReply = frame.WithKind(PlayerInventoryMessage.ConsumeRemoved);
                _intakeStarted = Stopwatch.GetTimestamp();
                SendIntake(_intakeReply); return;
            }
            if (kind != PlayerInventoryMessage.ConsumeProject) return;
            if (_intakeReply.Action.Kind == PlayerInventoryMessage.ConsumeApplied && frame == _intakeReply.WithKind(kind))
            { SendIntake(_intakeReply); return; }
            if (IsFaulted || !_intakePending || _intakeReply.Action.Kind != PlayerInventoryMessage.ConsumeRemoved
                || !_intakeCommand.SameReservation(frame)) return;
            BeginConsumptionPresentation(frame);
        }
    }

    public sealed partial class PlayerInventoryAuthority
    {
        internal bool TryAuthorizeNativeIntake(in PlayerConsumptionFrame request, out PlayerConsumptionFrame prepared, out string detail)
            => TryAuthorizeNativeDefinedIntake(in request, out prepared, out detail);

        internal bool TryAuthorizeNativeEquip(in PlayerInventoryFrame request, out bool settled, out string detail)
        {
            settled = false;
            detail = "The current inventory transaction must settle before equip authorization.";
            if (!Ready(out _) || _pending != null || _stow != null || _equip != null || MutationEnforcement?.CanQueue != true) return false;
            settled = true;
            detail = "Equip intent does not match exact server-owned stored custody.";
            if (!HandSlot(request.BeforeQuantity) || request.Quantity != 1 || request.EquipmentOptions > 3
                || !_items.TryGetValue(request.Key, out var item) || item.Consumed || item.Item == null
                || item.Id != request.ItemId || item.Quantity != 1 || item.HeldSlot >= 0 || item.Clothing
                || item.ArmourSlot >= 0 || _lastTransaction == ulong.MaxValue) return false;
            try
            {
                if (!item.IdentityCurrent() || PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(item.Item))
                    != request.ModuleFingerprint) return false;
                return TryEquipHeldItem(_lastTransaction + 1, item.Key, request.BeforeQuantity, out detail,
                    (request.EquipmentOptions & 1) != 0, (request.EquipmentOptions & 2) != 0);
            }
            catch (Exception error) { detail = "Server equip authorization rejected: " + error.Message; return false; }
        }

        internal bool TryAuthorizeNativeStow(in PlayerInventoryFrame request, out bool settled, out string detail)
        {
            settled = false;
            detail = "Existing server inventory transaction must settle before stow authorization.";
            if (!Ready(out _) || _pending != null || _stow != null || MutationEnforcement?.CanQueue != true) return false;
            settled = true;
            detail = "The requested stow does not match an exact server-owned hand instance.";
            if (!HandSlot(request.BeforeQuantity) || request.Quantity != 1
                || !_items.TryGetValue(request.Key, out var item) || item.Consumed || item.Item == null
                || item.Id != request.ItemId || item.Quantity != 1 || item.HeldSlot != request.BeforeQuantity
                || _lastTransaction == ulong.MaxValue) return false;
            try
            {
                if (!item.IdentityCurrent() || PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(item.Item))
                    != request.ModuleFingerprint) return false;
                // Client intent cannot choose a ledger transaction number. Reuse the qualified
                // native stow path; its capacity, module and publication receipts remain required.
                return TryStowHeldItem(_lastTransaction + 1, item.Key, out detail);
            }
            catch (Exception error) { detail = "Server stow authorization rejected: " + error.Message; return false; }
        }
    }
}
