// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bolt;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed record CartSaveEnvelope(int Version, Guid MigrationId, ulong OriginalNetworkId,
            int PrefabId, CartSaveTransfer Transfer);

        private const int CartSaveReceiptLimit = 16 * 1024;
        private static readonly JsonSerializerOptions CartSaveJson = new()
        {
            IncludeFields = true, MaxDepth = 8, PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };

        /// <summary>
        /// Captures exact peer-local save custody before cart retirement. The caller supplies
        /// the same migration ID on every peer and owns writer holds and authenticated delivery.
        /// Does not mutate a save file, suspend writers, or certify aggregate migration readiness.
        /// </summary>
        public static bool TryCaptureCartSaveParticipant(BoltEntity original, Guid migrationId,
            out CartSaveParticipant participant, out string detail)
        {
            participant = null;
            detail = "Cart save capture requires the selected game thread without reentry.";
            if (!Enter()) return false;
            try
            {
                RequireContext();
                Require(migrationId != Guid.Empty, "A nonempty migration identity is required.");
                var binding = new Binding(original);
                Require(binding.Cart != null && Live(binding.Saver)
                    && binding.Cart._saver?.Pointer == binding.Saver.Pointer, "The exact physical cart saver is required.");
                RequireExactSaveType<WorldItemSaver>(binding.Saver);
                participant = new(binding, migrationId);
                detail = "Peer-local save references captured; reserve only after cart writers are held.";
                return true;
            }
            catch (Exception error) { detail = Failure("Cart save capture rejected", error); return false; }
            finally { _busy = false; }
        }

        /// <summary>
        /// Independent save domain for source, destination and observers. Only the source
        /// owner exports; every peer accepts that exact authenticated receipt before binding.
        /// Keep all cart/occupant writers held through reserve, bind and aggregate repair.
        /// An Unconfirmed mutation is terminal and must not be retried or cancelled.
        /// </summary>
        public sealed class CartSaveParticipant
        {
            private readonly Binding _source;
            private readonly CartMigrationSaveCustody _custody;
            private readonly bool _sourceOwner;
            private CartSaveEnvelope _accepted;
            private byte[] _receipt;
            private bool _reserved, _bound, _finished, _uncertain;

            internal CartSaveParticipant(Binding source, Guid migrationId)
            {
                _source = source; MigrationId = migrationId; _sourceOwner = source.Entity.isOwner;
                _custody = new(source);
            }

            public Guid MigrationId { get; }
            public ulong OriginalNetworkId => _source.Id;
            public ulong ReplacementNetworkId { get; private set; }

            public BoltMigrationMutationResult TryReserve(out string detail)
                => Mutate(() =>
                {
                    Require(!_reserved && !_bound, "Save reservation is single-use.");
                    Require(_source.Matches() && _source.Entity.isFrozen && !_source.Entity.gameObject.activeInHierarchy,
                        "Suspend the exact source cart before reserving its save slot.");
                }, () => { _custody.Reserve(); _reserved = true; }, "Cart save reservation", out detail);

            /// <summary>Returns an independent payload; transport must authenticate the source owner.</summary>
            public bool TryExportReceipt(out byte[] receipt, out string detail)
            {
                receipt = null;
                detail = "Save receipt export requires the selected game thread without reentry.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireOpen();
                    Require(_sourceOwner && _reserved && !_bound && _source.Matches() && _source.Entity.isOwner,
                        "Only the held original owner can export authoritative save custody.");
                    _custody.RequireHeld();
                    var envelope = new CartSaveEnvelope(1, MigrationId, OriginalNetworkId, _source.Prefab, _custody.Captured);
                    Require(_accepted == null || _accepted == envelope, "Accepted save receipt differs from the source capture.");
                    _receipt ??= JsonSerializer.SerializeToUtf8Bytes(envelope, CartSaveJson);
                    Require(_receipt.Length <= CartSaveReceiptLimit && !_reentered, "Save receipt exceeds its bound or capture reentered.");
                    _accepted = envelope;
                    receipt = (byte[])_receipt.Clone();
                    detail = "Exact source save receipt exported; caller owns authenticated peer delivery.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Save receipt export rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>Must receive only the source receipt authenticated by the migration coordinator.</summary>
            public bool TryAcceptReceipt(ReadOnlySpan<byte> receipt, out string detail)
            {
                detail = "Save receipt acceptance requires the selected game thread without reentry.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireOpen();
                    Require(!_bound && receipt.Length is > 0 and <= CartSaveReceiptLimit, "Save receipt is late, empty or oversized.");
                    var envelope = JsonSerializer.Deserialize<CartSaveEnvelope>(receipt, CartSaveJson);
                    Require(envelope != null && envelope.Version == 1 && envelope.MigrationId == MigrationId
                        && envelope.OriginalNetworkId == OriginalNetworkId && envelope.PrefabId == _source.Prefab,
                        "Save receipt does not belong to this exact migration/cart.");
                    Require(_accepted == null || (_accepted == envelope && receipt.SequenceEqual(_receipt)),
                        "A different receipt cannot replace accepted save custody.");
                    Require(!_sourceOwner || envelope.Transfer == _custody.Captured,
                        "The source owner cannot import a different authoritative save record.");
                    if (_reserved) _custody.RequireHeld();
                    _custody.ValidateImport(envelope.Transfer);
                    Require(!_reentered, "Save receipt acceptance reentered.");
                    _accepted = envelope; _receipt ??= receipt.ToArray();
                    detail = "Exact save receipt accepted without native mutation.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Save receipt rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>
            /// Bind after this peer's original cart/saver destruction, while the replacement is
            /// held. Destination must equal the actual new owner role; proxies never register.
            /// </summary>
            public BoltMigrationMutationResult TryBindReplacement(BoltEntity replacement,
                bool destination, out string detail)
            {
                Binding target = null;
                return Mutate(() =>
                {
                    Require(_reserved && !_bound && _accepted != null, "Reserve and accept the source receipt before binding.");
                    _custody.RequireHeld();
                    target = new Binding(replacement);
                    Require(target.Cart != null && Live(target.Saver) && target.Cart._saver?.Pointer == target.Saver.Pointer,
                        "The exact physical replacement saver is required.");
                }, () =>
                {
                    _custody.Bind(target, _accepted.Transfer, destination);
                    ReplacementNetworkId = target.Id; _bound = true;
                }, "Cart save binding", out detail);
            }

            public bool TryValidateHeld(out string detail)
            {
                detail = "Cart save validation requires the selected game thread without reentry.";
                if (!Enter()) return false;
                try
                {
                    RequireContext(); RequireOpen();
                    Require(_reserved, "Save custody has not been reserved.");
                    _custody.RequireHeld();
                    Require(!_reentered, "Cart save validation reentered.");
                    detail = "Exact peer-local save custody remains held; no aggregate readiness is implied.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Cart save validation rejected", error); return false; }
                finally { _busy = false; }
            }

            /// <summary>After all-peer repair and serializer pose restoration, before writer release.</summary>
            public BoltMigrationMutationResult TryResumeSaving(out string detail)
                => Mutate(() => Require(_bound, "Save custody is not bound."),
                    () => { _custody.ResumeSaving(); _finished = true; }, "Cart saving resumption", out detail);

            /// <summary>Before source retirement only; restores the exact original slot and policy.</summary>
            public BoltMigrationMutationResult TryCancel(out string detail)
                => Mutate(() => Require(!_bound && _source.Matches(), "Save custody cannot cancel after source retirement or binding."),
                    () => { _custody.Cancel(); _finished = true; }, "Cart save cancellation", out detail);

            private void RequireOpen() => Require(!_finished && !_uncertain, "Save custody is terminal; do not retry a native mutation.");

            private BoltMigrationMutationResult Mutate(Action preflight, Action mutation,
                string operation, out string detail)
            {
                detail = operation + " requires the selected game thread without reentry.";
                if (!Enter()) return BoltMigrationMutationResult.Rejected;
                bool attempted = false;
                try
                {
                    RequireContext(); RequireOpen(); preflight();
                    Require(!_reentered, "Save custody preflight reentered.");
                    attempted = true; mutation();
                    Require(!_reentered, "Save custody mutation reentered.");
                    detail = operation + " applied; independent vehicle/occupant lifecycle barriers remain caller-owned.";
                    return BoltMigrationMutationResult.Applied;
                }
                catch (Exception error)
                {
                    if (attempted) { _uncertain = true; _custody.QuarantineSaving(); }
                    detail = Failure(operation + (attempted ? " unconfirmed; keep writers held" : " rejected"), error);
                    return attempted ? BoltMigrationMutationResult.Unconfirmed : BoltMigrationMutationResult.Rejected;
                }
                finally { _busy = false; }
            }
        }
    }
}
