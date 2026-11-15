// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using Modstoke.Sons;
using Application = UnityEngine.Application;

namespace Modstoke.Loader
{
    /// <summary>One photo on its way to disk. The pixels are rented from the shared array pool and the writer returns them.</summary>
    internal sealed class PhotoJob
    {
        internal byte[] Pixels;
        internal int Width, Height;
        /// <summary>The part of the frame that is kept, measured from its top-left corner.</summary>
        internal (int X, int Y, int Width, int Height) Crop;
        /// <summary>Rows arrive bottom row first, as Unity's texture data does.</summary>
        internal bool BottomUp;
        internal string Folder, Fallback;
        internal DateTimeOffset Taken;
        internal string Description, MetadataJson;
        internal bool Steam;
        /// <summary>The capture that hears the result; only touched on the game thread.</summary>
        internal PhotoCapture Owner;
    }

    /// <summary>
    /// Encodes photos to PNG on one low-priority thread, so a large shot never holds a game frame. Files are written
    /// as name.png.part and renamed when complete, so a crash or a full disk never leaves a broken picture under a
    /// photo's name. At most two photos wait at a time, which bounds the memory an 8K burst can tie up. The thread
    /// never touches Unity; results are collected on the game thread by <see cref="PhotoLibrary.Tick"/>.
    /// </summary>
    internal static class PhotoWriter
    {
        internal const string FilePrefix = "SonsOfTheForest_";
        private const int Capacity = 2;
        private static readonly TimeSpan QuitWait = TimeSpan.FromSeconds(3);
        private static readonly object Sync = new();
        private static readonly Queue<PhotoJob> Queue = new();
        private static readonly ConcurrentQueue<Written> Results = new();
        private static readonly ManualResetEventSlim Idle = new(true);
        private static int _outstanding;
        private static Thread _thread;
        private static Il2CppSystem.Action _onQuit;

        /// <summary>What became of one job. <see cref="Error"/> is the reason a failed photo was not saved.</summary>
        internal sealed record Written(PhotoJob Job, bool Saved, string Path, int Width, int Height, TimeSpan Encode, string Error, Exception Failure);

        /// <summary>True while another photo can be handed over.</summary>
        internal static bool HasRoom
        {
            get { lock (Sync) return _outstanding < Capacity; }
        }

        /// <summary>
        /// Starts the writer if it is not running; call on the game thread. Its first work is clearing what an
        /// earlier run left behind: unfinished .part files and the copies made for Steam, which has long taken them.
        /// </summary>
        internal static void Start()
        {
            lock (Sync)
            {
                if (_thread != null) return;
                var folders = new[] { PhotoLibrary.Folder(true), PhotoLibrary.Folder(false) };
                _thread = new Thread(() => Run(folders)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Modstoke photo writer" };
                _thread.Start();
            }
            try
            {
                _onQuit = (Il2CppSystem.Action)new Action(OnQuitting);
                Application.add_quitting(_onQuit);
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Photos still being written may be lost if the game quits: " + error.Message);
            }
        }

        /// <summary>Hands a job to the writer, which then owns its pixels; false when two photos are already waiting.</summary>
        internal static bool TryEnqueue(PhotoJob job)
        {
            ArgumentNullException.ThrowIfNull(job);
            Start();
            lock (Sync)
            {
                if (_outstanding >= Capacity) return false;
                _outstanding++;
                Idle.Reset();
                Queue.Enqueue(job);
                Monitor.Pulse(Sync);
                return true;
            }
        }

        internal static bool TryTake(out Written written) => Results.TryDequeue(out written);

        internal static bool HasResults => !Results.IsEmpty;

        /// <summary>Waits up to <paramref name="timeout"/> for every waiting photo to be written; false if time ran out.</summary>
        internal static bool Flush(TimeSpan timeout) => Idle.Wait(timeout);

        private static void OnQuitting()
        {
            try
            {
                if (!Flush(QuitWait))
                    RuntimeEntryPoint.LogSource?.LogWarning("The game quit before the last photo was written.");
            }
            catch (Exception)
            {
                // Called from the engine while it shuts down; nothing may escape.
            }
        }

        private static void Run(string[] folders)
        {
            CleanStale(folders);
            while (true)
            {
                PhotoJob job;
                lock (Sync)
                {
                    while (Queue.Count == 0) Monitor.Wait(Sync);
                    job = Queue.Dequeue();
                }
                Written written;
                try { written = Write(job); }
                catch (Exception error) { written = new Written(job, false, null, 0, 0, TimeSpan.Zero, error.Message, error); }
                finally
                {
                    if (job.Pixels != null) ArrayPool<byte>.Shared.Return(job.Pixels);
                    job.Pixels = null;
                }
                Results.Enqueue(written);
                lock (Sync)
                {
                    if (--_outstanding == 0) Idle.Set();
                }
            }
        }

        private static Written Write(PhotoJob job)
        {
            var started = Stopwatch.GetTimestamp();
            var stem = FilePrefix + job.Taken.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            FileStream stream;
            string part;
            try { stream = Reserve(job.Folder, stem, out part); }
            catch (Exception) when (job.Fallback != null && !string.Equals(job.Fallback, job.Folder, StringComparison.OrdinalIgnoreCase))
            {
                // A Pictures folder that is redirected, read-only or offline still leaves the Modstoke folder.
                stream = Reserve(job.Fallback, stem, out part);
            }

            try
            {
                using (stream)
                {
                    PngWriter.WriteRgb(stream, job.Pixels.AsSpan(0, job.Width * job.Height * 4), job.Width, job.Height, job.Crop, job.BottomUp, new[]
                    {
                        ("Software", "Sons of the Forest · Modstoke " + RuntimeEntryPoint.Version, false),
                        ("Creation Time", job.Taken.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture), false),
                        ("Description", job.Description ?? "", true),
                        ("Modstoke.Photo", job.MetadataJson ?? "{}", true)
                    });
                }
                var path = Commit(part, stem);
                return new Written(job, true, path, job.Crop.Width, job.Crop.Height, Stopwatch.GetElapsedTime(started), null, null);
            }
            catch
            {
                try { File.Delete(part); }
                catch (Exception) { }
                throw;
            }
        }

        // Claims the first free name: SonsOfTheForest_<time>.png, then _2, _3 and so on. Creating the .part file
        // with CreateNew is the claim, so two photos in the same second never share one.
        private static FileStream Reserve(string folder, string stem, out string part)
        {
            Directory.CreateDirectory(folder);
            for (var n = 1; n < 1000; n++)
            {
                var final = Path.Combine(folder, Name(stem, n));
                if (File.Exists(final)) continue;
                part = final + ".part";
                try { return new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16); }
                catch (IOException) when (File.Exists(part)) { }
            }
            throw new IOException("No free photo name is left for " + stem + ".");
        }

        private static string Commit(string part, string stem)
        {
            var folder = Path.GetDirectoryName(part);
            var final = part.Substring(0, part.Length - ".part".Length);
            for (var n = 2; ; n++)
            {
                try
                {
                    File.Move(part, final);
                    return final;
                }
                catch (IOException) when (File.Exists(final) && n < 1000)
                {
                    // Something else took the name while the photo was being written.
                    final = Path.Combine(folder, Name(stem, n));
                }
            }
        }

        private static string Name(string stem, int n) => (n == 1 ? stem : stem + "_" + n.ToString(CultureInfo.InvariantCulture)) + ".png";

        private static void CleanStale(string[] folders)
        {
            foreach (var folder in folders)
            {
                Delete(folder, FilePrefix + "*.png.part");
                Delete(Path.Combine(folder, PhotoLibrary.SteamCopies), FilePrefix + "*.jpg");
            }
        }

        private static void Delete(string folder, string pattern)
        {
            try
            {
                if (!Directory.Exists(folder)) return;
                foreach (var file in Directory.EnumerateFiles(folder, pattern, SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(file); }
                    catch (Exception) { }
                }
            }
            catch (Exception)
            {
                // Housekeeping only; a folder that cannot be listed is left alone.
            }
        }
    }
}
