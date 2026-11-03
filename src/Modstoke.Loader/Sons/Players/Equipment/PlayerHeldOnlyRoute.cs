// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json;
using Modstoke.Loader;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;
using Enforcement = Modstoke.Sons.PlayerInventoryEnforcement;

namespace Modstoke.Sons
{
    /// <summary>Receipt-bound drop custody over the existing enrolled-peer datagrams.</summary>
    internal sealed partial class PlayerHeldOnlyRoute : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly int _load = SaveLifecycle.State.BeforeLoadCount;
        private readonly PlayerBridge _bridge;
        private readonly Enforcement _inventory;
        private readonly Held _held;
        private readonly Enforcement.HeldOnlySource _source;
        private readonly PlayerCarrier _carrier;
        private readonly string _baseline;
        private readonly byte[] _baselineHash;
        private Held.Mutation _mutation;
        private Held.NativeCommand _command;
        private Held.NativeReceipt _native;
        private Request _request;
        private WorldCommit _worldCommit;
        private ulong _requestId, _lastRequest, _equipTransaction;
        private PlayerInventoryFrame? _equipRequest, _equipCommand;
        private bool _completed, _acknowledged, _sourceComplete, _busy, _disposed, _published, _audioDone;
        private string _failure;
        internal string Detail { get; private set; }
        internal bool HasPendingCustody => _requestId != 0 && (!_completed
            || _held != null && !_acknowledged && _command != null);

        private sealed record Request(string Before, Held.MutationKind Kind, bool Restore,
            ulong EquipKey, int EquipItem, string EquipModules, ulong ForcedTransition = 0, ForcedHandRequest[] ForcedHands = null);
        private sealed record WorldCommit(string Command, ulong[] Objects);
        private sealed record EquipmentCommit(string Command, ulong Transaction, ulong[] ForcedTransactions = null);

        private PlayerHeldOnlyRoute(PlayerBridge bridge, Enforcement inventory,
            Held held, Enforcement.HeldOnlySource source, PlayerCarrier carrier)
        {
            _bridge = bridge; _inventory = inventory; _held = held; _source = source;
            _carrier = carrier;
            _baseline = held?.AcceptedBaselineFingerprint ?? source.Baseline.Fingerprint;
            _baselineHash = Convert.FromHexString(_baseline);
        }

        internal static bool TryBindServer(PlayerBridge bridge, Enforcement inventory, Held held,
            PlayerCarrier carrier,
            out PlayerHeldOnlyRoute route, out string detail)
        {
            route = null;
            detail = "Held drop route requires exact approved server custody and restored actor; world geometry binds before dispatch.";
            if (!GameBuild.IsRecognized || bridge?.IsServer != true || inventory == null || held == null || carrier == null
                || inventory.HeldOnlyRoute != null || !ReferenceEquals(inventory.HeldOnlyAuthority, held)
                || !inventory.HeldOnlyServerBindingReady(bridge)
                || !held.TryRead(bridge, out var state, out detail) || !carrier.TryReadRestoredServerStats(out _)
                || !bridge.TryReadCurrentTarget(out var actor) || carrier.Entity?.Pointer != actor.Pointer) return false;
            try
            {
                // The migration already preserved these exact native presentation fields.
                // Raw custody and Amount remain solely in held; never seed a second controller.
                var publication = actor.GetState<IPlayerState>();
                int publishedId = state.ItemId > 0 ? state.ItemId : 0;
                if (publication == null || publication.WasCollected || publication.HeldOnlyItemId != publishedId
                    || publication.HeldOnlyItemCount < 0 || publication.HeldOnlyItemCount > state.Count)
                { detail = "Preserved native held publication disagrees with approved actor custody."; return false; }
                if (state.Count > 0 && !held.CanRouteDropModules)
                { detail = "Nonempty module-bearing held custody still requires its native module/effect owner."; return false; }
                if (state.Count > 0 && !DropActorAvailable(actor, state.ItemId, out detail)) return false;
            }
            catch (Exception error) { detail = "Held drop binding rejected: " + error.Message; return false; }
            route = new(bridge, inventory, held, null, carrier);
            route.Bind();
            detail = route.Detail;
            return true;
        }

        internal static bool TryBindSource(Enforcement.HeldOnlySource source,
            out PlayerHeldOnlyRoute route, out string detail)
        {
            route = null;
            detail = "Held route requires the bound original source and its current inventory endpoint.";
            if (source == null || !source.Lifetime(true, false) || source.InventoryOwner.HeldOnlyRoute != null) return false;
            route = new(source.Bridge, source.InventoryOwner, null, source, null);
            route.Bind();
            detail = route.Detail;
            return true;
        }

        private void Bind()
        {
            _inventory.HeldOnlyRoute = this;
            Datagrams.Received += Receive;
            _bridge.Retired += Dispose;
            Detail = "Concrete held drop endpoint bound without activation; pending effects and unsupported held operations remain fenced.";
        }

        private bool Current() => !_disposed && _failure == null && Environment.CurrentManagedThreadId == _thread
            && _load == SaveLifecycle.State.BeforeLoadCount && !_bridge.IsDisposed
            && _bridge.IsGameplayActive && _bridge.Peer.IsCurrent && _bridge.TryReadCurrentTarget(out _)
            && !_inventory.IsDisposed && !_inventory.IsFaulted;

        internal void Pump()
        {
            if (_busy || !Current()) return;
            _busy = true;
            try
            {
                if (_source != null) PumpSource();
                else PumpServer();
                PumpSend();
            }
            catch (Exception error) { Fault(error.Message); }
            finally { _busy = false; }
        }

        private void PumpSource()
        {
            if (!HasPendingCustody)
            {
                if (TryStartForcedSource()) return;
                if (!_inventory.HeldOnlySourceReady || !Enforcement.TryTakeHeldOnlyIntent(_source, out var intent)) return;
                if (intent.Kind is not (Held.MutationKind.PutDown or Held.MutationKind.DropAll)
                    || !intent.Drop || intent.Fake || intent.RequiresLifeOwner || intent.PreSpawned != null || intent.Delay != -1f
                    || intent.Kind == Held.MutationKind.PutDown && intent.ItemId != -1)
                { Detail = "Intent still needs its concrete lift/storage/settings/life owner; no action dispatched."; return; }
                bool restore = intent.EquipPrevious && _source.Controller._heldCount == 1;
                if (!_source.TryPrepareCompoundRestore(restore, out var detail)) { Detail = detail; return; }
                ResetExchange(intent.Sequence);
                _request = new(_source.SettledFingerprint, intent.Kind, restore, _source.CompoundKey,
                    _source.CompoundItemId, _source.CompoundModules);
                SendJson(1, _request);
                return;
            }
            if (_command == null) return;
            if (_source.FailureReason != null) throw new InvalidOperationException(_source.FailureReason);
            if (_native == null && _source.TryCaptureReceipt(out var native, out _))
            { _native = native; SetSend(3, native.Export()); }
            if (_worldCommit == null || _sourceComplete) return;
            if (!_audioDone)
            {
                if (!ConfirmSourceWorldObjects()) return;
                PlayConfirmedAudio();
                _audioDone = true;
            }
            if (Held.IsForcedDrop(_command.Kind))
            {
                if (!_source.TryPumpForcedRestore(out var transactions, out var forcedDetail))
                { Detail = forcedDetail; return; }
                _sourceComplete = true;
                SendJson(5, new EquipmentCommit(_command.Fingerprint, 0, transactions));
                return;
            }
            if (!_source.TryPumpCompoundRestore(out var receipt, out var equipmentDetail))
            { Detail = equipmentDetail; return; }
            _sourceComplete = true;
            SendJson(5, new EquipmentCommit(_command.Fingerprint, receipt?.Transaction ?? 0));
        }

        private void PumpServer()
        {
            if (_completed || !HasPendingCustody || _native == null) return;
            if (_worldCommit == null)
            {
                if (!ApplyWorldDrops()) return;
                if (!_held.TryPublishDropEquipment(_mutation, _native, _carrier, out var detail))
                    throw new InvalidOperationException(detail);
                _published = true;
                _worldCommit = new(_command.Fingerprint, WorldIds());
                SendJson(4, _worldCommit);
            }
            if (!_sourceComplete || !_published) return;
            if (Held.IsForcedDrop(_command.Kind) && !ForcedServerEquipmentComplete()) return;
            if (_request.EquipKey != 0 && (_equipCommand is not { } issued || issued.Transaction != _equipTransaction
                || !_inventory.TryReadCompoundServerEquip(_request.EquipKey,
                    _request.EquipItem, out var equipment) || equipment.Transaction != _equipTransaction)) return;
            if (!_held.TryCompleteSettled(_mutation, _native.After, out var completion))
                throw new InvalidOperationException(completion);
            _completed = true;
            SendJson(6, _command.Fingerprint);
            Detail = "World objects, no-strength drop receipt, held renderer publication and exact hand equip settled.";
        }

        private void Dispatch(byte phase, byte[] bytes)
        {
            if (_source != null)
            {
                if (phase == 2)
                {
                    Require(_command == null, "Held command was already dispatched.");
                    Require(Held.NativeCommand.TryRead(_bridge.Plan, bytes, out _command, out _),
                        "Held command could not be decoded.");
                    Require(_command.Kind == _request.Kind && _command.Drop && _command.Before.Fingerprint == _request.Before,
                        "Server command differs from the retained original drop intent.");
                    Require(_source.TryExecute(_command, null, out var detail), detail);
                }
                else if (phase == 4)
                {
                    var world = JsonSerializer.Deserialize<WorldCommit>(bytes);
                    Require(_native != null && world?.Command == _command.Fingerprint && world.Objects != null
                        && world.Objects.Length == _command.DropCount, "World receipt differs from the native action.");
                    _worldCommit = world;
                }
                else if (phase == 6)
                {
                    Require(_sourceComplete && JsonSerializer.Deserialize<string>(bytes) == _command.Fingerprint,
                        "Aggregate acknowledgement has no exact completed action.");
                    Require(_source.TryAcknowledgeSettlement(_command.Transaction, _command.Fingerprint, out var detail), detail);
                    _completed = true;
                    if (Held.IsForcedDrop(_command.Kind)) _forcedSettled = true;
                    SendJson(7, _command.Fingerprint);
                }
                else if (phase == 8)
                {
                    Require(_command == null, "Cannot reject an action after native dispatch.");
                    Require(!Held.IsForcedDrop(_request.Kind), "Forced drop was declined; keep its owning life/water transition held.");
                    _source.CancelCompoundBeforeDispatch(); _completed = true; _send = null;
                    Detail = "Server declined the drop before native dispatch.";
                }
                return;
            }
            if (phase == 1)
            {
                _request = JsonSerializer.Deserialize<Request>(bytes);
                if (_request == null || _request.Kind is not (Held.MutationKind.PutDown or Held.MutationKind.DropAll) && !Held.IsForcedDrop(_request.Kind)
                    || !ForcedRequestMatches(_request)
                    || HasPendingForcedDrop && !Held.IsForcedDrop(_request.Kind)
                    || !_inventory.HeldOnlyServerReady(_bridge) || _held.LastTransaction == ulong.MaxValue
                    || !_held.TryRead(_bridge, out var before, out _) || before.Count < 1
                    || !_held.CanRouteDropModules
                    || !CanDispatchDrop(before.ItemId)
                    || _request.Restore && (_request.Kind != Held.MutationKind.PutDown || before.Count != 1)
                    || !_request.Restore && _request.EquipKey != 0
                    || (_request.EquipKey == 0 ? _request.EquipItem != 0 || _request.EquipModules != null
                        : _request.EquipItem <= 0 || _request.EquipModules?.Length != 64)
                    || !_held.MatchesSettledFingerprint(_request.Before))
                { Reject(); return; }
                bool reserved = Held.IsForcedDrop(_request.Kind)
                    ? _held.TryReserveForcedDrop(_held.LastTransaction + 1, _request.Kind, out _mutation, out _)
                    : _request.Kind == Held.MutationKind.DropAll
                    ? _held.TryReserveDropAll(_held.LastTransaction + 1, out _mutation, out _)
                    : _held.TryReservePutDown(_held.LastTransaction + 1, true, out _mutation, out _);
                if (!reserved) { Reject(); return; }
                if (!_held.TryDispatchNative(_mutation, out _command, out var detail))
                    throw new InvalidOperationException(detail);
                SetSend(2, _command.Export());
            }
            else if (phase == 3)
            {
                Require(_command != null, "No native command is reserved.");
                Require(Held.NativeReceipt.TryRead(_command, bytes, out _native, out _), "Native drop receipt is invalid.");
                Require(_held.TryRecordNativeReceipt(_mutation, _native, out var detail), detail);
            }
            else if (phase == 5)
            {
                var receipt = JsonSerializer.Deserialize<EquipmentCommit>(bytes);
                Require(_worldCommit != null && receipt?.Command == _command.Fingerprint
                    && (_request.EquipKey == 0 ? receipt.Transaction == 0 : receipt.Transaction != 0),
                    "Equipment receipt does not match this compound drop.");
                Require(Held.IsForcedDrop(_command.Kind) ? receipt.ForcedTransactions is { Length: 2 }
                    : receipt.ForcedTransactions == null, "Forced equipment receipt shape differs from its command.");
                _forcedSourceTransactions = receipt.ForcedTransactions;
                _equipTransaction = receipt.Transaction; _sourceComplete = true;
            }
            else if (phase == 7)
            {
                Require(_completed && JsonSerializer.Deserialize<string>(bytes) == _command.Fingerprint,
                    "Source acknowledgement differs from settled custody.");
                _acknowledged = true; _send = null;
                if (Held.IsForcedDrop(_command.Kind)) _forcedSettled = true;
            }
        }

        internal bool MatchesRestoreRequest(in PlayerInventoryFrame frame)
        {
            if (Current() && _held != null && _request?.ForcedHands != null) return MatchesForcedRestoreRequest(in frame);
            if (!Current() || _held == null || _worldCommit == null || _request.EquipKey == 0
                || frame.Kind != PlayerInventoryMessage.EquipRequest || frame.Key != _request.EquipKey
                || frame.ItemId != _request.EquipItem || frame.Quantity != 1 || frame.BeforeQuantity != 0
                || frame.EquipmentOptions != 0 || frame.ModuleFingerprint != _request.EquipModules) return false;
            if (_equipRequest is { } request) return frame == request;
            _equipRequest = frame;
            return true;
        }

        internal bool MatchesRestoreCommand(in PlayerInventoryFrame frame)
        {
            if (Current() && _held != null && _request?.ForcedHands != null) return MatchesForcedRestoreCommand(in frame);
            if (!Current() || _held == null || _equipRequest is not { } request || frame.Key != request.Key
                || frame.ItemId != request.ItemId || frame.Quantity != 1 || frame.BeforeQuantity != 0
                || frame.EquipmentOptions != 0 || frame.ModuleFingerprint != request.ModuleFingerprint
                || frame.Kind is not (PlayerInventoryMessage.EquipApplied or PlayerInventoryMessage.EquipSettled
                    or PlayerInventoryMessage.EquipRejected or PlayerInventoryMessage.EquipUnconfirmed)) return false;
            if (_equipCommand is { } command) return frame == (command with { Kind = frame.Kind });
            _equipCommand = frame;
            return true;
        }

        private void ResetExchange(ulong request)
        {
            _requestId = _lastRequest = request; _completed = _acknowledged = _sourceComplete = _published = _audioDone = false;
            _request = null; _command = null; _mutation = null; _native = null; _worldCommit = null;
            _equipRequest = _equipCommand = null; _equipTransaction = 0;
            Array.Clear(_forcedEquipRequests); Array.Clear(_forcedEquipCommands); Array.Clear(_forcedEquipSettled);
            _forcedSourceTransactions = null;
            _assembly = null; _receivedPhase = 0; _receivedHash = null; _destinations = null;
            _send = _sendHash = null; _sendPhase = 0; _sendOffset = 0; _sent = 0;
        }

        private void Reject() { _completed = true; SendJson(8, "Refused before dispatch"); }
        private void Fault(string reason)
        {
            _failure ??= reason; Detail = "Held compound custody uncertain; no replay/refund: " + _failure;
            _source?.Fault(reason);
            if (_mutation != null) _held.HoldUnconfirmed(_mutation, Detail);
        }
        private static void Require(bool value, string reason)
        { if (!value) throw new InvalidOperationException(reason); }
        public void Dispose()
        {
            if (_disposed || Environment.CurrentManagedThreadId != _thread) return;
            if (HasPendingCustody || HasPendingForcedDrop) Fault("Endpoint retired before complete custody acknowledgement.");
            _disposed = true;
            Datagrams.Received -= Receive;
            _bridge.Retired -= Dispose;
            // Keep the endpoint rooted by its inventory; unresolved objects are never refunded.
        }
    }
}
