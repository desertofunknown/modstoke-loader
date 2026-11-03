// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerCarrierProfile
    {
        public const int EncodedLength = 420;
        private const uint WireMagic = 0x5043414f;
        private const ushort WireVersion = 3;

        public bool TryEncode(Span<byte> destination, out int written)
        {
            written = 0;
            if (destination.Length < EncodedLength || !ValidateProfile(this, out _)) return false;
            var w = new ProfileWriter(destination.Slice(0, EncodedLength));
            w.U32(WireMagic); w.U16(WireVersion);
            w.U16((ushort)((Crouched ? 1 : 0) | (Grounded ? 2 : 0) | (Body.UseGravity ? 4 : 0)
                | (Material.Present ? 8 : 0) | (Body.IsKinematic ? 16 : 0) | (Body.DetectCollisions ? 32 : 0)
                | (Geometry.Enabled ? 64 : 0) | (Geometry.IsTrigger ? 128 : 0)));
            w.U64(SourceNetworkId); w.I32(PrefabId); w.I32(StateTypeId); w.I32(Layer);
            var m = Motion;
            w.F32(m.WalkSpeed); w.F32(m.RunSpeed); w.F32(m.CrouchSpeed); w.F32(m.StrafeSpeed);
            w.F32(m.BackwardSpeedMultiplier); w.F32(m.Gravity); w.F32(m.MaximumVelocityChange);
            w.F32(m.MaximumVelocity); w.F32(m.JumpHeight); w.F32(m.JumpMultiplier);
            w.F32(m.FixedDeltaTime); w.F32(m.DefaultMass);
            var g = Geometry;
            w.F32(g.Radius); w.F32(g.Height); w.V3(g.Center); w.F32(g.StandingHeight); w.V3(g.StandingCenter);
            w.F32(g.CrouchingHeight); w.F32(g.CrouchingCenterY); w.V3(g.LocalPosition);
            w.Q4(g.LocalRotation); w.F32(g.ContactOffset);
            var b = Body;
            w.F32(b.Mass); w.F32(b.Drag); w.F32(b.AngularDrag); w.F32(b.MaximumDepenetrationVelocity);
            w.F32(b.MaximumLinearVelocity); w.F32(b.MaximumAngularVelocity); w.F32(b.SleepThreshold);
            w.I32((int)b.Constraints); w.I32((int)b.CollisionDetection); w.I32((int)b.Interpolation);
            w.I32(b.SolverIterations); w.I32(b.SolverVelocityIterations);
            w.F32(Contacts.HeightOffset); w.F32(Contacts.GroundAngleMaximumConstruction); w.I32(Contacts.SmoothedLayerMask);
            w.F32(Material.StaticFriction); w.F32(Material.DynamicFriction); w.F32(Material.Bounciness);
            w.I32((int)Material.FrictionCombine); w.I32((int)Material.BounceCombine);
            w.V3(Velocity); w.V3(AngularVelocity);
            w.I32(CapsuleLayer);
            var o = Observations;
            w.F32(o.CurrentSpeed); w.F32(o.ClampInput); w.F32(o.OverrideClampInput); w.F32(o.GroundStableForce);
            w.F32(o.ExtremeAngleStart); w.F32(o.ExtremeAngleMaximum); w.F32(o.TerrainExtremeAngleStart);
            w.F32(o.TerrainExtremeAngleMaximum); w.F32(o.OverallSpeed); w.F32(o.WalkToRunThreshold);
            w.F32(o.RunCostPerSecond); w.F32(o.TimeToRecoverFromRun); w.F32(o.RecoveryRemaining); w.F32(o.StaminaMinimumToRun);
            w.U32((uint)((o.CanJump ? 1 : 0) | (o.AllowJump ? 2 : 0) | (o.CanRun ? 4 : 0) | (o.Locked ? 8 : 0)
                | (o.MovementLocked ? 16 : 0) | (o.JumpInputBlocked ? 32 : 0) | (o.OnSlipperySlope ? 64 : 0)
                | (o.DisableGravity ? 128 : 0) | (o.ForceStopRun ? 256 : 0) | (o.DoingClampInput ? 512 : 0)
                | (o.IsJumping ? 1024 : 0) | (o.TerrainColliderContact ? 2048 : 0)));
            w.F32(ZiplineConnectedHeightOffset);
            w.I32(Interaction.TargetLayer);
            w.V3(Interaction.FirstBox.Center); w.V3(Interaction.FirstBox.HalfExtents); w.Q4(Interaction.FirstBox.Rotation);
            w.V3(Interaction.SecondBox.Center); w.V3(Interaction.SecondBox.HalfExtents); w.Q4(Interaction.SecondBox.Rotation);
            w.V3(Interaction.DistancePivot);
            written = w.Offset;
            return written == EncodedLength;
        }

        public static bool TryDecode(ReadOnlySpan<byte> payload, out PlayerCarrierProfile profile,
            out string detail)
        {
            profile = null;
            detail = "Unknown or non-exact native movement profile encoding.";
            if (payload.Length != EncodedLength) return false;
            var r = new ProfileReader(payload);
            if (r.U32() != WireMagic || r.U16() != WireVersion) return false;
            ushort flags = r.U16();
            if ((flags & ~255) != 0) return false;
            ulong id = r.U64();
            int prefab = r.I32(), state = r.I32(), layer = r.I32();
            var motion = new PlayerCarrierMotionProfile(r.F32(), r.F32(), r.F32(), r.F32(),
                r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32(), r.F32());
            var geometry = new PlayerCarrierGeometryProfile(r.F32(), r.F32(), r.V3(),
                r.F32(), r.V3(), r.F32(), r.F32(), r.V3(), r.Q4(), r.F32(), (flags & 64) != 0, (flags & 128) != 0);
            var body = new PlayerCarrierBodyProfile(r.F32(), r.F32(), r.F32(), r.F32(),
                r.F32(), r.F32(), r.F32(), (RigidbodyConstraints)r.I32(), (CollisionDetectionMode)r.I32(),
                (RigidbodyInterpolation)r.I32(), r.I32(), r.I32(), (flags & 4) != 0, (flags & 16) != 0, (flags & 32) != 0);
            var contacts = new PlayerCarrierContactProfile(r.F32(), r.F32(), r.I32());
            var material = new PlayerCarrierMaterialProfile((flags & 8) != 0,
                r.F32(), r.F32(), r.F32(), (PhysicMaterialCombine)r.I32(), (PhysicMaterialCombine)r.I32());
            var velocity = r.V3();
            var angularVelocity = r.V3();
            int capsuleLayer = r.I32();
            float speed = r.F32(), clamp = r.F32(), overrideClamp = r.F32(), stableForce = r.F32();
            float angleStart = r.F32(), angleMax = r.F32(), terrainStart = r.F32(), terrainMax = r.F32();
            float overallSpeed = r.F32(), runThreshold = r.F32(), runCost = r.F32(), recoverTime = r.F32();
            float recoverRemaining = r.F32(), staminaMinimum = r.F32();
            uint observationsFlags = r.U32();
            if ((observationsFlags & ~4095u) != 0) return false;
            var observations = new PlayerCarrierObservations(speed, clamp, overrideClamp, stableForce,
                angleStart, angleMax, terrainStart, terrainMax, overallSpeed, runThreshold, runCost, recoverTime,
                recoverRemaining, staminaMinimum, (observationsFlags & 1) != 0, (observationsFlags & 2) != 0,
                (observationsFlags & 4) != 0, (observationsFlags & 8) != 0, (observationsFlags & 16) != 0,
                (observationsFlags & 32) != 0, (observationsFlags & 64) != 0, (observationsFlags & 128) != 0,
                (observationsFlags & 256) != 0, (observationsFlags & 512) != 0, (observationsFlags & 1024) != 0,
                (observationsFlags & 2048) != 0);
            float ziplineConnectedHeightOffset = r.F32();
            var interaction = new PlayerInteractionProfile(r.I32(),
                new(r.V3(), r.V3(), r.Q4()), new(r.V3(), r.V3(), r.Q4()), r.V3());
            var candidate = new PlayerCarrierProfile(id, prefab, state, layer, capsuleLayer, motion, geometry,
                body, material, contacts, velocity, angularVelocity, (flags & 1) != 0, (flags & 2) != 0,
                observations, ziplineConnectedHeightOffset, interaction);
            if (r.Offset != EncodedLength || !ValidateProfile(candidate, out detail)) return false;
            profile = candidate;
            return true;
        }

        private ref struct ProfileWriter
        {
            private readonly Span<byte> _bytes;
            internal int Offset;
            internal ProfileWriter(Span<byte> bytes) { _bytes = bytes; Offset = 0; }
            internal void U16(ushort value) { BinaryPrimitives.WriteUInt16LittleEndian(_bytes.Slice(Offset, 2), value); Offset += 2; }
            internal void U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(_bytes.Slice(Offset, 4), value); Offset += 4; }
            internal void I32(int value) => U32(unchecked((uint)value));
            internal void U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(_bytes.Slice(Offset, 8), value); Offset += 8; }
            internal void F32(float value) => I32(BitConverter.SingleToInt32Bits(value));
            internal void V3(Vector3 value) { F32(value.x); F32(value.y); F32(value.z); }
            internal void Q4(Quaternion value) { F32(value.x); F32(value.y); F32(value.z); F32(value.w); }
        }

        private ref struct ProfileReader
        {
            private readonly ReadOnlySpan<byte> _bytes;
            internal int Offset;
            internal ProfileReader(ReadOnlySpan<byte> bytes) { _bytes = bytes; Offset = 0; }
            internal ushort U16() { ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.Slice(Offset, 2)); Offset += 2; return value; }
            internal uint U32() { uint value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(Offset, 4)); Offset += 4; return value; }
            internal int I32() => unchecked((int)U32());
            internal ulong U64() { ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_bytes.Slice(Offset, 8)); Offset += 8; return value; }
            internal float F32() => BitConverter.Int32BitsToSingle(I32());
            internal Vector3 V3() => new(F32(), F32(), F32());
            internal Quaternion Q4() => new(F32(), F32(), F32(), F32());
        }
    }
}
