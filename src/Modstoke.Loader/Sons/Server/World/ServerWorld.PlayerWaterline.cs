// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Multiplayer.Client;
using UnityEngine;
using Vector3 = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the remote animator controller's root position plus its native underwater
        /// offset along world up. This geometry analogue is not a head position, underwater/dry
        /// verdict or authoritative movement observation. Dedicated-server availability is not
        /// established; missing native presentation fails closed. Gameplay life is separate.
        /// </summary>
        public static bool TryReadPlayerWaterline(PlayerHandle binding,
            out PlayerWaterlineSnapshot snapshot, out CollisionReadStatus status)
        {
            snapshot = null;
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (binding == null)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (binding.IsLocalPlayer)
                { status = CollisionReadStatus.UnsupportedProfile; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!PlayerWaterlineObservation.TryLookup(binding, world, frame, out var observation, out status)
                    || !observation.IsCurrent(out status)) return false;
                snapshot = new PlayerWaterlineSnapshot(observation);
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
        /// Rechecks the exact binding, native chain, copied geometry and flags in the same
        /// world/frame. Reads and freshness checks each use one shared collision-read budget unit.
        /// Does not establish when replication or animation last updated the geometry.
        /// </summary>
        public static bool IsCurrentPlayerWaterline(PlayerWaterlineSnapshot snapshot,
            out CollisionReadStatus status)
        {
            bool entered = false;
            try
            {
                if (!TryCollisionContext(out var world, out int frame, out status)) return false;
                if (snapshot == null)
                { status = CollisionReadStatus.InvalidQuery; return false; }
                if (!ReserveCollisionRead(world, frame, out status)) return false;
                _collisionReading = entered = true;
                if (!ReferenceEquals(world, snapshot.Observation.World) || frame != snapshot.Frame)
                { status = CollisionReadStatus.StaleHit; return false; }
                return snapshot.Observation.IsCurrent(out status);
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
            finally { if (entered) _collisionReading = false; }
        }

        internal sealed class PlayerWaterlineObservation
        {
            private const int MaximumSetups = 32;
            private readonly Component[] _chain;
            private readonly IntPtr[] _pointers;
            private readonly int[] _instanceIds;
            private readonly int _offsetBits;

            private PlayerWaterlineObservation(PlayerHandle binding,
                WorldRecordContext world, int frame, Component[] chain,
                Vector3 rootPosition, float offset, Vector3 position, bool enabled)
            {
                Binding = binding;
                World = world;
                Frame = frame;
                _chain = chain;
                _pointers = new IntPtr[chain.Length];
                _instanceIds = new int[chain.Length];
                for (int i = 0; i < chain.Length; i++)
                {
                    _pointers[i] = chain[i].Pointer;
                    _instanceIds[i] = chain[i].GetInstanceID();
                }
                RootPosition = rootPosition;
                VerticalOffset = offset;
                _offsetBits = BitConverter.SingleToInt32Bits(offset);
                Position = position;
                ControllerEnabled = enabled;
            }

            internal PlayerHandle Binding { get; }
            internal WorldRecordContext World { get; }
            internal int Frame { get; }
            internal Vector3 RootPosition { get; }
            internal float VerticalOffset { get; }
            internal Vector3 Position { get; }
            internal bool ControllerEnabled { get; }

            internal static bool TryLookup(PlayerHandle binding,
                WorldRecordContext world, int frame, out PlayerWaterlineObservation observation,
                out CollisionReadStatus status)
            {
                observation = null;
                status = CollisionReadStatus.BindingChanged;
                if (!MatchesPlayerAttachment(binding, binding.Player)) return false;
                status = CollisionReadStatus.UnsupportedProfile;
                if (binding.IsLocalPlayer) return false;
                var setups = binding.Player.GetComponentsInChildren<CoopPlayerRemoteSetup>(true);
                if (setups == null || setups.WasCollected) return false;
                if (setups.Length > MaximumSetups)
                { status = CollisionReadStatus.LimitReached; return false; }
                CoopPlayerRemoteSetup setup = null;
                for (int i = 0; i < setups.Length; i++)
                {
                    var candidate = setups[i];
                    if (!LiveBallisticObject(candidate)) return false;
                    // The public entity getter can lazily bind; use only its existing cache.
                    if (!MatchesPlayerAttachment(binding, candidate._entity)) continue;
                    if (setup != null) return false;
                    setup = candidate;
                }
                if (!Active(setup)) return false;
                var animator = setup._playerAnimator;
                if (!Active(animator)) return false;
                // Native attachment resolves the controller on this exact Animator object.
                var controllers = animator.gameObject.GetComponents<global::PlayerAnimatorControl>();
                if (controllers == null || controllers.WasCollected || controllers.Length != 1) return false;
                var controller = controllers[0];
                if (!Active(controller) || !SameBallisticObject(controller._animator, animator)) return false;
                var transform = controller._transform;
                var root = controller.rootTr;
                // Observe Awake/Start's cached fields without running either initializer.
                if (!Active(transform) || !Active(root)
                    || !SameBallisticObject(transform, controller.transform)
                    || !SameBallisticObject(root, transform.root)) return false;
                var rootEntity = root.GetComponent<BoltEntity>();
                if (!Active(rootEntity) || !MatchesPlayerAttachment(binding, rootEntity))
                { status = CollisionReadStatus.PlayerMismatch; return false; }
                var nativePosition = root.position;
                float offset = controller._underWaterOffset;
                float y = nativePosition.y + offset;
                if (!Finite(nativePosition) || !float.IsFinite(offset) || !float.IsFinite(y))
                { status = CollisionReadStatus.InvalidQuery; return false; }
                Component[] chain = { setup, animator, controller, transform, root, rootEntity };
                observation = new PlayerWaterlineObservation(binding, world, frame, chain,
                    new Vector3(nativePosition.x, nativePosition.y, nativePosition.z), offset,
                    new Vector3(nativePosition.x, y, nativePosition.z), controller.enabled);
                status = CollisionReadStatus.Success;
                return true;
            }

            internal bool IsCurrent(out CollisionReadStatus status)
            {
                status = CollisionReadStatus.StaleHit;
                if (!CollisionContextCurrent(World, Frame)) return false;
                if (!TryValidatePlayerBinding(Binding, out var bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!TryLookup(Binding, World, Frame, out var current, out status)) return false;
                status = CollisionReadStatus.HitChanged;
                for (int i = 0; i < _chain.Length; i++)
                {
                    if (!Active(_chain[i]) || !SameBallisticObject(_chain[i], current._chain[i])
                        || _chain[i].Pointer != _pointers[i] || current._pointers[i] != _pointers[i]
                        || _chain[i].GetInstanceID() != _instanceIds[i]
                        || current._instanceIds[i] != _instanceIds[i]) return false;
                }
                if (_offsetBits != current._offsetBits || !RootPosition.Equals(current.RootPosition)
                    || !Position.Equals(current.Position) || ControllerEnabled != current.ControllerEnabled) return false;
                if (!TryValidatePlayerBinding(Binding, out bindingStatus))
                    return CollisionBindingFailed(bindingStatus, out status);
                if (!CollisionContextCurrent(World, Frame))
                { status = CollisionReadStatus.StaleHit; return false; }
                status = CollisionReadStatus.Success;
                return true;
            }

            private static bool Active(Component component)
            {
                if (!LiveBallisticObject(component) || component.Pointer == IntPtr.Zero) return false;
                var gameObject = component.gameObject;
                return LiveBallisticObject(gameObject) && gameObject.activeInHierarchy;
            }
        }
    }

    /// <summary>
    /// Same-frame geometry from one exact remote player's native animator root and offset.
    /// It is not a head location, water-state verdict or gameplay-life observation, and does
    /// not establish authoritative movement or availability on dedicated servers.
    /// </summary>
    public sealed class PlayerWaterlineSnapshot
    {
        internal PlayerWaterlineSnapshot(ServerWorld.PlayerWaterlineObservation observation)
            => Observation = observation;

        internal ServerWorld.PlayerWaterlineObservation Observation { get; }
        public PlayerHandle Binding => Observation.Binding;
        public int Frame => Observation.Frame;
        public Vector3 RootPosition => Observation.RootPosition;
        public float VerticalOffset => Observation.VerticalOffset;
        public Vector3 Position => Observation.Position;
        public bool ControllerEnabled => Observation.ControllerEnabled;
    }
}
