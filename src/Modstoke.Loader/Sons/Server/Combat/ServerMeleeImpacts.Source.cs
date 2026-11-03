// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Ai.Vail;
using global::Sons.Animation.Mae;
using global::Sons.Items.Core;
using UnityEngine;
using MeleeWeapon = global::Sons.Gameplay.MeleeWeapon;
using WeaponEntry = global::Sons.Ai.Vail.MeleeWeapon;

namespace Modstoke.Sons;

public static partial class ServerMeleeImpacts
{
    internal sealed class SourceCapture
    {
        private const int MaximumWeapons = 32;
        private readonly MeleeWeapon _weapon;
        private readonly GameObject _weaponObject;
        private readonly VailActor _actor;
        private readonly VailActorNetworked _network;
        private readonly BoltEntity _entity;
        private readonly Bolt.Entity _attachment;
        private readonly Il2CppSystem.Collections.Generic.List<WeaponEntry> _weapons;
        private readonly WeaponEntry _entry;
        private readonly MeleeImpactData _data;
        private readonly MeleeWeaponData _weaponData;
        private readonly MaeSequence _actionAsset, _action;
        private readonly Il2CppObjectBase _actionTarget, _statusEffects;
        private readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<VailActorClassId> _actorClasses;
        private readonly int _weaponCount, _actionFlags;
        private readonly Vector3 _effectVelocity;

        private SourceCapture(MeleeWeapon weapon, VailActor actor, VailActorNetworked network,
            BoltEntity entity, Bolt.Entity attachment,
            Il2CppSystem.Collections.Generic.List<WeaponEntry> weapons, WeaponEntry entry,
            MeleeImpactData data, MeleeWeaponData weaponData)
        {
            _weapon = weapon;
            _weaponObject = weapon.gameObject;
            _actor = actor;
            _network = network;
            _entity = entity;
            _attachment = attachment;
            _weapons = weapons;
            _weaponCount = weapons.Count;
            _entry = entry;
            _data = data;
            _weaponData = weaponData;
            _actionAsset = actor._activeActionAsset;
            _action = actor._activeActionInstance;
            if (_action is not null && Live(_action))
            {
                _actionTarget = _action._target;
                _actionFlags = (_action._InUse_k__BackingField ? 1 : 0)
                    | (_action._ended ? 2 : 0) | (_action._interrupted ? 4 : 0) | (_action._failed ? 8 : 0);
            }
            HasRunningAction = Live(_actionAsset) && Live(_action) && Same(_actionTarget, actor)
                && _actionFlags == 1;
            ActorInstanceId = actor.GetInstanceID();
            ActorTypeId = (int)actor._id;
            _actorClasses = VailTypes._typeToClass;
            if (Native(_actorClasses) && ActorTypeId >= 0 && ActorTypeId < _actorClasses.Length)
                ActorClassId = (int)_actorClasses[ActorTypeId];
            WeaponInstanceId = weapon.GetInstanceID();
            ActorNetworkId = attachment.NetworkId.PackedValue;
            AnimationStateHash = entry._animStateHash;
            OriginalDamage = data._originalRawDamage;
            RemainingDamage = data._remainingRawDamage;
            AccumulatedDamage = data._accumulatedDamage;
            NativeImpactId = data._uniqueId;
            MeleeType = (int)data._type;
            IsDemonic = data._isDemonic;
            Weight = weaponData.Weight;
            Center = data._center;
            Velocity = data._velocity;
            _effectVelocity = data._impactEffectVelocity;
            _statusEffects = data._statusEffects;
            StatusEffectCount = ReadStatusEffectCount(_statusEffects);
        }

        internal int ActorInstanceId { get; }
        internal int ActorTypeId { get; }
        internal int? ActorClassId { get; }
        internal int? StatusEffectCount { get; }
        internal int WeaponInstanceId { get; }
        internal ulong ActorNetworkId { get; }
        internal int AnimationStateHash { get; }
        internal bool HasRunningAction { get; }
        internal float OriginalDamage { get; }
        internal float RemainingDamage { get; }
        internal float AccumulatedDamage { get; }
        internal int NativeImpactId { get; }
        internal int MeleeType { get; }
        internal bool IsDemonic { get; }
        internal float Weight { get; }
        internal Vector3 Center { get; }
        internal Vector3 Velocity { get; }

        internal static bool TryCapture(MeleeWeapon weapon, MeleeImpactData data, out SourceCapture capture,
            bool requireMeleeActive = true)
        {
            capture = null;
            if (!Live(weapon) || !weapon.isActiveAndEnabled || !Native(data)
                || !Same(weapon._cachedImpactData, data) || !Same(data._sender, weapon)) return false;
            var actor = weapon._ownerActor;
            if (!Live(actor) || !actor.isActiveAndEnabled || actor._isDead
                || (requireMeleeActive && !actor._meleeIsActive)) return false;
            var network = actor._actorNetworked;
            if (!Live(network) || !Same(network._vailActor, actor)) return false;
            // Read existing attachments; the public entity getter can bind a parent lazily.
            var entity = network._entity;
            if (!Live(entity) || !entity.isAttached) return false;
            var attachment = entity._entity;
            if (!Native(attachment) || !attachment.IsOwner || attachment.IsFrozen
                || !Same(attachment.UnityObject, entity)) return false;
            var weapons = actor._meleeWeapons;
            if (!Native(weapons) || weapons.Count <= 0 || weapons.Count > MaximumWeapons) return false;
            var weaponObject = weapon.gameObject;
            WeaponEntry matched = null;
            int count = weapons.Count;
            for (int index = 0; index < count; index++)
            {
                var entry = weapons[index];
                if (!Native(entry) || !Same(entry._gameObject, weaponObject)) continue;
                if (matched != null) return false;
                matched = entry;
            }
            // A zero animation hash is valid: the game's string-based enable path uses it.
            if (matched == null || !matched._enabled || weapons.Count != count) return false;
            var weaponData = data._meleeWeaponData;
            if (!Native(weaponData)) return false;
            var candidate = new SourceCapture(weapon, actor, network, entity, attachment, weapons, matched, data, weaponData);
            if (!float.IsFinite(candidate.OriginalDamage) || !float.IsFinite(candidate.RemainingDamage)
                || !float.IsFinite(candidate.AccumulatedDamage) || !float.IsFinite(candidate.Weight)
                || !Finite(candidate.Center) || !Finite(candidate.Velocity) || !Finite(candidate._effectVelocity)) return false;
            capture = candidate;
            return true;
        }

        internal bool IsWeapon(MeleeWeapon weapon) => Same(_weapon, weapon);

        internal bool MatchesActivation(SourceCapture other)
            => other != null && Same(_weapon, other._weapon) && Same(_weaponObject, other._weaponObject)
                && Same(_actor, other._actor) && Same(_network, other._network) && Same(_entity, other._entity)
                && Same(_attachment, other._attachment) && Same(_weapons, other._weapons)
                && Same(_entry, other._entry) && Same(_data, other._data) && Same(_weaponData, other._weaponData)
                && SameOptional(_statusEffects, other._statusEffects) && StatusEffectCount == other.StatusEffectCount
                && SameOptional(_actorClasses, other._actorClasses) && ActorTypeId == other.ActorTypeId
                && ActorClassId == other.ActorClassId && _weaponCount == other._weaponCount
                && ActorInstanceId == other.ActorInstanceId && WeaponInstanceId == other.WeaponInstanceId
                && ActorNetworkId == other.ActorNetworkId && MeleeType == other.MeleeType && IsDemonic == other.IsDemonic
                && Bits(OriginalDamage, other.OriginalDamage) && Bits(Weight, other.Weight);

        // Constant-size lifetime check. The request path still rescans the complete bounded weapon
        // list; registry maintenance must not scan every actor's list for every retained token.
        // _meleeIsActive is deliberately absent: SetEnabled sets it after OnEnable returns.
        internal bool IsActivationCurrent()
            => Live(_weapon) && _weapon.isActiveAndEnabled && _weapon.GetInstanceID() == WeaponInstanceId
                && Live(_weaponObject) && Same(_weapon.gameObject, _weaponObject)
                && Live(_actor) && _actor.isActiveAndEnabled && !_actor._isDead
                && _actor.GetInstanceID() == ActorInstanceId && (int)_actor._id == ActorTypeId
                && Same(_weapon._ownerActor, _actor) && Live(_network)
                && Same(_actor._actorNetworked, _network) && Same(_network._vailActor, _actor)
                && Live(_entity) && _entity.isAttached && Same(_network._entity, _entity)
                && Same(_entity._entity, _attachment) && _attachment.IsOwner && !_attachment.IsFrozen
                && Same(_attachment.UnityObject, _entity) && _attachment.NetworkId.PackedValue == ActorNetworkId
                && Same(_actor._meleeWeapons, _weapons) && _weapons.Count == _weaponCount
                && Native(_entry) && _entry._enabled && Same(_entry._gameObject, _weaponObject)
                && Same(_weapon._cachedImpactData, _data) && Same(_data._sender, _weapon)
                && Same(_data._meleeWeaponData, _weaponData) && Bits(_data._originalRawDamage, OriginalDamage)
                && (int)_data._type == MeleeType && _data._isDemonic == IsDemonic
                && Bits(_weaponData.Weight, Weight) && SameOptional(_data._statusEffects, _statusEffects)
                && ReadStatusEffectCount(_statusEffects) == StatusEffectCount
                && SameOptional(VailTypes._typeToClass, _actorClasses)
                && (!ActorClassId.HasValue || (Native(_actorClasses) && ActorTypeId >= 0
                    && ActorTypeId < _actorClasses.Length && (int)_actorClasses[ActorTypeId] == ActorClassId.Value));

        internal bool IsCurrent()
            => TryCapture(_weapon, _data, out var other)
                && Same(_weaponObject, other._weaponObject) && Same(_actor, other._actor)
                && Same(_network, other._network) && Same(_entity, other._entity)
                && Same(_attachment, other._attachment) && Same(_weapons, other._weapons)
                && Same(_entry, other._entry) && Same(_weaponData, other._weaponData)
                && SameOptional(_actionAsset, other._actionAsset) && SameOptional(_action, other._action)
                && SameOptional(_actionTarget, other._actionTarget) && SameOptional(_statusEffects, other._statusEffects)
                && SameOptional(_actorClasses, other._actorClasses)
                && ActorTypeId == other.ActorTypeId && ActorClassId == other.ActorClassId
                && StatusEffectCount == other.StatusEffectCount
                && _weaponCount == other._weaponCount && _actionFlags == other._actionFlags
                && ActorInstanceId == other.ActorInstanceId && WeaponInstanceId == other.WeaponInstanceId
                && ActorNetworkId == other.ActorNetworkId && AnimationStateHash == other.AnimationStateHash
                && HasRunningAction == other.HasRunningAction && NativeImpactId == other.NativeImpactId
                && MeleeType == other.MeleeType && IsDemonic == other.IsDemonic
                && Bits(OriginalDamage, other.OriginalDamage) && Bits(RemainingDamage, other.RemainingDamage)
                && Bits(AccumulatedDamage, other.AccumulatedDamage) && Bits(Weight, other.Weight)
                && Bits(Center, other.Center) && Bits(Velocity, other.Velocity) && Bits(_effectVelocity, other._effectVelocity);

        private static int? ReadStatusEffectCount(Il2CppObjectBase effects)
        {
            if (effects is null) return 0;
            if (!Native(effects)) return null;
            try
            {
                var collection = effects.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<global::Sons.Gameplay.StatusEffect>>();
                if (!Native(collection)) return null;
                int count = collection.Count;
                return count >= 0 ? count : null;
            }
            catch (Exception) { return null; }
        }
    }

    private static bool Native(Il2CppObjectBase value) => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero;
    private static bool Live(UnityEngine.Object value) => Native(value) && value;
    private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
        => Native(left) && Native(right) && left.Pointer == right.Pointer;
    private static bool SameOptional(Il2CppObjectBase left, Il2CppObjectBase right)
        => left is null ? right is null : Same(left, right);
    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    private static bool Bits(float left, float right) => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    private static bool Bits(Vector3 left, Vector3 right) => Bits(left.x, right.x) && Bits(left.y, right.y) && Bits(left.z, right.z);
}
