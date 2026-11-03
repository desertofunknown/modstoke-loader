// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Weapon;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        private const int MaximumWeaponObservationsPerFrame = 64;
        private static WorldRecordContext _weaponObservationWorld;
        private static int _weaponObservationFrame, _weaponObservationReads;
        private static bool _weaponObservationReading;

        /// <summary>
        /// Copies one explicitly selected transform from an existing equipped pistol (355).
        /// Does not equip, load, activate, aim or fire anything. Remote presentation may be absent
        /// or driven by client replication; the result does not authorize a shot.
        /// </summary>
        public static bool TryReadPlayerPistolTransform(PlayerHandle binding,
            WeaponTransformSource source, out WeaponTransformSnapshot snapshot,
            out WeaponObservationStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                status = WeaponObservationStatus.InvalidRequest;
                if (binding == null || source < WeaponTransformSource.Projectile
                    || source > WeaponTransformSource.VisualSpawn) return false;
                if (!TryWeaponObservationContext(out var world, out int frame, out status)
                    || !ReserveWeaponObservation(world, frame, out status)) return false;
                _weaponObservationReading = entered = true;
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                {
                    status = bindingStatus == WorldReadStatus.BindingChanged
                        ? WeaponObservationStatus.BindingChanged : WeaponObservationStatus.Unavailable;
                    return false;
                }
                if (!WeaponTransformObservation.TryCapture(binding, source, world, frame, out var captured, out status))
                    return false;
                snapshot = new WeaponTransformSnapshot(captured);
                return true;
            }
            catch (Exception error)
            {
                status = WeaponObservationStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _weaponObservationReading = false; }
        }

        /// <summary>
        /// Rechecks the exact binding, equipment, selected field, copied pose and flags on the
        /// server game thread in the same world/frame. Reads and checks share a 64-call frame budget.
        /// This does not establish when native animation or replication last updated the transform.
        /// </summary>
        public static bool IsCurrentWeaponTransformSnapshot(WeaponTransformSnapshot snapshot,
            out WeaponObservationStatus status)
        {
            bool entered = false;
            try
            {
                status = WeaponObservationStatus.InvalidRequest;
                if (snapshot == null) return false;
                if (!TryWeaponObservationContext(out var world, out int frame, out status)
                    || !ReserveWeaponObservation(world, frame, out status)) return false;
                _weaponObservationReading = entered = true;
                status = WeaponObservationStatus.ContextChanged;
                if (!ReferenceEquals(world, snapshot.Observation.World) || frame != snapshot.Frame) return false;
                if (!snapshot.Observation.IsCurrent()) return false;
                status = WeaponObservationStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WeaponObservationStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _weaponObservationReading = false; }
        }

        private static bool TryWeaponObservationContext(out WorldRecordContext world, out int frame,
            out WeaponObservationStatus status)
        {
            world = null;
            frame = 0;
            var context = CheckContext();
            status = context == WorldReadStatus.WrongThread ? WeaponObservationStatus.WrongThread
                : context == WorldReadStatus.NotServer ? WeaponObservationStatus.NotServer
                : WeaponObservationStatus.Unavailable;
            if (context != WorldReadStatus.Success) return false;
            status = WeaponObservationStatus.WorldUnavailable;
            if (!WorldRecords.TryGetCurrent(out world, out _)) return false;
            frame = Time.frameCount;
            return true;
        }

        private static bool ReserveWeaponObservation(WorldRecordContext world, int frame,
            out WeaponObservationStatus status)
        {
            status = WeaponObservationStatus.Busy;
            if (_weaponObservationReading) return false;
            if (!ReferenceEquals(world, _weaponObservationWorld) || frame != _weaponObservationFrame)
            {
                _weaponObservationWorld = world;
                _weaponObservationFrame = frame;
                _weaponObservationReads = 0;
            }
            status = WeaponObservationStatus.LimitReached;
            if (_weaponObservationReads >= MaximumWeaponObservationsPerFrame) return false;
            _weaponObservationReads++;
            return true;
        }

        internal sealed class WeaponTransformObservation
        {
            private readonly EquippedPistolCapture _equipment;
            private readonly Transform _transform;
            private readonly IntPtr _transformPointer;
            private readonly int _transformInstanceId;
            private readonly SpawnPointReference _physicalSpawn, _projectilePhysicalSpawn;

            private WeaponTransformObservation(PlayerHandle binding, WeaponTransformSource source,
                WorldRecordContext world, int frame, EquippedPistolCapture equipment, Transform transform)
            {
                Binding = binding;
                Source = source;
                World = world;
                Frame = frame;
                _equipment = equipment;
                _transform = transform;
                _transformPointer = transform.Pointer;
                _transformInstanceId = transform.GetInstanceID();
                var position = transform.position;
                var rotation = transform.rotation;
                Position = new Vector3(position.x, position.y, position.z);
                Rotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
                ItemActive = equipment.ItemObject.activeInHierarchy;
                WeaponActive = equipment.Weapon.gameObject.activeInHierarchy;
                TransformActive = transform.gameObject.activeInHierarchy;
                RenderableIsLoaded = equipment.Weapon._renderableIsLoaded;
                UseVisualTransformForSpawn = equipment.Weapon._useVisualTransformForSpawn;
                TraceForwardOffset = equipment.Weapon._traceForwardOffset;
                _physicalSpawn = new SpawnPointReference(equipment.Weapon.physicalSpawnPoint);
                _projectilePhysicalSpawn = new SpawnPointReference(equipment.Weapon._projectilePhysicalSpawnPoint);
                PhysicalSpawnMatchesProjectilePoint = _physicalSpawn.SameLiveTransform(_projectilePhysicalSpawn);
            }

            internal PlayerHandle Binding { get; }
            internal WorldRecordContext World { get; }
            internal int Frame { get; }
            internal WeaponTransformSource Source { get; }
            internal Vector3 Position { get; }
            internal Quaternion Rotation { get; }
            internal bool ItemActive { get; }
            internal bool WeaponActive { get; }
            internal bool TransformActive { get; }
            internal bool RenderableIsLoaded { get; }
            internal bool UseVisualTransformForSpawn { get; }
            internal float TraceForwardOffset { get; }
            internal bool PhysicalSpawnMatchesProjectilePoint { get; }

            internal static bool TryCapture(PlayerHandle binding, WeaponTransformSource source,
                WorldRecordContext world, int frame, out WeaponTransformObservation observation,
                out WeaponObservationStatus status)
            {
                observation = null;
                if (!EquippedPistolCapture.TryCapture(binding, out var equipment, out status)) return false;
                var transform = SelectTransform(equipment.Weapon, source);
                status = WeaponObservationStatus.TransformUnavailable;
                if (!LiveBallisticObject(transform) || transform.Pointer == IntPtr.Zero) return false;
                var candidate = new WeaponTransformObservation(binding, source, world, frame, equipment, transform);
                status = WeaponObservationStatus.InvalidTransform;
                var rotation = candidate.Rotation;
                if (!Finite(candidate.Position) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
                    || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W)
                    || ((double)rotation.X * rotation.X + (double)rotation.Y * rotation.Y
                        + (double)rotation.Z * rotation.Z + (double)rotation.W * rotation.W) == 0) return false;
                status = WeaponObservationStatus.InvalidLaunchOffset;
                if (!float.IsFinite(candidate.TraceForwardOffset)) return false;
                status = WeaponObservationStatus.ContextChanged;
                if (!candidate.IsCurrent()) return false;
                observation = candidate;
                status = WeaponObservationStatus.Success;
                return true;
            }

            internal bool IsCurrent()
            {
                if (!TryWeaponObservationContext(out var world, out int frame, out _)
                    || !ReferenceEquals(World, world) || Frame != frame
                    || !TryValidatePlayerBinding(Binding, out _) || !_equipment.IsCurrent()
                    || !MatchesTransform(_transform) || !MatchesTransform(SelectTransform(_equipment.Weapon, Source))) return false;
                var position = _transform.position;
                var rotation = _transform.rotation;
                if (Position != new Vector3(position.x, position.y, position.z)
                    || Rotation != new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w)
                    || ItemActive != _equipment.ItemObject.activeInHierarchy
                    || WeaponActive != _equipment.Weapon.gameObject.activeInHierarchy
                    || TransformActive != _transform.gameObject.activeInHierarchy
                    || RenderableIsLoaded != _equipment.Weapon._renderableIsLoaded
                    || UseVisualTransformForSpawn != _equipment.Weapon._useVisualTransformForSpawn
                    || TraceForwardOffset != _equipment.Weapon._traceForwardOffset
                    || !_physicalSpawn.Matches(_equipment.Weapon.physicalSpawnPoint)
                    || !_projectilePhysicalSpawn.Matches(_equipment.Weapon._projectilePhysicalSpawnPoint)) return false;
                return TryValidatePlayerBinding(Binding, out _)
                    && TryWeaponObservationContext(out world, out frame, out _)
                    && ReferenceEquals(World, world) && Frame == frame;
            }

            private bool MatchesTransform(Transform transform)
                => LiveBallisticObject(transform) && transform.Pointer == _transformPointer
                    && transform.GetInstanceID() == _transformInstanceId;

            private static Transform SelectTransform(RangedWeapon weapon, WeaponTransformSource source)
            {
                switch (source)
                {
                    case WeaponTransformSource.Projectile: return weapon._projectileTransform;
                    case WeaponTransformSource.ProjectileVisual: return weapon._projectileVisualTransform;
                    case WeaponTransformSource.PhysicalSpawn: return weapon._projectilePhysicalSpawnPoint;
                    case WeaponTransformSource.Aim: return weapon._aimTransform;
                    case WeaponTransformSource.BallisticSpawn: return weapon.physicalSpawnPoint;
                    case WeaponTransformSource.VisualSpawn: return weapon.visualSpawnPoint;
                    default: return null;
                }
            }

            private readonly struct SpawnPointReference
            {
                private readonly Transform _value;
                private readonly IntPtr _pointer;
                private readonly int _instanceId;

                internal SpawnPointReference(Transform value)
                {
                    _value = value;
                    bool live = LiveBallisticObject(value);
                    _pointer = live ? value.Pointer : IntPtr.Zero;
                    _instanceId = live ? value.GetInstanceID() : 0;
                }

                internal bool Matches(Transform current)
                {
                    if (_value is null) return current is null;
                    return _pointer != IntPtr.Zero && LiveBallisticObject(_value) && LiveBallisticObject(current)
                        && _value.Pointer == _pointer && current.Pointer == _pointer
                        && _value.GetInstanceID() == _instanceId && current.GetInstanceID() == _instanceId;
                }

                internal bool SameLiveTransform(SpawnPointReference other)
                    => _pointer != IntPtr.Zero && _pointer == other._pointer && _instanceId == other._instanceId
                        && Matches(_value) && other.Matches(other._value);
            }
        }
    }
}
