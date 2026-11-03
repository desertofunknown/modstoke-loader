// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal sealed class InteropGeneration
    {
        private readonly InteropSettings _settings;
        internal InteropGenerationState State { get; } = new InteropGenerationState();

        internal InteropGeneration(InteropSettings settings) => _settings = settings;

        internal void EnsureReady()
        {
            var elapsed = Stopwatch.StartNew();
            try
            {
                EnsureReadyCore();
            }
            catch (Exception ex)
            {
                State.Status = "failed";
                State.Error = InteropGenerationDiagnostics.Describe(ex);
                State.Detail = InteropGenerationDiagnostics.Message(ex);
                throw;
            }
            finally
            {
                State.ElapsedMilliseconds = elapsed.ElapsedMilliseconds;
                InteropGenerationDiagnostics.Log(LogLevel.Debug,
                    $"Interop cache {State.Status}: {State.ElapsedMilliseconds}ms total, " +
                    $"{State.CacheValidationMilliseconds}ms validation, {State.InputHashMilliseconds}ms input hashing, " +
                    $"{State.ConversionMilliseconds}ms conversion, {State.OutputValidationMilliseconds}ms output validation.");
            }
        }

        private void EnsureReadyCore()
        {
            State.Status = "checking-cache";
            var directory = _settings.InteropDirectory;
            var parent = Path.GetDirectoryName(directory);
            Directory.CreateDirectory(parent);
            // Serialize generation across processes using the same configured output directory.
            using var generationLock = new FileStream(directory + ".openacai-generation.lock",
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            State.InteropDirectory = directory;
            State.InteropRecovery = StagedDirectory.Recover(directory);
            var existingDirectory = Directory.Exists(directory);
            var existingUsable = TryReadExisting(directory, out var manifest, out var cacheIssue);
            State.CacheIssue = cacheIssue;

            if (!_settings.UpdateEnabled && existingDirectory)
            {
                if (!existingUsable)
                {
                    State.Status = "failed";
                    State.Detail = "Existing interop output is invalid and UpdateInteropAssemblies is disabled: " + cacheIssue;
                    throw new InvalidDataException(State.Detail);
                }
                State.Status = "updates-disabled";
                State.Detail = "Using validated existing files. Automatic updates are disabled; input freshness was not checked.";
                InteropGenerationDiagnostics.Log(LogLevel.Info, State.Detail);
                return;
            }

            try
            {
                State.BaseLibraryRecovery = StagedDirectory.Recover(_settings.BaseLibraryDirectory);
                // Without a manifest there is nothing to compare until the base libraries are ready.
                var inputHash = existingUsable && manifest != null ? ComputeInputHash() : null;
                if (inputHash != null && manifest.InputHash == inputHash)
                {
                    State.Status = "cache-current";
                    State.Detail = "Interop inputs and the complete recorded DLL/database set match the cache.";
                    State.InputHash = inputHash;
                    return;
                }

                State.Status = "generating";
                State.Detail = existingUsable
                    ? "Interop inputs changed or no Modstoke cache manifest exists; generating a new output set."
                    : "No complete usable interop output is available; generating a new output set.";
                InteropGenerationDiagnostics.Log(LogLevel.Info, State.Detail);

                UnityBaseLibraries.EnsureAvailable(_settings.BaseLibraryDirectory, _settings.BaseLibrarySource, PreloaderLog.Log);
                inputHash = ComputeInputHash();
                using var output = new StagedDirectory(directory);
                State.StagingDirectory = output.StagingPath;
                State.ConversionEnginesInvoked = true;
                var conversion = Stopwatch.StartNew();
                try { InteropConversion.Generate(_settings, output.StagingPath, inputHash); }
                finally { State.ConversionMilliseconds = conversion.ElapsedMilliseconds; }
                if (ComputeInputHash() != inputHash)
                    throw new IOException("Interop inputs changed during generation; the staged output will not be installed.");

                // The explicit BepInEx mode can reuse this marker when its generator is available.
                // It is not part of the owned cache's provenance or validation.
                var compatibilityHash = InteropCache.ComputeCompatibilityHash(_settings);
                if (compatibilityHash != null)
                    File.WriteAllText(Path.Combine(output.StagingPath, "assembly-hash.txt"), compatibilityHash);
                var validation = Stopwatch.StartNew();
                try { InteropCache.Write(output.StagingPath, inputHash); }
                finally { State.OutputValidationMilliseconds = validation.ElapsedMilliseconds; }
                State.BackupDirectory = output.BackupPath;
                output.Promote();
                // InteropCache.Write validated the promoted set; the previous one is only kept if it cannot be removed.
                output.DeleteBackup();
                State.BackupDirectory = Directory.Exists(output.BackupPath) ? output.BackupPath : "";
                State.InputHash = inputHash;
                State.Status = "generated";
                State.Detail = "Validated and installed a complete interop output set.";
                InteropGenerationDiagnostics.Log(LogLevel.Info, State.Detail);
            }
            catch (Exception ex)
            {
                State.Error = InteropGenerationDiagnostics.Describe(ex);
                // Recheck after a failed promotion: a failed rollback may have left only a backup.
                if (existingUsable && TryReadExisting(directory, out _, out _))
                {
                    State.Status = "generation-failed-using-existing";
                    State.Detail = "Generation failed. Continuing with validated output at the configured path; it may be stale.";
                    InteropGenerationDiagnostics.Log(LogLevel.Warning, State.Detail, ex);
                    return;
                }
                State.Status = "failed";
                State.Detail = "Generation failed and no validated output is available at the configured interop path.";
                throw;
            }
        }

        private string ComputeInputHash()
        {
            var elapsed = Stopwatch.StartNew();
            try { return InteropCache.ComputeInputHash(_settings); }
            finally { State.InputHashMilliseconds += elapsed.ElapsedMilliseconds; }
        }

        private bool TryReadExisting(string directory, out InteropCacheManifest manifest, out string issue)
        {
            var elapsed = Stopwatch.StartNew();
            manifest = null;
            try
            {
                manifest = InteropCache.ReadAndValidate(directory);
                issue = manifest == null ? "Legacy output has no Modstoke cache manifest." : "";
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException
                || ex is BadImageFormatException || ex is JsonException || ex is ArgumentException)
            {
                issue = InteropGenerationDiagnostics.Message(ex);
                return false;
            }
            finally { State.CacheValidationMilliseconds += elapsed.ElapsedMilliseconds; }
        }
    }

    internal sealed class InteropGenerationState
    {
        public string Status { get; internal set; } = "not-started";
        public string Detail { get; internal set; } = "";
        public string InteropDirectory { get; internal set; } = "";
        public string StagingDirectory { get; internal set; } = "";
        public string BackupDirectory { get; internal set; } = "";
        public string InputHash { get; internal set; } = "";
        public string CacheIssue { get; internal set; } = "";
        public string Error { get; internal set; } = "";
        public string InteropRecovery { get; internal set; } = "";
        public string BaseLibraryRecovery { get; internal set; } = "";
        public bool ConversionEnginesInvoked { get; internal set; }
        public long ElapsedMilliseconds { get; internal set; }
        public long CacheValidationMilliseconds { get; internal set; }
        public long InputHashMilliseconds { get; internal set; }
        public long ConversionMilliseconds { get; internal set; }
        public long OutputValidationMilliseconds { get; internal set; }
    }
}
