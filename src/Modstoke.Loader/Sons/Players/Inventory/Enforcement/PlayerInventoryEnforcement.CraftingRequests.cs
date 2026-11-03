// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Modstoke.Sons
{
    public enum NativeCraftRequestState { Pending, Reserved, Applied, Rejected, Unconfirmed }

    /// <summary>Custody settlement only. Native animation, statistics and unlock effects have separate owners.</summary>
    public readonly record struct NativeCraftRequestReceipt(ulong Request,
        NativeCraftRequestState State, ulong Transaction, ulong FirstNativeTransaction,
        ulong LastNativeTransaction, string Detail);

    public sealed partial class PlayerInventoryEnforcement
    {
        private PlayerCraftFrame _craftSourceRequest, _craftSourceReply, _craftServerRequest, _craftServerReply;
        private CraftIngredient[] _craftSourceRows, _craftServerRows;
        private int[] _craftSourceBefore;
        private bool[] _craftServerReceived;
        private bool _craftSourcePending, _craftServerPending, _craftServerReserved, _craftServerApplied, _craftServerReady;
        private int _craftSendOffset, _craftReceivedRows;
        private ulong _nextCraftRequest, _lastCraftRequest, _craftProgress;
        private long _craftStarted, _craftSent;
        private NativeCraftRequestReceipt? _craftRequestReceipt;

        internal bool HasPendingNativeCraftRequest => _craftSourcePending || _craftServerPending;
        internal bool NativeCraftRequestMayMutate => !_craftServerPending || _craftServerReady;

        /// <summary>
        /// Submit the exact reserved mat selection before authoritative debits/effect writes.
        /// Every selected key retains logical account custody. Keep native writers held
        /// until TryReadNativeCraftRequestReceipt reports a terminal result.
        /// </summary>
        public bool TryRequestCraft(int recipeIndex, IReadOnlyList<CraftIngredient> ingredients,
            out ulong request, out string detail)
        {
            request = 0;
            detail = "Native craft intent needs an idle active source and an exact retained mat selection.";
            if (!OnThread || _writerDepth != 0 || _busy || IsDisposed || IsFaulted || !IsArmed
                || _authority != null || SourceActionInputHeld || _bridge?.IsGameplayActive != true || HasPendingCustody || _craftUiPending || _craftEffectsReplyPending
                || _craftContinuations.Count != 0
                || _nextCraftRequest == ulong.MaxValue || recipeIndex is < 0 or >= 4096
                || ingredients == null || ingredients.Count is <= 0 or > PlayerCraftWire.MaximumSelections) return false;
            try
            {
                if (!CraftSourceCurrent() || !_craftMatBindings || !CraftMatCurrent() || _craftResults.Count != 0) return false;
                var recipe = InventoryCraftRecipe.Capture(_craftSource._recipeDatabase, recipeIndex);
                if (PlayerLifecycle.IsInventoryIngressFenced(_inventory, out _)
                    || !Current() || !Unchanged()) return false;
                var rows = new CraftIngredient[ingredients.Count];
                var before = new int[rows.Length];
                var keys = new HashSet<ulong>();
                for (int i = 0; i < rows.Length; i++)
                {
                    rows[i] = ingredients[i];
                    if (!keys.Add(rows[i].InstanceKey) || rows[i].InstanceKey == 0 || rows[i].Quantity <= 0) return false;
                    SourceItem selected = null;
                    foreach (var item in _sourceItems)
                        if (item.Entry.Key == rows[i].InstanceKey) { selected = item; break; }
                    if (selected == null || !Stowed(selected.Entry) || selected.Quantity < rows[i].Quantity
                        || MatCount(rows[i].InstanceKey) != rows[i].Quantity
                        || (selected.Instance != null && (selected.Instance.WasCollected
                            || selected.Quantity != 1 || rows[i].Quantity != 1))) return false;
                    before[i] = selected.Quantity;
                }
                int selectedCount = 0;
                foreach (var row in rows) selectedCount = checked(selectedCount + row.Quantity);
                if (!recipe.Current() || selectedCount != _craftMat.Count || !CanPresentCraft(recipe)
                    || !_craftSource.HasSatisfiedRecipeWithCurrentIngredients(out var selectedRecipe)
                    || selectedRecipe?.Pointer != recipe.Recipe.Pointer) return false;
                request = ++_nextCraftRequest;
                _craftSourceRows = rows;
                _craftSourceBefore = before;
                _craftSourceRequest = new(_baseline.Plan.Id, _baseline.Plan.Generation, _bridge.NetworkId,
                    _baseline.Fingerprint, request, recipeIndex, rows.Length, PlayerCraftWire.HashSelections(rows), PlayerCraftMessage.Request,
                    RecipeFingerprint: recipe.Fingerprint);
                _craftSourceReply = default;
                _craftSourcePending = true;
                _craftSendOffset = 0;
                _craftStarted = Stopwatch.GetTimestamp();
                _craftSent = 0;
                _craftProgress = _lastReceived;
                _craftSourceRecipe = recipe;
                _craftUiRequest = request;
                _craftSourceFloor = LastCompletedTransaction;
                _craftPresentationAttempted = _craftAnimationStarted = _craftResultsPlaced = _craftIngredientsCleared = _craftRevealed = false;
                _craftDebitIndex = 0;
                _craftEffectsIncoming = null;
                _craftEffectsReplyPending = false;
                _craftEffectsSent = 0;
                _craftUiPending = true;
                detail = Detail = "Exact mat keys/instances reserved; server recipe approval and native custody receipts are pending.";
                _craftRequestReceipt = new(request, NativeCraftRequestState.Pending, 0, 0, 0, detail);
                return true;
            }
            catch (Exception error) { detail = "Native craft intent refused: " + error.Message; return false; }
        }

        public bool TryReadCraftRequestReceipt(ulong request, out NativeCraftRequestReceipt receipt)
        {
            receipt = default;
            if (!OnThread || _craftRequestReceipt is not { } current || current.Request != request) return false;
            if (_craftSourcePending && (IsDisposed || IsFaulted))
                current = current with { State = NativeCraftRequestState.Unconfirmed, Detail = Detail };
            receipt = current;
            return true;
        }

        private void ProcessCraftRequest()
        {
            if (_authority == null || !_craftServerPending || _craftServerApplied) return;
            if (!_craftServerReserved)
            {
                if (_craftReceivedRows != _craftServerRows.Length || !CanQueue) return;
                if (PlayerCraftWire.HashSelections(_craftServerRows) != _craftServerRequest.SelectionFingerprint)
                { RejectCraftRequest("Craft selection fragments disagree with their complete fingerprint."); return; }
                bool reserved = _authority.TryAuthorizeCraftRequest(_craftServerRequest, _craftServerRows,
                    out var receipt, out var detail);
                if (!reserved && receipt.Transaction == 0 && !_authority.HasNativeCraftReservation)
                { RejectCraftRequest(detail); return; }
                if (!reserved || receipt.State == CraftState.Unconfirmed)
                { Fail("Native craft reservation is uncertain: " + detail); return; }
                _craftServerReserved = true;
                _craftStarted = Stopwatch.GetTimestamp();
                _craftProgress = LastCompletedTransaction;
                _craftServerReply = _craftServerRequest with { Kind = PlayerCraftMessage.Reserved,
                    FirstTransaction = receipt.FirstNativeTransaction, LastTransaction = receipt.LastNativeTransaction };
                _craftSent = 0;
            }
            if (!_authority.TryReadCraftReceipt(_craftServerReply.FirstTransaction - 1, out var current))
            { Fail("Reserved native craft lost its exact server receipt."); return; }
            if (current.State == CraftState.Unconfirmed)
            {
                _craftServerReply = _craftServerReply with { Kind = PlayerCraftMessage.Unconfirmed };
                SendCraft(_craftServerReply);
                Fail(current.Detail);
            }
            else if (current.State == CraftState.Applied)
            {
                if (LastCompletedTransaction != current.LastNativeTransaction || _hasPending
                    || current.ConfirmedIngredientRemovals != _craftServerRows.Length
                    || current.ConfirmedIngredientRemovals != current.IngredientRemovals
                    || current.ConfirmedResults != current.Results || current.Results <= 0)
                { Fail("Craft terminal receipt does not match every exact native mutation."); return; }
                _craftServerApplied = true;
                _craftServerReply = _craftServerReply with { Kind = PlayerCraftMessage.Applied };
                _craftStarted = Stopwatch.GetTimestamp();
                _craftSent = 0;
                Detail = "All craft mutations committed; exact source settlement is pending.";
            }
        }

        private void RejectCraftRequest(string detail)
        {
            _craftServerReply = _craftServerRequest with { Kind = PlayerCraftMessage.Rejected };
            _craftServerPending = false;
            Detail = detail;
            SendCraft(_craftServerReply);
        }

        private void PumpCraftRequest()
        {
            if (!HasPendingNativeCraftRequest) return;
            long now = Stopwatch.GetTimestamp();
            ulong progress = _authority == null ? _lastReceived : LastCompletedTransaction;
            if (progress != _craftProgress) { _craftProgress = progress; _craftStarted = now; }
            if (now - _craftStarted >= TimeoutTicks)
            {
                if (_authority != null && !_craftServerReserved)
                    RejectCraftRequest("Incomplete craft request expired before any recipe reservation or native write.");
                else Fail("Native craft acknowledgement timed out; custody is uncertain and cannot be repeated or refunded.");
                return;
            }
            if (_craftSent != 0 && now - _craftSent < RetryTicks) return;
            _craftSent = now;
            if (_authority != null)
            {
                if (_craftServerReserved) SendCraft(_craftServerReply);
                return;
            }
            if (_craftSourceReply.Kind == PlayerCraftMessage.Reserved)
            {
                SendCraft(_craftSourceReply with { Kind = PlayerCraftMessage.Ready });
                return;
            }
            // At most one existing datagram window per retry. WindowFull never advances a row.
            for (int packet = 0; packet < Datagrams.MaxOutstandingSendsPerPeer; packet++)
            {
                int count = System.Math.Min(PlayerCraftWire.RowsPerPacket, _craftSourceRows.Length - _craftSendOffset);
                var rows = new CraftIngredient[count];
                Array.Copy(_craftSourceRows, _craftSendOffset, rows, 0, count);
                if (!SendCraft(_craftSourceRequest with { Offset = _craftSendOffset, Rows = rows })) return;
                _craftSendOffset += count;
                if (_craftSendOffset == _craftSourceRows.Length) { _craftSendOffset = 0; break; }
            }
        }

        private bool SendCraft(in PlayerCraftFrame frame)
        {
            if (_bridge?.IsGameplayActive != true) return false;
            Span<byte> bytes = stackalloc byte[PlayerCraftWire.Length];
            PlayerCraftWire.Write(frame, bytes);
            var result = Datagrams.Send(_bridge.Peer, bytes);
            if (result == DatagramSendResult.Queued) return true;
            if (result != DatagramSendResult.WindowFull)
                Fail("Native craft transport unavailable; existing custody remains held.");
            return false;
        }

        private bool ReceiveCraft(Datagram message)
        {
            if (message == null || !PlayerCraftWire.IsFrame(message.Payload.Span)) return false;
            if (!OnThread || IsDisposed || _busy || _bridge?.IsGameplayActive != true || !IsArmed
                || (_authority == null && _writerDepth != 0) || !ReferenceEquals(message.Peer, _bridge.Peer)
                || !PlayerCraftWire.TryRead(message.Payload.Span, out var frame)
                || frame.MigrationId != _baseline.Plan.Id || frame.Generation != _baseline.Plan.Generation
                || frame.NetworkId != _bridge.NetworkId || frame.BaselineFingerprint != _baseline.Fingerprint) return true;
            _busy = true;
            try
            {
                if (!Current()) { Fail("Native craft endpoint lost its exact bridge lifetime."); return true; }
                if (_authority != null) ReceiveServerCraft(frame);
                else ReceiveSourceCraft(frame);
            }
            catch (Exception error) { Fail("Native craft receive failed: " + error.Message); }
            finally { _busy = false; }
            return true;
        }

        private void ReceiveServerCraft(in PlayerCraftFrame frame)
        {
            if (frame.Kind == PlayerCraftMessage.Ready)
            {
                if (_craftServerPending && _craftServerReserved && frame.SameRequest(_craftServerReply)
                    && frame.FirstTransaction == _craftServerReply.FirstTransaction
                    && frame.LastTransaction == _craftServerReply.LastTransaction)
                {
                    if (!_craftServerReady) _craftStarted = Stopwatch.GetTimestamp();
                    _craftServerReady = true;
                }
                return;
            }
            if (frame.Kind == PlayerCraftMessage.Settled)
            {
                if (_craftServerApplied && _craftServerPending && frame.SameRequest(_craftServerReply)
                    && frame.FirstTransaction == _craftServerReply.FirstTransaction
                    && frame.LastTransaction == _craftServerReply.LastTransaction)
                {
                    _craftServerPending = false;
                    Detail = "Craft custody settled by both exact peers.";
                }
                return;
            }
            if (frame.Kind != PlayerCraftMessage.Request) return;
            if (frame.SameRequest(_craftServerReply)) { SendCraft(_craftServerReply); return; }
            if (!_craftServerPending)
            {
                if (IsFaulted || frame.Request <= _lastCraftRequest || _hasPending || _intakePending || HasNativePowerCustody
                    || _receivedStowRequest || _receivedEquipRequest || _receivedIntakeRequest
                    || _authority.HasNativeCraftReservation || _authority.HasNativeConsumptionReservation) return;
                _lastCraftRequest = frame.Request;
                _craftServerRequest = frame with { Offset = 0, Rows = null };
                _craftServerRows = new CraftIngredient[frame.SelectionCount];
                _craftServerReceived = new bool[frame.SelectionCount];
                _craftReceivedRows = 0;
                _craftServerReserved = _craftServerApplied = _craftServerReady = false;
                _craftServerReply = default;
                _craftServerPending = true;
                _craftStarted = Stopwatch.GetTimestamp();
                _craftSent = 0;
                _craftProgress = LastCompletedTransaction;
            }
            if (!frame.SameRequest(_craftServerRequest)) return;
            for (int i = 0; i < frame.Rows.Length; i++)
            {
                int index = frame.Offset + i;
                if (_craftServerReceived[index])
                {
                    if (_craftServerRows[index] != frame.Rows[i])
                    { RejectCraftRequest("Craft request changed an already received ingredient selection."); return; }
                }
                else
                {
                    _craftServerRows[index] = frame.Rows[i];
                    _craftServerReceived[index] = true;
                    _craftReceivedRows++;
                    _craftStarted = Stopwatch.GetTimestamp();
                }
            }
        }

        private void ReceiveSourceCraft(in PlayerCraftFrame frame)
        {
            if (!frame.SameRequest(_craftSourceRequest) || frame.Kind is PlayerCraftMessage.Request or PlayerCraftMessage.Settled or PlayerCraftMessage.Ready) return;
            if (!_craftSourcePending)
            {
                if (_craftSourceReply.Kind == PlayerCraftMessage.Settled && frame.Kind == PlayerCraftMessage.Applied
                    && frame.FirstTransaction == _craftSourceReply.FirstTransaction
                    && frame.LastTransaction == _craftSourceReply.LastTransaction) SendCraft(_craftSourceReply);
                return;
            }
            if (frame.Kind == PlayerCraftMessage.Rejected)
            {
                if (_craftSourceReply.Kind != 0 || !Unchanged() || !CraftInputsMatch(false))
                { Fail("Rejected craft no longer has wholly unmodified source custody."); return; }
                _craftSourcePending = false;
                Detail = "Server declined this craft before reserving or changing inventory.";
                _craftRequestReceipt = new(frame.Request, NativeCraftRequestState.Rejected, 0, 0, 0, Detail);
                return;
            }
            if (_craftSourceReply.Kind != 0 && (frame.FirstTransaction != _craftSourceReply.FirstTransaction
                || frame.LastTransaction != _craftSourceReply.LastTransaction))
            { Fail("Craft response changed its reserved native transaction range."); return; }
            if (frame.LastTransaction - frame.FirstTransaction < (ulong)_craftSourceRows.Length)
            { Fail("Craft response has no complete ingredient and result transaction range."); return; }
            if (frame.Kind == PlayerCraftMessage.Unconfirmed)
            { Fail("Server reports uncertain native craft custody; no local retry or refund is authorized."); return; }
            if (frame.Kind == PlayerCraftMessage.Reserved)
            {
                if (_craftSourceReply.Kind == 0)
                {
                    if (frame.FirstTransaction <= _craftSourceFloor || _craftSourceRecipe == null
                        || frame.LastTransaction - frame.FirstTransaction + 1 != (ulong)(_craftSourceRows.Length + _craftSourceRecipe.Results.Length)
                        || !CraftInputsMatch(false) || !CraftMatCurrent() || !Unchanged())
                    { Fail("Craft reservation differs from the exact untouched mat selection."); return; }
                    _craftStarted = Stopwatch.GetTimestamp();
                }
                _craftSourceReply = frame;
                _craftRequestReceipt = new(frame.Request, NativeCraftRequestState.Reserved,
                    frame.FirstTransaction - 1, frame.FirstTransaction, frame.LastTransaction,
                    "Exact server recipe reserved; native mutations and final settlement are pending.");
                SendCraft(frame with { Kind = PlayerCraftMessage.Ready });
                return;
            }
            if (frame.Kind != PlayerCraftMessage.Applied) return;
            if (_hasIncomingGrant || _grantReplyQueued || SourceEquipPending || SourceStowPending
                || _lastReceived != frame.LastTransaction || LastCompletedTransaction != frame.LastTransaction
                || !Unchanged() || !CraftInputsMatch(true))
            { Fail("Craft completion does not match all retained native ingredient/result receipts."); return; }
            _craftSourcePending = false;
            _craftSourceReply = frame with { Kind = PlayerCraftMessage.Settled };
            Detail = "Native craft ingredient debits and result custody agree with the authenticated server receipt.";
            _craftRequestReceipt = new(frame.Request, NativeCraftRequestState.Applied,
                frame.FirstTransaction - 1, frame.FirstTransaction, frame.LastTransaction, Detail);
            SendCraft(_craftSourceReply);
        }

        private bool CraftInputsMatch(bool applied)
        {
            for (int i = 0; i < _craftSourceRows.Length; i++)
            {
                SourceItem selected = null;
                foreach (var item in _sourceItems)
                    if (item.Entry.Key == _craftSourceRows[i].InstanceKey) { selected = item; break; }
                if (selected == null || !Stowed(selected.Entry)
                    || selected.Quantity != _craftSourceBefore[i] - (applied ? _craftSourceRows[i].Quantity : 0)) return false;
            }
            return true;
        }
    }
}
