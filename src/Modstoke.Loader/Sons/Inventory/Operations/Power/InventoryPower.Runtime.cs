// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryAuthority
    {
        private PendingPowerProjection _powerProjection;
        private long _nextPowerTick;
        private ulong _powerCursor;

        /// <summary>Include in inventory mutation and lifecycle-idle gates until exact source acknowledgement.</summary>
        public bool HasPendingNativePowerProjection => _powerProjection != null;

        // Called by the endpoint before it enters its own busy section. Unity scaled time,
        // not packet cadence or source time, determines drain across acknowledgement waits.
        internal void PumpPower(bool allowNewActivity)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended
                || !_bridge.IsGameplayActive || !Current(out _) || MutationEnforcement?.IsArmed != true
                || HasPendingNativePowerProjection) return;
            if (_powerSupply != null)
            {
                // Air remains owned by the existing StrictCombat refill state machine.
                if (_powerSupply.Supply.Id == InventoryPower.BatteryItemId)
                    TryCompletePowerSupply(out _, out _, out _, out _);
                if (IsFaulted) MutationEnforcement.FailPassive(Detail);
                return;
            }
            if (!allowNewActivity || !Ready(out _) || _pending != null || _stow != null || _equip != null
                || !MutationEnforcement.CanQueue || Stopwatch.GetTimestamp() < _nextPowerTick) return;
            try
            {
                FlashlightPower selected = null;
                foreach (var power in _flashlightPower.Values)
                {
                    if (!power.Active && power.Revision == power.AcknowledgedRevision) continue;
                    if (selected == null || (power.Item.Key > _powerCursor
                        ? selected.Item.Key <= _powerCursor || power.Item.Key < selected.Item.Key
                        : selected.Item.Key <= _powerCursor && power.Item.Key < selected.Item.Key)) selected = power;
                }
                if (selected == null) return;
                RequireFlashlightCurrent(selected);
                _powerCursor = selected.Item.Key;
                _nextPowerTick = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
                if (selected.Revision == selected.AcknowledgedRevision && PowerState(selected).NeedsBattery)
                {
                    foreach (var supply in _items.Values)
                    {
                        if (supply.Id != InventoryPower.BatteryItemId || supply.Consumed
                            || supply.Quantity < 1 || supply.HeldSlot >= 0 || supply.Clothing || supply.ArmourSlot >= 0) continue;
                        if (_lastTransaction == ulong.MaxValue) throw new InvalidOperationException("Inventory transaction sequence exhausted.");
                        if (!TryQueueFlashlightBattery(_lastTransaction + 1, selected.Item.Key, supply.Key, out var detail))
                            throw new InvalidOperationException("Admitted battery removal could not be reserved: " + detail);
                        return;
                    }
                }
                if (!TryAdvanceFlashlightPower(selected.Item.Key, out _, out var progression))
                    throw new InvalidOperationException(progression);
            }
            catch (Exception error) { MutationEnforcement.FailPassive("Flashlight power progression is unconfirmed: " + error.Message); }
        }

        private void StagePowerProjection(FlashlightPower power)
        {
            if (power.Revision == power.AcknowledgedRevision) return;
            RequireFlashlightCurrent(power);
            if (_powerProjection != null || _lastTransaction == ulong.MaxValue
                || !Current(out var target)) throw new InvalidOperationException("Power projection cannot reserve exact inventory custody.");
            var equipment = EquipmentState.Capture(target.GetState<IPlayerState>());
            if (equipment == null || !OwnedEquipmentMatches(equipment))
                throw new InvalidOperationException("Power projection lost its equipment boundary.");
            var moduleState = power.AcknowledgedModuleState with { ContainerVolume = power.Charge };
            if (PlayerInventoryBaseline.CaptureModuleState(power.Item.Item) != moduleState)
                throw new InvalidOperationException("Power progression changed unrelated supplemental module state.");
            var frame = new PowerFrame(_bridge.Plan.Id, _bridge.Plan.Generation, _bridge.NetworkId,
                AcceptedBaselineFingerprint, _lastTransaction + 1, PowerState(power), power.AcknowledgedCharge,
                PlayerInventoryWire.HashModules(power.AcknowledgedModules),
                PlayerInventoryWire.HashModules(PlayerInventoryBaseline.CaptureModules(power.Item.Item)),
                PowerMessage.Project);
            _powerProjection = new(frame, power, equipment, moduleState);
            _lastTransaction = frame.Transaction;
        }

        internal bool TryGetPowerProjection(out PowerFrame frame)
        {
            frame = default;
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended
                || _powerProjection == null || !_bridge.IsGameplayActive || !Current(out _)) return false;
            frame = _powerProjection.Frame;
            return true;
        }

        internal bool CompletePowerProjection(in PowerFrame receipt)
        {
            if (Environment.CurrentManagedThreadId != _thread || _busy || _suspended || _powerProjection == null
                || receipt != (_powerProjection.Frame with { Kind = PowerMessage.Applied })) return false;
            _busy = true;
            try
            {
                var pending = _powerProjection;
                RequireFlashlightCurrent(pending.Power);
                if (!Current(out var target) || receipt.State != PowerState(pending.Power)
                    || !pending.Equipment.Matches(EquipmentState.Capture(target.GetState<IPlayerState>()))) return false;
                string modules = PlayerInventoryBaseline.CaptureModules(pending.Power.Item.Item);
                if (PlayerInventoryWire.HashModules(modules) != receipt.AfterModules
                    || PlayerInventoryBaseline.CaptureModuleState(pending.Power.Item.Item) != pending.ModuleState) return false;
                pending.Power.AcknowledgedModules = modules;
                pending.Power.AcknowledgedModuleState = pending.ModuleState;
                pending.Power.AcknowledgedCharge = receipt.State.Charge;
                pending.Power.AcknowledgedRevision = receipt.State.Revision;
                _powerProjection = null;
                return true;
            }
            finally { _busy = false; }
        }

        private sealed class PendingPowerProjection(PowerFrame frame, FlashlightPower power, EquipmentState equipment,
            PlayerInventoryModuleState moduleState)
        {
            internal readonly PowerFrame Frame = frame;
            internal readonly FlashlightPower Power = power;
            internal readonly EquipmentState Equipment = equipment;
            internal readonly PlayerInventoryModuleState ModuleState = moduleState;
        }
    }

    public sealed partial class PlayerInventoryEnforcement
    {
        private ulong _sentPowerTransaction;
        private long _powerSent, _powerStarted;
        private PowerFrame _powerReply;

        internal bool HasNativePowerCustody => _authority?.HasPendingNativePowerProjection == true
            || _authority?.HasPendingNativePowerSupply == true;

        // Route from Pump before its busy section; do not also subscribe a second game-loop driver.
        internal void PumpPower()
        {
            if (!OnThread || _busy || IsDisposed || IsFaulted || !IsArmed || _authority == null
                || _bridge?.IsGameplayActive != true) return;
            if (!Current()) { Fail("Power endpoint lost its actor lifetime."); return; }
            // Pending native intent gets first opportunity after a power receipt settles;
            // an automatic drain projection must not starve stow/equip on a slow connection.
            _authority.PumpPower(!HasPendingCustody);
            if (IsFaulted || !_authority.TryGetPowerProjection(out var frame)) return;
            long now = Stopwatch.GetTimestamp();
            if (_sentPowerTransaction != frame.Transaction)
            { _sentPowerTransaction = frame.Transaction; _powerStarted = now; _powerSent = 0; }
            if (now - _powerStarted >= TimeoutTicks)
            { Fail("Exact power projection was not acknowledged; native consumers remain guarded."); return; }
            if (_powerSent == 0 || now - _powerSent >= RetryTicks)
            { _powerSent = now; SendPower(frame); }
        }

        // Route before the ordinary inventory codec. Authentication and the immutable
        // migration/baseline identity are checked here, never inferred from item IDs.
        internal bool ReceivePower(Datagram message)
        {
            if (!OnThread || IsDisposed || _busy || _bridge == null || message == null
                || !ReferenceEquals(message.Peer, _bridge.Peer)
                || !PowerWire.TryRead(message.Payload.Span, out var frame)) return false;
            if (frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.Fingerprint != _baseline.Fingerprint
                || !_bridge.IsGameplayActive || (_authority == null && _writerDepth != 0)) return true;
            _busy = true;
            try
            {
                if (!Current()) { Fail("Power projection lost its exact bridge lifetime."); return true; }
                if (_authority != null)
                {
                    if (IsFaulted || !IsArmed || !_authority.TryGetPowerProjection(out var pending)
                        || frame != (pending with { Kind = frame.Kind })) return true;
                    if (frame.Kind == PowerMessage.Project) return true;
                    if (frame.Kind != PowerMessage.Applied || !_authority.CompletePowerProjection(frame))
                    { Fail("Native power projection is unconfirmed; no retry or resource adoption is authorized."); return true; }
                    LastCompletedTransaction = frame.Transaction;
                    LastReceipt = null;
                    return true;
                }
                if (frame.Kind != PowerMessage.Project) return true;
                if (_powerReply.Transaction != 0 && frame == (_powerReply with { Kind = PowerMessage.Project }))
                { SendPower(_powerReply); return true; }
                if (IsFaulted || !IsArmed || _hasPending || _intakePending || _hasIncomingGrant
                    || _sourceStowAwaitingCommit || SourceEquipPending || InventoryFrozen
                    || frame.Transaction <= _lastReceived) return true;
                _lastReceived = frame.Transaction;
                _powerReply = frame with { Kind = PowerMessage.Unconfirmed };
                if (ApplyPower(frame))
                {
                    _powerReply = frame with { Kind = PowerMessage.Applied };
                    LastCompletedTransaction = frame.Transaction;
                    LastReceipt = null;
                }
                else Fail("Exact source power projection failed; competing native consumers remain excluded.");
                SendPower(_powerReply);
                return true;
            }
            catch (Exception error)
            {
                Fail("Power transport or native projection failed: " + error.Message);
                return true;
            }
            finally { _busy = false; }
        }

        private void SendPower(in PowerFrame frame)
        {
            Span<byte> bytes = stackalloc byte[PowerWire.Length];
            PowerWire.Write(frame, bytes);
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result != DatagramSendResult.Queued && result != DatagramSendResult.WindowFull)
                Fail("Power projection transport unavailable (" + result + ").");
        }
    }
}
