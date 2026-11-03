// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private static VehicleBodyValues CaptureBody(Rigidbody body) => new(
            V(body.position), Q(body.rotation), V(body.velocity), V(body.angularVelocity),
            Finite(body.mass), Finite(body.drag), Finite(body.angularDrag), body.useGravity,
            body.isKinematic, body.detectCollisions, (int)body.constraints, (int)body.collisionDetectionMode,
            (int)body.interpolation, body.automaticCenterOfMass, V(body.centerOfMass), body.automaticInertiaTensor,
            V(body.inertiaTensor), Q(body.inertiaTensorRotation), Finite(body.maxDepenetrationVelocity),
            Finite(body.sleepThreshold), Finite(body.maxAngularVelocity), Finite(body.maxLinearVelocity),
            body.solverIterations, body.solverVelocityIterations, body.includeLayers.value,
            body.excludeLayers.value, body.IsSleeping());

        private static void RestoreBody(Rigidbody body, VehicleBodyValues value)
        {
            // The caller keeps the hierarchy inactive. Dynamic mode is needed for velocity setters;
            // Unity ignores those setters on kinematic bodies. This does not advance the simulation.
            body.detectCollisions = false;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            body.isKinematic = false;
            body.mass = value.Mass;
            body.drag = value.Drag;
            body.angularDrag = value.AngularDrag;
            body.useGravity = value.UseGravity;
            body.constraints = (RigidbodyConstraints)value.Constraints;
            body.interpolation = (RigidbodyInterpolation)value.Interpolation;
            body.automaticCenterOfMass = false;
            body.centerOfMass = U(value.CenterOfMass);
            body.automaticCenterOfMass = value.AutomaticCenterOfMass;
            body.automaticInertiaTensor = false;
            body.inertiaTensor = U(value.InertiaTensor);
            body.inertiaTensorRotation = U(value.InertiaTensorRotation);
            body.automaticInertiaTensor = value.AutomaticInertiaTensor;
            body.maxDepenetrationVelocity = value.MaxDepenetrationVelocity;
            body.sleepThreshold = value.SleepThreshold;
            body.maxAngularVelocity = value.MaxAngularVelocity;
            body.maxLinearVelocity = value.MaxLinearVelocity;
            body.solverIterations = value.SolverIterations;
            body.solverVelocityIterations = value.SolverVelocityIterations;
            body.includeLayers = new LayerMask { value = value.IncludeLayers };
            body.excludeLayers = new LayerMask { value = value.ExcludeLayers };
            body.position = U(value.Position);
            body.rotation = U(value.Rotation);
            body.velocity = U(value.Velocity);
            body.angularVelocity = U(value.AngularVelocity);
            body.isKinematic = value.IsKinematic;
            body.collisionDetectionMode = (CollisionDetectionMode)value.CollisionDetectionMode;
            body.detectCollisions = value.DetectCollisions;
            if (value.IsSleeping) body.Sleep(); else body.WakeUp();
        }

        private static GolfCartDriveValues CaptureDrive(GolfCartController cart) => new(
            Finite(cart._currentPower), cart._boostInput, cart._handBrakeInput,
            Finite(cart._accelerationFactor), Finite(cart._accelerationFactorGoal), Finite(cart._accelerationFactorVel),
            Finite(cart._engineLoadFactor), Finite(cart._engineLoadFactorSmoothed), Finite(cart._brakeTorque),
            Finite(cart._brakingFactor), Finite(cart._maxWheelRpm), Finite(cart._steeringAngle),
            Finite(cart._steeringFactor), Finite(cart._steeringFactorGoal), Finite(cart._steeringFactorVel),
            V(cart._rigidBodyVelocity), V(cart._rigidbodyPreviousVelocity), Finite(cart._rigidBodyVelocitySqrMagnitude),
            new(Finite(cart._frontLeftFinalTorque), Finite(cart._frontLeftWheelRpm), Finite(cart._frontLeftWheelRpmAbs),
                Finite(cart._frontLeftWheelTraction), Finite(cart._frontLeftWheelTractionApplied)),
            new(Finite(cart._frontRightFinalTorque), Finite(cart._frontRightWheelRpm), Finite(cart._frontRightWheelRpmAbs),
                Finite(cart._frontRightWheelTraction), Finite(cart._frontRightWheelTractionApplied)),
            new(Finite(cart._rearLeftFinalTorque), Finite(cart._rearLeftWheelRpm), Finite(cart._rearLeftWheelRpmAbs),
                Finite(cart._rearLeftWheelTraction), Finite(cart._rearLeftWheelTractionApplied)),
            new(Finite(cart._rearRightFinalTorque), Finite(cart._rearRightWheelRpm), Finite(cart._rearRightWheelRpmAbs),
                Finite(cart._rearRightWheelTraction), Finite(cart._rearRightWheelTractionApplied)));

        private static void RestoreCart(GolfCartController cart, GolfCartState state, GolfCartValues value)
        {
            Require(state.AudioParams.Length == value.AudioParameters.Count, "Cart audio schema changed.");
            state.Velocity = U(value.Velocity);
            state.SteeringWheelRotation = value.SteeringWheelRotation;
            state.Power = value.Power;
            state.FrontLeftWheelRpm = value.WheelRpms[0];
            state.FrontRightWheelRpm = value.WheelRpms[1];
            state.BackLeftWheelRpm = value.WheelRpms[2];
            state.BackRightRpm = value.WheelRpms[3];
            for (int i = 0; i < value.AudioParameters.Count; i++) state.AudioParams[i] = value.AudioParameters[i];

            var drive = value.Drive;
            cart._currentPower = drive.CurrentPower;
            cart._boostInput = drive.BoostInput;
            cart._handBrakeInput = drive.HandBrakeInput;
            cart._accelerationFactor = drive.Acceleration;
            cart._accelerationFactorGoal = drive.AccelerationGoal;
            cart._accelerationFactorVel = drive.AccelerationVelocity;
            cart._engineLoadFactor = drive.EngineLoad;
            cart._engineLoadFactorSmoothed = drive.SmoothedEngineLoad;
            cart._brakeTorque = drive.BrakeTorque;
            cart._brakingFactor = drive.BrakingFactor;
            cart._maxWheelRpm = drive.MaxWheelRpm;
            cart._steeringAngle = drive.SteeringAngle;
            cart._steeringFactor = drive.Steering;
            cart._steeringFactorGoal = drive.SteeringGoal;
            cart._steeringFactorVel = drive.SteeringVelocity;
            cart._rigidBodyVelocity = U(drive.BodyVelocity);
            cart._rigidbodyPreviousVelocity = U(drive.PreviousBodyVelocity);
            cart._rigidBodyVelocitySqrMagnitude = drive.BodyVelocitySquared;
            cart._frontLeftFinalTorque = drive.FrontLeft.FinalTorque;
            cart._frontLeftWheelRpm = drive.FrontLeft.Rpm;
            cart._frontLeftWheelRpmAbs = drive.FrontLeft.AbsoluteRpm;
            cart._frontLeftWheelTraction = drive.FrontLeft.Traction;
            cart._frontLeftWheelTractionApplied = drive.FrontLeft.AppliedTraction;
            cart._frontRightFinalTorque = drive.FrontRight.FinalTorque;
            cart._frontRightWheelRpm = drive.FrontRight.Rpm;
            cart._frontRightWheelRpmAbs = drive.FrontRight.AbsoluteRpm;
            cart._frontRightWheelTraction = drive.FrontRight.Traction;
            cart._frontRightWheelTractionApplied = drive.FrontRight.AppliedTraction;
            cart._rearLeftFinalTorque = drive.BackLeft.FinalTorque;
            cart._rearLeftWheelRpm = drive.BackLeft.Rpm;
            cart._rearLeftWheelRpmAbs = drive.BackLeft.AbsoluteRpm;
            cart._rearLeftWheelTraction = drive.BackLeft.Traction;
            cart._rearLeftWheelTractionApplied = drive.BackLeft.AppliedTraction;
            cart._rearRightFinalTorque = drive.BackRight.FinalTorque;
            cart._rearRightWheelRpm = drive.BackRight.Rpm;
            cart._rearRightWheelRpmAbs = drive.BackRight.AbsoluteRpm;
            cart._rearRightWheelTraction = drive.BackRight.Traction;
            cart._rearRightWheelTractionApplied = drive.BackRight.AppliedTraction;
        }

        private static void ValidateRestoreShape(Binding binding, VehicleValues values)
        {
            if (binding.Cart != null)
            {
                Require(binding.CartState.AudioParams.Length == values.GolfCart.AudioParameters.Count, "Cart audio schema changed.");
                return;
            }
            var state = binding.SledState;
            var value = values.LogSled;
            Require(state.VectorBuffer.Length == value.VectorBuffer.Count && state.StorageSlots.Length == value.StorageSlots.Count
                && state.SlotAvailability.Length == value.SlotAvailability.Count, "Mobile storage schema changed.");
            // Validate every token destination before writing any slot. Unknown/unique instances
            // cannot be reconstructed from counts and must never be silently discarded.
            for (int i = 0; i < value.StorageSlots.Count; i++)
            {
                var slot = state.StorageSlots[i];
                var wanted = value.StorageSlots[i];
                Require(!wanted.AreUnique && !wanted.HasOpaqueToken && !slot.AreUnique
                    && slot.ItemInstances.Length == wanted.TokenCapacity, "Unique/opaque storage requires an item-instance adapter.");
                for (int j = 0; j < slot.ItemInstances.Length; j++)
                    Require(slot.ItemInstances[j] == null, "A storage token appeared before restoration.");
            }
        }

        private static void RestoreSled(ScrewStructureWithStorageMobileState state, LogSledValues value)
        {
            state.GoldPlated = value.GoldPlated;
            state.ElectricityConnectorNum = value.ElectricityConnectorNum;
            state.IsActiveState = value.IsActiveState;
            for (int i = 0; i < value.VectorBuffer.Count; i++) state.VectorBuffer[i] = U(value.VectorBuffer[i]);
            for (int i = 0; i < value.StorageSlots.Count; i++)
            {
                var slot = state.StorageSlots[i];
                var wanted = value.StorageSlots[i];
                slot.ItemId = wanted.ItemId;
                slot.Count = wanted.Count;
                slot.AreUnique = wanted.AreUnique;
            }
            for (int i = 0; i < value.SlotAvailability.Count; i++) state.SlotAvailability[i] = value.SlotAvailability[i];
        }
    }
}
