// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private const uint IntakeFragmentMagic = 0x4649504D; // MPIF, carrying one exact MPIC V7 frame.
        private const int IntakeFragmentLength = 240, IntakeFragmentHeader = 64, IntakeFragmentBody = 176;
        private readonly byte[] _intakeSendBytes = new byte[2 * IntakeFragmentLength];
        private readonly byte[] _intakeAssembly = new byte[PlayerConsumptionWire.Length];
        private readonly byte[] _intakeAssemblyHash = new byte[32];
        private readonly byte[] _intakeLastBytes = new byte[PlayerConsumptionWire.Length];
        private readonly byte[] _intakeLastHash = new byte[32];
        private PlayerConsumptionFrame _intakeEncodedFrame, _intakeDecodedFrame;
        private int _intakeSendFragment, _intakeAssemblyMask;
        private ulong _intakeAssemblyRequest, _intakeAssemblyTransaction;
        private PlayerInventoryMessage _intakeAssemblyKind;

        private void SendIntakeFragments(in PlayerConsumptionFrame frame)
        {
            if (frame != _intakeEncodedFrame)
            {
                Span<byte> full = stackalloc byte[PlayerConsumptionWire.Length];
                PlayerConsumptionWire.Write(frame, full);
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(full, hash);
                for (int i = 0; i < 2; i++)
                {
                    var fragment = _intakeSendBytes.AsSpan(i * IntakeFragmentLength, IntakeFragmentLength);
                    fragment.Clear();
                    BinaryPrimitives.WriteUInt32LittleEndian(fragment, IntakeFragmentMagic);
                    fragment[4] = 1;
                    fragment[5] = (byte)i;
                    hash.CopyTo(fragment[8..40]);
                    BinaryPrimitives.WriteUInt64LittleEndian(fragment[40..], frame.Request);
                    BinaryPrimitives.WriteUInt64LittleEndian(fragment[48..], frame.Action.Transaction);
                    fragment[56] = (byte)frame.Action.Kind;
                    int offset = i * IntakeFragmentBody;
                    full.Slice(offset, Math.Min(IntakeFragmentBody, full.Length - offset)).CopyTo(fragment[IntakeFragmentHeader..]);
                }
                _intakeEncodedFrame = frame;
                _intakeSendFragment = 0;
            }
            // Keep the unsent fragment when the existing per-peer window is full.
            for (int sent = 0; sent < 2; sent++)
            {
                var result = Datagrams.Send(_bridge.Peer,
                    _intakeSendBytes.AsSpan(_intakeSendFragment * IntakeFragmentLength, IntakeFragmentLength));
                if (result == DatagramSendResult.WindowFull) return;
                if (result != DatagramSendResult.Queued)
                { Fail("Consumption transport unavailable; partial custody remains held."); return; }
                _intakeSendFragment++;
                if (_intakeSendFragment == 2) { _intakeSendFragment = 0; return; }
            }
        }

        private static bool IsIntakeFragment(ReadOnlySpan<byte> bytes)
            => bytes.Length == IntakeFragmentLength && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == IntakeFragmentMagic;

        // Called only after exact peer/thread/lifetime admission. One assembly and one immutable
        // completed frame are retained; repeats reuse decoded state and never native effects.
        private bool TryAssembleIntake(ReadOnlySpan<byte> bytes, out PlayerConsumptionFrame frame)
        {
            frame = default;
            if (!IsIntakeFragment(bytes) || bytes[4] != 1 || bytes[5] > 1 || bytes[6] != 0 || bytes[7] != 0) return false;
            for (int i = 57; i < IntakeFragmentHeader; i++) if (bytes[i] != 0) return false;
            var kind = (PlayerInventoryMessage)bytes[56];
            ulong request = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
            ulong transaction = BinaryPrimitives.ReadUInt64LittleEndian(bytes[48..]);
            if (request == 0 || transaction == 0 || kind is < PlayerInventoryMessage.ConsumeRequest or > PlayerInventoryMessage.ConsumeUnconfirmed)
                return false;
            int part = bytes[5], offset = part * IntakeFragmentBody;
            int count = Math.Min(IntakeFragmentBody, PlayerConsumptionWire.Length - offset);
            var content = bytes.Slice(IntakeFragmentHeader, count);
            for (int i = IntakeFragmentHeader + count; i < bytes.Length; i++) if (bytes[i] != 0) return false;
            var hash = bytes[8..40];
            if (_intakeDecodedFrame.Request == request && _intakeDecodedFrame.Action.Transaction == transaction
                && _intakeDecodedFrame.Action.Kind == kind)
            {
                if (!hash.SequenceEqual(_intakeLastHash) || !content.SequenceEqual(_intakeLastBytes.AsSpan(offset, count)))
                { Fail("Consumption retry changed an already authenticated immutable frame."); return false; }
                frame = _intakeDecodedFrame;
                return true;
            }
            bool same = _intakeAssemblyRequest == request && _intakeAssemblyTransaction == transaction && _intakeAssemblyKind == kind;
            if (_intakeAssemblyMask != 0 && !same)
            {
                // Later phases supersede an incomplete earlier retry, never the reverse.
                if (request < _intakeAssemblyRequest || (request == _intakeAssemblyRequest && kind <= _intakeAssemblyKind)) return false;
                _intakeAssemblyMask = 0;
            }
            if (_intakeAssemblyMask == 0)
            {
                _intakeAssemblyRequest = request;
                _intakeAssemblyTransaction = transaction;
                _intakeAssemblyKind = kind;
                hash.CopyTo(_intakeAssemblyHash);
            }
            else if (!hash.SequenceEqual(_intakeAssemblyHash))
            { Fail("Consumption fragments changed their immutable whole-frame identity."); return false; }
            if ((_intakeAssemblyMask & (1 << part)) != 0)
            {
                if (!content.SequenceEqual(_intakeAssembly.AsSpan(offset, count)))
                    Fail("Consumption fragment changed before assembly completed.");
                return false;
            }
            content.CopyTo(_intakeAssembly.AsSpan(offset, count));
            _intakeAssemblyMask |= 1 << part;
            if (_intakeAssemblyMask != 3) return false;
            _intakeAssemblyMask = 0;
            Span<byte> computed = stackalloc byte[32];
            SHA256.HashData(_intakeAssembly, computed);
            if (!computed.SequenceEqual(_intakeAssemblyHash) || !PlayerConsumptionWire.TryRead(_intakeAssembly, out frame)
                || frame.Request != request || frame.Action.Transaction != transaction || frame.Action.Kind != kind)
            { frame = default; Fail("Consumption fragment envelope did not match the complete MPIC V7 frame."); return false; }
            _intakeAssembly.CopyTo(_intakeLastBytes, 0);
            computed.CopyTo(_intakeLastHash);
            _intakeDecodedFrame = frame;
            return true;
        }
    }
}
