// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    internal enum PlayerZiplineRecordKind : byte { Entry = 1, Exit = 2, Terminal = 3, EntryRequest = 4, ExitMotion = 5 }

    internal readonly record struct PlayerZiplineRecord(PlayerZiplineRecordKind Kind,
        PlayerFlightZiplineIdentity Identity, uint RequestSequence, uint ExitSequence,
        Vector3 Position, Quaternion Rotation, Vector3 Velocity,
        Vector3 RiderLocalPosition, Quaternion RiderLocalRotation, bool ReachedDestination)
    {
        internal float ConnectedHeightOffset { get; init; }
        internal uint MotionSequence { get; init; }
        internal float ExitElapsed { get; init; }
        internal float ExitMultiplier { get; init; }
        internal Vector3 ExitForce { get; init; }
        internal bool ExitForceActive { get; init; }
        internal NativeModeKey Key => new(Identity.DestinationIsAnchorA
            ? NativeMode.ZiplineToAnchorA : NativeMode.ZiplineToAnchorB,
            Identity.TransitionSequence, Identity.ControllerNetworkId, -1);
    }

    public sealed partial class PlayerBridge
    {
        // Companion records carry the hook assigned AFTER proposal admission. They never
        // select a mode: the existing proposal/approval/application stream still owns that.
        private const uint ZiplineMagic = 0x4c5a504d; // MPZL
        private const int ZiplineRecordLength = 172;
        private PlayerZiplineRecord? _ziplineEntry, _ziplineExit, _ziplineTerminal, _ziplineEntryRequest, _ziplineExitMotion;

        internal bool IsZiplineTransitionPending(PlayerModeTransitionProposal proposal)
            => CanAccessModeTransitions() && _server && ValidateCurrent() && proposal.RequestSequence != 0
                && _modeProposal == proposal && _modeApproval.Decision == PlayerModeTransitionDecision.Approved;

        internal bool TryReadZiplineEntryRequest(PlayerModeTransitionProposal proposal, out PlayerZiplineRecord record)
        {
            record = default;
            if (!CanAccessModeTransitions() || !_server || !ValidateCurrent() || proposal != _modeProposal
                || _ziplineEntryRequest is not { } request || !ZiplineMatchesProposal(request, proposal)) return false;
            record = request;
            return true;
        }

        internal bool TryReadZiplineExitMotion(PlayerModeTransitionProposal proposal, out PlayerZiplineRecord record)
        {
            record = default;
            if (!CanAccessModeTransitions() || _server || !ValidateCurrent() || proposal != _modeProposal
                || _ziplineExitMotion is not { } motion || !ZiplineMatchesProposal(motion, proposal)
                || _ziplineExit is not { } exit || motion.Identity != exit.Identity) return false;
            record = motion;
            return true;
        }

        internal bool TryReadZiplineRecord(PlayerZiplineRecordKind kind,
            PlayerModeTransitionProposal proposal, out PlayerZiplineRecord record)
        {
            record = default;
            if (!CanAccessModeTransitions() || _server || !ValidateCurrent()
                || proposal != _modeProposal || _modeApproval.Decision != PlayerModeTransitionDecision.Approved)
                return false;
            var retained = kind == PlayerZiplineRecordKind.Entry ? _ziplineEntry : _ziplineExit;
            if (retained is not { } candidate || candidate.Kind != kind || !ZiplineMatchesProposal(candidate, proposal)) return false;
            record = candidate;
            return true;
        }

        internal bool HasZiplineTerminal(PlayerFlightZiplineIdentity identity)
            => CanAccessModeTransitions() && !_server && ValidateCurrent()
                && _ziplineTerminal is { } terminal && terminal.Identity == identity && terminal.Key == _movementModeKey;

        internal DatagramSendResult SendZiplineRecord(PlayerZiplineRecord record)
        {
            bool request = record.Kind == PlayerZiplineRecordKind.EntryRequest;
            if (!CanAccessModeTransitions() || _server == request) return DatagramSendResult.Unavailable;
            _busy = true;
            try
            {
                if (!ValidateCurrent() || !ZiplineRecordValid(record)
                    || !Matches(record.Identity.MigrationId, record.Identity.Generation, record.Identity.NetworkId)
                    || (record.Kind == PlayerZiplineRecordKind.Terminal
                        ? record.Key != _movementModeKey
                        : !ZiplineMatchesProposal(record, _modeProposal)
                            || (request ? _modeClientApplied
                                : _modeApproval.Decision != PlayerModeTransitionDecision.Approved)))
                    return DatagramSendResult.InvalidPayload;
                Span<byte> bytes = stackalloc byte[ZiplineRecordLength];
                bytes.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(bytes, ZiplineMagic);
                bytes[4] = 2; bytes[5] = (byte)record.Kind;
                record.Identity.MigrationId.TryWriteBytes(bytes.Slice(8, 16));
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(24), record.Identity.Generation);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(32), record.Identity.NetworkId);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(40), record.Identity.TransitionSequence);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(44), record.RequestSequence);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(48), record.Identity.ControllerNetworkId);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(56), record.Identity.HookNetworkId);
                bytes[64] = record.Identity.DestinationIsAnchorA ? (byte)1 : (byte)0;
                bytes[65] = record.ReachedDestination ? (byte)1 : (byte)0;
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(68), record.ExitSequence);
                WriteZiplineVector(bytes.Slice(72), record.Position);
                WriteZiplineRotation(bytes.Slice(84), record.Rotation);
                WriteZiplineVector(bytes.Slice(100), record.Velocity);
                WriteZiplineVector(bytes.Slice(112), record.RiderLocalPosition);
                WriteZiplineRotation(bytes.Slice(124), record.RiderLocalRotation);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(140), record.ConnectedHeightOffset);
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(144), record.MotionSequence);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(148), record.ExitElapsed);
                BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(152), record.ExitMultiplier);
                WriteZiplineVector(bytes.Slice(156), record.ExitForce);
                bytes[168] = record.ExitForceActive ? (byte)1 : (byte)0;
                return Send(bytes);
            }
            catch (Exception error) { Retire(error); return DatagramSendResult.Failed; }
            finally { _busy = false; }
        }

        private bool TryReceiveZipline(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ZiplineMagic) return false;
            if (!CanAccessModeTransitions()) return true;
            _busy = true;
            try
            {
                if (bytes.Length != ZiplineRecordLength || bytes[4] != 2
                    || bytes[6] != 0 || bytes[7] != 0 || bytes[64] > 1 || bytes[65] > 1
                    || bytes[66] != 0 || bytes[67] != 0 || bytes[168] > 1
                    || bytes[169] != 0 || bytes[170] != 0 || bytes[171] != 0) return true;
                var identity = new PlayerFlightZiplineIdentity(new Guid(bytes.Slice(8, 16)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(40)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(56)), bytes[64] != 0);
                var record = new PlayerZiplineRecord((PlayerZiplineRecordKind)bytes[5], identity,
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(44)),
                    BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(68)),
                    ReadZiplineVector(bytes.Slice(72)), ReadZiplineRotation(bytes.Slice(84)),
                    ReadZiplineVector(bytes.Slice(100)), ReadZiplineVector(bytes.Slice(112)),
                    ReadZiplineRotation(bytes.Slice(124)), bytes[65] != 0)
                {
                    ConnectedHeightOffset = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(140)),
                    MotionSequence = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(144)),
                    ExitElapsed = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(148)),
                    ExitMultiplier = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(152)),
                    ExitForce = ReadZiplineVector(bytes.Slice(156)), ExitForceActive = bytes[168] != 0
                };
                // The live binding check is costly, so it runs only for well-formed frames for this bridge.
                if (!Matches(identity.MigrationId, identity.Generation, identity.NetworkId) || !ZiplineRecordValid(record)
                    || !ValidateCurrent()) return true;
                if (_server)
                {
                    if (record.Kind == PlayerZiplineRecordKind.EntryRequest && ZiplineMatchesProposal(record, _modeProposal)
                        && _modeApproval.Decision == PlayerModeTransitionDecision.None)
                    {
                        if (_ziplineEntryRequest is not { } old || old.RequestSequence < record.RequestSequence)
                            _ziplineEntryRequest = record;
                        else if (old != record) Dispose();
                    }
                    return true;
                }
                if (record.Kind == PlayerZiplineRecordKind.EntryRequest) return true;
                if (record.Kind == PlayerZiplineRecordKind.Terminal)
                {
                    if (record.Key == _movementModeKey && _ziplineEntry is { } entered && entered.Identity == identity)
                        _ziplineTerminal ??= record;
                }
                else if (ZiplineMatchesProposal(record, _modeProposal))
                {
                    if (record.Kind == PlayerZiplineRecordKind.Entry)
                    {
                        if (_ziplineEntry is not { } old || old.Identity.TransitionSequence < identity.TransitionSequence)
                        { _ziplineEntry = record; _ziplineExit = _ziplineTerminal = _ziplineExitMotion = null; }
                    }
                    else if (_ziplineEntry is { } entered && entered.Identity == identity)
                    {
                        if (record.Kind == PlayerZiplineRecordKind.Exit) _ziplineExit ??= record;
                        else if (record.Kind == PlayerZiplineRecordKind.ExitMotion
                            && (_ziplineExitMotion is not { } old || old.MotionSequence < record.MotionSequence))
                            _ziplineExitMotion = record;
                    }
                }
            }
            catch (Exception error) { Retire(error); }
            finally { _busy = false; }
            return true;
        }

        private static bool ZiplineMatchesProposal(PlayerZiplineRecord record, PlayerModeTransitionProposal proposal)
            => proposal.RequestSequence != 0 && record.RequestSequence == proposal.RequestSequence
                && record.Identity.MigrationId == proposal.MigrationId && record.Identity.Generation == proposal.Generation
                && record.Identity.NetworkId == proposal.NetworkId
                && (record.Kind is PlayerZiplineRecordKind.Entry or PlayerZiplineRecordKind.EntryRequest
                    ? proposal.Expected.Mode == NativeMode.Ground && proposal.Requested == record.Key && record.ExitSequence == 0
                    : record.Kind is PlayerZiplineRecordKind.Exit or PlayerZiplineRecordKind.ExitMotion && proposal.Expected == record.Key
                        && proposal.Requested == NativeModeKey.Ground(record.ExitSequence)
                        && record.ExitSequence == record.Key.TransitionSequence + 1);

        private static bool ZiplineRecordValid(PlayerZiplineRecord record)
            => record.Identity.MigrationId != Guid.Empty && record.Identity.Generation != 0
                && record.Identity.NetworkId != 0 && record.Identity.TransitionSequence != 0
                && record.Identity.ControllerNetworkId != 0
                && (record.Kind == PlayerZiplineRecordKind.EntryRequest ? record.Identity.HookNetworkId == 0 : record.Identity.HookNetworkId != 0)
                && (record.Kind == PlayerZiplineRecordKind.Terminal
                    ? record.RequestSequence == 0 && record.ExitSequence == 0
                    : record.Kind is PlayerZiplineRecordKind.Entry or PlayerZiplineRecordKind.EntryRequest
                        ? record.RequestSequence != 0 && record.ExitSequence == 0
                    : record.Kind is PlayerZiplineRecordKind.Exit or PlayerZiplineRecordKind.ExitMotion && record.RequestSequence != 0
                        && record.Identity.TransitionSequence != uint.MaxValue
                        && record.ExitSequence == record.Identity.TransitionSequence + 1)
                && ZiplineFinite(record.Position) && ZiplineFinite(record.Velocity) && ZiplineFinite(record.RiderLocalPosition)
                && ZiplineUnit(record.Rotation) && ZiplineUnit(record.RiderLocalRotation)
                && float.IsFinite(record.ConnectedHeightOffset)
                && (record.Kind == PlayerZiplineRecordKind.ExitMotion
                    ? record.MotionSequence != 0 && float.IsFinite(record.ExitElapsed) && record.ExitElapsed >= 0
                        && float.IsFinite(record.ExitMultiplier) && record.ExitMultiplier >= 0 && record.ExitMultiplier <= 1
                        && ZiplineFinite(record.ExitForce)
                    : record.MotionSequence == 0 && record.ExitElapsed == 0 && record.ExitMultiplier == 0
                        && record.ExitForce == Vector3.zero && !record.ExitForceActive);

        private void ClearZiplineReceipts() => _ziplineEntry = _ziplineExit = _ziplineTerminal = _ziplineEntryRequest = _ziplineExitMotion = null;
        private static bool ZiplineFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static bool ZiplineUnit(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y)
            && float.IsFinite(q.z) && float.IsFinite(q.w)
            && MathF.Abs(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w - 1) < 0.001f;
        private static void WriteZiplineVector(Span<byte> bytes, Vector3 v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, v.x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(4), v.y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(8), v.z);
        }
        private static void WriteZiplineRotation(Span<byte> bytes, Quaternion q)
        { WriteZiplineVector(bytes, new Vector3(q.x, q.y, q.z)); BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(12), q.w); }
        private static Vector3 ReadZiplineVector(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b),
            BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4)), BinaryPrimitives.ReadSingleLittleEndian(b.Slice(8)));
        private static Quaternion ReadZiplineRotation(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadSingleLittleEndian(b),
            BinaryPrimitives.ReadSingleLittleEndian(b.Slice(4)), BinaryPrimitives.ReadSingleLittleEndian(b.Slice(8)),
            BinaryPrimitives.ReadSingleLittleEndian(b.Slice(12)));
    }
}
