// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Reads selected replicated player fields for an exact live remote session.
        /// These replicated observations are not a complete inventory or authoritative health record.
        /// </summary>
        public static bool TryReadPlayerState(ServerSession session,
            out PlayerStateSnapshot snapshot, out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != WorldReadStatus.Success)
                    return false;
                if (!ServerSessions.HooksInstalled)
                {
                    status = WorldReadStatus.Unavailable;
                    return false;
                }

                var connection = ServerSessions.ResolveCurrent(session);
                if (connection == null)
                {
                    status = ServerSessions.HooksInstalled
                        ? WorldReadStatus.SessionUnavailable : WorldReadStatus.Unavailable;
                    return false;
                }
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (!MatchesPlayerSession(player, connection, session))
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }

                var networkId = player.networkId.PackedValue;
                var state = player.GetState<IPlayerState>();
                if (state is null || state.WasCollected)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var held = state.HeldItems;
                if (held is null || held.WasCollected)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }

                var itemIds = new int[held.Length];
                for (var index = 0; index < itemIds.Length; index++)
                    itemIds[index] = held[index];
                var rightHandItemId = state.RightHandHeldItemId;
                var heldOnlyItemId = state.HeldOnlyItemId;
                var heldOnlyItemCount = state.HeldOnlyItemCount;
                var dying = state.Dying;
                var armour = CopyPlayerStateArmour(state, out var armourCount, out var armourIds);
                var goldArmorPlated = state.IsGoldArmorPlated;
                var rawView = state.CurrentView;
                var rawTiedUpState = state.TiedUpState;

                var current = ServerSessions.ResolveCurrent(session);
                if (connection.WasCollected || current == null || current.WasCollected
                    || current.Pointer != connection.Pointer || current.ConnectionId != session.ConnectionId)
                {
                    status = ServerSessions.HooksInstalled
                        ? WorldReadStatus.SessionUnavailable : WorldReadStatus.Unavailable;
                    return false;
                }
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (player.WasCollected || !player || !MatchesPlayerSession(currentPlayer, current, session)
                    || currentPlayer.Pointer != player.Pointer
                    || currentPlayer.networkId.PackedValue != networkId)
                {
                    status = WorldReadStatus.EntityUnavailable;
                    return false;
                }
                var currentState = currentPlayer.GetState<IPlayerState>();
                if (state.WasCollected || currentState is null || currentState.WasCollected
                    || currentState.Pointer != state.Pointer)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var currentHeld = currentState.HeldItems;
                if (held.WasCollected || currentHeld is null || currentHeld.WasCollected || currentHeld.Pointer != held.Pointer
                    || currentHeld.Length != itemIds.Length)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                // Armour is optional: a changed or missing array drops only the armour fields.
                if (armour != null && !SamePlayerStateArmour(currentState, armour, armourCount))
                {
                    armour = null;
                    armourCount = 0;
                    armourIds = Array.Empty<int>();
                }

                snapshot = new PlayerStateSnapshot(session, networkId, itemIds,
                    rightHandItemId, heldOnlyItemId, heldOnlyItemCount, dying,
                    armour != null, armourCount, armourIds, goldArmorPlated, rawView, rawTiedUpState);
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static Bolt.NetworkArray_Integer CopyPlayerStateArmour(IPlayerState state,
            out int count, out int[] ids)
        {
            count = 0;
            ids = Array.Empty<int>();
            try
            {
                var armour = state.PlayerArmourIds;
                if (armour is null || armour.WasCollected)
                    return null;
                var length = armour.Length;
                if (length < 0)
                    return null;
                var copied = length == 0 ? Array.Empty<int>()
                    : new int[Math.Min(length, MaximumPlayerPresentationEntries)];
                for (var index = 0; index < copied.Length; index++)
                    copied[index] = armour[index];
                count = length;
                ids = copied;
                return armour;
            }
            catch (Exception)
            {
                return null;
            }
        }

        internal static PlayerView? KnownPlayerView(int value)
            => value is >= (int)PlayerView.PlaneCrash and <= (int)PlayerView.EndDeactivate ? (PlayerView)value : null;

        private static bool SamePlayerStateArmour(IPlayerState state, Bolt.NetworkArray_Integer armour, int count)
        {
            try
            {
                var current = state.PlayerArmourIds;
                return !armour.WasCollected && current is not null && !current.WasCollected
                    && current.Pointer == armour.Pointer && current.Length == count;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Copies selected native player-state movement and activity fields for one exact local
        /// or remote attachment on the server game thread. Requires a current world context.
        /// Obtain the binding with TryCaptureLocalPlayerBinding for a listen host or
        /// TryCapturePlayerBinding for a remote session; dedicated servers have no local player.
        /// These are presentation observations, not trusted velocity, water coverage or action readiness.
        /// Success does not establish when replication last updated the fields or an atomic simulation sample.
        /// </summary>
        public static bool TryReadPlayerMovement(PlayerHandle binding,
            out PlayerMovementSnapshot snapshot, out PlayerMovementReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryValidatePlayerBinding(binding, out var bindingStatus))
                {
                    status = PlayerMovementStatus(bindingStatus);
                    return false;
                }
                if (!WorldRecords.TryGetCurrent(out var world, out _))
                {
                    status = PlayerMovementReadStatus.WorldUnavailable;
                    return false;
                }
                var frame = UnityEngine.Time.frameCount;

                var state = binding.Player.GetState<IPlayerState>();
                if (state is null || state.WasCollected)
                {
                    status = PlayerMovementReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var horizontalSpeed = state.hSpeed;
                var verticalSpeed = state.vSpeed;
                var overallSpeed = state.overallSpeed;
                var crouch = state.crouch;
                var climbSpeed = state.climbSpeed;
                var inWaterBlend = state.inWaterBlend;
                var inWater = state.inWater;
                var caveMask = state.caveMask;
                var drivingGolfCart = state.DrivingGolfCart;
                var passengerGolfCart = state.PassengerGolfCart;
                var onRaft = state.onRaft;
                var zipLineAttached = state.zipLineAttach;
                var inMidAction = state.IsInMidAction;
                var inMenus = state.InMenus;

                if (!TryValidatePlayerBinding(binding, out bindingStatus))
                {
                    status = PlayerMovementStatus(bindingStatus);
                    return false;
                }
                var currentState = binding.Player.GetState<IPlayerState>();
                if (state.WasCollected || currentState is null || currentState.WasCollected
                    || currentState.Pointer != state.Pointer)
                {
                    status = PlayerMovementReadStatus.PlayerStateUnavailable;
                    return false;
                }
                if (!world.IsCurrent || UnityEngine.Time.frameCount != frame)
                {
                    status = PlayerMovementReadStatus.ContextChanged;
                    return false;
                }
                if (!float.IsFinite(horizontalSpeed) || !float.IsFinite(verticalSpeed)
                    || !float.IsFinite(overallSpeed) || !float.IsFinite(crouch)
                    || !float.IsFinite(climbSpeed) || !float.IsFinite(inWaterBlend))
                {
                    status = PlayerMovementReadStatus.NonFiniteValues;
                    return false;
                }

                snapshot = new PlayerMovementSnapshot(binding, world, frame,
                    horizontalSpeed, verticalSpeed, overallSpeed, crouch, climbSpeed, inWaterBlend,
                    inWater, zipLineAttached, inMidAction, inMenus,
                    caveMask, drivingGolfCart, passengerGolfCart, onRaft);
                status = PlayerMovementReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                status = PlayerMovementReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static PlayerMovementReadStatus PlayerMovementStatus(WorldReadStatus status) => status switch
        {
            WorldReadStatus.Success => PlayerMovementReadStatus.Success,
            WorldReadStatus.WrongThread => PlayerMovementReadStatus.WrongThread,
            WorldReadStatus.NotServer => PlayerMovementReadStatus.NotServer,
            WorldReadStatus.BindingChanged => PlayerMovementReadStatus.BindingChanged,
            WorldReadStatus.Failed => PlayerMovementReadStatus.Failed,
            _ => PlayerMovementReadStatus.Unavailable
        };

        private static bool MatchesPlayerSource(BoltEntity player, BoltConnection connection)
        {
            if (connection is null || connection.WasCollected
                || player is null || player.WasCollected || !player || !player.isAttached)
                return false;
            var source = player.source;
            return source != null && !source.WasCollected
                && source.Pointer == connection.Pointer && source.ConnectionId == connection.ConnectionId;
        }
    }

    public enum PlayerMovementReadStatus
    {
        Unavailable,
        Success,
        WrongThread,
        NotServer,
        WorldUnavailable,
        BindingChanged,
        PlayerStateUnavailable,
        ContextChanged,
        /// <summary>At least one native numeric field was NaN or infinite. No partial snapshot is returned.</summary>
        NonFiniteValues,
        Failed
    }

    /// <summary>
    /// Immutable copies of native movement/activity fields. Remote values may be client-controlled
    /// and initial or stale; local values remain presentation state, not physics or vitals authority.
    /// No health, grounded/swimming verdict or permission to act is inferred from these fields.
    /// </summary>
    public sealed class PlayerMovementSnapshot
    {
        internal PlayerMovementSnapshot(PlayerHandle binding,
            WorldRecordContext world, int frame, float horizontalSpeed, float verticalSpeed,
            float overallSpeed, float crouch, float climbSpeed, float inWaterBlend,
            bool inWater, bool zipLineAttached, bool inMidAction, bool inMenus,
            int caveMask, bool drivingGolfCart, bool passengerGolfCart, bool onRaft)
        {
            Binding = binding;
            World = world;
            Frame = frame;
            RawHorizontalSpeed = horizontalSpeed;
            RawVerticalSpeed = verticalSpeed;
            RawOverallSpeed = overallSpeed;
            RawCrouch = crouch;
            RawClimbSpeed = climbSpeed;
            RawInWaterBlend = inWaterBlend;
            InWater = inWater;
            ZipLineAttached = zipLineAttached;
            IsInMidAction = inMidAction;
            InMenus = inMenus;
            RawCaveMask = caveMask;
            DrivingGolfCart = drivingGolfCart;
            PassengerGolfCart = passengerGolfCart;
            OnRaft = onRaft;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact input attachment, including its remote session or local-player identity. May retire after the read.</summary>
        public PlayerHandle Binding { get; }
        /// <summary>The exact world lifetime at the read, not just a reusable save ID.</summary>
        public WorldRecordContext World { get; }
        /// <summary>Unity frame of the read, not a remote simulation or last-replication frame.</summary>
        public int Frame { get; }
        /// <summary>Raw IPlayerState.hSpeed. No world-space axis, units or physical speed is inferred.</summary>
        public float RawHorizontalSpeed { get; }
        /// <summary>Raw IPlayerState.vSpeed. Not an observed vertical world velocity.</summary>
        public float RawVerticalSpeed { get; }
        /// <summary>Raw IPlayerState.overallSpeed, not a computed magnitude or trusted travel speed.</summary>
        public float RawOverallSpeed { get; }
        /// <summary>Raw IPlayerState.crouch, copied without clamping or a crouched threshold.</summary>
        public float RawCrouch { get; }
        /// <summary>Raw IPlayerState.climbSpeed, not proof of climbing or a measured displacement.</summary>
        public float RawClimbSpeed { get; }
        /// <summary>Raw IPlayerState.inWaterBlend, copied without clamping or inferring immersion.</summary>
        public float RawInWaterBlend { get; }
        /// <summary>Raw IPlayerState.inWater. False is not evidence of dry geometry or breathable air.</summary>
        public bool InWater { get; }
        /// <summary>Raw IPlayerState.zipLineAttach, not independent proof of a zip-line attachment.</summary>
        public bool ZipLineAttached { get; }
        /// <summary>Raw IPlayerState.IsInMidAction. False does not establish action readiness.</summary>
        public bool IsInMidAction { get; }
        /// <summary>Raw IPlayerState.InMenus. False does not establish input availability.</summary>
        public bool InMenus { get; }
        /// <summary>Raw IPlayerState.caveMask. Nonzero is the game's in-cave marker; the bit layout is not decoded.</summary>
        public int RawCaveMask { get; }
        /// <summary>Raw IPlayerState.DrivingGolfCart, not proof of a live vehicle seat.</summary>
        public bool DrivingGolfCart { get; }
        /// <summary>Raw IPlayerState.PassengerGolfCart, not proof of a live vehicle seat.</summary>
        public bool PassengerGolfCart { get; }
        /// <summary>Raw IPlayerState.onRaft, not proof that the raft is afloat.</summary>
        public bool OnRaft { get; }
        /// <summary>UTC read completion time, not a simulation, replication or last-change timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }

    /// <summary>Known values of the replicated Dying field, not independently verified player health.</summary>
    public enum PlayerLifeState
    {
        Alive = 0,
        Dying = 1,
        Dead = 2
    }

    /// <summary>
    /// Known values of the replicated CurrentView field, matching the game's PlayerInventory.PlayerViews.
    /// A view is presentation state written by the owning client, not a verified activity.
    /// </summary>
    public enum PlayerView
    {
        PlaneCrash = -1,
        Loading = 0,
        WakingUp = 1,
        World = 2,
        Inventory = 3,
        ClosingInventory = 4,
        GrabBag = 5,
        PauseObsolete = 6,
        Death = 7,
        Loot = 8,
        Sleep = 9,
        Hidden = 10,
        PlayerList = 11,
        EndDeactivate = 12
    }

    /// <summary>Immutable managed copies of selected player-state fields. No native state or item instance is retained.</summary>
    public sealed class PlayerStateSnapshot
    {
        internal PlayerStateSnapshot(ServerSession session, ulong networkId, int[] heldItemIds,
            int rightHandHeldItemId, int heldOnlyItemId, int heldOnlyItemCount, int rawDyingState,
            bool armourAvailable, int armourEntryCount, int[] armourIds, bool isGoldArmorPlated,
            int rawCurrentView, int rawTiedUpState)
        {
            Session = session;
            NetworkId = networkId;
            HeldItemIds = Array.AsReadOnly(heldItemIds);
            RightHandHeldItemId = rightHandHeldItemId;
            HeldOnlyItemId = heldOnlyItemId;
            HeldOnlyItemCount = heldOnlyItemCount;
            RawDyingState = rawDyingState;
            ArmourAvailable = armourAvailable;
            ArmourEntryCount = armourEntryCount;
            PlayerArmourIds = Array.AsReadOnly(armourIds);
            IsGoldArmorPlated = isGoldArmorPlated;
            RawCurrentView = rawCurrentView;
            RawTiedUpState = rawTiedUpState;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The requested lifetime handle, which may close after this read.</summary>
        public ServerSession Session { get; }
        /// <summary>Current-world player entity ID, not a persistent player or item identity.</summary>
        public ulong NetworkId { get; }
        /// <summary>Raw native entry order including zeros. Entries are not fixed hand slots, quantities or item instances.</summary>
        public IReadOnlyList<int> HeldItemIds { get; }
        /// <summary>Separate replicated right-hand item field; not inferred from HeldItemIds.</summary>
        public int RightHandHeldItemId { get; }
        /// <summary>Separate held-only controller item field, not a replacement for the right-hand item field.</summary>
        public int HeldOnlyItemId { get; }
        /// <summary>Replicated held-only presentation count, not a full inventory count.</summary>
        public int HeldOnlyItemCount { get; }
        public int RawDyingState { get; }
        /// <summary>Null for an unknown raw value; no unknown state is treated as Alive.</summary>
        public PlayerLifeState? LifeState => RawDyingState switch
        {
            0 => PlayerLifeState.Alive,
            1 => PlayerLifeState.Dying,
            2 => PlayerLifeState.Dead,
            _ => null
        };
        /// <summary>
        /// False when the native armour array was null, collected or changed during the read; the armour fields
        /// are then empty and the rest of the snapshot is still valid.
        /// </summary>
        public bool ArmourAvailable { get; }
        /// <summary>Observed native armour-array length, including empty entries, not a protection or item count.</summary>
        public int ArmourEntryCount { get; }
        /// <summary>
        /// Raw client-written armour IDs in native order, zeros and duplicates kept, bounded to
        /// ServerWorld.MaximumPlayerPresentationEntries. Not inventory ownership, durability, absorption or
        /// proof that damage reduction ran.
        /// </summary>
        public IReadOnlyList<int> PlayerArmourIds { get; }
        public bool ArmourTruncated => PlayerArmourIds.Count < ArmourEntryCount;
        /// <summary>Raw client-written IPlayerState.IsGoldArmorPlated; not armour protection, durability or ownership.</summary>
        public bool IsGoldArmorPlated { get; }
        /// <summary>
        /// Raw client-written IPlayerState.CurrentView. Not proof that the player is sleeping, dead, loading or
        /// unable to take damage; a modified client can hold any view.
        /// </summary>
        public int RawCurrentView { get; }
        /// <summary>Null for a value outside the known set; unknown codes stay in RawCurrentView.</summary>
        public PlayerView? KnownView => ServerWorld.KnownPlayerView(RawCurrentView);
        /// <summary>
        /// Raw client-written IPlayerState.TiedUpState. The encoding is not established, and 0 is not known to
        /// mean free. Not proof of a cutscene, restraint or suppressed damage.
        /// </summary>
        public int RawTiedUpState { get; }
        /// <summary>Read completion time, not a simulation, replication or last-change timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
