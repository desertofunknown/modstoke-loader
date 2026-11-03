// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;

namespace Modstoke.Sons.Combat
{
    public readonly struct StrictCombatRespawnRequest
    {
        public StrictCombatRespawnRequest(StrictCombatBinding binding, Guid requestId, ulong lifeGeneration)
        {
            Binding = binding;
            RequestId = requestId;
            LifeGeneration = lifeGeneration;
        }

        public StrictCombatBinding Binding { get; }
        public Guid RequestId { get; }
        public ulong LifeGeneration { get; }
        public bool IsValid => StrictCombatProtocol.IsValidBinding(Binding) && RequestId != Guid.Empty
            && LifeGeneration != 0 && LifeGeneration < ulong.MaxValue;

        public bool SameRequest(in StrictCombatRespawnRequest other)
        {
            return Binding.SameBinding(other.Binding)
                && RequestId == other.RequestId && LifeGeneration == other.LifeGeneration;
        }
    }

    public readonly struct StrictCombatRespawnOffer
    {
        public StrictCombatRespawnOffer(StrictCombatRespawnRequest request, Guid operationId,
            Vector3 position, Quaternion rotation)
        {
            Request = request;
            OperationId = operationId;
            Position = position;
            Rotation = rotation;
        }

        public StrictCombatRespawnRequest Request { get; }
        public Guid OperationId { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public bool IsValid
        {
            get
            {
                double lengthSquared = (double)Rotation.X * Rotation.X + (double)Rotation.Y * Rotation.Y
                    + (double)Rotation.Z * Rotation.Z + (double)Rotation.W * Rotation.W;
                return Request.IsValid && OperationId != Guid.Empty
                    && float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z)
                    && double.IsFinite(lengthSquared) && Math.Abs(lengthSquared - 1) <= 0.001;
            }
        }

        public bool SameOffer(in StrictCombatRespawnOffer other)
        {
            return Request.SameRequest(other.Request) && OperationId == other.OperationId
                && SameBits(Position.X, other.Position.X) && SameBits(Position.Y, other.Position.Y)
                && SameBits(Position.Z, other.Position.Z) && SameBits(Rotation.X, other.Rotation.X)
                && SameBits(Rotation.Y, other.Rotation.Y) && SameBits(Rotation.Z, other.Rotation.Z)
                && SameBits(Rotation.W, other.Rotation.W);
        }

        private static bool SameBits(float left, float right)
            => BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);
    }

    public enum StrictCombatRespawnPlacement : byte
    {
        Placed = 1, Rejected = 2, Uncertain = 3
    }

    /// <summary>A client placement claim, not authoritative physical proof.</summary>
    public readonly struct StrictCombatRespawnReport
    {
        public StrictCombatRespawnReport(StrictCombatRespawnRequest request, Guid operationId,
            StrictCombatRespawnPlacement placement)
        {
            Request = request;
            OperationId = operationId;
            Placement = placement;
        }

        public StrictCombatRespawnRequest Request { get; }
        public Guid OperationId { get; }
        public StrictCombatRespawnPlacement Placement { get; }
        public bool IsValid => Request.IsValid && OperationId != Guid.Empty
            && Placement >= StrictCombatRespawnPlacement.Placed && Placement <= StrictCombatRespawnPlacement.Uncertain;
    }

    public enum StrictCombatRespawnResult : byte
    {
        Committed = 1, Rejected = 2, Uncertain = 3
    }

    public readonly struct StrictCombatRespawnOutcome
    {
        public StrictCombatRespawnOutcome(StrictCombatRespawnRequest request, Guid operationId,
            StrictCombatRespawnResult result, StrictCombatSnapshot state)
        {
            Request = request;
            OperationId = operationId;
            Result = result;
            State = state;
        }

        public StrictCombatRespawnRequest Request { get; }
        public Guid OperationId { get; }
        public StrictCombatRespawnResult Result { get; }
        public StrictCombatSnapshot State { get; }
        public bool IsValid
        {
            get
            {
                if (!Request.IsValid || OperationId == Guid.Empty
                    || Result < StrictCombatRespawnResult.Committed || Result > StrictCombatRespawnResult.Uncertain)
                    return false;
                var receipt = new StrictCombatReceipt(Request.Binding.Epoch, 1, StrictCombatAction.ReadState,
                    StrictCombatResult.Accepted, 0, State);
                if (!StrictCombatProtocol.IsValidReceipt(in receipt))
                    return false;
                if (Result == StrictCombatRespawnResult.Committed)
                    return State.LifeStage == StrictCombatLifeStage.Alive
                        && State.LifeGeneration == Request.LifeGeneration + 1 && !State.Equipped
                        && State.ReloadRounds == 0 && State.ReloadRemainingMs == 0 && State.Health > 0;
                return State.LifeStage == StrictCombatLifeStage.Dead && State.LifeGeneration == Request.LifeGeneration;
            }
        }
    }
}
