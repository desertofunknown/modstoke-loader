// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Bolt;
using global::Sons.Inventory;
using global::Sons.Save;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private sealed record CartSaveTransfer(CartSaveRecord Record, bool Active, bool IgnorePosition,
            bool IgnoreRotation, System.Numerics.Vector3 Position, System.Numerics.Vector3 Rotation);

        // A peer-local reservation, followed by an independent imported destination record.
        // Never use RegisterItemInternal/UpdateState here: those can deserialize or steal a claim.
        private sealed class CartMigrationSaveCustody
        {
            private readonly Binding _source;
            private readonly WorldItemManager _manager;
            private readonly WorldItemManager.WorldItemSaveData _data;
            private readonly Il2CppSystem.Collections.Generic.List<WorldItemSaver> _savers;
            private readonly Il2CppSystem.Collections.Generic.List<WorldItemState> _records;
            private readonly WorldItemState _original, _reserved;
            private readonly WorldItemState _localState;
            private readonly CartSaveRecord _registered;
            private readonly IntPtr _socket;
            private readonly int _loadCount;
            private readonly bool _server;
            private bool _held, _bound, _finished;
            private Binding _target;
            private CartWriterHold _sourceWriters, _targetWriters;
            private WorldItemState _imported;
            private CartSaveTransfer _transfer;
            internal readonly CartSaveTransfer Captured;

            internal CartMigrationSaveCustody(Binding source)
            {
                _source = source;
                Require(WorldItemManager.TryGetInstance(out _manager) && Live(_manager), "World-item registry is unavailable.");
                _data = _manager._saveData; _savers = _manager._worldItems; _records = _data?.WorldItemStates;
                _socket = BoltCore._udpSocket?.Pointer ?? IntPtr.Zero; _server = BoltNetwork.isServer;
                _loadCount = SaveLifecycle.State.BeforeLoadCount;
                _localState = source.Saver._itemState;
                Captured = new(ReadCartSaveRecord(_localState), source.Saver._saveActive,
                    source.Saver._ignorePosition, source.Saver._ignoreRotation,
                    V(source.Saver.transform.position), V(source.Saver.transform.eulerAngles));
                Validate(Captured);
                RequireRegistry();
                int index = FindOwner(source.Saver);
                Require(index >= 0 || !source.Entity.isOwner, "The source owner has no exact save registration.");
                if (index >= 0)
                {
                    _original = _records[index]; _registered = ReadCartSaveRecord(_original);
                    Require(_registered == Captured.Record, "Source live and registered save records differ.");
                    Require(FindIdentity(_registered) == index, "Source save identity is not unique.");
                    _reserved = CopyCartSaveRecord(_original);
                }
                else Require(FindIdentity(Captured.Record) < 0, "Unregistered replica collides with another saved identity.");
            }

            internal void Reserve()
            {
                Require(!_held && !_bound && !_finished && _source.Matches()
                    && _source.Entity.isFrozen && !_source.Entity.gameObject.activeInHierarchy,
                    "Save reservation requires the single-use held source.");
                RequireRegistry(); RequireSource();
                _sourceWriters = CartWriterHooks.RequireExistingHold(_source);
                _held = true;
                if (_original != null)
                {
                    int index = FindOwner(_source.Saver);
                    Require(index >= 0 && _records[index].Pointer == _original.Pointer, "Source save claim changed.");
                    // OnDestroy may mutate _itemState even with saving disabled. Keep its
                    // object separate from the reserved registry record on every peer.
                    _records[index] = _reserved;
                    _manager.UnregisterItemInternal(_source.Saver, false);
                }
                _source.Saver.SetSaveActive(false);
                RequireHeld();
            }

            internal void RequireHeld()
            {
                Require(_held && !_finished, "Save custody is not held.");
                RequireRegistry();
                if (_bound) { RequireBound(); return; }
                if (_reserved != null)
                {
                    int index = FindIdentity(_registered);
                    Require(index >= 0 && _savers[index] is null && _records[index].Pointer == _reserved.Pointer
                        && ReadCartSaveRecord(_reserved) == _registered, "Reserved save custody changed or was claimed.");
                }
                else Require(FindIdentity(Captured.Record) < 0, "Unregistered source identity was claimed while held.");
                Require(FindOwner(_source.Saver) < 0, "Retired or held source saver regained a registry slot.");
                if (Live(_source.Entity))
                {
                    _sourceWriters.RequireSuspended();
                    Require(_source.Matches() && !_source.Saver._saveActive
                        && _source.Saver._itemState?.Pointer == _localState.Pointer
                        && _source.Saver._ignorePosition == Captured.IgnorePosition
                        && _source.Saver._ignoreRotation == Captured.IgnoreRotation
                        && ReadCartSaveRecord(_localState) == Captured.Record,
                        "Original saver changed while held.");
                }
            }

            internal void ValidateImport(CartSaveTransfer transfer)
            {
                Validate(transfer);
                RequireRegistry();
                Require(transfer.Record.ItemId == Captured.Record.ItemId
                    && transfer.IgnorePosition == Captured.IgnorePosition && transfer.IgnoreRotation == Captured.IgnoreRotation,
                    "Authoritative save item or serializer policy differs from this cart.");
                int index = FindIdentity(transfer.Record);
                Require(index < 0 || (_registered != null && SameIdentity(_registered, transfer.Record)
                    && _records[index].Pointer == (_held ? _reserved.Pointer : _original.Pointer)
                    && (_held ? _savers[index] is null : _savers[index]?.Pointer == _source.Saver.Pointer)),
                    "Destination save identity already belongs to an unrelated record.");
            }

            internal Binding Bind(Binding target, CartSaveTransfer transfer, bool destination)
            {
                RequireHeld(); ValidateImport(transfer);
                Require(!_bound && !Live(_source.Entity) && !Live(_source.Saver)
                    && target.Matches() && target.Entity.isFrozen && !target.Entity.gameObject.activeInHierarchy
                    && target.Entity.isOwner == destination && target.Id != _source.Id && target.Prefab == _source.Prefab
                    && target.Saver._ignorePosition == transfer.IgnorePosition
                    && target.Saver._ignoreRotation == transfer.IgnoreRotation,
                    "Save import requires the exact replacement role after source destruction.");
                _targetWriters = CartWriterHooks.RequireExistingHold(target);
                var temporary = ReadCartSaveRecord(target.Saver._itemState);
                int index = FindOwner(target.Saver);
                Require((index >= 0 || !destination) && !SameIdentity(temporary, transfer.Record)
                    && temporary.ItemId == transfer.Record.ItemId && temporary.DataLength == -1,
                    "Replacement must have its own proven fresh temporary registration.");
                if (index >= 0)
                {
                    var registered = ReadCartSaveRecord(_records[index]);
                    Require(_records[index].Pointer != target.Saver._itemState.Pointer
                        && registered == (temporary with { Position = registered.Position, Rotation = registered.Rotation })
                        && registered.Position == V(target.Saver.transform.position)
                        && registered.Rotation == V(target.Saver.transform.eulerAngles)
                        && FindIdentity(temporary) == index,
                        "Replacement registration is not a unique unsaved native registration.");
                }
                else Require(FindIdentity(temporary) < 0, "Unregistered replacement collides with a saved identity.");
                _imported = MaterializeCartSaveRecord(transfer.Record);
                _target = target;
                target.Saver.SetSaveActive(false);
                if (index >= 0) _manager.UnregisterItemInternal(target.Saver, true);
                Require(FindOwner(target.Saver) < 0 && FindIdentity(temporary) < 0,
                    "Temporary replacement save registration was not released.");
                // Both removals shift the parallel lists. Never reuse either previous index.
                if (_reserved != null)
                {
                    index = FindIdentity(_registered);
                    Require(index >= 0 && _savers[index] is null && _records[index].Pointer == _reserved.Pointer,
                        "Reserved slot changed during replacement unregister.");
                    _savers.RemoveAt(index); _records.RemoveAt(index);
                }
                Require(FindIdentity(transfer.Record) < 0, "Imported save identity was claimed during binding.");
                target.Saver._itemState = _imported;
                if (destination) { _savers.Add(target.Saver); _records.Add(_imported); }
                _target = new Binding(target.Entity);
                OccupantsFor(target.Cart)?.RebindTarget(_target);
                _targetWriters.Rebind(_target);
                _transfer = transfer; _bound = true;
                RequireBound();
                return _target;
            }

            internal void ResumeSaving()
            {
                RequireHeld(); Require(_bound, "Replacement save custody is not bound.");
                if (_target.Entity.isOwner)
                {
                    Require((_transfer.IgnorePosition || V(_target.Saver.transform.position) == _transfer.Position)
                        && (_transfer.IgnoreRotation || V(_target.Saver.transform.eulerAngles) == _transfer.Rotation),
                        "Restore serializer pose before resuming saving.");
                    _targetWriters.SerializeAllowed = true;
                    _target.Saver.SetSaveActive(_transfer.Active);
                }
                Require(_target.Saver._saveActive == (_target.Entity.isOwner && _transfer.Active),
                    "Replacement saving-policy readback failed.");
                _finished = true;
            }

            internal void Cancel()
            {
                RequireRegistry();
                Require(!_finished && !_bound && _source.Matches()
                    && _source.Saver._itemState?.Pointer == _localState.Pointer
                    && _source.Saver._ignorePosition == Captured.IgnorePosition
                    && _source.Saver._ignoreRotation == Captured.IgnoreRotation
                    && ReadCartSaveRecord(_localState) == Captured.Record,
                    "Cannot cancel changed save custody or a retired source.");
                if (_reserved != null)
                {
                    int index = FindIdentity(_registered);
                    Require(index >= 0 && (_records[index].Pointer == _original.Pointer || _records[index].Pointer == _reserved.Pointer)
                        && (_savers[index] is null || _savers[index]?.Pointer == _source.Saver.Pointer)
                        && ReadCartSaveRecord(_records[index]) == _registered
                        && ReadCartSaveRecord(_original) == _registered,
                        "Original save slot changed or was claimed before cancellation.");
                    int owner = FindOwner(_source.Saver);
                    Require(owner < 0 || owner == index, "Original saver acquired a different registry slot.");
                    _records[index] = _original; _savers[index] = _source.Saver;
                }
                else Require(FindOwner(_source.Saver) < 0 && FindIdentity(Captured.Record) < 0,
                    "Originally unregistered replica acquired save custody.");
                _source.Saver.SetSaveActive(Captured.Active);
                RequireSource(); _finished = true;
            }

            private void RequireSource()
                => Require(_source.Matches() && _source.Saver._itemState?.Pointer == _localState.Pointer
                    && _source.Saver._ignorePosition == Captured.IgnorePosition
                    && _source.Saver._ignoreRotation == Captured.IgnoreRotation
                    && ReadCartSaveRecord(_localState) == Captured.Record
                    && _source.Saver._saveActive == Captured.Active, "Original save input changed.");

            private void RequireBound()
            {
                _targetWriters.RequireSuspended();
                Require(_target != null && _target.Matches() && !_target.Saver._saveActive
                    && _target.Saver._ignorePosition == _transfer.IgnorePosition
                    && _target.Saver._ignoreRotation == _transfer.IgnoreRotation
                    && _target.Saver._itemState.Pointer == _imported.Pointer
                    && ReadCartSaveRecord(_imported) == _transfer.Record, "Imported saver changed or resumed early.");
                int index = FindIdentity(_transfer.Record);
                Require(_target.Entity.isOwner ? index >= 0 && _savers[index]?.Pointer == _target.Saver.Pointer
                    && _records[index].Pointer == _imported.Pointer : index < 0 && FindOwner(_target.Saver) < 0,
                    "Only the exact destination may own the imported registry slot.");
            }

            internal void QuarantineSaving()
            {
                // A setter may throw after its native write. Retain all exact handles and do
                // not retry registry mutations or attempt a compensating registration.
                _finished = true;
                _sourceWriters?.QuarantineIfHeld();
                _targetWriters?.QuarantineIfHeld();
                try { if (Live(_source.Saver)) _source.Saver.SetSaveActive(false); } catch (Exception) { }
                try { if (_target != null && Live(_target.Saver)) _target.Saver.SetSaveActive(false); } catch (Exception) { }
            }

            private void RequireRegistry()
            {
                RequireContext();
                var life = SaveLifecycle.State;
                Require(life.HarmonyPatchesInstalled && life.BeforeSaveCount == life.AfterSaveCount
                    && life.BeforeLoadCount == life.AfterLoadCount && life.BeforeLoadCount == _loadCount
                    && SaveGameManager.TryGetInstance(out var save) && Live(save) && !save._loadingInProgress,
                    "Save/load is active or changed during cart custody.");
                Require(_socket != IntPtr.Zero && BoltCore._udpSocket?.Pointer == _socket && BoltNetwork.isServer == _server
                    && Live(_manager) && WorldItemManager.TryGetInstance(out var current) && current?.Pointer == _manager.Pointer
                    && _data != null && !_data.WasCollected && _savers != null && !_savers.WasCollected
                    && _records != null && !_records.WasCollected && _manager._saveData?.Pointer == _data.Pointer
                    && _manager._worldItems?.Pointer == _savers.Pointer && _data.WorldItemStates?.Pointer == _records.Pointer
                    && _savers.Count == _records.Count && _records.Count <= 65536, "World-item registry lifetime changed.");
            }

            private int FindIdentity(CartSaveRecord value)
            {
                RequireRegistry(); int found = -1;
                for (int i = 0; i < _records.Count; i++)
                {
                    var record = _records[i];
                    Require(record != null && !record.WasCollected, "Registry contains an unavailable save record.");
                    if (record.ItemId != value.ItemId || record.ObjectNameId != value.Name) continue;
                    Require(found < 0, "Duplicate save identity."); found = i;
                }
                return found;
            }

            private int FindOwner(WorldItemSaver saver)
            {
                RequireRegistry(); int found = -1;
                for (int i = 0; i < _savers.Count; i++)
                    if (_savers[i]?.Pointer == saver.Pointer) { Require(found < 0, "Duplicate saver registration."); found = i; }
                return found;
            }

            private static bool SameIdentity(CartSaveRecord a, CartSaveRecord b) => a.ItemId == b.ItemId && a.Name == b.Name;

            internal static void Validate(CartSaveTransfer transfer)
            {
                var record = transfer?.Record;
                Require(record != null && !string.IsNullOrEmpty(record.Name) && record.Name.Length <= 256
                    && record.ItemId > 0 && record.State != 1 && record.DataLength is >= -1 and <= 1
                    && (record.Version == null || record.Version.Length <= 128)
                    && (record.DataLength == 1 || (record.Version == null && record.BaseTypeId == 0
                        && record.CartTypeId == 0 && record.Power == 0)), "Invalid cart save transfer.");
                RequireFinite(record.Position); RequireFinite(record.Rotation);
                RequireFinite(transfer.Position); RequireFinite(transfer.Rotation);
                _ = Finite(record.Power);
            }

            private static void RequireFinite(System.Numerics.Vector3 value)
                => Require(float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z),
                    "Cart save pose contains a non-finite component.");
        }
    }
}
