// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Actor-bound ceiling and solid-body sweeps for the server climb motor.
    /// </summary>
    public sealed class PlayerClimbCollision : IDisposable
    {
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private readonly PlayerBridge _bridge;
        private readonly PlayerCarrier _carrier;
        private readonly BoltEntity _entity;
        private readonly Transform _root, _actionOrigin;
        private readonly int _entityId, _originId, _scene, _ceilingLayers;
        private readonly Il2CppReferenceArray<Collider> _overlaps = new(32);
        private bool _disposed;

        private PlayerClimbCollision(PlayerBridge bridge,
            PlayerCarrier carrier, Transform actionOrigin, int ceilingLayers)
        {
            _bridge = bridge; _carrier = carrier; _entity = carrier.Entity;
            _root = _entity.transform; _actionOrigin = actionOrigin;
            _entityId = _entity.GetInstanceID(); _originId = actionOrigin.GetInstanceID();
            _scene = _entity.gameObject.scene.handle; _ceilingLayers = ceilingLayers;
        }

        /// <summary>
        /// Supply the server-owned equivalent of the native rope action's transform and its
        /// approved _ceilingsLayers mask. Do not substitute the body center or a client position.
        /// The caller owns physics synchronization and any special-mode motor handoff.
        /// </summary>
        public static bool TryCreate(PlayerBridge bridge, PlayerCarrier carrier,
            Transform actionOrigin, int serverCeilingLayers,
            out PlayerClimbCollision collision, out string detail)
        {
            collision = null;
            detail = "A current server carrier and its qualified native rope-action origin/layer mask are required.";
            if (!GameBuild.IsRecognized || !PlayerRoles.IsAvailable
                || bridge?.IsServer != true || carrier == null || !Alive(actionOrigin)
                || serverCeilingLayers == 0) return false;
            try
            {
                if (!carrier.IsCurrentForMotor) return false;
                var candidate = new PlayerClimbCollision(bridge, carrier, actionOrigin, serverCeilingLayers);
                if (!candidate.Current()) return false;
                bridge.Retired += candidate.Dispose;
                collision = candidate;
                detail = "Native rope ceiling geometry bound to the server actor, without LocalPlayer access.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb collision binding failed (" + error.GetType().Name + ")."; return false; }
        }

        /// <summary>
        /// False means unavailable, not clear. Queries the current server origin; never accepts a
        /// replicated client pose. The result is only valid at the observed pose/physics boundary.
        /// </summary>
        public bool TryCheckRopeCeiling(bool exitAtTop, out bool blocked, out Vector3 checkedOrigin,
            out string detail)
        {
            blocked = false; checkedOrigin = default;
            detail = "The server actor's climb collision binding is no longer current.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed) return false;
            try
            {
                if (!Current()) return false;
                checkedOrigin = _actionOrigin.position;
                if (!Finite(checkedOrigin)) return false;
                // Matched PlayerClimbRopeAction.CheckForCeiling, RVA 0x30A6CD0:
                // world up 1.1 (climb) / 1.4 (top exit), radius .2, Ignore triggers.
                int count = Physics.OverlapSphereNonAlloc(
                    checkedOrigin + Vector3.up * (exitAtTop ? 1.4f : 1.1f),
                    0.2f, _overlaps, _ceilingLayers, QueryTriggerInteraction.Ignore);
                if (count >= _overlaps.Length)
                { detail = "Ceiling overlap storage filled; clearance is unknown."; return false; }
                for (int i = 0; i < count; i++)
                {
                    var collider = _overlaps[i];
                    if (!Alive(collider)) return false;
                    if (!Same(collider.attachedRigidbody, _carrier.Body)
                        && !Same(collider.transform, _root) && !collider.transform.IsChildOf(_root))
                        blocked = true;
                }
                if (!Current() || _actionOrigin.position != checkedOrigin) return false;
                detail = blocked ? "Native rope ceiling volume is occupied." : "Native rope ceiling volume is clear.";
                return true;
            }
            catch (Exception error)
            { detail = "Climb collision observation failed (" + error.GetType().Name + ")."; return false; }
        }

        internal bool Matches(PlayerBridge bridge, PlayerCarrier carrier)
            => Environment.CurrentManagedThreadId == _thread && ReferenceEquals(bridge, _bridge)
                && ReferenceEquals(carrier, _carrier) && Current();

        /// <summary>Restrict an actor-owned animation delta, never a client-provided position.</summary>
        public bool TryConstrainRootMotion(Vector3 requested, bool rope, out Vector3 allowed,
            out bool blocked, out string detail)
        {
            allowed = default; blocked = false;
            detail = "A current dynamic server capsule and a finite root-motion delta are required.";
            if (Environment.CurrentManagedThreadId != _thread || _disposed || !Finite(requested)) return false;
            try
            {
                if (!Current() || !_carrier.IsPhysicsActive || !_carrier.IsCurrentForMotor) return false;
                var body = _carrier.Body;
                var start = body.position;
                allowed = requested;
                if (rope && requested.y > 0)
                {
                    if (!TryCheckRopeCeiling(false, out blocked, out _, out detail)) return false;
                    if (blocked) allowed.y = 0;
                }
                float distance = allowed.magnitude;
                if (!float.IsFinite(distance)) return false;
                if (distance > 0)
                {
                    Vector3 direction = allowed / distance;
                    float skin = _carrier.PrimaryCollider.contactOffset;
                    if (!float.IsFinite(skin) || skin < 0) return false;
                    if (body.SweepTest(direction, out var hit, distance + skin, QueryTriggerInteraction.Ignore))
                    {
                        if (!float.IsFinite(hit.distance) || hit.distance < 0) return false;
                        float travel = Mathf.Min(distance, Mathf.Max(0, hit.distance - skin));
                        blocked |= travel < distance;
                        allowed = direction * travel;
                    }
                }
                if (!Current() || !start.Equals(body.position)) return false;
                detail = blocked ? "Actor root motion constrained by a server collision." : "Actor root-motion sweep clear.";
                return true;
            }
            catch (Exception error)
            { detail = "Root-motion sweep failed (" + error.GetType().Name + ")."; return false; }
        }

        private bool Current() => !_disposed && !_carrier.IsDisposed && !_bridge.IsDisposed
            && PlayerRoles.IsAvailable && _bridge.IsServer
            && _bridge.TryReadCurrentTarget(out var entity) && Same(_entity, entity)
            && Same(_carrier.Entity, _entity) && _entity.GetInstanceID() == _entityId
            && PlayerRoles.IsCurrentCarrier(_entity) && Same(_entity.transform, _root)
            && Alive(_actionOrigin) && _actionOrigin.GetInstanceID() == _originId
            && (Same(_actionOrigin, _root) || _actionOrigin.IsChildOf(_root))
            && _entity.gameObject.activeInHierarchy && _actionOrigin.gameObject.activeInHierarchy
            && _entity.gameObject.scene.handle == _scene && _actionOrigin.gameObject.scene.handle == _scene
            && PhysicsSceneExtensions.GetPhysicsScene(_entity.gameObject.scene) == Physics.defaultPhysicsScene;

        public void Dispose()
        {
            if (Environment.CurrentManagedThreadId != _thread || _disposed) return;
            _disposed = true;
            _bridge.Retired -= Dispose;
        }

        private static bool Finite(Vector3 value)
            => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        private static bool Alive(UnityEngine.Object value)
            => value is not null && !value.WasCollected && value.Pointer != IntPtr.Zero && value != null;
        private static bool Same(UnityEngine.Object left, UnityEngine.Object right)
            => Alive(left) && Alive(right) && left.Pointer == right.Pointer
                && left.GetInstanceID() == right.GetInstanceID();
    }
}
