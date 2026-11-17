// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Modstoke.Sons;
using Steamworks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    /// <summary>
    /// Where photos live and what happens once one is written: it goes into the player's Steam screenshot
    /// library when they want that, and the capture that took it hears the outcome. Runs on the game thread from
    /// the front end's tick, also after photo mode has closed, so a photo taken on the way out still arrives.
    /// </summary>
    internal static class PhotoLibrary
    {
        /// <summary>The image format handed to Steam.</summary>
        internal enum ImageFormat { Png, Jpeg }

        /// <summary>The hidden folder beside the photos that holds the JPEG copies made for Steam.</summary>
        internal const string SteamCopies = ".steam";
        private const int JpegQuality = 92;
        private const int JpegLongEdge = 3840;

        private sealed record Conversion(PhotoWriter.Written Written, Task<PngTextures.Image> Decode);

        private static readonly List<Conversion> Conversions = new();
        private static bool _refusalLogged;

        /// <summary>
        /// PNG until Steam refuses one, then JPEG for the rest of the game run: a copy is made from the saved photo,
        /// at most 3840 pixels on its long edge, and the photo itself stays the PNG.
        /// </summary>
        internal static ImageFormat SteamFormat { get; set; } = ImageFormat.Png;

        /// <summary>
        /// The photo folder: Pictures\Sons Of The Forest, or Modstoke\UserData\Photos when <paramref name="pictures"/>
        /// is false or Windows has no Pictures folder. A Pictures folder that cannot be written falls back to the
        /// Modstoke one when the photo is saved.
        /// </summary>
        internal static string Folder(bool pictures)
        {
            if (pictures)
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(root)) return Path.Combine(root, "Sons Of The Forest");
            }
            return Path.Combine(LoaderPaths.UserDataDirectory, "Photos");
        }

        /// <summary>Shows the photo folder in Explorer, creating it first so there is something to open.</summary>
        internal static void OpenFolder(bool pictures)
        {
            try
            {
                var folder = Folder(pictures);
                try { Directory.CreateDirectory(folder); }
                catch (Exception) when (pictures)
                {
                    folder = Folder(false);
                    Directory.CreateDirectory(folder);
                }
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                start.ArgumentList.Add(folder);
                using (Process.Start(start)) { }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The photo folder could not be opened: " + error.Message);
            }
        }

        /// <summary>Collects written photos and finishes them; call once a frame on the game thread.</summary>
        internal static void Tick()
        {
            try
            {
                while (PhotoWriter.TryTake(out var written)) Finish(written);
                for (var i = Conversions.Count - 1; i >= 0; i--)
                {
                    var conversion = Conversions[i];
                    if (!conversion.Decode.IsCompleted) continue;
                    Conversions.RemoveAt(i);
                    SendJpeg(conversion);
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogError("Photo results could not be collected: " + error);
            }
        }

        private static void Finish(PhotoWriter.Written written)
        {
            var steam = false;
            try
            {
                if (!written.Saved)
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("A photo could not be saved: " + written.Failure);
                    return;
                }
                Timings.RecordDuration("Photo", "Encode", written.Encode);
                if (written.Job.Steam && SteamRunning())
                {
                    if (SteamFormat == ImageFormat.Png)
                    {
                        steam = AddToSteam(written.Path, written.Width, written.Height, written.Job.Description);
                        if (steam) return;
                        SteamFormat = ImageFormat.Jpeg;
                        if (!_refusalLogged)
                        {
                            _refusalLogged = true;
                            RuntimeEntryPoint.LogSource?.LogWarning("Steam did not take a PNG photo; it gets JPEG copies from now on.");
                        }
                    }
                    // PngTextures' mip chain doubles as the downscale for photos larger than Steam's copy.
                    var mipmaps = Math.Max(written.Width, written.Height) > JpegLongEdge;
                    var path = written.Path;
                    Conversions.Add(new Conversion(written, Task.Run(() => PngTextures.DecodePooled(path, mipmaps))));
                    written = null;
                }
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A photo could not be added to Steam: " + error.Message);
            }
            finally
            {
                if (written != null) Report(written, steam);
            }
        }

        private static void SendJpeg(Conversion conversion)
        {
            var steam = false;
            try
            {
                var written = conversion.Written;
                using var image = conversion.Decode.GetAwaiter().GetResult();
                int width = image.Width, height = image.Height;
                var offset = 0L;
                for (var level = 1; level < image.Mips && Math.Max(width, height) > JpegLongEdge; level++)
                {
                    offset += (long)width * height * 4;
                    width = Math.Max(1, width / 2);
                    height = Math.Max(1, height / 2);
                }
                var jpeg = Path.Combine(Path.GetDirectoryName(written.Path), SteamCopies, Path.GetFileNameWithoutExtension(written.Path) + ".jpg");
                WriteJpeg(jpeg, image.Data.AsSpan((int)offset, width * height * 4), width, height);
                steam = AddToSteam(jpeg, width, height, written.Job.Description);
                if (!steam) RuntimeEntryPoint.LogSource?.LogWarning("Steam did not take the JPEG copy of a photo either.");
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("A photo could not be added to Steam: " + error.Message);
            }
            finally
            {
                Report(conversion.Written, steam);
            }
        }

        // Unity's encoder needs a readable texture on the game thread, so this costs a hitch; it only runs once
        // Steam has refused a PNG.
        private static unsafe void WriteJpeg(string path, ReadOnlySpan<byte> rgba, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            try
            {
                fixed (byte* pixels = rgba)
                    texture.LoadRawTextureData((IntPtr)pixels, rgba.Length);
                Il2CppStructArray<byte> encoded = ImageConversion.EncodeToJPG(texture, JpegQuality);
                if (encoded == null || encoded.Length == 0) throw new InvalidOperationException("Unity returned no JPEG data.");
                var folder = Directory.CreateDirectory(Path.GetDirectoryName(path));
                folder.Attributes |= FileAttributes.Hidden;
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                // The engine's array data starts after its object header, bounds and length words.
                file.Write(new ReadOnlySpan<byte>((void*)IntPtr.Add(encoded.Pointer, 4 * IntPtr.Size), encoded.Length));
            }
            finally
            {
                Object.Destroy(texture);
            }
        }

        // Steam copies the file in the background, so it stays where it is; the next game run clears old copies.
        private static bool AddToSteam(string path, int width, int height, string location)
        {
            var handle = SteamScreenshots.AddScreenshotToLibrary(path, null, width, height);
            if (handle.m_ScreenshotHandle == 0) return false;
            if (!string.IsNullOrWhiteSpace(location)) SteamScreenshots.SetLocation(handle, location);
            return true;
        }

        private static bool SteamRunning()
        {
            try { return SteamAPI.IsSteamRunning(); }
            catch (Exception) { return false; }
        }

        private static void Report(PhotoWriter.Written written, bool steam)
        {
            var owner = written.Job.Owner;
            if (owner != null) owner.Finish(written.Saved, written.Path, steam, written.Error);
        }
    }
}
