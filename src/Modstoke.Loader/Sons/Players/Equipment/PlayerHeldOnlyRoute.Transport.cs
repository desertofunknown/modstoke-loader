// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerHeldOnlyRoute
    {
        private const uint Magic = 0x4843504D; // MPCH, separate from inventory and migration envelopes.
        private const int Header = 120, Body = 136;
        private byte[] _send, _sendHash, _receivedHash;
        private byte _sendPhase, _receivedPhase;
        private int _sendOffset;
        private long _sent;
        private Assembly _assembly;

        private sealed class Assembly(ulong request, byte phase, int length, byte[] hash)
        {
            internal readonly ulong Request = request;
            internal readonly byte Phase = phase;
            internal readonly byte[] Bytes = new byte[length], Hash = hash;
            internal readonly bool[] Chunks = new bool[(length + Body - 1) / Body];
            internal int Count;
        }

        private void SendJson<T>(byte phase, T value) => SetSend(phase, JsonSerializer.SerializeToUtf8Bytes(value));
        private void SetSend(byte phase, byte[] bytes)
        {
            Require(bytes is { Length: > 0 } && bytes.Length <= PlayerHeldOnlyAuthority.MaximumNativeActionBytes,
                "Held compound envelope exceeds its whole-message bound.");
            _send = bytes; _sendHash = SHA256.HashData(bytes); _sendPhase = phase; _sendOffset = 0; _sent = 0;
        }

        private void PumpSend()
        {
            if (_send == null || _sent != 0 && Stopwatch.GetTimestamp() - _sent < Stopwatch.Frequency / 4) return;
            Span<byte> packet = stackalloc byte[Header + Body];
            for (int i = 0; i < Datagrams.MaxOutstandingSendsPerPeer; i++)
            {
                int count = Math.Min(Body, _send.Length - _sendOffset);
                packet.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
                packet[4] = 2; packet[5] = _sendPhase;
                BinaryPrimitives.WriteInt32LittleEndian(packet[8..], _send.Length);
                BinaryPrimitives.WriteInt32LittleEndian(packet[12..], _sendOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(packet[16..], _requestId);
                BinaryPrimitives.WriteUInt64LittleEndian(packet[24..], _bridge.Plan.Generation);
                BinaryPrimitives.WriteUInt64LittleEndian(packet[32..], _bridge.NetworkId);
                _bridge.Plan.Id.TryWriteBytes(packet[40..56]);
                _baselineHash.CopyTo(packet[56..88]);
                _sendHash.CopyTo(packet[88..120]);
                _send.AsSpan(_sendOffset, count).CopyTo(packet[Header..]);
                var result = Datagrams.Send(_bridge.Peer, packet[..(Header + count)]);
                if (result == DatagramSendResult.WindowFull) return;
                Require(result == DatagramSendResult.Queued, "Held compound transport lost its enrolled peer.");
                _sendOffset += count;
                if (_sendOffset == _send.Length)
                { _sendOffset = 0; _sent = Stopwatch.GetTimestamp(); return; }
            }
        }

        private void Receive(Datagram message)
        {
            if (_busy || !Current() || message == null || !ReferenceEquals(message.Peer, _bridge.Peer)) return;
            var bytes = message.Payload.Span;
            if (bytes.Length <= Header || bytes.Length > Header + Body || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != 2 || bytes[6] != 0 || bytes[7] != 0
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]) != _bridge.Plan.Generation
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]) != _bridge.NetworkId
                || new Guid(bytes[40..56]) != _bridge.Plan.Id
                || !bytes[56..88].SequenceEqual(_baselineHash)) return;
            byte phase = bytes[5];
            if (_source != null ? phase is not (2 or 4 or 6 or 8) : phase is not (1 or 3 or 5 or 7)) return;
            ulong request = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
            int maximum = phase is 2 or 3 ? PlayerHeldOnlyAuthority.MaximumNativeActionBytes : 16384;
            if (request == 0 || length <= 0 || length > maximum || offset < 0 || offset >= length
                || offset % Body != 0 || bytes.Length - Header != Math.Min(Body, length - offset)) return;
            _busy = true;
            try
            {
                if (_source == null && phase == 1 && request > _lastRequest)
                {
                    // A new ordinal after our complete commit also proves that the source
                    // consumed its final ACK; the separate final ACK packet may be lost.
                    if (HasPendingCustody && !_completed) return;
                    if (_completed && _command != null
                        && PlayerHeldOnlyAuthority.IsForcedDrop(_command.Kind)
                        && MatchesForcedTransition(_command.Kind, _request.ForcedTransition)) _forcedSettled = true;
                    ResetExchange(request);
                }
                if (request != _requestId || phase < _receivedPhase) return;
                if (phase == _receivedPhase)
                {
                    Require(bytes[88..120].SequenceEqual(_receivedHash), "Conflicting duplicate compound receipt.");
                    return;
                }
                if (_source != null
                    ? phase != (_command == null ? 2 : _worldCommit == null ? 4 : 6) && !(phase == 8 && _command == null)
                    : phase != (_request == null ? 1 : _native == null ? 3 : !_sourceComplete ? 5 : 7)) return;
                if (_assembly == null) _assembly = new(request, phase, length, bytes[88..120].ToArray());
                var assembly = _assembly;
                Require(assembly.Request == request && assembly.Phase == phase && assembly.Bytes.Length == length
                    && bytes[88..120].SequenceEqual(assembly.Hash), "Compound fragment identity changed mid-message.");
                int chunk = offset / Body;
                if (assembly.Chunks[chunk])
                {
                    Require(bytes[Header..].SequenceEqual(assembly.Bytes.AsSpan(offset, bytes.Length - Header)),
                        "Conflicting duplicate compound fragment.");
                    return;
                }
                bytes[Header..].CopyTo(assembly.Bytes.AsSpan(offset));
                assembly.Chunks[chunk] = true; assembly.Count++;
                if (assembly.Count != assembly.Chunks.Length) return;
                Require(SHA256.HashData(assembly.Bytes).AsSpan().SequenceEqual(assembly.Hash), "Compound message digest mismatch.");
                // Mark before dispatch. Any partial native effect is retained, never redriven.
                _receivedPhase = phase; _receivedHash = assembly.Hash; _assembly = null;
                Dispatch(phase, assembly.Bytes);
            }
            catch (Exception error) { Fault(error.Message); }
            finally { _busy = false; }
        }
    }
}
