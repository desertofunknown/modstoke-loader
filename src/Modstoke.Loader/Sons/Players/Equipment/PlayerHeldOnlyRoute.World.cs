// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using global::Sons.Ai.Vail.StimuliTypes;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Utils;
using UnityEngine;
using Held = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerHeldOnlyRoute
    {
        private sealed class Destination(Held.NativeDropEffect effect, ItemData definition, Transform prefab, Bolt.PrefabId prefabId)
        {
            internal readonly Held.NativeDropEffect Effect = effect;
            internal readonly ItemData Definition = definition;
            internal readonly Transform Prefab = prefab;
            internal readonly Bolt.PrefabId PrefabId = prefabId;
            internal ItemInstance Item;
            internal BoltEntity Entity;
            internal PickUp Pickup;
            internal bool Attempted, Confirmed;
        }

        private Destination[] _destinations;

        private static bool DropActorAvailable(BoltEntity actor, int itemId, out string detail)
        {
            detail = "The exact actor and native singleton held pickup prefab must be available.";
            var data = ItemDatabaseManager.ItemById(itemId);
            var prefab = data?.PickupPrefab;
            if (!Live(actor) || !Live(data) || !Live(prefab)) return false;
            var pickup = prefab.GetComponent<PickUp>();
            if (!ItemUtils.IsValidForCurrentGameMode(data) || !Live(prefab.GetComponent<BoltEntity>())
                || !Live(pickup) || pickup._itemId != itemId || pickup.GetIl2CppType().FullName != "Sons.Gameplay.PickUp"
                || pickup._amount == null || pickup._amount._min != 1 || pickup._amount._max != 1
                || pickup._infinite || pickup._disableInsteadOfDestroy) return false;
            if (Live(prefab.GetComponentInChildren<ThrownLogDamage>(true)) && !Live(actor.GetComponent<PlayerStimuli>()))
            { detail = "A real actor-owned PlayerStimuli is required for thrown-log damage attribution."; return false; }
            detail = "Native drop prefab and actor-owned attribution are available.";
            return true;
        }

        private bool CanDispatchDrop(int itemId)
            => _carrier.TryReadRestoredServerStats(out var stats) && !stats._playerIsDowned
                && stats._dyingState == PlayerStats.DyingState.Alive
                && _bridge.TryReadCurrentTarget(out var actor) && _carrier.Entity?.Pointer == actor.Pointer
                && DropActorAvailable(actor, itemId, out _);

        private bool ApplyWorldDrops()
        {
            Require(_native.StrengthGain == 0 && _command.Before.DropProfile == _held.DropProfile,
                "Drop configuration differs from approved custody or claims a strength write.");
            if (!_bridge.TryReadCurrentTarget(out var actor) || _carrier.Entity?.Pointer != actor.Pointer
                || !_carrier.TryReadRestoredServerStats(out _))
                throw new InvalidOperationException("Native drop no longer belongs to its restored server actor.");
            if (_destinations == null)
            {
                var drops = _native.GetDrops();
                Require(drops.Length > 0, "Compound drop has no outgoing native instances.");
                _destinations = new Destination[drops.Length];
                for (int i = 0; i < drops.Length; i++)
                {
                    var drop = drops[i];
                    var data = ItemDatabaseManager.ItemById(drop.ItemId);
                    var prefab = data?.PickupPrefab;
                    var prefabEntity = Live(prefab) ? prefab.GetComponent<BoltEntity>() : null;
                    Require(DropActorAvailable(actor, drop.ItemId, out _) && Live(prefabEntity)
                        && prefabEntity.prefabId.Value == drop.PrefabId
                        && Mathf.Abs(Quaternion.Dot(drop.Rotation, drop.Rotation) - 1f) < 0.001f
                        && _inventory.HeldOnlyWorldGeometry(prefab, drop.Position) && NativeVelocity(drop.Velocity),
                        "Drop prefab, server geometry, native force profile or singleton pickup is unsupported.");
                    _destinations[i] = new(drop, data, prefab, prefabEntity.prefabId);
                }
            }
            foreach (var destination in _destinations)
            {
                Require(destination != null, "Partial destination preflight remains uncertain.");
                if (destination.Confirmed) continue;
                Require(!destination.Attempted && Current() && Live(destination.Prefab)
                    && destination.Definition.PickupPrefab?.Pointer == destination.Prefab.Pointer,
                    "Destination cannot be replayed or moved after native dispatch.");
                // Clearance was checked for the whole batch above, before any of its
                // own pickups could obstruct a later drop in this synchronous pass.
                destination.Attempted = true;
                var effect = destination.Effect;
                var entry = new PlayerInventoryBaseline.Entry { ItemId = effect.ItemId,
                    Modules = effect.NativeModuleSaveData, ModuleState = effect.ModuleState };
                PlayerInventoryBaseline.RestoreInstance(entry, out destination.Item);
                Require(PlayerInventoryBaseline.MatchesInstance(entry, destination.Item)
                    && destination.Item._modules != null && !destination.Item._modules.WasCollected
                    && destination.Item._modules.Count == 0,
                    "Outgoing destination lost its exact module-free custody.");
                // Current OnEvent(DropItem), non-preSpawned branch: retain the native
                // return immediately, even if a later component or module call fails.
                destination.Entity = BoltNetwork.Instantiate(destination.PrefabId, effect.Position, effect.Rotation);
                var entity = destination.Entity;
                Require(Live(entity) && entity.isAttached && entity.isOwner && entity.source == null
                    && entity.networkId.PackedValue != 0 && entity.prefabId.Value == effect.PrefabId,
                    "Native drop did not produce the exact server-owned network prefab.");
                var body = entity.GetComponentInChildren<Rigidbody>();
                if (Live(body))
                {
                    body.isKinematic = false; body.useGravity = true; body.WakeUp();
                    body.AddForce(effect.Velocity, ForceMode.VelocityChange);
                    // The SERVER receiver ignores DropItem.Torque. Its current native
                    // body samples insideUnitSphere * 10 and applies world-space torque.
                    body.AddTorque(UnityEngine.Random.insideUnitSphere * 10f, ForceMode.VelocityChange);
                }
                var thrown = entity.GetComponentInChildren<ThrownLogDamage>();
                if (Live(thrown))
                {
                    var stimuli = actor.GetComponent<PlayerStimuli>();
                    Require(Live(stimuli), "Thrown-log source actor changed after spawn.");
                    thrown.SetSourceStimuli(stimuli);
                    Require(thrown._sourceStimuli?.Pointer == stimuli.Pointer, "Thrown-log attribution did not settle.");
                }
                destination.Pickup = entity.GetComponent<PickUp>();
                var pickup = destination.Pickup;
                Require(Live(pickup) && pickup._itemId == effect.ItemId, "Native drop has no exact pickup receiver.");
                pickup.SetDynamic(true);
                pickup.SetItemInstance(destination.Item, true);
                destination.Item.SetItemObject(entity.gameObject, true);
                var projectile = entity.GetComponentInChildren<ProjectileNetworkProxy>();
                if (Live(projectile)) projectile.SetItemInstance(destination.Item);
                Require(pickup._ItemInstance_k__BackingField?.Pointer == destination.Item.Pointer
                    && destination.Item.ItemObject?.Pointer == entity.gameObject.Pointer
                    && PlayerInventoryBaseline.MatchesInstance(entry, destination.Item)
                    && BoltNetwork.FindEntity(entity.networkId)?.Pointer == entity.Pointer && Current(),
                    "World pickup/module readback did not establish destination custody.");
                destination.Confirmed = true;
            }
            return true;
        }

        private bool NativeVelocity(Vector3 velocity)
        {
            float force = _held.DropProfile.Force, up = _held.DropProfile.UpForce;
            if (!float.IsFinite(force) || force < 0 || !float.IsFinite(up)
                || !float.IsFinite(velocity.x) || !float.IsFinite(velocity.y) || !float.IsFinite(velocity.z)) return false;
            if (force == 0) return velocity == Vector3.zero;
            var direction = velocity / force - Vector3.up * up;
            return Mathf.Abs(direction.sqrMagnitude - 1f) < 0.001f;
        }

        private ulong[] WorldIds()
        {
            var ids = new ulong[_destinations.Length];
            var unique = new HashSet<ulong>();
            for (int i = 0; i < ids.Length; i++)
            {
                var destination = _destinations[i];
                Require(destination.Confirmed && Live(destination.Entity), "World destination lifetime ended before receipt.");
                ids[i] = destination.Entity.networkId.PackedValue;
                Require(unique.Add(ids[i]), "Two outgoing items alias the same world destination.");
            }
            return ids;
        }

        private bool ConfirmSourceWorldObjects()
        {
            var unique = new HashSet<ulong>();
            var drops = _native.GetDrops();
            for (int i = 0; i < _worldCommit.Objects.Length; i++)
            {
                ulong id = _worldCommit.Objects[i];
                Require(id != 0 && unique.Add(id), "World receipt contains duplicate or empty network identity.");
                var entity = BoltNetwork.FindEntity(new NetworkId(id));
                if (!Live(entity)) return false; // Wait for ordinary Bolt replication, not a native-return bool.
                var pickup = entity.GetComponent<PickUp>();
                Require(entity.isAttached && !entity.isOwner && entity.prefabId.Value == drops[i].PrefabId
                    && Live(pickup) && pickup._itemId == drops[i].ItemId, "Replicated drop identity disagrees with its receipt.");
            }
            return true;
        }

        private void PlayConfirmedAudio()
        {
            // One actual native sound per confirmed drop, at the captured renderer transform.
            foreach (var drop in _source.Drops)
            {
                Require(Live(drop.AudioTransform), "Original drop sound transform was lost.");
                FMODCommon.PlayOneshotNetworked("event:/player/foley/log_drop_exert", drop.AudioTransform,
                    (FMODCommon.NetworkRole)1);
            }
        }

        private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
    }
}
