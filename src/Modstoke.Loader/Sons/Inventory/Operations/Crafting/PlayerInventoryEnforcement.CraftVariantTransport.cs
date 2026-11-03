// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        // MPCV is the retained-custody extension. MPCF's remove-all/grant-all receipts
        // remain unchanged for fixed new-item recipes.
        private const uint CraftVariantMagic = 0x5643504D;
        private const int VariantHeader = 120, VariantBody = 136, VariantMaximumBytes = 131072;
        // Source messages carry at most a recipe and its selection rows, about 60 JSON bytes per row.
        private const int VariantRequestMaximumBytes = PlayerCraftWire.MaximumSelections * 64 + 1024;
        private const int MaximumAbandonedVariants = 4;
        private int _abandonedVariants;
        private CraftVariantExchange _craftVariant;
        private VariantAssembly _variantAssembly;
        private byte[] _variantSend;
        private byte[] _variantSendHash;
        private byte _variantSendPhase;
        private int _variantSendOffset;
        private ulong _variantRequest, _lastVariantRequest;
        private long _variantSent;
        private bool _variantSending;
        private bool CraftVariantPending => _craftVariant != null && !_craftVariant.Settled;

        private sealed record VariantMessage(int RecipeIndex = 0, string RecipeFingerprint = null,
            CraftIngredient[] Selections = null, CraftVariantPlan Plan = null, string Digest = null);

        private sealed class CraftVariantExchange(ulong request)
        {
            internal readonly ulong Request = request;
            internal CraftVariantPlan Plan;
            internal string Digest;
            internal bool Prepared, Committed, Completed, Settled;
            internal long Started = Stopwatch.GetTimestamp();
        }

        private sealed class VariantAssembly(ulong request, byte phase, int length, byte[] digest)
        {
            internal readonly ulong Request = request;
            internal readonly byte Phase = phase;
            internal readonly byte[] Bytes = new byte[length], Digest = digest;
            internal readonly bool[] Received = new bool[(length + VariantBody - 1) / VariantBody];
            internal int Count;
            internal bool Dispatched;
        }

        private void BeginCraftVariant(InventoryCraftRecipe recipe, CraftIngredient[] rows)
        {
            CraftVariant.Require(_authority == null && !CraftVariantPending && !_craftUiPending && !HasPendingCustody
                && !_craftEffectsReplyPending && _nextCraftRequest < ulong.MaxValue
                && _craftMat.Count > 0 && rows.Length is > 0 and <= PlayerCraftWire.MaximumSelections
                && !InventoryFrozen && CraftSourceCurrent() && Unchanged() && CraftMatCurrent(),
                "Variant craft requires exact idle source mat custody.");
            _craftSourceRecipe = recipe;
            _craftSourceRows = rows;
            _craftSourceBefore = new int[rows.Length];
            for (int i = 0; i < rows.Length; i++)
            {
                var key = rows[i].InstanceKey;
                var source = _sourceItems.Find(item => item.Entry.Key == key);
                CraftVariant.Require(source != null && Stowed(source.Entry) && source.Quantity >= rows[i].Quantity
                    && MatCount(key) == rows[i].Quantity, "Variant mat selection differs from source custody.");
                _craftSourceBefore[i] = source.Quantity;
            }
            _craftUiRequest = _variantRequest = ++_nextCraftRequest;
            _craftSourceFloor = LastCompletedTransaction;
            _craftVariant = new(_variantRequest);
            _variantAssembly = null;
            _variantNativeResults = null;
            _craftPresentationAttempted = _craftAnimationStarted = _craftResultsPlaced = _craftIngredientsCleared = _craftRevealed = false;
            _craftEffectsIncoming = null;
            _craftUiPending = true;
            SetVariantSend(1, new(recipe.Index, recipe.Fingerprint, rows));
        }

        private void SetVariantSend(byte phase, VariantMessage message)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            CraftVariant.Require(bytes.Length is > 0 and <= VariantMaximumBytes, "Variant message exceeds its bounded custody envelope.");
            _variantSend = bytes;
            _variantSendHash = SHA256.HashData(bytes);
            _variantSendPhase = phase;
            _variantSending = true;
            _variantSendOffset = 0;
            _variantSent = 0;
        }

        private void PumpCraftVariant()
        {
            var exchange = _craftVariant;
            if (exchange == null) return;
            long now = Stopwatch.GetTimestamp();
            if (_authority == null && exchange.Committed && !exchange.Completed && !_craftUiPending
                && _craftMat.Count == 0 && _craftResults.Count == 0 && _craftContinuations.Count == 0)
            {
                CraftVariant.Require(SourceVariantCollected(), "Variant collection changed committed custody.");
                exchange.Completed = true;
                LastCompletedTransaction = exchange.Plan.LastTransaction;
                SetVariantSend(5, new(Digest: exchange.Digest));
            }
            // Animation and deliberate result collection may take arbitrarily long. A timeout
            // never releases their reservation or fabricates a terminal native callback.
            if (!exchange.Committed && !exchange.Settled && now - exchange.Started >= TimeoutTicks)
                throw new InvalidOperationException("Variant preparation timed out; custody remains reserved.");
            if (!_variantSending || _variantSend == null) return;
            if (_variantSent != 0 && now - _variantSent < RetryTicks) return;
            Span<byte> frame = stackalloc byte[VariantHeader + VariantBody];
            for (int packet = 0; packet < Datagrams.MaxOutstandingSendsPerPeer; packet++)
            {
                int count = Math.Min(VariantBody, _variantSend.Length - _variantSendOffset);
                frame.Clear();
                BinaryPrimitives.WriteUInt32LittleEndian(frame, CraftVariantMagic);
                frame[4] = 1; frame[5] = _variantSendPhase;
                BinaryPrimitives.WriteInt32LittleEndian(frame[8..], _variantSend.Length);
                BinaryPrimitives.WriteInt32LittleEndian(frame[12..], _variantSendOffset);
                BinaryPrimitives.WriteUInt64LittleEndian(frame[16..], exchange.Request);
                BinaryPrimitives.WriteUInt64LittleEndian(frame[24..], _baseline.Plan.Generation);
                BinaryPrimitives.WriteUInt64LittleEndian(frame[32..], _bridge.NetworkId);
                _baseline.Plan.Id.TryWriteBytes(frame[40..56]);
                Convert.FromHexString(_baseline.Fingerprint).CopyTo(frame[56..88]);
                _variantSendHash.CopyTo(frame[88..120]);
                _variantSend.AsSpan(_variantSendOffset, count).CopyTo(frame[VariantHeader..]);
                var result = Datagrams.Send(_bridge.Peer, frame[..(VariantHeader + count)]);
                if (result == DatagramSendResult.WindowFull) return;
                CraftVariant.Require(result == DatagramSendResult.Queued, "Variant transport is unavailable.");
                _variantSendOffset += count;
                if (_variantSendOffset == _variantSend.Length)
                { _variantSendOffset = 0; _variantSent = now; if (exchange.Settled) _variantSending = false; return; }
            }
        }

        private void ReceiveCraftVariant(Datagram datagram)
        {
            if (HasPendingHeldOnlyCustody) return;
            if (datagram == null || !OnThread || !_craftEffectsTransportBound || IsDisposed || IsFaulted || _busy
                || !IsArmed || _bridge?.IsGameplayActive != true || !ReferenceEquals(datagram.Peer, _bridge.Peer)
                || (_authority == null && _writerDepth != 0)) return;
            var bytes = datagram.Payload.Span;
            if (bytes.Length <= VariantHeader || bytes.Length > VariantHeader + VariantBody
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != CraftVariantMagic || bytes[4] != 1
                || bytes[6] != 0 || bytes[7] != 0) return;
            byte phase = bytes[5];
            if (_authority == null ? phase is not (2 or 4 or 6 or 7) : phase is not (1 or 3 or 5)) return;
            ulong request = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
            if (request == 0 || BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]) != _baseline.Plan.Generation
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes[32..]) != _bridge.NetworkId
                || new Guid(bytes[40..56]) != _baseline.Plan.Id
                || Convert.ToHexString(bytes[56..88]) != _baseline.Fingerprint) return;
            if (_authority == null ? request != _variantRequest : request < _lastVariantRequest) return;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
            int offset = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
            if (length <= 0 || length > (_authority == null ? VariantMaximumBytes : VariantRequestMaximumBytes)
                || offset < 0 || offset >= length || offset % VariantBody != 0
                || bytes.Length - VariantHeader != Math.Min(VariantBody, length - offset)) return;
            try
            {
                CraftVariant.Require(Current(), "Variant peer lifetime changed.");
                if (_authority == null)
                {
                    if (_craftVariant == null || (phase == 4 && !_craftVariant.Prepared)
                        || (phase == 6 && !_craftVariant.Completed) || (phase == 7 && _craftVariant.Prepared)) return;
                }
                else if (phase != 1 && (_craftVariant?.Request != request || !_craftVariant.Prepared
                    || (phase == 5 && !_craftVariant.Committed))) return;
                if (_authority != null && request > _lastVariantRequest)
                {
                    if (phase != 1 || request <= _lastCraftRequest || CraftVariantPending || HasPendingCustody
                        || _authority.HasNativeCraftReservation) return;
                    // An honest source never starts a new request before the last one completed.
                    if (_variantAssembly is { Dispatched: false } && ++_abandonedVariants > MaximumAbandonedVariants)
                    {
                        Fail("Variant source abandoned too many incomplete requests.");
                        return;
                    }
                    _lastVariantRequest = request;
                    _lastCraftRequest = request;
                    _variantAssembly = null;
                }
                var assembly = _variantAssembly;
                if (assembly != null && assembly.Request == request && phase < assembly.Phase) return;
                if (assembly == null || assembly.Request != request || phase > assembly.Phase)
                    _variantAssembly = assembly = new(request, phase, length, bytes[88..120].ToArray());
                CraftVariant.Require(assembly.Bytes.Length == length && bytes[88..120].SequenceEqual(assembly.Digest),
                    "Variant retransmission changed its immutable phase body.");
                int part = offset / VariantBody;
                var content = bytes[VariantHeader..];
                if (assembly.Received[part])
                    CraftVariant.Require(content.SequenceEqual(assembly.Bytes.AsSpan(offset, content.Length)),
                        "Variant retransmission changed an accepted fragment.");
                else
                {
                    content.CopyTo(assembly.Bytes.AsSpan(offset));
                    assembly.Received[part] = true;
                    assembly.Count++;
                    if (_craftVariant != null) _craftVariant.Started = Stopwatch.GetTimestamp();
                }
                if (assembly.Count != assembly.Received.Length) return;
                if (assembly.Dispatched)
                {
                    if (_authority != null && _craftVariant?.Settled == true && _craftVariant.Request == request)
                        _variantSending = true;
                    return;
                }
                CraftVariant.Require(SHA256.HashData(assembly.Bytes).AsSpan().SequenceEqual(assembly.Digest),
                    "Variant complete payload differs from its digest.");
                var message = JsonSerializer.Deserialize<VariantMessage>(assembly.Bytes);
                CraftVariant.Require(message != null, "Variant message is absent.");
                assembly.Dispatched = true;
                if (_authority != null) ReceiveServerVariant(request, phase, message);
                else ReceiveSourceVariant(phase, message);
            }
            catch (Exception error) { Fail("Variant custody is unconfirmed: " + error.Message); }
        }

        private void ReceiveServerVariant(ulong request, byte phase, VariantMessage message)
        {
            if (phase == 1)
            {
                if (_craftVariant?.Request == request) return;
                if (CraftVariantPending || message.Selections == null || message.Selections.Length is <= 0 or > PlayerCraftWire.MaximumSelections) return;
                CraftVariantPlan plan;
                try { plan = _authority.PrepareCraftVariant(message.RecipeIndex, message.RecipeFingerprint, message.Selections); }
                catch
                {
                    if (_authority.HasNativeCraftReservation || _authority.IsFaulted) throw;
                    _craftVariant = new(request) { Settled = true };
                    SetVariantSend(7, new());
                    return;
                }
                _craftVariant = new(request) { Plan = plan, Prepared = true,
                    Digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(plan))) };
                SetVariantSend(2, new(Plan: plan, Digest: _craftVariant.Digest));
                return;
            }
            var exchange = _craftVariant;
            if (exchange == null || request != exchange.Request || message.Digest != exchange.Digest || !exchange.Prepared) return;
            if (phase == 3 && !exchange.Committed)
            {
                _authority.CommitCraftVariant(exchange.Plan);
                exchange.Committed = true;
                SetVariantSend(4, new(Digest: exchange.Digest));
            }
            else if (phase == 5 && exchange.Committed && !exchange.Settled)
            {
                _authority.CompleteCraftVariant(exchange.Plan);
                LastCompletedTransaction = exchange.Plan.LastTransaction;
                exchange.Completed = exchange.Settled = true;
                SetVariantSend(6, new(Digest: exchange.Digest));
            }
        }

        private void ReceiveSourceVariant(byte phase, VariantMessage message)
        {
            var exchange = _craftVariant;
            if (exchange == null) return;
            if (phase == 7 && !exchange.Prepared)
            {
                CraftVariant.Require(Unchanged() && CraftMatCurrent(), "Rejected variant custody changed.");
                exchange.Settled = true;
                _craftUiPending = false;
                _variantSend = null;
                _variantSending = false;
                _craftSourceRecipe = null;
                Detail = "Server declined the variant before reserving custody.";
                return;
            }
            if (phase == 2)
            {
                if (exchange.Prepared) return;
                CraftVariant.Require(message.Plan != null && message.Digest == Convert.ToHexString(
                    SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(message.Plan))), "Variant plan digest differs.");
                PrepareSourceVariant(message.Plan);
                exchange.Plan = message.Plan;
                exchange.Digest = message.Digest;
                exchange.Prepared = true;
                SetVariantSend(3, new(Digest: exchange.Digest));
                return;
            }
            if (!exchange.Prepared || message.Digest != exchange.Digest) return;
            if (phase == 4 && !exchange.Committed)
            {
                exchange.Committed = true; // Mark before native reentry. Never replay on failure.
                _variantSend = null;
                _variantSending = false;
                StartSourceVariant();
            }
            else if (phase == 6 && exchange.Completed)
            {
                exchange.Settled = true;
                _variantSend = null;
                _variantSending = false;
                _craftEffectsReplyPending = false;
                Detail = "Variant server custody, retained native presentation and result collection settled.";
            }
        }
    }
}
