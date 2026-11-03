// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Modstoke.Sons
{
    [Flags]
    public enum VehiclePreservationBlockers
    {
        None = 0,
        WriterQuiescence = 1,
        NetworkTransformHistory = 2,
        SaveRegistryHandoff = 4,
        InboundReferences = 8,
        OccupantLifecycle = 16,
        LocalControllerLifecycle = 32,
        NativePhysicsHistory = 64,
        StorageInstancesAndCallbacks = 128,
        UnresolvedReference = 256,
        LocalConnectionObserved = 512,
        LocalConnectionUnavailable = 1024,
        OpaqueStorageToken = 2048,
        OpaqueSaveData = 4096
    }

    /// <summary>Raw identity is preserved even when Bolt cannot resolve the referenced object.</summary>
    public readonly record struct VehicleReference(ulong NetworkId, bool IsResolved);

    /// <summary>Observed network pose, not the NetworkTransform buffers or its binding graph.</summary>
    public readonly record struct VehicleNetworkPose(Vector3 Position, Quaternion Rotation);

    /// <summary>Exposed Rigidbody values only. Contacts, solver caches and WheelCollider state are not included.</summary>
    public sealed record VehicleBodyValues(
        Vector3 Position, Quaternion Rotation, Vector3 Velocity, Vector3 AngularVelocity,
        float Mass, float Drag, float AngularDrag, bool UseGravity, bool IsKinematic, bool DetectCollisions,
        int Constraints, int CollisionDetectionMode, int Interpolation,
        bool AutomaticCenterOfMass, Vector3 CenterOfMass, bool AutomaticInertiaTensor,
        Vector3 InertiaTensor, Quaternion InertiaTensorRotation,
        float MaxDepenetrationVelocity, float SleepThreshold, float MaxAngularVelocity, float MaxLinearVelocity,
        int SolverIterations, int SolverVelocityIterations, int IncludeLayers, int ExcludeLayers, bool IsSleeping);

    /// <summary>Save identity and known values; not a registry reservation or a transferable save record.</summary>
    public sealed record VehicleSaveIdentity(string ObjectNameId, int ItemId, int State,
        bool RuntimeCreated, bool SaveActive, bool IgnorePosition, bool IgnoreRotation,
        Vector3 SavedPosition, Vector3 SavedRotation, float? SavedCartPower, bool HasOpaqueData);

    public readonly record struct VehicleStorageSlot(int ItemId, int Count, bool AreUnique,
        int TokenCapacity, bool HasOpaqueToken);

    public readonly record struct GolfCartWheelValues(float FinalTorque, float Rpm,
        float AbsoluteRpm, float Traction, float AppliedTraction);

    /// <summary>Explicit controller fields, excluding timers, audio instances, callbacks, water sensors and lights.</summary>
    public sealed record GolfCartDriveValues(float CurrentPower, bool BoostInput, bool HandBrakeInput,
        float Acceleration, float AccelerationGoal, float AccelerationVelocity,
        float EngineLoad, float SmoothedEngineLoad, float BrakeTorque, float BrakingFactor,
        float MaxWheelRpm, float SteeringAngle, float Steering, float SteeringGoal, float SteeringVelocity,
        Vector3 BodyVelocity, Vector3 PreviousBodyVelocity, float BodyVelocitySquared,
        GolfCartWheelValues FrontLeft, GolfCartWheelValues FrontRight,
        GolfCartWheelValues BackLeft, GolfCartWheelValues BackRight);

    /// <summary>
    /// Immutable managed capture of named fields. Capture does not suspend writers, certify vacancy,
    /// or authorize retirement. No complete vanilla vehicle migration adapter is provided.
    /// </summary>
    public sealed class VehicleValues
    {
        internal VehicleValues(ulong id, int prefab, int serializer,
            VehicleBodyValues body, VehicleSaveIdentity save, int? structureSaveId,
            VehicleNetworkPose[] poses, VehicleReference[] references,
            VehiclePreservationBlockers blockers, GolfCartValues cart,
            LogSledValues sled)
        {
            NetworkId = id; PrefabId = prefab; StateTypeId = serializer;
            Body = body; WorldItemSave = save; StructureSaveId = structureSaveId;
            NetworkPoses = Array.AsReadOnly(poses); References = Array.AsReadOnly(references);
            Blockers = blockers; GolfCart = cart; LogSled = sled;
        }

        public ulong NetworkId { get; }
        public int PrefabId { get; }
        /// <summary>233 for a physical cart; 214 for the sled's linked mobile storage state. Not prefab IDs.</summary>
        public int StateTypeId { get; }
        public VehicleBodyValues Body { get; }
        public VehicleSaveIdentity WorldItemSave { get; }
        public int? StructureSaveId { get; }
        /// <summary>Cart: root, front left/right, back left/right. Sled: mobile root.</summary>
        public IReadOnlyList<VehicleNetworkPose> NetworkPoses { get; }
        /// <summary>Cart: exact four ConnectedSlots. Sled: TranslationController, then SupportingScrewStructures.</summary>
        public IReadOnlyList<VehicleReference> References { get; }
        public VehiclePreservationBlockers Blockers { get; }
        public bool CanMigrate => Blockers == VehiclePreservationBlockers.None;
        public GolfCartValues GolfCart { get; }
        public LogSledValues LogSled { get; }
    }

    public sealed class GolfCartValues
    {
        internal GolfCartValues(Vector3 velocity, float steering, float power,
            float[] rpms, float[] audio, GolfCartDriveValues drive)
        {
            Velocity = velocity; SteeringWheelRotation = steering; Power = power;
            WheelRpms = Array.AsReadOnly(rpms); AudioParameters = Array.AsReadOnly(audio); Drive = drive;
        }
        public Vector3 Velocity { get; }
        public float SteeringWheelRotation { get; }
        public float Power { get; }
        /// <summary>Front left/right, back left/right. Last value uses the native BackRightRpm property.</summary>
        public IReadOnlyList<float> WheelRpms { get; }
        public IReadOnlyList<float> AudioParameters { get; }
        public GolfCartDriveValues Drive { get; }
    }

    public sealed class LogSledValues
    {
        internal LogSledValues(bool gold, int connectors, bool active, Vector3[] vectors,
            VehicleStorageSlot[] slots, int[] availability)
        {
            GoldPlated = gold; ElectricityConnectorNum = connectors; IsActiveState = active;
            VectorBuffer = Array.AsReadOnly(vectors); StorageSlots = Array.AsReadOnly(slots);
            SlotAvailability = Array.AsReadOnly(availability);
        }
        public bool GoldPlated { get; }
        public int ElectricityConnectorNum { get; }
        public bool IsActiveState { get; }
        public IReadOnlyList<Vector3> VectorBuffer { get; }
        public IReadOnlyList<VehicleStorageSlot> StorageSlots { get; }
        public IReadOnlyList<int> SlotAvailability { get; }
    }
}
