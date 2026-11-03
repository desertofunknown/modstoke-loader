// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
// Adapted from BepInEx's Il2CppInteropManager.DownloadUnityAssemblies,
// Copyright (C) BepInEx contributors (upstream LGPL-2.1).

using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;

namespace Modstoke.Preloader
{
    internal static class UnityBaseLibraries
    {
        internal static void EnsureAvailable(string directory, string source, ManualLogSource log)
        {
            if (string.IsNullOrEmpty(source))
                return;

            var destination = Path.GetFullPath(directory);
            RejectReparsePointAncestors(destination);
            var isHttp = Uri.TryCreate(source, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            var fileName = Path.GetFileName(isHttp ? uri.AbsolutePath : source);
            if (string.IsNullOrEmpty(fileName) || fileName == "." || fileName == ".."
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Unity base libraries source must specify a valid archive filename.", nameof(source));

            string downloadPath = null;
            try
            {
                using (var staged = new StagedDirectory(destination))
                {
                    var cachePath = Path.Combine(staged.StagingPath, fileName);
                    var archivePath = Path.Combine(destination, fileName);
                    if (File.Exists(archivePath))
                    {
                        RejectReparsePointAncestors(archivePath);
                        File.Copy(archivePath, cachePath, overwrite: false);
                        archivePath = cachePath;
                    }
                    else
                    {
                        if (!isHttp)
                            throw new ArgumentException($"Unity base libraries source \"{source}\" is not a valid HTTP/HTTPS URL and its archive does not exist locally. Place the archive in \"{destination}\" for offline use.", nameof(source));

                        InteropGenerationDiagnostics.Log(LogLevel.Message, $"Downloading unity base libraries from {source}", source: log);
                        var temporaryPath = Path.Combine(Path.GetTempPath(), "openacai-unity-" + Guid.NewGuid().ToString("N") + ".zip");
                        RejectReparsePointAncestors(Path.GetDirectoryName(temporaryPath));
                        using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            // Ownership starts only after CreateNew succeeds.
                            downloadPath = temporaryPath;
                            Download(uri, output);
                        }
                        archivePath = downloadPath;
                    }

                    InteropGenerationDiagnostics.Log(LogLevel.Message, $"Extracting unity base libraries from {archivePath}", source: log);
                    RejectReparsePointAncestors(archivePath);
                    ExtractArchive(archivePath, staged.StagingPath, fileName);
                    ValidateAssemblies(staged.StagingPath);
                    if (Directory.Exists(destination))
                        CopyExistingDirectory(destination, staged.StagingPath, true);
                    if (downloadPath != null)
                        File.Copy(downloadPath, cachePath, overwrite: false);

                    RejectReparsePointAncestors(destination);
                    staged.Promote();
                    staged.DeleteBackup();
                }
            }
            finally
            {
                if (downloadPath != null)
                {
                    try
                    {
                        RejectReparsePointAncestors(downloadPath);
                        File.Delete(downloadPath);
                    }
                    catch (Exception ex)
                    {
                        InteropGenerationDiagnostics.Log(LogLevel.Warning, "Could not remove owned unity base libraries download: " + downloadPath, ex, log);
                    }
                }
            }
        }

        // A Unity managed-library set is tens of MB; these limits only stop runaway or hostile responses.
        private const long MaxArchiveBytes = 512L * 1024 * 1024;
        private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(100);

        // The timeout applies per read, so a slow but progressing first-run download still completes.
        private static void Download(Uri uri, Stream output)
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            using var idle = new CancellationTokenSource(ReadTimeout);
            using var response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, idle.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new InvalidDataException($"Unity base libraries archive is larger than {MaxArchiveBytes} bytes: {uri}");

            using var input = response.Content.ReadAsStreamAsync(idle.Token).GetAwaiter().GetResult();
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                idle.CancelAfter(ReadTimeout);
                var read = input.ReadAsync(buffer, idle.Token).AsTask().GetAwaiter().GetResult();
                if (read == 0)
                    return;
                total += read;
                if (total > MaxArchiveBytes)
                    throw new InvalidDataException($"Unity base libraries archive is larger than {MaxArchiveBytes} bytes: {uri}");
                output.Write(buffer, 0, read);
            }
        }

        private static void CopyExistingDirectory(string source, string destination, bool isRoot)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            {
                var attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                var target = Path.Combine(destination, Path.GetFileName(entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (File.Exists(target))
                        continue;
                    Directory.CreateDirectory(target);
                    CopyExistingDirectory(entry, target, false);
                }
                else if ((!isRoot || !IsDll(entry)) && !File.Exists(target) && !Directory.Exists(target))
                {
                    // The selected archive owns its entries; preserve unrelated support files.
                    File.Copy(entry, target, overwrite: false);
                }
            }
        }

        private static void ExtractArchive(string archivePath, string destination, string archiveName)
        {
            using (var input = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(input, ZipArchiveMode.Read))
            {
                var cachePath = Path.Combine(destination, archiveName);
                long extractedBytes = 0;
                foreach (var entry in archive.Entries)
                {
                    extractedBytes += entry.Length;
                    if (extractedBytes > MaxExtractedBytes)
                        throw new InvalidDataException($"Unity base libraries archive expands to more than {MaxExtractedBytes} bytes.");
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (unixType == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException($"Unity base libraries archive contains a symbolic link or reparse-point entry: {entry.FullName}");
                    if (string.Equals(Path.GetFullPath(Path.Combine(destination, entry.FullName)),
                        cachePath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Unity base libraries archive contains its own cache filename: " + entry.FullName);
                }

                // The framework checks traversal and duplicate files before support files are merged.
                archive.ExtractToDirectory(destination);
            }
        }

        private static void ValidateAssemblies(string directory)
        {
            var count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!IsDll(file))
                    continue;

                try
                {
                    AssemblyName.GetAssemblyName(file);
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException($"Extracted unity base library is not a readable managed assembly: {file}", ex);
                }
                count++;
            }

            if (count == 0)
                throw new InvalidDataException("Unity base libraries archive contains no top-level DLL assemblies.");
        }

        private static bool IsDll(string path)
        {
            return string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePointAncestors(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            RejectReparsePointAncestors(Path.GetDirectoryName(path));
            try
            {
                RejectReparsePoint(path, File.GetAttributes(path));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        private static void RejectReparsePoint(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Unity base libraries cannot use reparse-point entry: {path}");
        }
    }
}
