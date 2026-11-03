// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using FMOD;
using FMOD.Studio;

namespace Modstoke.Sons
{
    public static partial class AudioBanks
    {
        public const int MaximumMetadataBankEvents = 4096;
        public const int MaximumMetadataParametersPerRead = 128;

        /// <summary>
        /// Reads one event in an already owned bank, without loading samples or creating an instance.
        /// </summary>
        /// <remarks>
        /// Call on the initialized Unity lifecycle thread.
        /// Parameter indices use FMOD's order; request another page explicitly when truncated.
        /// </remarks>
        public static bool TryGetEventMetadata(string modId, string relativePath, string eventId,
            int parameterOffset, int maximumParameters, out AudioEventMetadata snapshot, out string status)
        {
            snapshot = null;
            if (!TryBegin("get-event-metadata", out status))
                return false;
            try
            {
                ValidateMetadataPage(parameterOffset, maximumParameters);
                if (!Guid.TryParse(eventId, out var requestedId))
                    throw new ArgumentException("A valid event GUID is required.", nameof(eventId));
                var registration = GetBankRegistration(modId, relativePath);
                if (!registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by Modstoke.");
                ValidateRegistration(registration, GetSystem());

                // The installed wrapper returns the full bank list, so bound it before lookup.
                Check(registration.Content.Bank.getEventCount(out var eventCount), "Reading bank event count");
                if (eventCount < 0 || eventCount > MaximumMetadataBankEvents)
                    throw new InvalidOperationException("Event metadata inspection is unavailable: bank event count "
                        + eventCount + " is outside the supported range 0.." + MaximumMetadataBankEvents + ".");
                var description = FindEvent(registration, requestedId);
                var result = ReadEventMetadata(registration, description, requestedId, null, parameterOffset, maximumParameters);
                ValidateRegistration(registration, GetSystem());
                snapshot = result;
                status = MetadataStatus(result);
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

        /// <summary>
        /// Reads metadata and parameter values from a retained playback owned by this mod.
        /// </summary>
        /// <remarks>
        /// Call on the initialized Unity lifecycle thread.
        /// Success means metadata was retrieved; inspect each ValueStatus for unavailable values.
        /// Final values are asynchronous FMOD observations, not an atomic or audible-state guarantee.
        /// </remarks>
        public static bool TryGetPlaybackMetadata(string modId, long playbackId,
            int parameterOffset, int maximumParameters, out AudioEventMetadata snapshot, out string status)
        {
            snapshot = null;
            if (!TryBegin("get-playback-metadata", out status))
                return false;
            try
            {
                ValidateMetadataPage(parameterOffset, maximumParameters);
                var playback = GetPlayback(modId, playbackId);
                if (playback.ReleaseSubmitted || playback.Error.Length != 0)
                    throw new InvalidOperationException("This instance needs cleanup; parameter values are unavailable.");
                if (!playback.Registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by Modstoke.");
                var system = GetSystem();
                ValidateRegistration(playback.Registration, system);
                ValidatePlayback(playback, system);
                Check(playback.Instance.getDescription(out var description), "Reading playback event description");
                var result = ReadEventMetadata(playback.Registration, description, Guid.Parse(playback.EventId),
                    playback, parameterOffset, maximumParameters);
                system = GetSystem();
                ValidateRegistration(playback.Registration, system);
                ValidatePlayback(playback, system);
                snapshot = result;
                status = MetadataStatus(result);
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

        private static void ValidateMetadataPage(int parameterOffset, int maximumParameters)
        {
            if (parameterOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(parameterOffset), "Parameter offset must be nonnegative.");
            if (maximumParameters < 1 || maximumParameters > MaximumMetadataParametersPerRead)
                throw new ArgumentOutOfRangeException(nameof(maximumParameters), "Parameter page size must be between 1 and "
                    + MaximumMetadataParametersPerRead + ".");
        }

        private static AudioEventMetadata ReadEventMetadata(Registration registration,
            EventDescription description, Guid expectedId, Playback playback, int parameterOffset, int maximumParameters)
        {
            if (!description.isValid())
                throw new InvalidOperationException("The owned event description is no longer valid.");
            Check(description.getID(out var id), "Reading event ID");
            if (Guid.Parse(id.ToString()) != expectedId)
                throw new InvalidOperationException("The event description no longer matches the owned event.");
            var pathResult = description.getPath(out var path);
            if (pathResult != RESULT.OK && pathResult != RESULT.ERR_EVENT_NOTFOUND)
                Check(pathResult, "Reading event path");
            Check(description.getLength(out var length), "Reading event timeline length");
            Check(description.is3D(out var is3D), "Reading event spatial mode");
            Check(description.isOneshot(out var isOneShot), "Reading event duration mode");
            Check(description.getParameterDescriptionCount(out var total), "Reading event parameter count");
            if (total < 0)
                throw new InvalidOperationException("FMOD returned a negative event parameter count.");
            if (parameterOffset > total)
                throw new ArgumentOutOfRangeException(nameof(parameterOffset), "Parameter offset exceeds the event parameter count.");

            var parameters = new AudioParameterSnapshot[Math.Min(maximumParameters, total - parameterOffset)];
            var unavailableValues = 0;
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameterIndex = parameterOffset + index;
                Check(description.getParameterDescriptionByIndex(parameterIndex, out var parameter),
                    "Reading event parameter " + parameterIndex);
                // Copy the native-backed name while its owning bank is still validated and retained.
                string name = parameter.name;
                float? value = null;
                float? finalValue = null;
                var valueStatus = "NOT_REQUESTED";
                if (playback != null)
                {
                    var valueResult = playback.Instance.getParameterByID(parameter.id, out var current, out var final);
                    if (valueResult == RESULT.ERR_INVALID_HANDLE)
                        Check(valueResult, "Reading event parameter " + parameterIndex + " value");
                    valueStatus = valueResult.ToString();
                    if (valueResult == RESULT.OK && float.IsFinite(current) && float.IsFinite(final))
                    {
                        value = current;
                        finalValue = final;
                    }
                    else
                    {
                        if (valueResult == RESULT.OK)
                            valueStatus = "NON_FINITE_VALUE";
                        unavailableValues++;
                    }
                }
                parameters[index] = new AudioParameterSnapshot(parameterIndex, name,
                    parameter.id.data1, parameter.id.data2, parameter.minimum, parameter.maximum,
                    parameter.defaultvalue, parameter.type.ToString(), (uint)parameter.flags,
                    (parameter.flags & PARAMETER_FLAGS.READONLY) != 0,
                    (parameter.flags & PARAMETER_FLAGS.AUTOMATIC) != 0,
                    (parameter.flags & PARAMETER_FLAGS.GLOBAL) != 0, value, finalValue, valueStatus);
            }
            if (!description.isValid())
                throw new InvalidOperationException("The owned event description became unavailable during inspection.");
            return new AudioEventMetadata(registration.ModId, registration.Path, expectedId.ToString(),
                pathResult == RESULT.OK ? path : "", pathResult.ToString(), playback?.Id ?? 0,
                length, is3D, isOneShot, parameterOffset, total, Array.AsReadOnly(parameters), unavailableValues);
        }

        private static string MetadataStatus(AudioEventMetadata snapshot)
        {
            var status = snapshot.Truncated ? "Event metadata retrieved; parameter page is truncated."
                : "Event metadata retrieved.";
            if (snapshot.PathStatus != RESULT.OK.ToString())
                status += " Event path unavailable: " + snapshot.PathStatus + ".";
            if (snapshot.UnavailableValueCount != 0)
                status += " " + snapshot.UnavailableValueCount + " parameter values unavailable; inspect ValueStatus.";
            return status;
        }
    }

    public sealed class AudioEventMetadata
    {
        internal AudioEventMetadata(string modId, string bankPath, string eventId, string path,
            string pathStatus, long playbackId, int length, bool is3D, bool isOneShot, int parameterOffset,
            int totalParameterCount, IReadOnlyList<AudioParameterSnapshot> parameters, int unavailableValueCount)
        {
            ModId = modId;
            BankFullPath = bankPath;
            EventId = eventId;
            Path = path ?? "";
            PathStatus = pathStatus;
            PlaybackId = playbackId;
            TimelineLengthMilliseconds = length;
            Is3D = is3D;
            IsOneShot = isOneShot;
            ParameterOffset = parameterOffset;
            TotalParameterCount = totalParameterCount;
            Parameters = parameters;
            UnavailableValueCount = unavailableValueCount;
        }

        public string ModId { get; }
        public string BankFullPath { get; }
        public string EventId { get; }
        public string Path { get; }
        public string PathStatus { get; }
        /// <summary>Zero for bank-only metadata; otherwise the inspected retained playback ID.</summary>
        public long PlaybackId { get; }
        /// <summary>Authored timeline length, not the duration of looping or parameter-driven playback.</summary>
        public int TimelineLengthMilliseconds { get; }
        public bool Is3D { get; }
        public bool IsOneShot { get; }
        public int ParameterOffset { get; }
        public int TotalParameterCount { get; }
        public IReadOnlyList<AudioParameterSnapshot> Parameters { get; }
        /// <summary>True when this page omits any parameters, including preceding indices.</summary>
        public bool Truncated => Parameters.Count != TotalParameterCount;
        /// <summary>The next offset to request, or -1 when this page reaches the end.</summary>
        public int NextParameterOffset => ParameterOffset + Parameters.Count < TotalParameterCount
            ? ParameterOffset + Parameters.Count : -1;
        public int UnavailableValueCount { get; }
    }

    public sealed class AudioParameterSnapshot
    {
        internal AudioParameterSnapshot(int index, string name, uint idData1, uint idData2,
            float minimum, float maximum, float defaultValue, string type, uint flags,
            bool isReadOnly, bool isAutomatic, bool isGlobal, float? value, float? finalValue, string valueStatus)
        {
            Index = index;
            Name = name ?? "";
            IdData1 = idData1;
            IdData2 = idData2;
            Minimum = minimum;
            Maximum = maximum;
            DefaultValue = defaultValue;
            Type = type;
            Flags = flags;
            IsReadOnly = isReadOnly;
            IsAutomatic = isAutomatic;
            IsGlobal = isGlobal;
            Value = value;
            FinalValue = finalValue;
            ValueStatus = valueStatus;
        }

        public int Index { get; }
        public string Name { get; }
        public uint IdData1 { get; }
        public uint IdData2 { get; }
        public float Minimum { get; }
        public float Maximum { get; }
        public float DefaultValue { get; }
        public string Type { get; }
        public uint Flags { get; }
        public bool IsReadOnly { get; }
        public bool IsAutomatic { get; }
        public bool IsGlobal { get; }
        /// <summary>FMOD's requested value; automatic parameters return zero. Null when not read successfully.</summary>
        public float? Value { get; }
        /// <summary>FMOD's asynchronously calculated value after modulation and seek speed; null when unavailable.</summary>
        public float? FinalValue { get; }
        /// <summary>NOT_REQUESTED for bank metadata, OK for a successful value read, or an unavailable reason.</summary>
        public string ValueStatus { get; }
    }
}
