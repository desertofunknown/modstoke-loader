// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Reads the native Dying, CurrentView and TiedUpState fields and entity state for an exact retained
        /// local or remote player attachment on the server game thread. This observation is not health
        /// authority, a life-generation token, or proof of safe arrival at a destination.
        /// </summary>
        public static bool TryReadPlayerLife(PlayerHandle binding,
            out PlayerLifeSnapshot snapshot, out WorldReadStatus status)
        {
            snapshot = null;
            try
            {
                if (!TryValidatePlayerBinding(binding, out status))
                    return false;

                var state = binding.Player.GetState<IPlayerState>();
                if (state is null || state.WasCollected)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }
                var dying = state.Dying;
                var rawView = state.CurrentView;
                var rawTiedUpState = state.TiedUpState;

                if (!TryReadPlayerBinding(binding, out var entity, out status))
                    return false;
                var currentState = binding.Player.GetState<IPlayerState>();
                if (state.WasCollected || currentState is null || currentState.WasCollected
                    || currentState.Pointer != state.Pointer)
                {
                    status = WorldReadStatus.PlayerStateUnavailable;
                    return false;
                }

                snapshot = new PlayerLifeSnapshot(binding, entity, dying, rawView, rawTiedUpState);
                status = WorldReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                RetireEndedPlayerBinding(binding);
                status = WorldReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }
    }

    /// <summary>
    /// Immutable observation of one attachment's native Dying, CurrentView and TiedUpState fields. Neither
    /// the observation nor its binding establishes health authority, a life generation, or safe arrival.
    /// </summary>
    public sealed class PlayerLifeSnapshot
    {
        internal PlayerLifeSnapshot(PlayerHandle binding, EntitySnapshot entity, int rawDyingState,
            int rawCurrentView, int rawTiedUpState)
        {
            Binding = binding;
            Entity = entity;
            RawDyingState = rawDyingState;
            RawCurrentView = rawCurrentView;
            RawTiedUpState = rawTiedUpState;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>The exact requested attachment handle, which may retire after this read.</summary>
        public PlayerHandle Binding { get; }
        /// <summary>Entity state copied by the final attachment validation, after reading Dying. These are not an atomic sample.</summary>
        public EntitySnapshot Entity { get; }
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
        /// Raw IPlayerState.CurrentView, read beside Dying. Client-written for a remote player and host-written
        /// for the listen host's own player. Not proof of sleep, death, loading or immunity to damage.
        /// </summary>
        public int RawCurrentView { get; }
        /// <inheritdoc cref="PlayerStateSnapshot.KnownView"/>
        public PlayerView? KnownView => ServerWorld.KnownPlayerView(RawCurrentView);
        /// <summary>
        /// Raw IPlayerState.TiedUpState, with the same trust as RawCurrentView. The encoding is not established,
        /// and 0 is not known to mean free.
        /// </summary>
        public int RawTiedUpState { get; }
        /// <summary>UTC read completion time, not a simulation, replication or last-change timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
