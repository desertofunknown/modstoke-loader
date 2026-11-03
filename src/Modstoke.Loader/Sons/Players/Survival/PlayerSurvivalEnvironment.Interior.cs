// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Construction;
using Construction.Utils;
using Endnight.Utilities;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Modstoke.Sons
{
    internal readonly record struct PlayerSurvivalInterior(
        int Frame, int Scene, Vector3 Position, bool HasCeiling, bool InteriorSpaceWarmth);

    internal sealed partial class PlayerSurvivalEnvironmentReader
    {
        private const int InteriorHitCapacity = 256;
        private Il2CppStructArray<RaycastHit> _interiorGroundHits;
        private Il2CppReferenceArray<Collider> _interiorOverlaps;
        private SuperStructureManager _interiorManager;
        private int _interiorManagerId;

        /// <summary>
        /// Samples this server carrier's root against the native construction room database.
        /// Call after native room production and settled actor physics. This is an observation,
        /// not a stat writer, native probe clock, or authority to toggle shared scene volumes.
        /// </summary>
        internal bool TryReadInterior(out PlayerSurvivalInterior interior, out string detail)
        {
            interior = default;
            detail = "Interior sampling requires a live, non-reentrant game-thread render phase.";
            if (IsDisposed || _reading || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep)
                return false;
            _reading = true;
            int overlapCount = 0;
            try
            {
                RequireCurrent();
                ReadSwimming();
                RequireInteriorManager();
                if (PhysicsSceneExtensions.GetPhysicsScene(_entity.gameObject.scene) != Physics.defaultPhysicsScene)
                    throw new InvalidOperationException("Construction queries require the actor's native default physics scene.");
                int frame = Time.frameCount;
                Vector3 position = _root.position;
                Quaternion rotation = _root.rotation;
                if (!Finite(position)) throw new InvalidOperationException("The interior sample position is nonfinite.");
                int propMask = Layers.PropMask;
                int volumeMask = Layers.BasicColliderMask;
                if (propMask == 0 || volumeMask == 0)
                    throw new InvalidOperationException("The native construction query layers are unavailable.");

                _interiorGroundHits ??= new Il2CppStructArray<RaycastHit>(InteriorHitCapacity);
                _interiorOverlaps ??= new Il2CppReferenceArray<Collider>(InteriorHitCapacity);
                bool hasCeiling = false, warmth = false, found = false;

                // ConstructionWarmthProbe.LookupRoomAndWarmth and CastUtils.TryCastDownToLayers:
                // 0.05m down-sphere, 8m range, PropMask, filters 164, triggers ignored.
                // Own buffers expose saturation instead of trusting the native shared scratch array.
                var down = new Ray(position, Vector3.down);
                int hits = Physics.SphereCastNonAlloc(down, 0.05f, _interiorGroundHits, 8f,
                    propMask, QueryTriggerInteraction.Ignore);
                if (hits < 0 || hits >= _interiorGroundHits.Length)
                    throw new InvalidOperationException("The construction ground query is incomplete (hit buffer full).");
                if (hits > 0 && CastUtils.TryFindSupportFromHits(hits, _interiorGroundHits, out int index,
                    CastUtils.Filters.StartedInsideAndForwardHits, down, null))
                {
                    if (index < 0 || index >= hits || !Alive(_interiorGroundHits[index].collider))
                        throw new InvalidOperationException("The native construction support result is invalid.");
                    found = LookupInteriorRoom(_interiorGroundHits[index].collider.gameObject, position,
                        out hasCeiling, out warmth);
                }

                if (!found)
                {
                    // Preserve native first-match order, including structure-before-volume on
                    // each collider. A geometric fake-volume hit sets the ACTOR result only.
                    overlapCount = Physics.OverlapSphereNonAlloc(position, 0.01f, _interiorOverlaps,
                        volumeMask, QueryTriggerInteraction.Collide);
                    if (overlapCount < 0 || overlapCount >= _interiorOverlaps.Length)
                        throw new InvalidOperationException("The construction volume query is incomplete (hit buffer full).");
                    for (int i = 0; i < overlapCount; i++)
                    {
                        var collider = _interiorOverlaps[i];
                        if (!Alive(collider))
                            throw new InvalidOperationException("A construction overlap collider retired during sampling.");
                        if (LookupInteriorRoom(collider.gameObject, position, out hasCeiling, out warmth)) break;
                        var volume = collider.GetComponent<FakeInsideConstructionVolume>();
                        if (!Alive(volume)) continue;
                        // Native SetIsInside(true) writes IsInside and enables presentation
                        // objects. Never mutate that shared receiver for a remote actor.
                        hasCeiling = true;
                        warmth = volume._IsWarm_k__BackingField;
                        break;
                    }
                }

                RequireCurrent();
                ReadSwimming();
                RequireInteriorManager();
                if (Time.frameCount != frame || !_root.position.Equals(position) || !_root.rotation.Equals(rotation)
                    || Layers.PropMask != propMask || Layers.BasicColliderMask != volumeMask)
                    throw new InvalidOperationException("The actor pose or construction query configuration changed during sampling.");
                interior = new(frame, _scene, position, hasCeiling, warmth);
                detail = "Actor construction ceiling and interior warmth sampled from native rooms and volumes.";
                return true;
            }
            catch (Exception error) { detail = "Interior observation refused: " + error.Message; return false; }
            finally
            {
                try
                {
                    if (_interiorOverlaps != null && !_interiorOverlaps.WasCollected)
                        for (int i = 0; i < Math.Min(overlapCount, _interiorOverlaps.Length); i++)
                            _interiorOverlaps[i] = null;
                }
                finally { _reading = false; }
            }
        }

        private void RequireInteriorManager()
        {
            var manager = SuperStructureManager._instance;
            if (!Alive(manager) || !manager.isActiveAndEnabled || SuperStructureManager._skipRoomCalculations
                || manager._allSuperStructures == null || manager._allSuperStructures.WasCollected)
                throw new InvalidOperationException("The native construction room producer is unavailable or disabled.");
            if (_interiorManager is null)
            {
                _interiorManager = manager;
                _interiorManagerId = manager.GetInstanceID();
            }
            else if (!Same(_interiorManager, manager) || manager.GetInstanceID() != _interiorManagerId)
                throw new InvalidOperationException("The construction room producer changed; retire and reprepare this actor reader.");
        }

        private bool LookupInteriorRoom(GameObject candidate, Vector3 position,
            out bool hasCeiling, out bool warmth)
        {
            hasCeiling = warmth = false;
            if (!Alive(candidate))
                throw new InvalidOperationException("The construction candidate retired during sampling.");
            if (!TryGetComponentExtensions.TryGetComponentInParent<Structure>(candidate, out var structure)) return false;
            if (!Alive(structure))
                throw new InvalidOperationException("The construction structure retired during sampling.");
            var superStructure = structure.SuperStructure;
            if (superStructure is null) return false;
            if (superStructure.WasCollected || !_interiorManager._allSuperStructures.Contains(superStructure)
                || superStructure._rooms == null || superStructure._rooms.WasCollected)
                throw new InvalidOperationException("The construction superstructure has no live registered room database.");
            // The native Vector3 overload performs the same CalcTileFromWorldPos + ordered
            // IsWithinAnyRoom lookup as ConstructionWarmthProbe's structure helper.
            if (!superStructure.IsWithinAnyRoom(position, out var room)) return false;
            if (room == null || room.WasCollected || room.SuperStructure?.Pointer != superStructure.Pointer)
                throw new InvalidOperationException("The native room result has a different construction lifetime.");
            hasCeiling = room.ClosedCeiling;
            warmth = hasCeiling && room.IsSealed;
            return true;
        }
    }
}
