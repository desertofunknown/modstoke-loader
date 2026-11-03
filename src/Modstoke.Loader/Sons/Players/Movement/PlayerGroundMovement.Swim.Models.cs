// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>
    /// Authoritative actor configuration, captured/approved by the migration adapter,
    /// never supplied by an input packet. Body values are the actual Rigidbody values;
    /// ContactOffset/Material are the capsule's values. The last three fields are from
    /// RigidBodyCollisionFlags. Capsule dimensions come only from the ground motor.
    /// The server body has frozen rotation, no interpolation and stays awake for contacts.
    /// No local-player globals, default masses or guessed buoyancy are substituted.
    /// </summary>
    public readonly record struct PlayerSwimBodyProfile(
        float Mass, float Drag, float AngularDrag, bool UseGravity,
        CollisionDetectionMode CollisionDetection, int SolverIterations,
        int SolverVelocityIterations, float MaximumDepenetrationVelocity, float MaximumLinearVelocity,
        float ContactOffset, PhysicMaterial Material, float ContactHeightOffset,
        float GroundAngleMaximumConstruction, int SmoothedLayerMask);
}
