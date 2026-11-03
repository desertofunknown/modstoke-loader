// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Ballistics;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        public const int MaximumRetainedBallisticSurfaces = 16;
        public const int MaximumBallisticExitHits = 32;
        private static Il2CppStructArray<RaycastHit> _ballisticExitHits;

        /// <summary>Captures the current hit transform's material without initializing assets or invoking impact effects.</summary>
        public static bool TryReadBallisticSurface(CollisionHit hit, BallisticSnapshot launch,
            out BallisticSurface surface, out global::Modstoke.Sons.BallisticMaterial material,
            out CollisionReadStatus status)
        {
            surface = null;
            material = null;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (hit == null || launch == null || !ReferenceEquals(world, hit.World)
                    || !ReferenceEquals(world, launch.World) || hit.Frame != frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                if (launch.IgnoreInstigator)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!SameCollision(hit)) return CollisionChanged(out status);
                status = CollisionReadStatus.SurfaceUnavailable;
                var handler = BulletHandler._instance;
                if (!LiveBallisticObject(handler)) return false;
                var settings = handler.ballisticSettings;
                if (!LiveBallisticObject(settings) || !settings.BallisticMaterials) return false;
                var ballisticObject = hit.SurfaceTransform.GetComponent<BallisticObject>();
                if (!LiveBallisticObject(ballisticObject)) ballisticObject = null;
                var selected = ballisticObject is null ? null : ballisticObject.ballisticMaterial;
                if (!LiveBallisticObject(selected)) selected = settings.DefaultBallisticMaterial;
                if (!LiveBallisticObject(selected)) return false;
                var retained = new BallisticSurface(hit, ballisticObject, selected);
                var copied = new global::Modstoke.Sons.BallisticMaterial(selected);
                if (!ValidBallisticMaterial(copied))
                { status = CollisionReadStatus.InvalidMaterial; return false; }

                if (!CollisionContextCurrent(world, frame) || !SameBallisticSurface(retained)
                    || !SameBallisticObject(handler, BulletHandler._instance)
                    || !SameBallisticObject(settings, handler.ballisticSettings) || !settings.BallisticMaterials)
                    return CollisionChanged(out status);
                var currentObject = hit.SurfaceTransform.GetComponent<BallisticObject>();
                if (ballisticObject is null ? LiveBallisticObject(currentObject)
                    : !SameBallisticObject(ballisticObject, currentObject))
                    return CollisionChanged(out status);
                var currentMaterial = ballisticObject is null ? null : ballisticObject.ballisticMaterial;
                if (!LiveBallisticObject(currentMaterial)) currentMaterial = settings.DefaultBallisticMaterial;
                if (!SameBallisticObject(selected, currentMaterial)
                    || !CollisionContextCurrent(world, frame) || !SameBallisticSurface(retained))
                    return CollisionChanged(out status);
                surface = retained;
                material = copied;
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

        /// <summary>Reads the retained material in this world, including later frames; it does not reselect the object's material.</summary>
        public static bool TryReadBallisticMaterial(BallisticSurface surface,
            out global::Modstoke.Sons.BallisticMaterial material, out CollisionReadStatus status)
        {
            material = null;
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
                var copied = new global::Modstoke.Sons.BallisticMaterial(surface.Material);
                if (!ValidBallisticMaterial(copied))
                { status = CollisionReadStatus.InvalidMaterial; return false; }
                if (!CollisionContextCurrent(world, frame) || !SameBallisticSurface(surface))
                    return CollisionChanged(out status);
                material = copied;
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

        /// <summary>
        /// Matches a retained surface's original collider attachment to an exact player, including later frames.
        /// A mismatch does not retire a valid binding. This does not establish current contact geometry,
        /// native health, or permission to apply damage.
        /// </summary>
        public static bool TryMatchBallisticSurfacePlayer(BallisticSurface surface,
            PlayerHandle binding, out EntitySnapshot player,
            out CollisionReadStatus status)
        {
            player = null;
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
                var hit = surface.EntryHit;
                if (hit.Candidate == null)
                {
                    status = CollisionReadStatus.EntityUnavailable;
                    return false;
                }
                if (!TryReadPlayerBinding(binding, out var snapshot, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!MatchesPlayerAttachment(binding, hit.Candidate)
                    || hit.Attachment.Pointer != binding.Attachment.Pointer)
                {
                    status = CollisionReadStatus.PlayerMismatch;
                    return false;
                }
                if (!CollisionContextCurrent(world, frame) || !SameBallisticSurface(surface))
                    return CollisionChanged(out status);
                // Revalidate the canonical player after native collider and ancestry reads.
                if (!TryReadPlayerBinding(binding, out snapshot, out bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!CollisionContextCurrent(world, frame))
                {
                    status = CollisionReadStatus.StaleHit;
                    return false;
                }
                player = snapshot;
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

        /// <summary>
        /// Traces from nextPosition back to currentPosition and selects the farthest positive hit on a retained transform.
        /// A full buffer is indeterminate. True with Miss means the unsaturated query found no matching exit.
        /// </summary>
        public static bool TryTraceBallisticExit(Vector3 currentPosition, Vector3 nextPosition,
            ReadOnlySpan<BallisticSurface> surfaces, int layerMask, CollisionTriggers triggers,
            out CollisionHit hit, out int surfaceIndex, out CollisionReadStatus status)
        {
            hit = null;
            surfaceIndex = -1;
            status = CollisionReadStatus.Unavailable;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (surfaces.Length == 0 || surfaces.Length > MaximumRetainedBallisticSurfaces || layerMask == 0
                    || (triggers != CollisionTriggers.Ignore && triggers != CollisionTriggers.Collide)
                    || !TrySegment(nextPosition, currentPosition, out var direction, out float distance))
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!CurrentBallisticSurfaces(surfaces, world, out status)) return false;
                if (!_collisionSynced)
                {
                    if (!Physics.autoSyncTransforms) Physics.SyncTransforms();
                    _collisionSynced = true;
                }
                var scene = Physics.defaultPhysicsScene;
                if (!scene.IsValid()) return CollisionUnavailable(out status);
                _ballisticExitHits ??= new Il2CppStructArray<RaycastHit>(MaximumBallisticExitHits);
                int count = scene.Raycast(new UnityEngine.Vector3(nextPosition.X, nextPosition.Y, nextPosition.Z),
                    direction, _ballisticExitHits, distance, layerMask,
                    triggers == CollisionTriggers.Collide ? QueryTriggerInteraction.Collide : QueryTriggerInteraction.Ignore);
                if (count < 0 || count > MaximumBallisticExitHits)
                { status = CollisionReadStatus.Failed; return false; }
                if (count == MaximumBallisticExitHits)
                { status = CollisionReadStatus.BufferFull; return false; }
                if (!CollisionContextCurrent(world, frame))
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!CurrentBallisticSurfaces(surfaces, world, out status)) return false;

                float bestDistance = 0;
                int bestHit = -1;
                int bestSurface = -1;
                for (int i = 0; i < count; i++)
                {
                    var candidate = _ballisticExitHits[i];
                    float candidateDistance = candidate.distance;
                    var transform = candidate.transform;
                    if (!float.IsFinite(candidateDistance) || candidateDistance < 0 || candidateDistance > distance
                        || !Finite(candidate.point) || !Finite(candidate.normal)
                        || !LiveCollider(candidate.collider) || !LiveBallisticObject(transform))
                        return CollisionChanged(out status);
                    if (candidateDistance <= bestDistance) continue;
                    int matched = -1;
                    // Keep scanning duplicates so the lowest retained index wins, as in the native traversal.
                    for (int j = surfaces.Length - 1; j >= 0; j--)
                    {
                        var entry = surfaces[j].EntryHit;
                        if (SameBallisticObject(transform, entry.SurfaceTransform)
                            && transform.GetInstanceID() == entry.SurfaceTransformInstanceId)
                            matched = j;
                    }
                    if (matched < 0) continue;
                    bestDistance = candidateDistance;
                    bestHit = i;
                    bestSurface = matched;
                }
                if (!CollisionContextCurrent(world, frame))
                { status = CollisionReadStatus.StaleHit; return false; }
                if (!CurrentBallisticSurfaces(surfaces, world, out status)) return false;
                if (!CollisionContextCurrent(world, frame))
                { status = CollisionReadStatus.StaleHit; return false; }
                if (bestHit < 0)
                { status = CollisionReadStatus.Miss; return true; }
                if (!TryCreateCollisionHit(world, frame, nextPosition, currentPosition, layerMask, triggers,
                    _ballisticExitHits[bestHit], distance, out var observation, out status)) return false;
                if (!CurrentBallisticSurfaces(surfaces, world, out status)) return false;
                if (!CollisionContextCurrent(world, frame) || !SameCollision(observation))
                    return CollisionChanged(out status);
                hit = observation;
                surfaceIndex = bestSurface;
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

        private static bool CurrentBallisticSurfaces(ReadOnlySpan<BallisticSurface> surfaces,
            WorldRecordContext world, out CollisionReadStatus status)
        {
            status = CollisionReadStatus.StaleHit;
            foreach (var surface in surfaces)
            {
                if (surface == null || !ReferenceEquals(world, surface.EntryHit.World)) return false;
                if (!SameBallisticSurface(surface)) return CollisionChanged(out status);
            }
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool SameBallisticSurface(BallisticSurface surface)
        {
            if (!SameCollision(surface.EntryHit) || !LiveBallisticObject(surface.Material)
                || surface.Material.GetInstanceID() != surface.MaterialInstanceId)
                return false;
            var ballisticObject = surface.BallisticObject;
            return !surface.HasBallisticObject || (LiveBallisticObject(ballisticObject)
                && ballisticObject.GetInstanceID() == surface.BallisticObjectInstanceId
                && SameBallisticObject(ballisticObject.transform, surface.EntryHit.SurfaceTransform));
        }

        private static bool ValidBallisticMaterial(global::Modstoke.Sons.BallisticMaterial value)
            => float.IsFinite(value.EnergyLossPerUnit) && float.IsFinite(value.Spread)
                && float.IsFinite(value.SpreadRicochet) && float.IsFinite(value.RicochetProbabilityMin)
                && float.IsFinite(value.RicochetProbabilityMax) && float.IsFinite(value.Bounciness);
    }
}
