// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Multiplayer;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerWorld
    {
        /// <summary>
        /// Reads selected replicated player fields for an exact live remote session.
        /// These replicated observations are not a complete inventory or authoritative health record.
        /// </summary>
        public static bool TryReadPlayerState(OpenACAISonsServerSession session,
            out OpenACAISonsPlayerStateSnapshot snapshot, out OpenACAISonsWorldReadStatus status)
        {
            snapshot = null;
            try
            {
                status = CheckContext();
                if (status != OpenACAISonsWorldReadStatus.Success)
                    return false;
                if (!OpenACAISonsServerSessions.HooksInstalled)
                {
                    status = OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }

                var connection = OpenACAISonsServerSessions.ResolveCurrent(session);
                if (connection == null)
                {
                    status = OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsWorldReadStatus.SessionUnavailable : OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }
                var player = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(connection);
                if (!MatchesPlayerSource(player, connection))
                {
                    status = OpenACAISonsWorldReadStatus.EntityUnavailable;
                    return false;
                }

                var networkId = player.networkId.PackedValue;
                var state = player.GetState<IPlayerState>();
                if (state is null || state.WasCollected)
                {
                    status = OpenACAISonsWorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var held = state.HeldItems;
                if (held is null || held.WasCollected)
                {
                    status = OpenACAISonsWorldReadStatus.PlayerStateUnavailable;
                    return false;
                }

                var itemIds = new int[held.Length];
                for (var index = 0; index < itemIds.Length; index++)
                    itemIds[index] = held[index];
                var rightHandItemId = state.RightHandHeldItemId;
                var heldOnlyItemId = state.HeldOnlyItemId;
                var heldOnlyItemCount = state.HeldOnlyItemCount;
                var dying = state.Dying;

                var current = OpenACAISonsServerSessions.ResolveCurrent(session);
                if (current == null || current.Pointer != connection.Pointer || current.ConnectionId != session.ConnectionId)
                {
                    status = OpenACAISonsServerSessions.HooksInstalled
                        ? OpenACAISonsWorldReadStatus.SessionUnavailable : OpenACAISonsWorldReadStatus.Unavailable;
                    return false;
                }
                var currentPlayer = MultiplayerUtilities.GetEntityFromBoltConnectionForServer(current);
                if (!MatchesPlayerSource(currentPlayer, current) || currentPlayer.Pointer != player.Pointer
                    || currentPlayer.networkId.PackedValue != networkId)
                {
                    status = OpenACAISonsWorldReadStatus.EntityUnavailable;
                    return false;
                }
                var currentState = currentPlayer.GetState<IPlayerState>();
                if (currentState is null || currentState.WasCollected || currentState.Pointer != state.Pointer)
                {
                    status = OpenACAISonsWorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var currentHeld = currentState.HeldItems;
                if (currentHeld is null || currentHeld.WasCollected || currentHeld.Pointer != held.Pointer
                    || currentHeld.Length != itemIds.Length)
                {
                    status = OpenACAISonsWorldReadStatus.PlayerStateUnavailable;
                    return false;
                }

                snapshot = new OpenACAISonsPlayerStateSnapshot(session, networkId, itemIds,
                    rightHandItemId, heldOnlyItemId, heldOnlyItemCount, dying);
                status = OpenACAISonsWorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = OpenACAISonsWorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        private static bool MatchesPlayerSource(BoltEntity player, BoltConnection connection)
        {
            if (player is null || player.WasCollected || !player || !player.isAttached)
                return false;
            var source = player.source;
            return source != null && source.Pointer == connection.Pointer && source.ConnectionId == connection.ConnectionId;
        }
    }

    /// <summary>Known values of the replicated Dying field, not independently verified player health.</summary>
    public enum OpenACAISonsPlayerLifeState
    {
        Alive = 0,
        Dying = 1,
        Dead = 2
    }

    /// <summary>Immutable managed copies of selected player-state fields. No native state or item instance is retained.</summary>
    public sealed class OpenACAISonsPlayerStateSnapshot
    {
        internal OpenACAISonsPlayerStateSnapshot(OpenACAISonsServerSession session, ulong networkId, int[] heldItemIds,
            int rightHandHeldItemId, int heldOnlyItemId, int heldOnlyItemCount, int rawDyingState)
        {
            Session = session;
            NetworkId = networkId;
            HeldItemIds = Array.AsReadOnly(heldItemIds);
            RightHandHeldItemId = rightHandHeldItemId;
            HeldOnlyItemId = heldOnlyItemId;
            HeldOnlyItemCount = heldOnlyItemCount;
            RawDyingState = rawDyingState;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The requested lifetime handle, which may close after this read.</summary>
        public OpenACAISonsServerSession Session { get; }
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
        public OpenACAISonsPlayerLifeState? LifeState => RawDyingState switch
        {
            0 => OpenACAISonsPlayerLifeState.Alive,
            1 => OpenACAISonsPlayerLifeState.Dying,
            2 => OpenACAISonsPlayerLifeState.Dead,
            _ => null
        };
        /// <summary>Read completion time, not a simulation, replication or last-change timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
