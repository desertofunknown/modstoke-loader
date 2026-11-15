// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Modstoke.Loader
{
    /// <summary>
    /// The season a saved world is in, read from its SaveData.zip on a worker. The weather system writes its state as
    /// JSON inside a string of the save's JSON, so the value is matched as text, escaped quote or not.
    /// </summary>
    internal static class SaveSeason
    {
        private static readonly Regex Current = new(@"""_currentSeason\\?""\s*:\s*(\d)", RegexOptions.Compiled);

        /// <summary>Spring 0, Summer 1, Fall 2 or Winter 3 for the save in <paramref name="folder"/>; -1 when it can't be read.</summary>
        internal static int Read(string folder)
        {
            try
            {
                using var zip = new ZipArchive(File.OpenRead(Path.Combine(folder, "SaveData.zip")), ZipArchiveMode.Read);
                var entry = zip.GetEntry("WeatherSystemSaveData.json");
                if (entry == null) return -1;
                using var reader = new StreamReader(entry.Open());
                var match = Current.Match(reader.ReadToEnd());
                // The game leaves values at their default out of the file, and Spring is 0.
                if (!match.Success) return 0;
                var season = match.Groups[1].Value[0] - '0';
                return season <= 3 ? season : -1;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return -1;
            }
        }
    }
}
