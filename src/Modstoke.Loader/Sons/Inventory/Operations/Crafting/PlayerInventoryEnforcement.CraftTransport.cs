// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Diagnostics;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private bool _craftEffectsTransportBound, _craftEffectsReplyPending;
        private long _craftEffectsSent;
        private int _craftEffectsSendOffset;
        private CraftEffectTransfer _craftEffectsIncoming;

        internal void BindCraftEffectTransport()
        {
            if (_craftEffectsTransportBound) return;
            if (!OnThread || _bridge == null || !Current()) throw new InvalidOperationException("Craft effect transport has no current inventory peer.");
            Datagrams.Received += ReceiveCraftEffects;
            Datagrams.Received += ReceiveCraftVariant;
            _bridge.Retired += RetireCraftEffectTransport;
            _craftEffectsTransportBound = true;
        }

        internal void RetireCraftEffectTransport()
        {
            if (!_craftEffectsTransportBound) return;
            Datagrams.Received -= ReceiveCraftEffects;
            Datagrams.Received -= ReceiveCraftVariant;
            _bridge.Retired -= RetireCraftEffectTransport;
            _craftEffectsTransportBound = false;
            if (CraftVariantPending) Fail("Craft variant transport retired with unresolved custody.");
            // Native source writer holds and uncertain effects deliberately survive transport retirement.
        }

        // Parent calls on BOTH endpoints from the ordinary inventory pump, outside its busy
        // scope. One bounded fragment per retry interval; no native operation is replayed.
        internal void PumpCraftEffects()
        {
            if (!OnThread || !_craftEffectsTransportBound || IsDisposed || IsFaulted || _busy || !IsArmed
                || _bridge?.IsGameplayActive != true) return;
            try
            {
                if (!Current()) { Fail("Craft effect transport lost its exact inventory lifetime."); return; }
                PumpCraftVariant();
                if (CraftVariantPending || IsFaulted) return;
                long now = Stopwatch.GetTimestamp();
                if (_authority != null)
                {
                    if (!_authority.HasPendingNativeCraftEffects || !_craftServerApplied || _craftServerReply.FirstTransaction == 0
                        || !_authority.TryReadCraftEffects(_craftServerReply.FirstTransaction - 1, out var effects)) return;
                    if (_craftEffectsSent != 0 && now - _craftEffectsSent < RetryTicks) return;
                    int total = effects.Counters.Count;
                    if (total is <= 0 or > InventoryCraftRecipe.MaximumRows)
                        throw new InvalidOperationException("Craft effects exceed their native recipe bound.");
                    if (_craftEffectsSendOffset >= total) _craftEffectsSendOffset = 0;
                    int count = Math.Min(CraftEffectWire.RowsPerFrame, total - _craftEffectsSendOffset);
                    var rows = new CraftCounter[count];
                    for (int i = 0; i < count; i++) rows[i] = effects.Counters[_craftEffectsSendOffset + i];
                    var frame = new CraftEffectFrame(_baseline.Plan.Id, _baseline.Plan.Generation, _bridge.NetworkId,
                        _baseline.Fingerprint, effects.RecipeFingerprint, _craftServerRequest.Request, effects.Transaction,
                        false, effects.RecipeWasNew, total, _craftEffectsSendOffset, rows);
                    if (SendCraftEffects(frame)) _craftEffectsSendOffset = (_craftEffectsSendOffset + count) % total;
                    _craftEffectsSent = now;
                    return;
                }
                TryReadCraftPresentationRequest(out _);
                var incoming = _craftEffectsIncoming;
                if (incoming == null || incoming.ReceivedCount != incoming.Rows.Length || IsFaulted) return;
                if (_craftUiPending)
                {
                    if (!TryReadCraftRequestReceipt(incoming.Frame.Request, out var receipt)
                        || receipt.State != NativeCraftRequestState.Applied) return;
                    incoming.Effects ??= new(incoming.Frame.Transaction, incoming.Frame.RecipeFingerprint,
                        incoming.Rows, incoming.Frame.RecipeWasNew);
                    if (!TryApplyCraftPresentation(incoming.Frame.Request, incoming.Effects, out var detail))
                    { Fail(detail); return; }
                    if (_craftUiPending) return; // Real animation/coroutine work is not a terminal effect receipt.
                    _craftEffectsReplyPending = true;
                }
                // Keep the server reservation until all result keys are back in physical
                // storage; otherwise a later grant/removal could miscount mat-held results.
                if (!_craftEffectsReplyPending || _craftResults.Count != 0 || _craftContinuations.Count != 0
                    || (_craftEffectsSent != 0 && now - _craftEffectsSent < RetryTicks)) return;
                var reply = incoming.Frame with { IsReceipt = true, RecipeWasNew = false, Total = 0, Offset = 0, Rows = null };
                if (SendCraftEffects(reply)) _craftEffectsReplyPending = false;
                _craftEffectsSent = now;
            }
            catch (Exception error) { Fail("Craft effects are unconfirmed: " + error.Message); }
        }

        private bool SendCraftEffects(in CraftEffectFrame frame)
        {
            Span<byte> bytes = stackalloc byte[CraftEffectWire.MaximumLength];
            int length = CraftEffectWire.Write(frame, bytes);
            var result = Datagrams.Send(_bridge.Peer, bytes[..length]);
            if (result is not (DatagramSendResult.Queued or DatagramSendResult.WindowFull))
                Fail("Craft effects transport unavailable: " + result);
            return result == DatagramSendResult.Queued;
        }

        private void ReceiveCraftEffects(Datagram datagram)
        {
            if (HasPendingHeldOnlyCustody) return;
            if (!OnThread || !_craftEffectsTransportBound || IsDisposed || IsFaulted || _busy || !IsArmed
                || _bridge?.IsGameplayActive != true || datagram == null || !ReferenceEquals(datagram.Peer, _bridge.Peer)
                || !CraftEffectWire.TryRead(datagram.Payload.Span, out var frame)
                || frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.BaselineFingerprint != _baseline.Fingerprint) return;
            try
            {
                if (!Current()) { Fail("Craft effects sender no longer owns this inventory lifetime."); return; }
                if (_authority != null)
                {
                    if (frame.IsReceipt && _craftServerApplied && frame.Request == _craftServerRequest.Request
                        && _craftServerReply.FirstTransaction != 0 && frame.Transaction == _craftServerReply.FirstTransaction - 1)
                        _authority.TryAcknowledgeCraftEffects(frame.Transaction, frame.RecipeFingerprint);
                    return;
                }
                if (frame.IsReceipt || _writerDepth != 0 || frame.Request != _craftUiRequest || _craftSourceRecipe == null
                    || frame.RecipeFingerprint != _craftSourceRecipe.Fingerprint || frame.Transaction <= _craftSourceFloor) return;
                if (_craftEffectsIncoming == null)
                    _craftEffectsIncoming = new(frame);
                var incoming = _craftEffectsIncoming;
                if (frame.Transaction != incoming.Frame.Transaction || frame.Request != incoming.Frame.Request
                    || frame.Total != incoming.Rows.Length || frame.RecipeWasNew != incoming.Frame.RecipeWasNew)
                    throw new InvalidOperationException("Craft effects changed their immutable receipt identity.");
                for (int i = 0; i < frame.Rows.Length; i++)
                {
                    int index = frame.Offset + i;
                    if (incoming.Received[index])
                    {
                        if (incoming.Rows[index] != frame.Rows[i]) throw new InvalidOperationException("Craft effect row changed on retransmission.");
                    }
                    else
                    {
                        incoming.Rows[index] = frame.Rows[i];
                        incoming.Received[index] = true;
                        incoming.ReceivedCount++;
                    }
                }
                if (!_craftUiPending && incoming.ReceivedCount == incoming.Rows.Length && _craftSourceEffects != null)
                    _craftEffectsReplyPending = true;
            }
            catch (Exception error) { Fail("Craft effects receive failed: " + error.Message); }
        }

        private sealed class CraftEffectTransfer(CraftEffectFrame frame)
        {
            internal readonly CraftEffectFrame Frame = frame with { Offset = 0, Rows = null };
            internal readonly CraftCounter[] Rows = new CraftCounter[frame.Total];
            internal readonly bool[] Received = new bool[frame.Total];
            internal int ReceivedCount;
            internal CraftEffectsSnapshot Effects;
        }
    }

    internal readonly record struct CraftEffectFrame(Guid MigrationId, ulong Generation, ulong NetworkId,
        string BaselineFingerprint, string RecipeFingerprint, ulong Request, ulong Transaction,
        bool IsReceipt, bool RecipeWasNew, int Total, int Offset, CraftCounter[] Rows);

    internal static class CraftEffectWire
    {
        private const uint Magic = 0x4543504D; // MPCE, distinct from native inventory/craft custody codecs.
        private const int HeaderLength = 128, RowLength = 12;
        internal const int RowsPerFrame = (Datagrams.MaxPayloadBytes - HeaderLength) / RowLength;
        internal const int MaximumLength = HeaderLength + RowsPerFrame * RowLength;

        internal static int Write(in CraftEffectFrame frame, Span<byte> bytes)
        {
            int count = frame.Rows?.Length ?? 0;
            int length = HeaderLength + count * RowLength;
            bytes[..length].Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
            bytes[4] = 1; bytes[5] = frame.IsReceipt ? (byte)2 : (byte)1; bytes[6] = frame.RecipeWasNew ? (byte)1 : (byte)0;
            frame.MigrationId.TryWriteBytes(bytes[8..24]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], frame.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], frame.NetworkId);
            Convert.FromHexString(frame.BaselineFingerprint).CopyTo(bytes[40..72]);
            Convert.FromHexString(frame.RecipeFingerprint).CopyTo(bytes[72..104]);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[104..], frame.Request);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[112..], frame.Transaction);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[120..], checked((ushort)frame.Total));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[122..], checked((ushort)frame.Offset));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes[124..], checked((ushort)count));
            for (int i = 0; i < count; i++)
            {
                var row = bytes.Slice(HeaderLength + i * RowLength, RowLength);
                BinaryPrimitives.WriteInt32LittleEndian(row, frame.Rows[i].ItemId);
                BinaryPrimitives.WriteInt32LittleEndian(row[4..], frame.Rows[i].Before);
                BinaryPrimitives.WriteInt32LittleEndian(row[8..], frame.Rows[i].After);
            }
            return length;
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out CraftEffectFrame frame)
        {
            frame = default;
            if (bytes.Length < HeaderLength || bytes.Length > MaximumLength || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
                || bytes[4] != 1 || bytes[5] is < 1 or > 2 || bytes[6] > 1 || bytes[7] != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes[126..]) != 0) return false;
            int total = BinaryPrimitives.ReadUInt16LittleEndian(bytes[120..]);
            int offset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[122..]);
            int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[124..]);
            bool receipt = bytes[5] == 2;
            if (count > RowsPerFrame || bytes.Length != HeaderLength + count * RowLength
                || (receipt ? total != 0 || offset != 0 || count != 0 || bytes[6] != 0
                    : total is <= 0 or > InventoryCraftRecipe.MaximumRows || count == 0 || offset + count > total)) return false;
            var rows = new CraftCounter[count];
            for (int i = 0; i < count; i++)
            {
                var row = bytes.Slice(HeaderLength + i * RowLength, RowLength);
                rows[i] = new(BinaryPrimitives.ReadInt32LittleEndian(row), BinaryPrimitives.ReadInt32LittleEndian(row[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(row[8..]));
                if (rows[i].ItemId <= 0 || rows[i].Before < 0 || rows[i].After <= rows[i].Before) return false;
            }
            frame = new(new Guid(bytes[8..24]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]), Convert.ToHexString(bytes[40..72]),
                Convert.ToHexString(bytes[72..104]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[104..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[112..]), receipt, bytes[6] != 0, total, offset, rows);
            return frame.MigrationId != Guid.Empty && frame.Generation != 0 && frame.NetworkId != 0 && frame.Request != 0 && frame.Transaction != 0;
        }
    }
}
