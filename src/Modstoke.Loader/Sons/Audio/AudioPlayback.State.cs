// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using FMOD;

namespace Modstoke.Sons
{
    public static partial class AudioBanks
    {
        /// <summary>
        /// Reads the state and timeline cursor of one retained playback owned by this mod.
        /// </summary>
        /// <remarks>
        /// Success includes partial observations; inspect readStatus and TimelineStatus.
        /// Values are separate asynchronous observations, not an atomic or audible-state guarantee.
        /// One-shots and successfully released IDs are not retained; their absence does not prove a stopped state.
        /// </remarks>
        public static bool TryGetPlaybackState(string modId, long playbackId,
            out AudioPlaybackStateSnapshot snapshot,
            out AudioPlaybackReadStatus readStatus, out string status)
        {
            snapshot = null;
            if (!TryBegin("get-event-playback-state", out status))
            {
                readStatus = _ownerThreadId == 0
                    ? AudioPlaybackReadStatus.NotReady
                    : Environment.CurrentManagedThreadId != _ownerThreadId
                        ? AudioPlaybackReadStatus.WrongThread
                        : _stopping ? AudioPlaybackReadStatus.NotReady
                        : AudioPlaybackReadStatus.Busy;
                return false;
            }

            readStatus = AudioPlaybackReadStatus.NotRetained;
            try
            {
                var playback = GetPlayback(modId, playbackId);
                playback.LastPlaybackState = "UNKNOWN";
                if (playback.ReleaseSubmitted)
                {
                    readStatus = AudioPlaybackReadStatus.ReleaseSubmitted;
                    _lastError = status = "Release was submitted for this playback; live state is unavailable.";
                    return false;
                }
                if (playback.Error.Length != 0)
                {
                    readStatus = AudioPlaybackReadStatus.CleanupPending;
                    _lastError = status = "This playback needs cleanup; live state is unavailable. " + playback.Error;
                    return false;
                }

                readStatus = AudioPlaybackReadStatus.NotReady;
                var system = GetSystem();
                readStatus = AudioPlaybackReadStatus.StaleHandle;
                ValidatePlayback(playback, system);
                readStatus = AudioPlaybackReadStatus.NotReady;
                if (!playback.Registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by Modstoke.");
                ValidateRegistration(playback.Registration, system);

                readStatus = AudioPlaybackReadStatus.Failed;
                var stateResult = playback.Instance.getPlaybackState(out var state);
                if (stateResult != RESULT.OK)
                {
                    readStatus = PlaybackReadFailure(stateResult);
                    Check(stateResult, "Reading event playback state");
                }

                var timelineResult = playback.Instance.getTimelinePosition(out var position);
                if (timelineResult == RESULT.ERR_INVALID_HANDLE
                    || timelineResult == RESULT.ERR_UNINITIALIZED
                    || timelineResult == RESULT.ERR_STUDIO_UNINITIALIZED)
                {
                    readStatus = PlaybackReadFailure(timelineResult);
                    Check(timelineResult, "Reading event timeline position");
                }

                // FMOD can report STOPPED for an invalid instance. Never publish that as a live observation.
                readStatus = AudioPlaybackReadStatus.NotReady;
                system = GetSystem();
                readStatus = AudioPlaybackReadStatus.StaleHandle;
                ValidatePlayback(playback, system);
                readStatus = AudioPlaybackReadStatus.NotReady;
                ValidateRegistration(playback.Registration, system);

                readStatus = AudioPlaybackReadStatus.Failed;
                var timelineStatus = timelineResult == RESULT.OK && position < 0
                    ? "NEGATIVE_POSITION" : timelineResult.ToString();
                int? timelinePosition = timelineResult == RESULT.OK && position >= 0 ? position : null;
                playback.LastPlaybackState = state.ToString();
                var observation = new AudioPlaybackStateSnapshot(playback.Snapshot(), (int)state,
                    timelinePosition, timelineStatus);
                var knownState = observation.PlaybackState != AudioPlaybackState.Unknown;
                status = "Event playback state retrieved.";
                if (!knownState)
                    status += " Playback state is unrecognized; inspect RawPlaybackState.";
                if (!timelinePosition.HasValue)
                    status += " Timeline position unavailable: " + timelineStatus + ".";
                readStatus = knownState && timelinePosition.HasValue
                    ? AudioPlaybackReadStatus.Success : AudioPlaybackReadStatus.Partial;
                snapshot = observation;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = status = ex.Message;
                return false;
            }
            finally
            {
                End();
            }
        }

        private static AudioPlaybackReadStatus PlaybackReadFailure(RESULT result) => result switch
        {
            RESULT.ERR_INVALID_HANDLE => AudioPlaybackReadStatus.StaleHandle,
            RESULT.ERR_NOTREADY or RESULT.ERR_UNINITIALIZED or RESULT.ERR_STUDIO_UNINITIALIZED
                => AudioPlaybackReadStatus.NotReady,
            _ => AudioPlaybackReadStatus.Failed
        };
    }

    public enum AudioPlaybackReadStatus
    {
        Success,
        /// <summary>A playback state was read, but it is unrecognized or the timeline cursor is unavailable.</summary>
        Partial,
        NotReady,
        WrongThread,
        Busy,
        /// <summary>No retained playback is owned by this mod and ID. Includes IDs discarded after release.</summary>
        NotRetained,
        /// <summary>Release was submitted for a retained cleanup record; native destruction is not confirmed.</summary>
        ReleaseSubmitted,
        CleanupPending,
        /// <summary>The retained instance is invalid or belongs to a different FMOD system.</summary>
        StaleHandle,
        Failed
    }

    /// <summary>FMOD playback phase, independent of pause, audibility and ownership.</summary>
    public enum AudioPlaybackState
    {
        Unknown = -1,
        Playing = 0,
        Sustaining = 1,
        /// <summary>The validated retained instance reported stopped; it has not been released by this read.</summary>
        Stopped = 2,
        Starting = 3,
        Stopping = 4
    }

    /// <summary>Copied state for one owned playback, without retained native handles.</summary>
    public sealed class AudioPlaybackStateSnapshot
    {
        internal AudioPlaybackStateSnapshot(AudioPlaybackSnapshot playback,
            int rawPlaybackState, int? timelinePositionMilliseconds, string timelineStatus)
        {
            Playback = playback;
            RawPlaybackState = rawPlaybackState;
            TimelinePositionMilliseconds = timelinePositionMilliseconds;
            TimelineStatus = timelineStatus;
            ObservedAt = DateTimeOffset.UtcNow;
        }

        /// <summary>Owned playback identity and submission flags copied at read completion.</summary>
        public AudioPlaybackSnapshot Playback { get; }
        /// <summary>The successful native state value, preserved even when unrecognized.</summary>
        public int RawPlaybackState { get; }
        public AudioPlaybackState PlaybackState => (uint)RawPlaybackState <= 4
            ? (AudioPlaybackState)RawPlaybackState : AudioPlaybackState.Unknown;
        /// <summary>Timeline cursor in milliseconds, not elapsed listening time or remaining duration; null when unavailable.</summary>
        public int? TimelinePositionMilliseconds { get; }
        /// <summary>OK, an FMOD error name, or NEGATIVE_POSITION for an unusable cursor value.</summary>
        public string TimelineStatus { get; }
        /// <summary>Read completion time, not the FMOD update timestamp.</summary>
        public DateTimeOffset ObservedAt { get; }
    }
}
