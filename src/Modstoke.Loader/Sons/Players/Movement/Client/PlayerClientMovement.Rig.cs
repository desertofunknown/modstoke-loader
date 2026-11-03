// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using TheForest.Utils;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Modstoke.Sons
{
    public sealed partial class PlayerClientMovement
    {
        /// <summary>
        /// Capture before Detach, alongside the migration's rig-cache capture. This token retains
        /// object lifetimes only; it does not claim to preserve FPC history or authorize migration.
        /// </summary>
        public static bool TryCaptureRig(PlayerRigBindings bindings, out RetainedRig rig, out string detail)
        {
            rig = null;
            detail = "Capture the attached local player's exact rig on the player-role game thread.";
            if (!PlayerRoles.IsAvailable || bindings == null) return false;
            try
            {
                var original = bindings.OriginalEntity;
                if (!Alive(original) || !original.isAttached || !original.isOwner
                    || original.networkId.PackedValue != bindings.OriginalNetworkId
                    || !Same(original, LocalPlayer.Entity) || !Same(bindings.Root, LocalPlayer.GameObject)
                    || !Same(original.gameObject, bindings.Root)) return false;
                var candidate = new RetainedRig(bindings);
                if (!candidate.MatchesObjects(true)) return false;
                rig = candidate;
                detail = "Exact local controller, body, controls, cameras and collider lifetimes captured without mutation.";
                return true;
            }
            catch (Exception error)
            { detail = "Retained rig capture rejected (" + error.GetType().Name + ")."; return false; }
        }

        public sealed class RetainedRig
        {
            internal readonly FirstPersonCharacter Character;
            internal readonly PlayerAnimatorControl Animation;
            internal readonly Rigidbody Body;
            internal readonly Transform CameraTransform;
            internal readonly GameObject Root;
            internal readonly BoltEntity Original;
            internal readonly ulong OriginalNetworkId;
            internal readonly Collider[] Colliders;
            internal bool Consumed;
            private readonly int _thread = Environment.CurrentManagedThreadId;
            private readonly List<RigSlot> _slots = new();
            private readonly Il2CppSystem.Collections.Generic.List<Rigidbody> _bodyBuffer = new(1);
            private readonly Il2CppSystem.Collections.Generic.List<Collider> _colliderBuffer = new(16);
            private readonly Transform _rootTransform;
            private readonly int _scene;
            private readonly int _layer;
            private readonly RigidbodyConstraints _constraints;
            private readonly RigidbodyInterpolation _interpolation;
            private readonly CollisionDetectionMode _collisionMode;

            internal RetainedRig(PlayerRigBindings bindings)
            {
                Root = bindings.Root;
                Original = bindings.OriginalEntity;
                OriginalNetworkId = bindings.OriginalNetworkId;
                Character = LocalPlayer.FpCharacter;
                Animation = LocalPlayer.AnimControl;
                Body = LocalPlayer.Rigidbody;
                CameraTransform = LocalPlayer.MainCamTr;
                _rootTransform = Root.transform;
                _scene = Root.scene.handle;
                _layer = Root.layer;
                _constraints = Body.constraints;
                _interpolation = Body.interpolation;
                _collisionMode = Body.collisionDetectionMode;
                Add(() => Root);
                Add(() => Original);
                Add(() => LocalPlayer._instance);
                Add(() => LocalPlayer.GameObject);
                Add(() => LocalPlayer.Transform);
                Add(() => LocalPlayer.PlayerBase);
                Add(() => LocalPlayer.FpCharacter);
                Add(() => LocalPlayer.Rigidbody);
                Add(() => Character._rigidbody);
                Add(() => Character._primaryCollider);
                Add(() => LocalPlayer.MainCam);
                Add(() => LocalPlayer.MainCamTr);
                Add(() => LocalPlayer.InventoryCam);
                Add(() => LocalPlayer.Inventory);
                Add(() => LocalPlayer.Stats);
                Add(() => LocalPlayer.Animator);
                Add(() => LocalPlayer.AnimControl);
                Add(() => LocalPlayer.CamRotator);
                Add(() => LocalPlayer.MainRotator);
                Add(() => LocalPlayer.ScriptSetup);
                var colliders = Root.GetComponentsInChildren<Collider>(true);
                if (colliders.Length == 0 || colliders.Length > 16)
                    throw new InvalidOperationException("Unsupported retained collider count.");
                Colliders = new Collider[colliders.Length];
                for (int i = 0; i < colliders.Length; i++)
                {
                    var collider = colliders[i];
                    Colliders[i] = collider;
                    Add(() => collider);
                }
            }

            private void Add(Func<UnityObject> read) => _slots.Add(new RigSlot(read));

            internal bool Matches(BoltEntity replacement, bool validateTopology = false, bool nativeMode = false)
                => MatchesObjects(validateTopology, nativeMode) && MatchesReplacement(replacement);

            internal bool MatchesLife(BoltEntity replacement, PlayerBridge bridge,
                PlayerLifePresentation owner)
                => owner != null && !owner.IsDisposed && owner.FailureReason == null && owner.BlocksMovement
                && owner.TryValidateRig(bridge, Character, out _)
                && MatchesObjects(true, false, owner) && MatchesReplacement(replacement);

            private bool MatchesReplacement(BoltEntity replacement)
                => Alive(replacement) && !Same(replacement, Original) && !Same(replacement.gameObject, Root)
                && Same(LocalPlayer.Entity, replacement) && Same(LocalPlayer.State, replacement.GetState<IPlayerState>())
                && !Original.isAttached && Original._entity == null
                && BoltNetwork.FindEntity(new Bolt.NetworkId(OriginalNetworkId)) == null
                && BoltCore.FindEntity(new Bolt.NetworkId(OriginalNetworkId)) == null;

            internal bool MatchesObjects(bool validateTopology = false, bool nativeMode = false)
                => MatchesObjects(validateTopology, nativeMode, null);

            private bool MatchesObjects(bool validateTopology, bool nativeMode, PlayerLifePresentation life)
            {
                if (Environment.CurrentManagedThreadId != _thread) return false;
                foreach (var slot in _slots) if (!slot.Matches(life == null)) return false;
                if (!Same(Body.gameObject, Root) || !Character.transform.IsChildOf(_rootTransform)
                    || !Same(Body, Character._rigidbody) || !Same(Root.transform, _rootTransform)
                    || (_rootTransform.parent != null && (!nativeMode || !IsRetainedZiplineParent(_rootTransform, _rootTransform.parent)))
                    || !UnitScale(_rootTransform.lossyScale)
                    || !Root.activeInHierarchy || Root.scene.handle != _scene || Root.layer != _layer
                    || (!nativeMode && ((!Character.enabled && life == null) || Body.isKinematic || !Body.detectCollisions
                        || Body.constraints != _constraints || Body.interpolation != _interpolation
                        || Body.collisionDetectionMode != _collisionMode))
                    || PhysicsSceneExtensions.GetPhysicsScene(Root.scene) != Physics.defaultPhysicsScene
                    || !Physics.autoSimulation) return false;
                if (!validateTopology) return true;
                Root.GetComponentsInChildren<Rigidbody>(true, _bodyBuffer);
                if (_bodyBuffer.Count != 1 || !Same(_bodyBuffer[0], Body)) return false;
                Root.GetComponentsInChildren<Collider>(true, _colliderBuffer);
                if (_colliderBuffer.Count != Colliders.Length) return false;
                for (int i = 0; i < _colliderBuffer.Count; i++)
                {
                    var collider = _colliderBuffer[i];
                    if (!Same(collider, Colliders[i]) || !Same(collider.attachedRigidbody, Body)) return false;
                }
                return true;
            }

            internal bool GroundMode() => !Character.Locked && !Character._movementLocked && !Character._sitting
                && !Character._isAnimControllerSwimming && !Character._disableGravity && !Character._hasOverrideMovement
                && !Character._isInMidAction && !Character._blockFullBodyInteraction
                && (!Character._doingExitVelocity || IsRetainedZiplineExit(Character))
                && !Character.standingOnRaft && !Character.StandingOnDynamicObject && !Character.onSlipperySlope
                && Character._primaryCollider.enabled && !Character._primaryCollider.isTrigger
                && Upright(Body.rotation * Vector3.up);

            internal bool SwimMode() => !Character.Locked && !Character._movementLocked && !Character._sitting
                && Character._isAnimControllerSwimming && Animation.IsSwimming
                && !Character._hasOverrideMovement && !Character._isInMidAction
                && !Character._blockFullBodyInteraction
                && (!Character._doingExitVelocity || IsRetainedZiplineExit(Character))
                && !Character.standingOnRaft && !Character.StandingOnDynamicObject
                && Character._primaryCollider.enabled && !Character._primaryCollider.isTrigger
                && Upright(Body.rotation * Vector3.up);

            internal PlayerMotionFlags ReadMotionFlags()
            {
                var flags = (Character._isGrounded ? PlayerMotionFlags.Grounded : 0)
                    | (Character._IsCrouchActive_k__BackingField ? PlayerMotionFlags.Crouched : 0);
                if (Character._isAnimControllerSwimming)
                {
                    flags |= PlayerMotionFlags.Swimming;
                    if (Animation.IsUnderwater) flags |= PlayerMotionFlags.Underwater;
                }
                return flags;
            }
        }

        private sealed class RigSlot
        {
            private readonly Func<UnityObject> _read;
            private readonly UnityObject _value;
            private readonly int _instance;
            private readonly GameObject _owner;
            private readonly Transform _transform;
            private readonly Transform _parent;

            internal RigSlot(Func<UnityObject> read)
            {
                _read = read;
                _value = read();
                if (!Alive(_value)) throw new InvalidOperationException("A retained rig object is unavailable.");
                _instance = _value.GetInstanceID();
                _owner = _value.TryCast<GameObject>() ?? _value.TryCast<Component>()?.gameObject;
                if (!Alive(_owner)) throw new InvalidOperationException("A retained rig owner is unavailable.");
                _transform = _owner.transform;
                _parent = _transform.parent;
            }

            internal bool Matches(bool allowZiplineParent = true) => Alive(_value) && _value.GetInstanceID() == _instance && Same(_read(), _value)
                && Alive(_owner) && Alive(_transform) && Same(_owner.transform, _transform)
                && (Same(_transform.parent, _parent)
                    || (allowZiplineParent && _parent == null && IsRetainedZiplineParent(_transform, _transform.parent)))
                && Same(_value.TryCast<GameObject>() ?? _value.TryCast<Component>()?.gameObject, _owner);
        }

        private static bool Live(Il2CppObjectBase value) => value != null && !value.WasCollected && value.Pointer != IntPtr.Zero;
        private static bool Alive(UnityObject value) => Live(value) && value;
        private static bool Same(Il2CppObjectBase left, Il2CppObjectBase right)
            => left == null ? right == null : Live(left) && Live(right) && left.Pointer == right.Pointer;
        private static bool UnitScale(Vector3 v) => MathF.Abs(v.x - 1) < 0.0001f
            && MathF.Abs(v.y - 1) < 0.0001f && MathF.Abs(v.z - 1) < 0.0001f;
    }
}
