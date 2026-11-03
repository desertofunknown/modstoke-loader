// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>Reads a qualified stock sound from the retained material without spawning or playing effects.</summary>
        public static bool TryReadBallisticImpactSound(BallisticSurface surface,
            out BallisticImpactSound sound, out CollisionReadStatus status)
        {
            sound = BallisticImpactSound.None;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (surface == null || !ReferenceEquals(world, surface.EntryHit.World))
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!SameBallisticSurface(surface)) return CollisionChanged(out status);

                var material = surface.Material;
                bool skipImpactObjects = material.doNotTriggerSpawningOfProjectileImpactObjects;
                var impact = material.impactObject;
                var impactPointer = impact is null ? IntPtr.Zero : impact.Pointer;
                bool impactLive = LiveBallisticObject(impact);
                int impactId = impactLive ? impact.GetInstanceID() : 0;
                var projectile = impactLive ? impact.TryCast<global::ProjectileImpactObject>() : null;
                bool projectileLive = LiveBallisticObject(projectile);
                bool enabled = false;
                string path = null;
                int replicationType = 0;
                if (projectileLive)
                {
                    if (projectile.Pointer != impactPointer || projectile.GetInstanceID() != impactId)
                        return CollisionChanged(out status);
                    enabled = projectile.enabled;
                    path = projectile._impactAudioEvent;
                    replicationType = (int)projectile._impactReplicationType;
                }

                var selected = BallisticImpactSound.None;
                if (!skipImpactObjects && projectileLive && enabled)
                {
                    if (replicationType == 9 && string.Equals(path,
                        "event:/SotF Events/Combat/Bullets/bullet_impact_generic", StringComparison.Ordinal))
                        selected = BallisticImpactSound.HardSurface;
                    else if (replicationType == 10 && string.Equals(path,
                        "event:/SotF Events/Combat/Bullets/bullet_impact_body", StringComparison.Ordinal))
                        selected = BallisticImpactSound.Flesh;
                    else if (replicationType == 5 && string.Equals(path,
                        "event:/SotF Events/Combat/Bullets/bullet_dirt_impact", StringComparison.Ordinal))
                        selected = BallisticImpactSound.Wood;
                }

                if (!CollisionContextCurrent(world, frame) || !SameBallisticSurface(surface))
                    return CollisionChanged(out status);
                var currentImpact = material.impactObject;
                if ((currentImpact is null ? IntPtr.Zero : currentImpact.Pointer) != impactPointer
                    || LiveBallisticObject(currentImpact) != impactLive
                    || (impactLive && currentImpact.GetInstanceID() != impactId)
                    || material.doNotTriggerSpawningOfProjectileImpactObjects != skipImpactObjects)
                    return CollisionChanged(out status);
                var currentProjectile = impactLive ? currentImpact.TryCast<global::ProjectileImpactObject>() : null;
                if (LiveBallisticObject(currentProjectile) != projectileLive)
                    return CollisionChanged(out status);
                if (projectileLive && (currentProjectile.Pointer != impactPointer
                    || currentProjectile.GetInstanceID() != impactId || currentProjectile.enabled != enabled
                    || !string.Equals(currentProjectile._impactAudioEvent, path, StringComparison.Ordinal)
                    || (int)currentProjectile._impactReplicationType != replicationType))
                    return CollisionChanged(out status);
                if (!CollisionContextCurrent(world, frame) || !SameBallisticSurface(surface))
                    return CollisionChanged(out status);
                sound = selected;
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }
    }
}
