// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text.Json;
using BepInEx.Logging;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class StagedDirectory : IDisposable
    {
        private readonly string _destination;
        private readonly string _transactionId;
        private readonly string _intentPath;
        private bool _promoted;
        private bool _intentWritten;

        internal string StagingPath { get; }
        internal string BackupPath { get; }

        internal StagedDirectory(string destination)
        {
            _destination = Path.GetFullPath(destination);
            var parent = Path.GetDirectoryName(_destination);
            if (string.IsNullOrEmpty(parent))
                throw new ArgumentException("A filesystem root cannot be replaced.", nameof(destination));
            Directory.CreateDirectory(parent);
            _intentPath = _destination + ".openacai-replacement.json";
            if (File.Exists(_intentPath))
                throw new IOException("Recover the pending directory replacement before creating another: " + _intentPath);
            _transactionId = Guid.NewGuid().ToString("N");
            StagingPath = _destination + ".openacai-staging-" + _transactionId;
            BackupPath = _destination + ".openacai-backup-" + _transactionId;
            Directory.CreateDirectory(StagingPath);
        }

        // The caller holds the generation lock. Never infer ownership by scanning old backups.
        internal static string Recover(string destination)
        {
            destination = Path.GetFullPath(destination);
            var intentPath = destination + ".openacai-replacement.json";
            if (!File.Exists(intentPath))
                return "";
            RejectLink(intentPath);
            var intent = JsonSerializer.Deserialize<ReplacementIntent>(File.ReadAllText(intentPath));
            if (intent == null || intent.Schema != 1 || !Guid.TryParseExact(intent.TransactionId, "N", out var id))
                throw new InvalidDataException("Invalid directory replacement record; files were left untouched: " + intentPath);

            // Derive sibling paths from a validated ID instead of trusting paths from a file.
            var staging = destination + ".openacai-staging-" + id.ToString("N");
            var backup = destination + ".openacai-backup-" + id.ToString("N");
            RejectLinkIfPresent(destination);
            RejectLinkIfPresent(staging);
            RejectLinkIfPresent(backup);
            string detail;
            if (Directory.Exists(destination))
            {
                detail = "Interrupted replacement already has a destination; retaining it at " + destination;
            }
            else if (intent.HadPrevious && Directory.Exists(backup))
            {
                Directory.Move(backup, destination);
                detail = "Restored the previous directory after an interrupted replacement: " + destination;
            }
            else if (!intent.HadPrevious && Directory.Exists(staging))
            {
                Directory.Move(staging, destination);
                detail = "Completed the interrupted first installation: " + destination;
            }
            else
            {
                throw new IOException("Cannot recover directory replacement automatically. Files and intent are preserved at " + intentPath);
            }
            // The normal cache validator decides whether the recovered output is usable.
            TryRemoveIntent(intentPath);
            if (Directory.Exists(staging))
                detail += "; unpublished staging files retained at " + staging;
            if (Directory.Exists(backup))
                detail += "; previous files retained at " + backup;
            InteropGenerationDiagnostics.Log(LogLevel.Info, detail);
            return detail;
        }

        internal void Promote()
        {
            if (_promoted)
                throw new InvalidOperationException("This directory has already been promoted.");

            var hadPrevious = Directory.Exists(_destination);
            if (hadPrevious)
                RejectLink(_destination);
            RejectLink(StagingPath);
            WriteIntent(hadPrevious);

            try
            {
                if (hadPrevious)
                    Directory.Move(_destination, BackupPath);
                Directory.Move(StagingPath, _destination);
                _promoted = true;
                TryRemoveIntent(_intentPath);
            }
            catch (Exception promotionError)
            {
                if (hadPrevious && !Directory.Exists(_destination) && Directory.Exists(BackupPath))
                {
                    try
                    {
                        Directory.Move(BackupPath, _destination);
                    }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException("Directory replacement and rollback failed. " +
                            "The previous files are preserved at " + BackupPath, promotionError, rollbackError);
                    }
                }
                if (Directory.Exists(_destination) || !hadPrevious)
                    TryRemoveIntent(_intentPath);
                throw;
            }
        }

        private void WriteIntent(bool hadPrevious)
        {
            var temporary = _intentPath + ".tmp-" + _transactionId;
            var temporaryCreated = false;
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    temporaryCreated = true;
                    JsonSerializer.Serialize(stream, new ReplacementIntent
                    {
                        Schema = 1,
                        TransactionId = _transactionId,
                        HadPrevious = hadPrevious
                    });
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _intentPath);
                _intentWritten = true;
            }
            finally
            {
                if (temporaryCreated && File.Exists(temporary))
                {
                    try
                    {
                        File.Delete(temporary);
                    }
                    catch (Exception ex)
                    {
                        InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not remove replacement intent temporary file: " + temporary, ex);
                    }
                }
            }
        }

        private static void TryRemoveIntent(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not remove directory replacement intent: " + path, ex);
            }
        }

        private static void RejectLinkIfPresent(string path)
        {
            if (Directory.Exists(path) || File.Exists(path))
                RejectLink(path);
        }

        private static void RejectLink(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to move a linked replacement path: " + path);
        }

        public void Dispose()
        {
            // Only this operation's unpublished staging directory is disposable. Backups survive.
            if (_promoted || (_intentWritten && File.Exists(_intentPath)) || !Directory.Exists(StagingPath))
                return;
            try
            {
                Directory.Delete(StagingPath, recursive: true);
            }
            catch (Exception ex)
            {
                InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not remove interop staging directory: " + StagingPath, ex);
            }
        }
    }

    internal sealed class ReplacementIntent
    {
        public ReplacementIntent() { }
        public int Schema { get; set; }
        public string TransactionId { get; set; }
        public bool HadPrevious { get; set; }
    }
}
