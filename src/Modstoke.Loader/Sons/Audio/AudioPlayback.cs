// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using FMOD;
using FMOD.Studio;
using UnityEngine;
using StudioSystem = FMOD.Studio.System;

namespace Modstoke.Sons
{
    public static partial class AudioBanks
    {
        private static readonly Dictionary<long, Playback> Playbacks = new();
        private static long _nextPlaybackId;

        public static bool TryPlayOneShot(string modId, string relativePath, string eventId,
            AudioPlaybackOptions options, out string status) =>
            TryPlay(modId, relativePath, eventId, options, true, out _, out status);

        public static bool TryStartEvent(string modId, string relativePath, string eventId,
            AudioPlaybackOptions options, out long playbackId, out string status) =>
            TryPlay(modId, relativePath, eventId, options, false, out playbackId, out status);

        private static bool TryPlay(string modId, string relativePath, string eventId,
            AudioPlaybackOptions options, bool oneShot, out long playbackId, out string status)
        {
            playbackId = 0;
            if (!TryBegin(oneShot ? "play-one-shot" : "start-event", out status))
                return false;

            Playback playback = null;
            var system = default(StudioSystem);
            try
            {
                var path = ResolveBankPath(modId, relativePath);
                if (!Banks.TryGetValue(path, out var registration) || !registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by Modstoke.");
                if (!Guid.TryParse(eventId, out var requestedId))
                    throw new ArgumentException("A valid event GUID is required.", nameof(eventId));
                var volume = options?.Volume ?? 1;
                var pitch = options?.Pitch ?? 1;
                var spatialAttributes = options?.Attributes;
                ValidateVolume(volume);
                ValidatePitch(pitch);
                var attributes = spatialAttributes == null ? default : ConvertAttributes(spatialAttributes);
                system = GetSystem();
                ValidateRegistration(registration, system);
                var description = FindEvent(registration, requestedId);
                Check(description.is3D(out var is3D), "Reading event spatial mode");
                if (is3D && spatialAttributes == null)
                    throw new ArgumentException("A 3D event requires explicit spatial attributes.", nameof(options));
                if (oneShot)
                {
                    Check(description.isOneshot(out var isOneShot), "Reading event duration mode");
                    if (!isOneShot)
                        throw new InvalidOperationException("This event does not automatically end. Use TryStartEvent and explicitly stop it.");
                }

                var id = checked(++_nextPlaybackId);
                Check(description.createInstance(out var instance), "Creating event instance");
                playback = new Playback(id, registration, requestedId.ToString(), system.handle, instance, is3D);
                Playbacks.Add(id, playback);
                Check(instance.setVolume(volume), "Setting event volume");
                Check(instance.setPitch(pitch), "Setting event pitch");
                if (is3D)
                    Check(instance.set3DAttributes(attributes), "Setting event spatial attributes");
                Check(instance.start(), "Starting event instance");
                playback.StartSubmitted = true;
                if (oneShot)
                {
                    Check(instance.release(), "Releasing one-shot instance");
                    playback.ReleaseSubmitted = true;
                    Playbacks.Remove(id);
                }
                else
                {
                    playbackId = id;
                }
                status = oneShot ? "One-shot start and release submitted to FMOD." : "Event start submitted to FMOD; the instance remains caller-owned.";
                return true;
            }
            catch (Exception ex)
            {
                status = ex.Message;
                if (playback != null)
                {
                    var cleanupError = StopPlayback(playback, system, false);
                    if (cleanupError.Length != 0)
                    {
                        playbackId = playback.Id;
                        status += " Cleanup pending for playback " + playback.Id + ": " + cleanupError;
                        playback.Error = status;
                    }
                }
                _lastError = status;
                return false;
            }
            finally
            {
                End();
            }
        }

        public static bool TryStopEvent(string modId, long playbackId, bool allowFadeout, out string status)
        {
            if (!TryBegin("stop-event", out status))
                return false;
            try
            {
                var playback = GetPlayback(modId, playbackId);
                status = StopPlayback(playback, GetSystem(), allowFadeout);
                _lastError = status;
                if (status.Length != 0)
                    return false;
                status = "Event stop and release submitted to FMOD.";
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

        public static bool TrySetEventVolume(string modId, long playbackId, float volume, out string status) =>
            TryEditPlayback(modId, playbackId, "set-event-volume", volume, static (playback, value) =>
            {
                ValidateVolume(value);
                return playback.Instance.setVolume(value);
            }, out status);

        public static bool TrySetEventPitch(string modId, long playbackId, float pitch, out string status) =>
            TryEditPlayback(modId, playbackId, "set-event-pitch", pitch, static (playback, value) =>
            {
                ValidatePitch(value);
                return playback.Instance.setPitch(value);
            }, out status);

        public static bool TrySetEventParameter(string modId, long playbackId, string name, float value,
            bool ignoreSeekSpeed, out string status) =>
            TryEditPlayback(modId, playbackId, "set-event-parameter", (Name: name, Value: value, IgnoreSeekSpeed: ignoreSeekSpeed), static (playback, parameter) =>
            {
                if (string.IsNullOrWhiteSpace(parameter.Name) || !float.IsFinite(parameter.Value))
                    throw new ArgumentException("A parameter name and finite value are required.");
                return playback.Instance.setParameterByName(parameter.Name, parameter.Value, parameter.IgnoreSeekSpeed);
            }, out status);

        public static bool TrySetEventAttributes(string modId, long playbackId,
            AudioAttributes attributes, out string status) =>
            TryEditPlayback(modId, playbackId, "set-event-attributes", attributes, static (playback, value) =>
            {
                if (!playback.Is3D)
                    throw new InvalidOperationException("This event is not spatial.");
                return playback.Instance.set3DAttributes(ConvertAttributes(value));
            }, out status);

        public static bool TryGetPlayback(string modId, long playbackId,
            out AudioPlaybackSnapshot snapshot, out string status)
        {
            snapshot = null;
            if (!TryBegin("get-event-playback", out status))
                return false;
            try
            {
                var playback = GetPlayback(modId, playbackId);
                playback.LastPlaybackState = "UNKNOWN";
                ValidatePlayback(playback, GetSystem());
                Check(playback.Instance.getPlaybackState(out var state), "Reading event playback state");
                playback.LastPlaybackState = state.ToString();
                snapshot = playback.Snapshot();
                status = "Event playback state retrieved.";
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

        private static bool TryEditPlayback<T>(string modId, long playbackId, string operation,
            T value, Func<Playback, T, RESULT> edit, out string status)
        {
            if (!TryBegin(operation, out status))
                return false;
            try
            {
                var playback = GetPlayback(modId, playbackId);
                if (playback.ReleaseSubmitted || playback.Error.Length != 0)
                    throw new InvalidOperationException("This instance needs cleanup. Stop it before starting another instance.");
                ValidatePlayback(playback, GetSystem());
                Check(edit(playback, value), operation);
                status = "Event change submitted to FMOD.";
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

        private static EventDescription FindEvent(Registration registration, Guid requestedId)
        {
            // A loaded bank's event list is fixed, so index it once per owned bank handle.
            var resource = registration.Content;
            if (resource.Events == null)
            {
                Check(resource.Bank.getEventList(out var descriptions), "Enumerating bank events");
                var events = new Dictionary<Guid, EventDescription>();
                if (descriptions != null)
                {
                    foreach (var description in descriptions)
                    {
                        Check(description.getID(out var id), "Reading event ID");
                        events.TryAdd(Guid.Parse(id.ToString()), description);
                    }
                }
                resource.Events = events;
            }
            if (!resource.Events.TryGetValue(requestedId, out var found))
                throw new InvalidOperationException("The event GUID is not present in this owned bank.");
            return found;
        }

        private static Playback GetPlayback(string modId, long id)
        {
            if (string.IsNullOrWhiteSpace(modId) || !Playbacks.TryGetValue(id, out var playback)
                || !string.Equals(playback.Registration.ModId, Assets.NormalizeModId(modId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Playback is not owned by this mod registration.");
            return playback;
        }

        private static void ValidatePlayback(Playback playback, StudioSystem system)
        {
            if (playback.SystemHandle != system.handle)
            {
                playback.LastPlaybackState = "UNKNOWN";
                throw new InvalidOperationException("The playback belongs to a different FMOD system.");
            }
            if (!playback.Instance.isValid())
            {
                playback.LastPlaybackState = "UNKNOWN";
                throw new InvalidOperationException("The event instance is no longer valid. Stop it to discard the stale registration.");
            }
        }

        private static void StopRegistrationPlaybacks(Registration registration, StudioSystem system, List<string> errors)
        {
            if (Playbacks.Count == 0)
                return;

            foreach (var playback in Playbacks.Values.Where(item => ReferenceEquals(item.Registration, registration)).ToArray())
            {
                var error = StopPlayback(playback, system, false);
                if (error.Length != 0)
                    errors.Add("Playback " + playback.Id + ": " + error);
            }
        }

        private static string StopPlayback(Playback playback, StudioSystem system, bool allowFadeout)
        {
            playback.LastPlaybackState = "UNKNOWN";
            try
            {
                if (playback.SystemHandle != system.handle)
                    throw new InvalidOperationException("FMOD system changed; refusing to stop a stale instance.");
                if (!playback.Instance.isValid())
                {
                    Playbacks.Remove(playback.Id);
                    return "";
                }
            }
            catch (Exception ex)
            {
                return playback.Error = ex.Message;
            }

            var errors = new List<string>();
            try { Check(playback.Instance.stop(allowFadeout ? STOP_MODE.ALLOWFADEOUT : STOP_MODE.IMMEDIATE), "Stopping event instance"); }
            catch (Exception ex) { errors.Add(ex.Message); }
            // A failed stop still needs release; remember success so cleanup retries do not release twice.
            if (!playback.ReleaseSubmitted)
            {
                try
                {
                    Check(playback.Instance.release(), "Releasing event instance");
                    playback.ReleaseSubmitted = true;
                }
                catch (Exception ex) { errors.Add(ex.Message); }
            }

            playback.Error = string.Join(" ", errors);
            if (errors.Count == 0)
                Playbacks.Remove(playback.Id);
            return playback.Error;
        }

        private static IReadOnlyList<AudioPlaybackSnapshot> GetPlaybackSnapshots(IReadOnlyList<AudioPlaybackSnapshot> previous)
        {
            if (previous.Count == Playbacks.Count)
            {
                var unchanged = true;
                for (var index = 0; index < previous.Count; index++)
                {
                    var snapshot = previous[index];
                    if (!Playbacks.TryGetValue(snapshot.Id, out var playback)
                        || !ReferenceEquals(snapshot, playback.Snapshot()))
                    {
                        unchanged = false;
                        break;
                    }
                }
                if (unchanged)
                    return previous;
            }
            return Array.AsReadOnly(Playbacks.Values.OrderBy(playback => playback.Id)
                .Select(playback => playback.Snapshot()).ToArray());
        }

        private static void ValidateVolume(float volume)
        {
            if (!float.IsFinite(volume) || volume < 0)
                throw new ArgumentOutOfRangeException(nameof(volume), "Volume must be finite and nonnegative.");
        }

        private static void ValidatePitch(float pitch)
        {
            if (!float.IsFinite(pitch) || pitch <= 0)
                throw new ArgumentOutOfRangeException(nameof(pitch), "Pitch must be finite and positive.");
        }

        private static ATTRIBUTES_3D ConvertAttributes(AudioAttributes attributes)
        {
            if (attributes == null)
                throw new ArgumentNullException(nameof(attributes));
            var position = ConvertVector(attributes.Position);
            var velocity = ConvertVector(attributes.Velocity);
            var forward = ConvertVector(attributes.Forward);
            var up = ConvertVector(attributes.Up);
            if (MathF.Abs(Dot(forward, forward) - 1) > 0.001f || MathF.Abs(Dot(up, up) - 1) > 0.001f
                || MathF.Abs(Dot(forward, up)) > 0.001f)
                throw new ArgumentException("Forward and up must be perpendicular unit vectors.", nameof(attributes));
            return new ATTRIBUTES_3D { position = position, velocity = velocity, forward = forward, up = up };
        }

        private static VECTOR ConvertVector(Vector3 vector)
        {
            if (!float.IsFinite(vector.x) || !float.IsFinite(vector.y) || !float.IsFinite(vector.z))
                throw new ArgumentException("Spatial coordinates must be finite.");
            return new VECTOR { x = vector.x, y = vector.y, z = vector.z };
        }

        private static float Dot(VECTOR left, VECTOR right) => left.x * right.x + left.y * right.y + left.z * right.z;

        private sealed class Playback
        {
            internal readonly long Id;
            internal readonly Registration Registration;
            internal readonly string EventId;
            internal readonly IntPtr SystemHandle;
            internal readonly EventInstance Instance;
            internal readonly bool Is3D;
            internal bool StartSubmitted;
            internal bool ReleaseSubmitted;
            internal string LastPlaybackState = "UNKNOWN";
            internal string Error = "";
            private AudioPlaybackSnapshot _snapshot;

            internal Playback(long id, Registration registration, string eventId, IntPtr systemHandle, EventInstance instance, bool is3D)
            {
                Id = id;
                Registration = registration;
                EventId = eventId;
                SystemHandle = systemHandle;
                Instance = instance;
                Is3D = is3D;
            }

            internal AudioPlaybackSnapshot Snapshot()
            {
                if (_snapshot == null || _snapshot.StartSubmitted != StartSubmitted
                    || _snapshot.ReleaseSubmitted != ReleaseSubmitted
                    || _snapshot.LastPlaybackState != LastPlaybackState || _snapshot.Error != Error)
                    _snapshot = new AudioPlaybackSnapshot(Id, Registration.ModId, Registration.Path,
                        EventId, Is3D, StartSubmitted, ReleaseSubmitted, LastPlaybackState, Error);
                return _snapshot;
            }
        }
    }

    public sealed class AudioPlaybackOptions
    {
        public float Volume { get; init; } = 1;
        public float Pitch { get; init; } = 1;
        public AudioAttributes Attributes { get; init; }
    }

    public sealed class AudioAttributes
    {
        public Vector3 Position { get; init; }
        public Vector3 Velocity { get; init; }
        public Vector3 Forward { get; init; } = new Vector3 { z = 1 };
        public Vector3 Up { get; init; } = new Vector3 { y = 1 };
    }

    public sealed class AudioPlaybackSnapshot
    {
        internal AudioPlaybackSnapshot(long id, string modId, string bankPath, string eventId, bool is3D,
            bool startSubmitted, bool releaseSubmitted, string lastPlaybackState, string error)
        {
            Id = id;
            ModId = modId;
            BankFullPath = bankPath;
            EventId = eventId;
            Is3D = is3D;
            StartSubmitted = startSubmitted;
            ReleaseSubmitted = releaseSubmitted;
            LastPlaybackState = lastPlaybackState;
            Error = error;
        }

        public long Id { get; }
        public string ModId { get; }
        public string BankFullPath { get; }
        public string EventId { get; }
        public bool Is3D { get; }
        public bool StartSubmitted { get; }
        public bool ReleaseSubmitted { get; }
        public string LastPlaybackState { get; }
        public string Error { get; }
    }
}
