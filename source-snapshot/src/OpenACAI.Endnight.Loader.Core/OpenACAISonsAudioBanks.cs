// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FMOD;
using FMOD.Studio;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;
using StudioSystem = FMOD.Studio.System;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsAudioBanks
    {
        public const string ServiceKey = "openacai.sons.audioBanks";

        private static readonly Dictionary<string, Registration> Banks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Resource> Resources = new(StringComparer.OrdinalIgnoreCase);
        private static OpenACAISonsAudioBankState _state = new(false, false, "initialize", "", Array.Empty<OpenACAISonsAudioBankSnapshot>(), Array.Empty<OpenACAISonsAudioPlaybackSnapshot>());
        private static int _ownerThreadId;
        private static bool _busy;
        private static bool _stopping;
        private static string _lastOperation = "initialize";
        private static string _lastError = "";

        public static OpenACAISonsAudioBankState State => Volatile.Read(ref _state);

        internal static void Initialize()
        {
            if (_ownerThreadId != 0)
                return;
            _ownerThreadId = Environment.CurrentManagedThreadId;
            Publish();
        }

        public static bool TryLoadBank(string modId, string relativePath, string stringsRelativePath,
            out OpenACAISonsAudioBankSnapshot snapshot, out string status)
        {
            snapshot = null;
            if (!TryBegin("load-bank", out status))
                return false;

            Registration registration = null;
            var added = false;
            var system = default(StudioSystem);
            try
            {
                var path = ResolveBankPath(modId, relativePath);
                var stringsPath = string.IsNullOrWhiteSpace(stringsRelativePath) ? "" : ResolveBankPath(modId, stringsRelativePath);
                if (string.Equals(path, stringsPath, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The content and strings bank paths must be different.");

                system = GetSystem();
                if (Banks.TryGetValue(path, out registration))
                {
                    if (!string.Equals(registration.StringsPath, stringsPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("This bank is registered with a different strings bank. Unload it before changing that association.");
                    if (!registration.Loaded)
                        throw new InvalidOperationException("This bank has unfinished cleanup. Unload it before retrying the load.");
                    ValidateRegistration(registration, system);
                    snapshot = registration.Snapshot();
                    status = "Bank is already loaded by OpenACAI.";
                    return true;
                }

                if (!File.Exists(path))
                    throw new FileNotFoundException("Content bank was not found.", path);
                if (stringsPath.Length != 0 && !File.Exists(stringsPath))
                    throw new FileNotFoundException("Strings bank was not found.", stringsPath);

                registration = new Registration(OpenACAISonsAssets.NormalizeModId(modId), path, stringsPath);
                Banks.Add(path, registration);
                added = true;
                if (stringsPath.Length != 0)
                {
                    registration.Strings = Acquire(stringsPath, system);
                    Validate(registration.Strings, system);
                }
                registration.Content = Acquire(path, system);
                Validate(registration.Content, system);
                registration.Loaded = true;
                snapshot = registration.Snapshot();
                status = "Bank metadata loaded through OpenACAI.";
                return true;
            }
            catch (Exception ex)
            {
                status = ex.Message;
                if (added)
                {
                    var cleanupError = Release(registration, system);
                    if (cleanupError.Length != 0)
                        status += " Cleanup: " + cleanupError;
                    registration.Error = status;
                    snapshot = registration.Snapshot();
                }
                _lastError = status;
                return false;
            }
            finally
            {
                End();
            }
        }

        public static bool TryUnloadBank(string modId, string relativePath, out string status)
        {
            if (!TryBegin("unload-bank", out status))
                return false;
            try
            {
                var path = ResolveBankPath(modId, relativePath);
                if (!Banks.TryGetValue(path, out var registration))
                    throw new InvalidOperationException("Bank is not registered with OpenACAI.");
                status = Release(registration, GetSystem());
                registration.Error = status;
                _lastError = status;
                if (status.Length != 0)
                    return false;
                status = "Owned bank references released.";
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

        public static int UnloadModBanks(string modId)
        {
            if (!TryBegin("unload-mod-banks", out var status))
                throw new InvalidOperationException(status);
            try
            {
                var normalizedId = OpenACAISonsAssets.NormalizeModId(modId);
                var registrations = Banks.Values.Where(bank => string.Equals(bank.ModId, normalizedId, StringComparison.OrdinalIgnoreCase)).Reverse().ToArray();
                if (registrations.Length == 0)
                    return 0;
                return ReleaseAll(registrations, GetSystem());
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                return 0;
            }
            finally
            {
                End();
            }
        }

        public static bool TryGetEvents(string modId, string relativePath,
            out IReadOnlyList<OpenACAISonsAudioEventSnapshot> events, out string status)
        {
            events = Array.Empty<OpenACAISonsAudioEventSnapshot>();
            if (!TryBegin("get-bank-events", out status))
                return false;
            try
            {
                var path = ResolveBankPath(modId, relativePath);
                if (!Banks.TryGetValue(path, out var registration) || !registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by OpenACAI.");
                var system = GetSystem();
                ValidateRegistration(registration, system);

                Check(registration.Content.Bank.getEventList(out var descriptions), "Enumerating bank events");
                var found = new List<OpenACAISonsAudioEventSnapshot>();
                if (descriptions != null)
                {
                    foreach (var description in descriptions)
                    {
                        Check(description.getID(out var id), "Reading event ID");
                        var pathResult = description.getPath(out var eventPath);
                        if (pathResult != RESULT.OK && pathResult != RESULT.ERR_EVENT_NOTFOUND)
                            Check(pathResult, "Reading event path");
                        found.Add(new OpenACAISonsAudioEventSnapshot(id.ToString(),
                            pathResult == RESULT.OK ? eventPath : "", pathResult.ToString()));
                    }
                }
                events = Array.AsReadOnly(found.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());
                status = "Bank event metadata retrieved.";
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

        public static bool TryLoadBankSamples(string modId, string relativePath, out string status)
        {
            if (!TryBegin("load-bank-samples", out status))
                return false;
            try
            {
                var registration = GetBankRegistration(modId, relativePath);
                if (!registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by OpenACAI.");
                ValidateRegistration(registration, GetSystem());
                if (registration.SamplesRequested)
                {
                    status = "Sample preloading is already requested for this bank registration.";
                    return true;
                }

                registration.LastSampleLoadingState = "UNKNOWN";
                Check(registration.Content.Bank.loadSampleData(), "Requesting bank sample data");
                registration.SamplesRequested = true;
                status = "Sample preload requested. Query the sample state separately to check readiness.";
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

        public static bool TryUnloadBankSamples(string modId, string relativePath, out string status)
        {
            if (!TryBegin("unload-bank-samples", out status))
                return false;
            try
            {
                var registration = GetBankRegistration(modId, relativePath);
                if (registration.SamplesRequested)
                    ReleaseBankSamples(registration, GetSystem());
                status = "No owned sample preload reference remains for this bank registration.";
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

        public static bool TryGetBankSampleState(string modId, string relativePath,
            out OpenACAISonsAudioBankSnapshot snapshot, out string status)
        {
            snapshot = null;
            if (!TryBegin("get-bank-sample-state", out status))
                return false;
            try
            {
                var registration = GetBankRegistration(modId, relativePath);
                registration.LastSampleLoadingState = "UNKNOWN";
                if (!registration.Loaded)
                    throw new InvalidOperationException("Bank metadata is not loaded by OpenACAI.");
                ValidateRegistration(registration, GetSystem());
                Check(registration.Content.Bank.getSampleLoadingState(out var state), "Reading bank sample state");
                registration.LastSampleLoadingState = state.ToString();
                snapshot = registration.Snapshot();
                status = "Bank sample state: " + registration.LastSampleLoadingState + ".";
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

        private static Registration GetBankRegistration(string modId, string relativePath)
        {
            if (!Banks.TryGetValue(ResolveBankPath(modId, relativePath), out var registration))
                throw new InvalidOperationException("Bank is not registered with OpenACAI.");
            return registration;
        }

        private static void ReleaseBankSamples(Registration registration, StudioSystem system)
        {
            if (!registration.SamplesRequested)
                return;
            var resource = registration.Content;
            if (resource == null || resource.SystemHandle != system.handle)
                throw new InvalidOperationException("FMOD system changed; refusing to release samples through a stale handle.");

            registration.LastSampleLoadingState = "UNKNOWN";
            if (resource.Bank.isValid())
                Check(resource.Bank.unloadSampleData(), "Releasing bank sample data");
            // Balance only our accepted request, even when other callers still retain samples.
            registration.SamplesRequested = false;
        }

        internal static void Shutdown()
        {
            if (_ownerThreadId == 0 || _stopping)
                return;
            if (Environment.CurrentManagedThreadId != _ownerThreadId || _busy)
                throw new InvalidOperationException("Audio bank shutdown requires the idle Unity lifecycle thread.");
            _stopping = true;
            _busy = true;
            _lastOperation = "shutdown";
            _lastError = "";
            try
            {
                if (Banks.Count != 0)
                    ReleaseAll(Banks.Values.Reverse().ToArray(), GetSystem());
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
            }
            finally
            {
                End();
            }
        }

        private static int ReleaseAll(IEnumerable<Registration> registrations, StudioSystem system)
        {
            var released = 0;
            foreach (var registration in registrations)
            {
                var error = Release(registration, system);
                registration.Error = error;
                if (error.Length == 0)
                    released++;
                else
                    _lastError = error;
            }
            return released;
        }

        private static Resource Acquire(string path, StudioSystem system)
        {
            if (Resources.TryGetValue(path, out var existing))
            {
                Validate(existing, system);
                existing.References++;
                return existing;
            }

            // Only OK transfers ownership. Never adopt or unload an already-loaded bank.
            Check(system.loadBankFile(path, LOAD_BANK_FLAGS.NORMAL, out var bank), "Loading " + path);
            var resource = new Resource(path, system.handle, bank);
            Resources.Add(path, resource);
            return resource;
        }

        private static void Validate(Resource resource, StudioSystem system)
        {
            if (resource == null || resource.SystemHandle != system.handle)
                throw new InvalidOperationException("The bank belongs to a different or unavailable FMOD system.");
            if (!resource.Bank.isValid())
                throw new InvalidOperationException("The owned bank handle is no longer valid: " + resource.Path);
            Check(resource.Bank.getLoadingState(out var loadingState), "Reading bank loading state");
            if (loadingState != LOADING_STATE.LOADED)
                throw new InvalidOperationException("Bank metadata is not loaded: " + loadingState + " (" + resource.Path + ").");
        }

        private static void ValidateRegistration(Registration registration, StudioSystem system)
        {
            try
            {
                Validate(registration.Content, system);
                if (registration.Strings != null)
                    Validate(registration.Strings, system);
            }
            catch (Exception ex)
            {
                registration.Loaded = false;
                registration.Error = ex.Message;
                throw;
            }
        }

        private static string Release(Registration registration, StudioSystem system)
        {
            registration.Loaded = false;
            var errors = new List<string>();
            StopRegistrationPlaybacks(registration, system, errors);
            if (errors.Count != 0)
                return string.Join(" ", errors);
            try
            {
                ReleaseBankSamples(registration, system);
            }
            catch (Exception ex)
            {
                return "Releasing samples for " + registration.Path + ": " + ex.Message;
            }
            ReleaseResource(ref registration.Content, system, errors);
            ReleaseResource(ref registration.Strings, system, errors);
            if (registration.Content == null && registration.Strings == null)
                Banks.Remove(registration.Path);
            return string.Join(" ", errors);
        }

        private static void ReleaseResource(ref Resource resource, StudioSystem system, List<string> errors)
        {
            if (resource == null)
                return;
            try
            {
                if (resource.References > 1)
                {
                    resource.References--;
                    resource = null;
                    return;
                }
                if (resource.SystemHandle != system.handle)
                    throw new InvalidOperationException("FMOD system changed; refusing to unload a stale handle.");
                if (resource.Bank.isValid())
                    Check(resource.Bank.unload(), "Unloading " + resource.Path);
                Resources.Remove(resource.Path);
                resource = null;
            }
            catch (Exception ex)
            {
                errors.Add(resource.Path + ": " + ex.Message);
            }
        }

        private static StudioSystem GetSystem()
        {
            if (!FMOD_StudioSystem.TryGetInstance(out var host, false) || host == null || !host.isInitialized)
                throw new InvalidOperationException("The game's FMOD Studio system is not initialized.");
            var system = host.System;
            if (!system.isValid())
                throw new InvalidOperationException("The game's FMOD Studio system is not valid.");
            return system;
        }

        private static string ResolveBankPath(string modId, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(modId))
                throw new ArgumentException("A mod ID is required.", nameof(modId));
            var path = OpenACAISonsAssets.GetModAudioFilePath(modId, relativePath);
            if (!string.Equals(Path.GetExtension(path), ".bank", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Audio banks must use the .bank extension.", nameof(relativePath));
            return path;
        }

        private static bool TryBegin(string operation, out string status)
        {
            if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                status = "Audio bank operations require the initialized Unity lifecycle thread.";
                return false;
            }
            if (_stopping || _busy)
            {
                status = _stopping ? "Audio bank service is shutting down." : "An audio bank operation is already in progress.";
                return false;
            }
            _busy = true;
            _lastOperation = operation;
            _lastError = "";
            status = "";
            return true;
        }

        private static void Check(RESULT result, string operation)
        {
            if (result != RESULT.OK)
                throw new InvalidOperationException(operation + " failed: " + result + ".");
        }

        private static void End()
        {
            _busy = false;
            Publish();
        }

        private static void Publish()
        {
            var banks = Banks.Values.OrderBy(bank => bank.Path, StringComparer.OrdinalIgnoreCase).Select(bank => bank.Snapshot()).ToArray();
            var playbacks = Playbacks.Values.OrderBy(playback => playback.Id).Select(playback => playback.Snapshot()).ToArray();
            var state = new OpenACAISonsAudioBankState(_ownerThreadId != 0, _stopping, _lastOperation, _lastError, banks, playbacks);
            Volatile.Write(ref _state, state);
            OpenACAIModding.SetService(ServiceKey, state);
            OpenACAIEndnightRuntime.RequestLoaderStateRefresh("audio-banks-changed");
        }

        private sealed class Resource
        {
            internal readonly string Path;
            internal readonly IntPtr SystemHandle;
            internal readonly Bank Bank;
            internal int References = 1;

            internal Resource(string path, IntPtr systemHandle, Bank bank)
            {
                Path = path;
                SystemHandle = systemHandle;
                Bank = bank;
            }
        }

        private sealed class Registration
        {
            internal readonly string ModId;
            internal readonly string Path;
            internal readonly string StringsPath;
            internal Resource Content;
            internal Resource Strings;
            internal bool Loaded;
            internal bool SamplesRequested;
            internal string LastSampleLoadingState = "UNKNOWN";
            internal string Error = "";

            internal Registration(string modId, string path, string stringsPath)
            {
                ModId = modId;
                Path = path;
                StringsPath = stringsPath;
            }

            internal OpenACAISonsAudioBankSnapshot Snapshot() => new(ModId, Path, StringsPath, Loaded,
                Content != null, Strings != null, SamplesRequested, LastSampleLoadingState, Error);
        }
    }

    public sealed class OpenACAISonsAudioBankState
    {
        internal OpenACAISonsAudioBankState(bool initialized, bool stopping, string operation, string error,
            OpenACAISonsAudioBankSnapshot[] banks, OpenACAISonsAudioPlaybackSnapshot[] playbacks)
        {
            Initialized = initialized;
            Stopping = stopping;
            LastOperation = operation;
            LastError = error;
            Banks = Array.AsReadOnly(banks);
            Playbacks = Array.AsReadOnly(playbacks);
        }

        public bool Initialized { get; }
        public bool Stopping { get; }
        public string LastOperation { get; }
        public string LastError { get; }
        public IReadOnlyList<OpenACAISonsAudioBankSnapshot> Banks { get; }
        public IReadOnlyList<OpenACAISonsAudioPlaybackSnapshot> Playbacks { get; }
    }

    public sealed class OpenACAISonsAudioBankSnapshot
    {
        internal OpenACAISonsAudioBankSnapshot(string modId, string path, string stringsPath, bool loaded,
            bool ownsContent, bool ownsStrings, bool samplesRequested, string lastSampleLoadingState, string error)
        {
            ModId = modId;
            FullPath = path;
            StringsFullPath = stringsPath;
            Loaded = loaded;
            OwnsContent = ownsContent;
            OwnsStrings = ownsStrings;
            SamplesRequested = samplesRequested;
            LastSampleLoadingState = lastSampleLoadingState;
            Error = error;
        }

        public string ModId { get; }
        public string FullPath { get; }
        public string StringsFullPath { get; }
        public bool Loaded { get; }
        public bool OwnsContent { get; }
        public bool OwnsStrings { get; }
        public bool SamplesRequested { get; }
        public string LastSampleLoadingState { get; }
        public string Error { get; }
    }

    public sealed class OpenACAISonsAudioEventSnapshot
    {
        internal OpenACAISonsAudioEventSnapshot(string id, string path, string pathStatus)
        {
            Id = id;
            Path = path ?? "";
            PathStatus = pathStatus;
        }

        public string Id { get; }
        public string Path { get; }
        public string PathStatus { get; }
    }
}
