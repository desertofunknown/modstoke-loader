// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Weapon;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        internal sealed class DamageNodeObservation : DamageNodeFields
        {
            private DamageNodeObservation(CollisionHit hit, MeleeImpactRequest meleeRequest,
                WorldRecordContext world, int frame, PlayerHandle binding, DamageNode node)
                : base(node, meleeRequest != null)
            {
                Hit = hit;
                MeleeRequest = meleeRequest;
                World = world;
                Binding = binding;
                Frame = frame;
            }

            internal CollisionHit Hit { get; }
            internal MeleeImpactRequest MeleeRequest { get; }
            internal WorldRecordContext World { get; }
            internal PlayerHandle Binding { get; }
            internal int Frame { get; }

            internal static bool TryCapture(CollisionHit hit, PlayerHandle binding,
                DamageNode node, out DamageNodeObservation observation, out CollisionReadStatus status)
            {
                observation = null;
                status = CollisionReadStatus.ReceiverUnsupported;
                if (node is null) return false;
                status = CollisionReadStatus.ReceiverUnavailable;
                if (!LiveBallisticObject(node) || !LiveBallisticObject(node.gameObject)) return false;
                status = CollisionReadStatus.InvalidDamageNode;
                if (hit == null || binding == null) return false;
                var candidate = new DamageNodeObservation(hit, null, hit.World, hit.Frame, binding, node);
                if (!candidate.Valid()) return false;
                observation = candidate;
                status = CollisionReadStatus.Success;
                return true;
            }

            internal static bool TryCaptureMelee(MeleeImpactRequest request,
                PlayerHandle binding, DamageNode node, out DamageNodeObservation observation,
                out CollisionReadStatus status)
            {
                observation = null;
                status = CollisionReadStatus.ReceiverUnsupported;
                if (node is null) return false;
                status = CollisionReadStatus.ReceiverUnavailable;
                if (!LiveBallisticObject(node) || !LiveBallisticObject(node.gameObject)) return false;
                status = CollisionReadStatus.InvalidDamageNode;
                if (request == null || binding == null) return false;
                var candidate = new DamageNodeObservation(null, request, request.World, request.Frame, binding, node);
                if (!candidate.Valid()) return false;
                observation = candidate;
                status = CollisionReadStatus.Success;
                return true;
            }

            internal bool IsCurrent(DamageNode node)
            {
                if (!MatchesNode(node)) return false;
                var current = new DamageNodeObservation(Hit, MeleeRequest, World, Frame, Binding, node);
                return current.Valid() && Matches(current);
            }

            internal bool HasSameNodeIdentity(DamageNodeObservation other)
                => other != null && ReferenceEquals(Binding, other.Binding)
                    && ReferenceEquals(World, other.World) && SameNode(other);

            internal bool Matches(DamageNodeObservation other)
                => other != null && ReferenceEquals(Hit, other.Hit) && ReferenceEquals(MeleeRequest, other.MeleeRequest)
                    && ReferenceEquals(World, other.World) && ReferenceEquals(Binding, other.Binding)
                    && Frame == other.Frame && MatchesFields(other);
        }

        /// <summary>Copied fields of one damage node, independent of the hit, player or actor that reached it.</summary>
        internal class DamageNodeFields
        {
            private readonly UnityReference _node, _gameObject, _parent, _child;
            private readonly NativeReference _validation, _break, _block, _dismember, _teamType;

            internal DamageNodeFields(DamageNode node, bool includeMeleeSettings)
            {
                _node = new UnityReference(node);
                var gameObject = node.gameObject;
                _gameObject = new UnityReference(gameObject);
                _parent = new UnityReference(node._parentDamageNode);
                _child = new UnityReference(node._childDamageNode);
                _validation = new NativeReference(node._impactValidateOverrideCallback);
                _break = new NativeReference(node._breakReceivers);
                _block = new NativeReference(node._blockReceivers);
                _dismember = new NativeReference(node._dismemberImpactReceivers);
                _teamType = new NativeReference(node._teamStimuliType);
                ProjectileMultiplier = node._damageMultiplierProjectile;
                if (includeMeleeSettings)
                    MeleeSettings = new MeleeNodeSettings(node._damageMultiplierBlunt,
                        node._damageMultiplierSharp, node._damageMultiplierBlocking);
                AbsorptionRemaining = node._damageAbsorptionAmount;
                UseBreak = node._useBreak;
                BreakAmount = node._breakAmount;
                AppliedNodeDamage = node._currentAppliedNodeDamage;
                TakeDamageAfterBreak = node._takeDamageAfterBreak;
                UpdateRemainingDamage = node._updateImpactDamage;
                IsBroken = node._isBroken;
                IsEnabled = node.enabled;
                ActiveInHierarchy = gameObject.activeInHierarchy;
                IsDodging = node._isDodging;
                IsParrying = node._isParrying;
                IsArmour = node._isArmour;
                CanBlock = node._canBlock;
                IsBlocking = node._isBlocking;
                VerifyBlockDirection = node._verifyBlockDirection;
                UsesTeamDamage = node._useTeamDamage;
                TeamDamageMultiplier = node._teamDamageMultiplier;
                LastImpactId = node._lastImpactId;
                LastImpactTime = node._lastImpactTime;
                IsBaseDamageNode = node.GetIl2CppType().Pointer == Il2CppType.Of<DamageNode>().Pointer;
            }

            internal int NodeInstanceId => _node.InstanceId;
            internal float ProjectileMultiplier { get; }
            internal MeleeNodeSettings MeleeSettings { get; }
            internal float AbsorptionRemaining { get; }
            internal bool UseBreak { get; }
            internal float BreakAmount { get; }
            internal float AppliedNodeDamage { get; }
            internal bool TakeDamageAfterBreak { get; }
            internal bool UpdateRemainingDamage { get; }
            internal bool IsBroken { get; }
            internal bool IsEnabled { get; }
            internal bool ActiveInHierarchy { get; }
            internal bool IsDodging { get; }
            internal bool IsParrying { get; }
            internal bool IsArmour { get; }
            internal bool CanBlock { get; }
            internal bool IsBlocking { get; }
            internal bool VerifyBlockDirection { get; }
            internal bool UsesTeamDamage { get; }
            internal float TeamDamageMultiplier { get; }
            internal bool HasParent => _parent.Present;
            internal bool HasChild => _child.Present;
            internal bool HasValidationOverride => _validation.Present;
            internal bool HasBreakCallback => _break.Present;
            internal bool HasBlockCallback => _block.Present;
            internal bool HasDismemberCallback => _dismember.Present;
            internal bool HasTeamStimuliType => _teamType.Present;
            internal int LastImpactId { get; }
            internal float LastImpactTime { get; }
            internal bool IsBaseDamageNode { get; }

            internal bool Valid()
                => _node.Present && _gameObject.Present && _node.Valid && _gameObject.Valid
                    && _parent.Valid && _child.Valid && _validation.Valid && _break.Valid && _block.Valid
                    && _dismember.Valid && _teamType.Valid
                    && float.IsFinite(ProjectileMultiplier) && float.IsFinite(AbsorptionRemaining)
                    && float.IsFinite(BreakAmount) && float.IsFinite(AppliedNodeDamage)
                    && float.IsFinite(TeamDamageMultiplier) && float.IsFinite(LastImpactTime)
                    && (MeleeSettings == null || (float.IsFinite(MeleeSettings.BluntMultiplier)
                        && float.IsFinite(MeleeSettings.SharpMultiplier)
                        && float.IsFinite(MeleeSettings.BlockingMultiplier)));

            internal bool MatchesNode(DamageNode node)
                => _node.Matches(node) && _gameObject.Matches(node.gameObject);

            internal bool SameNode(DamageNodeFields other)
                => _node.Present && _gameObject.Present
                    && _node.Matches(other._node) && _gameObject.Matches(other._gameObject);

            internal bool MatchesFields(DamageNodeFields other)
                => other != null && _node.Matches(other._node) && _gameObject.Matches(other._gameObject)
                    && _parent.Matches(other._parent) && _child.Matches(other._child)
                    && _validation.Matches(other._validation) && _break.Matches(other._break)
                    && _block.Matches(other._block) && _dismember.Matches(other._dismember)
                    && _teamType.Matches(other._teamType)
                    && SameBits(ProjectileMultiplier, other.ProjectileMultiplier)
                    && SameMeleeSettings(MeleeSettings, other.MeleeSettings)
                    && SameBits(AbsorptionRemaining, other.AbsorptionRemaining)
                    && UseBreak == other.UseBreak && SameBits(BreakAmount, other.BreakAmount)
                    && SameBits(AppliedNodeDamage, other.AppliedNodeDamage)
                    && TakeDamageAfterBreak == other.TakeDamageAfterBreak
                    && UpdateRemainingDamage == other.UpdateRemainingDamage && IsBroken == other.IsBroken
                    && IsEnabled == other.IsEnabled && ActiveInHierarchy == other.ActiveInHierarchy
                    && IsDodging == other.IsDodging && IsParrying == other.IsParrying && IsArmour == other.IsArmour
                    && CanBlock == other.CanBlock && IsBlocking == other.IsBlocking
                    && VerifyBlockDirection == other.VerifyBlockDirection && UsesTeamDamage == other.UsesTeamDamage
                    && SameBits(TeamDamageMultiplier, other.TeamDamageMultiplier)
                    && LastImpactId == other.LastImpactId && SameBits(LastImpactTime, other.LastImpactTime)
                    && IsBaseDamageNode == other.IsBaseDamageNode;

            private static bool SameBits(float left, float right)
                => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

            private static bool SameMeleeSettings(MeleeNodeSettings left, MeleeNodeSettings right)
                => left == null ? right == null : right != null
                    && SameBits(left.BluntMultiplier, right.BluntMultiplier)
                    && SameBits(left.SharpMultiplier, right.SharpMultiplier)
                    && SameBits(left.BlockingMultiplier, right.BlockingMultiplier);

            private readonly struct UnityReference
            {
                private readonly UnityEngine.Object _value;
                private readonly IntPtr _pointer;
                internal int InstanceId { get; }
                internal bool Present => _value is not null;
                internal bool Valid => !Present || (LiveBallisticObject(_value) && _pointer != IntPtr.Zero
                    && _value.Pointer == _pointer && _value.GetInstanceID() == InstanceId);

                internal UnityReference(UnityEngine.Object value)
                {
                    _value = value;
                    bool live = LiveBallisticObject(value);
                    _pointer = live ? value.Pointer : IntPtr.Zero;
                    InstanceId = live ? value.GetInstanceID() : 0;
                }

                internal bool Matches(UnityEngine.Object current)
                    => Valid && (!Present ? current is null : LiveBallisticObject(current)
                        && current.Pointer == _pointer && current.GetInstanceID() == InstanceId);

                internal bool Matches(UnityReference other)
                    => Valid && other.Valid && Present == other.Present
                        && _pointer == other._pointer && InstanceId == other.InstanceId;
            }

            private readonly struct NativeReference
            {
                private readonly Il2CppObjectBase _value;
                private readonly IntPtr _pointer;
                internal bool Present => _value is not null;
                internal bool Valid => !Present || (!_value.WasCollected && _pointer != IntPtr.Zero
                    && _value.Pointer == _pointer);

                internal NativeReference(Il2CppObjectBase value)
                {
                    _value = value;
                    _pointer = value is not null && !value.WasCollected ? value.Pointer : IntPtr.Zero;
                }

                internal bool Matches(NativeReference other)
                    => Valid && other.Valid && Present == other.Present && _pointer == other._pointer;
            }
        }
    }
}
