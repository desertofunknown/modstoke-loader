// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Modstoke.Localization;
using Modstoke.Sons;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace Modstoke.Loader
{
    /// <summary>
    /// What the loading screen would best show: the world being loaded (<see cref="LoadingIntent.WorldOf"/>), its season
    /// and day or night (-1 unknown).
    /// </summary>
    internal readonly record struct ArtWish(string World, int Season, int Light);

    /// <summary>A picture ready on the GPU for the loading screen, and how to frame it.</summary>
    internal sealed class LoadingPicture
    {
        internal Texture2D Texture;
        /// <summary>The point to keep in frame, u from the left and v from the top.</summary>
        internal Vector2 Focus = new(0.5f, 0.5f);
        /// <summary>Rows run from the top, as a DDS file stores them, so the picture is drawn flipped.</summary>
        internal bool TopDown;
        internal string Key = "", World = "", Source = "";
        internal int Season = -1, Light = -1;
        internal bool Photo;
        /// <summary>The photo's own description ("Day 12 · 18:40 · Spring") or date, for the credit line.</summary>
        internal string Where;
    }

    /// <summary>
    /// The loading screen's pictures: the player's photos of the world being loaded, Modstoke's own shots of the island
    /// (BC7 files under Runtime/Art/Loading with a list naming each one's season, light and focal point), then the
    /// game's blurred title picture. Files are read and decoded on workers; the game thread only uploads, and only at
    /// the title or in the lobby while the menu is idle. At most two prepared pictures are kept, and every one is
    /// destroyed once the screen has gone.
    /// </summary>
    internal static class LoadingArt
    {
        // Photo mode writes its text chunks in the first couple of kilobytes, before the image data.
        private const int PhotoScan = 200, PhotoHead = 8 * 1024, WorldPhotos = 6, Kept = 2;
        // Photo mode also crops square and portrait; those would show as a strip blown up to fill the screen.
        private const double PhotoAspect = 1.3;
        private const string ManifestName = "loading-art.json", TitleTexture = "Blurred Title Screen";
        // The same grade tools/build-loading-art.py gives the island set, so a photo sits under the veils like the
        // curated shots: 8 % less saturation in linear light, then a soft S-curve on the encoded values.
        private const float Saturation = 0.92f, Contrast = 2.5f, Pivot = 0.40f;
        private static readonly float[] ToLinear = BuildToLinear();
        private static readonly byte[] Graded = BuildGraded();

        // Workers fill these, so they hold no Unity types: even Unity's maths runs through the engine here.
        private sealed record Shot(string Path, int Season, int Light, float U, float V);

        private sealed record Photo(string Path, string World, int Season, int Light, string Description, DateTime Taken);

        // Plain data from a worker: BC7 blocks or RGBA32 rows, nothing of Unity's.
        private sealed record Decoded(byte[] Data, int Width, int Height, bool Bc7, bool Linear);

        private sealed record Wanted(string Key, bool IsPhoto, Shot Shot, Photo Photo);

        private static readonly List<LoadingPicture> Resident = new();
        private static readonly HashSet<string> Broken = new(StringComparer.OrdinalIgnoreCase);
        private static List<Shot> _shots = new();
        private static List<Photo> _photos = new();
        private static Task<List<Shot>> _shotsRead;
        private static Task<List<Photo>> _photosRead;
        private static Task<Decoded> _decoding;
        private static Wanted _decodingFor, _wanted;
        private static ArtWish _wish = new("", -1, -1);
        private static Texture2D _title;

        /// <summary>The loadingArt setting: "all" (this world's photos, then Modstoke's), "modstoke" or "photos" (any world's first).</summary>
        internal static string Setting { get; set; } = "all";

        /// <summary>The picture shown last, never picked twice running when there is another; kept in the screen's record.</summary>
        internal static string Last { get; set; } = "";

        /// <summary>
        /// A visit to the title or the lobby has settled: the art list and the photo index are read again on workers,
        /// and the game's title picture is found while its scene is loaded.
        /// </summary>
        internal static void Begin()
        {
            // Each list on its own, so a broken manifest still leaves the photos, and a photo folder that can't be read
            // still leaves Modstoke's shots.
            if (_shotsRead == null)
            {
                var folder = LoaderPaths.LoadingArtDirectory;
                _shotsRead = Task.Run(() => ReadManifest(folder));
            }
            if (_photosRead == null)
            {
                var photoFolders = new[] { PhotoLibrary.Folder(true), PhotoLibrary.Folder(false) };
                _photosRead = Task.Run(() => IndexPhotos(photoFolders));
            }
            if (_title == null || !_title)
            {
                _title = null;
                foreach (var texture in Resources.FindObjectsOfTypeAll<Texture2D>())
                    if (texture != null && texture.name == TitleTexture)
                    {
                        _title = texture;
                        break;
                    }
            }
        }

        /// <summary>What to prepare next; the picture for it is read and decoded in the background.</summary>
        internal static void Want(ArtWish wish)
        {
            wish = wish with { World = wish.World ?? "" };
            if (wish == _wish && _wanted != null) return;
            _wish = wish;
            _wanted = Choose(_wish);
        }

        /// <summary>
        /// Takes in finished work; with <paramref name="idle"/> (no menu animation and no input for a moment) it may
        /// upload one picture, about a millisecond for a curated shot and a few for a photo. Title and lobby only.
        /// </summary>
        internal static void Tick(bool idle)
        {
            var listed = Collect(ref _shotsRead, ref _shots, "Loading screen pictures");
            listed |= Collect(ref _photosRead, ref _photos, "Photos for the loading screen");
            if (listed) _wanted = Choose(_wish);
            if (_decoding != null)
            {
                if (!_decoding.IsCompleted || !idle && _decoding.Status == TaskStatus.RanToCompletion) return;
                var task = _decoding;
                var wanted = _decodingFor;
                _decoding = null;
                try
                {
                    Keep(Upload(task.GetAwaiter().GetResult(), wanted));
                }
                catch (Exception error)
                {
                    // A file that fails once is passed over for the rest of the game run.
                    Broken.Add(wanted.Key);
                    RuntimeEntryPoint.LogSource?.LogWarning("Loading screen picture " + Path.GetFileName(wanted.Key) + " could not be used: "
                        + error.Message);
                    _wanted = Choose(_wish);
                }
                return;
            }
            if (_wanted != null && !Resident.Any(picture => picture.Key == _wanted.Key))
            {
                var wanted = _decodingFor = _wanted;
                var lines = Screen.height;
                _decoding = Task.Run(() => wanted.IsPhoto ? DecodePhoto(wanted.Photo.Path, lines) : ReadDds(wanted.Shot.Path, lines));
            }
        }

        // A list read on a worker, taken in once done; true when it arrived. One that failed is logged and the last kept.
        private static bool Collect<T>(ref Task<List<T>> reading, ref List<T> list, string what)
        {
            if (reading is not { IsCompleted: true }) return false;
            var task = reading;
            reading = null;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                list = task.Result;
                return true;
            }
            RuntimeEntryPoint.LogSource?.LogWarning(what + " could not be listed: " + task.Exception?.GetBaseException().Message);
            return false;
        }

        /// <summary>
        /// The best picture already on the GPU for this load, or the game's title picture, or null for the plain dark
        /// base. Nothing is read or uploaded while the screen is up, and the picture is the screen's until it hides.
        /// </summary>
        internal static LoadingPicture Take(ArtWish wish)
        {
            wish = wish with { World = wish.World ?? "" };
            LoadingPicture best = null;
            var bestScore = -1;
            foreach (var picture in Resident)
            {
                var score = Score(picture, wish);
                if (score <= bestScore) continue;
                best = picture;
                bestScore = score;
            }
            if (best != null)
            {
                Last = best.Key;
                return best;
            }
            return _title != null && _title
                ? new LoadingPicture { Texture = _title, Key = TitleTexture, Source = "the game's title picture" }
                : null;
        }

        /// <summary>Destroys every prepared picture; the next visit to the title starts again with no wish.</summary>
        internal static void Release()
        {
            foreach (var picture in Resident)
                if (picture.Texture != null && picture.Texture) Object.Destroy(picture.Texture);
            Resident.Clear();
            // A decode still running or waiting for an idle frame goes too, so its pixels are not held through a game.
            _decoding = null;
            _decodingFor = null;
            _wish = new ArtWish("", -1, -1);
            _wanted = null;
        }

        // A photo of this world comes first, then with "photos" any other photo, then Modstoke's shots; the season and
        // the light break ties, then the picture chosen for this wish, and the one shown last loses.
        private static int Score(LoadingPicture picture, ArtWish wish)
        {
            int score;
            if (picture.Photo)
            {
                if (Setting == "modstoke") return -1;
                var ours = wish.World.Length > 0 && picture.World == wish.World;
                if (!ours && Setting != "photos") return -1;
                score = ours ? 100 : 60;
            }
            else score = 40;
            if (wish.Season >= 0 && picture.Season == wish.Season) score += 10;
            if (wish.Light >= 0 && picture.Light == wish.Light) score += 5;
            if (_wanted != null && picture.Key == _wanted.Key) score += 3;
            if (picture.Key == Last) score -= 2;
            return score;
        }

        private static Wanted Choose(ArtWish wish)
        {
            var photo = ChoosePhoto(wish);
            if (photo != null) return new Wanted(photo.Path, true, null, photo);
            var shot = ChooseShot(wish);
            return shot != null ? new Wanted(shot.Path, false, shot, null) : null;
        }

        // Among the newest photos of this world, preferring its season; with "photos", any world's when this one has none.
        private static Photo ChoosePhoto(ArtWish wish)
        {
            if (Setting == "modstoke") return null;
            var usable = _photos.Where(photo => !Broken.Contains(photo.Path)).ToList();
            var pool = wish.World.Length > 0 ? usable.Where(photo => photo.World == wish.World).Take(WorldPhotos).ToList() : new List<Photo>();
            if (pool.Count == 0 && Setting == "photos") pool = usable.Take(WorldPhotos).ToList();
            if (pool.Count == 0) return null;
            var seasonal = pool.Where(photo => wish.Season >= 0 && photo.Season == wish.Season).ToList();
            return Pick(seasonal.Count > 0 ? seasonal : pool, photo => photo.Path);
        }

        // Season and light both, then the season, then the light, then any: the set grows a season at a time, and a
        // load with nothing to match still gets a picture.
        private static Shot ChooseShot(ArtWish wish)
        {
            var usable = _shots.Where(shot => !Broken.Contains(shot.Path)).ToList();
            foreach (var (season, light) in new[] { (true, true), (true, false), (false, true), (false, false) })
            {
                var matches = usable.Where(shot => (!season || wish.Season < 0 || shot.Season == wish.Season)
                    && (!light || wish.Light < 0 || shot.Light == wish.Light)).ToList();
                if (matches.Count > 0) return Pick(matches, shot => shot.Path);
            }
            return null;
        }

        private static T Pick<T>(List<T> choices, Func<T, string> key)
        {
            if (choices.Count > 1) choices.RemoveAll(choice => key(choice) == Last);
            return choices[Random.Range(0, choices.Count)];
        }

        // Kept beside the one just made: the picture wanted now, then the newest; the rest are destroyed.
        private static void Keep(LoadingPicture picture)
        {
            Resident.Add(picture);
            while (Resident.Count > Kept)
            {
                var index = Resident.FindIndex(old => _wanted == null || old.Key != _wanted.Key);
                var old = Resident[index];
                Resident.RemoveAt(index);
                if (old.Texture != null && old.Texture) Object.Destroy(old.Texture);
            }
        }

        private static LoadingPicture Upload(Decoded decoded, Wanted wanted)
        {
            var texture = new Texture2D(decoded.Width, decoded.Height, decoded.Bc7 ? TextureFormat.BC7 : TextureFormat.RGBA32, false, decoded.Linear)
            {
                name = "Modstoke Loading Art",
                wrapMode = TextureWrapMode.Clamp,
                // Survives the unused-asset sweep of the scene loads it is shown over.
                hideFlags = HideFlags.HideAndDontSave
            };
            try
            {
                var pin = GCHandle.Alloc(decoded.Data, GCHandleType.Pinned);
                try { texture.LoadRawTextureData(pin.AddrOfPinnedObject(), decoded.Data.Length); }
                finally { pin.Free(); }
                texture.Apply(false, true);
            }
            catch
            {
                Object.Destroy(texture);
                throw;
            }
            if (!wanted.IsPhoto)
                return new LoadingPicture
                {
                    Texture = texture, Key = wanted.Key, Season = wanted.Shot.Season, Light = wanted.Shot.Light,
                    Focus = new Vector2(wanted.Shot.U, wanted.Shot.V), TopDown = true,
                    Source = "Modstoke's " + Path.GetFileNameWithoutExtension(wanted.Key)
                };
            var photo = wanted.Photo;
            return new LoadingPicture
            {
                Texture = texture, Key = wanted.Key, World = photo.World, Season = photo.Season, Light = photo.Light, Photo = true,
                Where = photo.Description.Length > 0 ? photo.Description : photo.Taken.ToString("d", Languages.CultureOf(Languages.Current)),
                Source = photo.World.Length > 0 && photo.World == _wish.World ? "a photo of this world" : "a photo"
            };
        }

        // { "version": 1, "art": [ { "file", "season", "light", "focus": [u, v] } ] }; an entry this build can't use is skipped.
        private static List<Shot> ReadManifest(string folder)
        {
            var shots = new List<Shot>();
            var path = Path.Combine(folder, ManifestName);
            if (!File.Exists(path)) return shots;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var number) || number != 1 || !root.TryGetProperty("art", out var art) || art.ValueKind != JsonValueKind.Array)
                return shots;
            foreach (var entry in art.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.String)
                    continue;
                var name = file.GetString();
                if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
                var season = entry.TryGetProperty("season", out var seasonValue) && seasonValue.ValueKind == JsonValueKind.String
                    ? LoadingIntent.SeasonOf(seasonValue.GetString()) : -1;
                var light = entry.TryGetProperty("light", out var lightValue) && lightValue.ValueKind == JsonValueKind.String
                    ? lightValue.GetString() switch { "day" => 0, "night" => 1, _ => -1 } : -1;
                float u = 0.5f, v = 0.5f;
                if (entry.TryGetProperty("focus", out var point) && point.ValueKind == JsonValueKind.Array && point.GetArrayLength() == 2
                    && point[0].ValueKind == JsonValueKind.Number && point[1].ValueKind == JsonValueKind.Number
                    && point[0].TryGetSingle(out var x) && point[1].TryGetSingle(out var y))
                {
                    u = Math.Clamp(x, 0f, 1f);
                    v = Math.Clamp(y, 0f, 1f);
                }
                var full = Path.Combine(folder, name);
                if (File.Exists(full)) shots.Add(new Shot(full, season, light, u, v));
            }
            return shots;
        }

        // The newest landscape photos in both photo folders, newest first, from the chunks before the image data: size
        // and format, the description Steam shows, and Modstoke's record of the world it was taken in.
        private static List<Photo> IndexPhotos(string[] folders)
        {
            var files = new List<FileInfo>();
            foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
                if (Directory.Exists(folder))
                    files.AddRange(new DirectoryInfo(folder).EnumerateFiles(PhotoWriter.FilePrefix + "*.png", SearchOption.TopDirectoryOnly));
            var photos = new List<Photo>();
            var head = new byte[PhotoHead];
            foreach (var file in files.OrderByDescending(file => file.LastWriteTimeUtc).Take(PhotoScan))
            {
                try
                {
                    int read;
                    using (var stream = file.OpenRead()) read = stream.ReadAtLeast(head, head.Length, false);
                    var photo = ReadPhoto(file, head.AsSpan(0, read));
                    if (photo != null) photos.Add(photo);
                }
                catch (IOException)
                {
                    // A photo still being written or gone since the listing.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            return photos;
        }

        private static Photo ReadPhoto(FileInfo file, ReadOnlySpan<byte> png)
        {
            if (png.Length < 33 || !png.Slice(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return null;
            string description = "", record = null;
            var usable = false;
            for (var offset = 8; offset + 8 <= png.Length;)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(png.Slice(offset));
                var type = png.Slice(offset + 4, 4);
                var start = offset + 8;
                // Written as a difference, so a corrupt length near int.MaxValue can't wrap past the check.
                if (length < 0 || type.SequenceEqual("IDAT"u8) || length > png.Length - start) break;
                var data = png.Slice(start, length);
                // Only what PngTextures decodes (8-bit, non-interlaced RGB or RGBA), and wide enough to fill the screen.
                if (type.SequenceEqual("IHDR"u8))
                    usable = length >= 13 && data[8] == 8 && data[12] == 0 && data[9] is 2 or 6
                        && BinaryPrimitives.ReadUInt32BigEndian(data) >= PhotoAspect * BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4));
                else if (type.SequenceEqual("tEXt"u8) || type.SequenceEqual("iTXt"u8))
                {
                    var (key, text) = Text(data, type[0] == (byte)'i');
                    if (key == "Description") description = text;
                    else if (key == "Modstoke.Photo") record = text;
                }
                offset = start + length + 4;
            }
            if (!usable) return null;
            var place = "";
            int season = -1, light = -1;
            if (record != null)
            {
                try
                {
                    using var document = JsonDocument.Parse(record, new JsonDocumentOptions { MaxDepth = 16 });
                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("world", out var world) && world.ValueKind == JsonValueKind.Object)
                    {
                        var gameId = world.TryGetProperty("gameId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                        var saveType = world.TryGetProperty("saveType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
                        var saveId = world.TryGetProperty("saveId", out var slot) && slot.ValueKind == JsonValueKind.Number && slot.TryGetUInt32(out var number)
                            ? number : 0u;
                        place = LoadingIntent.WorldOf(gameId, saveType, saveId);
                        if (world.TryGetProperty("season", out var name) && name.ValueKind == JsonValueKind.String) season = LoadingIntent.SeasonOf(name.GetString());
                        if (world.TryGetProperty("time", out var time) && time.ValueKind == JsonValueKind.String
                            && TimeSpan.TryParseExact(time.GetString(), "c", CultureInfo.InvariantCulture, out var clock))
                            light = LoadingIntent.LightAt(clock.Hours);
                    }
                }
                catch (JsonException)
                {
                    // A photo without a readable record still shows with the "photos" setting.
                }
            }
            return new Photo(file.FullName, place, season, light, description, file.LastWriteTime);
        }

        // tEXt is keyword, NUL, Latin-1 text; iTXt adds compression flag and method, a language tag and a translated
        // keyword before UTF-8 text. Photo mode writes its iTXt uncompressed.
        private static (string Key, string Text) Text(ReadOnlySpan<byte> data, bool international)
        {
            var end = data.IndexOf((byte)0);
            if (end <= 0) return ("", "");
            var key = Encoding.Latin1.GetString(data.Slice(0, end));
            var rest = data.Slice(end + 1);
            if (!international) return (key, Encoding.Latin1.GetString(rest));
            if (rest.Length < 2 || rest[0] != 0) return (key, "");
            rest = rest.Slice(2);
            for (var skip = 0; skip < 2; skip++)
            {
                var nul = rest.IndexOf((byte)0);
                if (nul < 0) return (key, "");
                rest = rest.Slice(nul + 1);
            }
            return (key, Encoding.UTF8.GetString(rest));
        }

        // A DX10 DDS of BC7 blocks, top row first, its levels one after another. Only the smallest level at least 0.9
        // of the screen's height is read, or the top one for a taller screen.
        private static Decoded ReadDds(string path, int screenHeight)
        {
            using var file = File.OpenRead(path);
            Span<byte> header = stackalloc byte[148];
            file.ReadExactly(header);
            if (!header.Slice(0, 4).SequenceEqual("DDS "u8) || BinaryPrimitives.ReadInt32LittleEndian(header.Slice(4)) != 124
                || !header.Slice(84, 4).SequenceEqual("DX10"u8))
                throw new InvalidDataException("Not a DX10 DDS file.");
            var format = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(128));
            if (format != 98 && format != 99) throw new InvalidDataException("DXGI format " + format + " is not BC7.");
            var height = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(12));
            var width = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(16));
            var levels = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(header.Slice(28)), 1, 16);
            if (width < 4 || height < 4 || width > 16384 || height > 16384) throw new InvalidDataException("Unsupported DDS size.");
            long offset = header.Length, chosenOffset = offset;
            int chosenWidth = width, chosenHeight = height;
            for (var level = 0; level < levels; level++)
            {
                int w = Math.Max(1, width >> level), h = Math.Max(1, height >> level);
                if (level > 0 && h >= screenHeight * 0.9f)
                {
                    chosenOffset = offset;
                    chosenWidth = w;
                    chosenHeight = h;
                }
                offset += LevelSize(w, h);
            }
            var size = LevelSize(chosenWidth, chosenHeight);
            if (chosenOffset + size > file.Length) throw new InvalidDataException("The DDS file is shorter than its levels.");
            var data = GC.AllocateUninitializedArray<byte>(size);
            file.Position = chosenOffset;
            file.ReadExactly(data);
            return new Decoded(data, chosenWidth, chosenHeight, true, format == 98);
        }

        private static int LevelSize(int width, int height) => Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 16;

        // At most as many lines as the screen shows, between 1080 and 1440, box-filtered in linear light and graded.
        // The full-size pixels are only read by the filter, so they come from the pool and go back to it.
        private static Decoded DecodePhoto(string path, int screenHeight)
        {
            using var image = PngTextures.DecodePooled(path, false);
            var lines = Math.Min(image.Height, Math.Max(1080, Math.Min(1440, screenHeight)));
            var width = Math.Max(1, (int)Math.Round(image.Width * (double)lines / image.Height));
            return new Decoded(Shrink(image.Data, image.Width, image.Height, width, lines), width, lines, false, false);
        }

        private static byte[] Shrink(byte[] rgba, int width, int height, int outWidth, int outHeight)
        {
            var output = GC.AllocateUninitializedArray<byte>(outWidth * outHeight * 4);
            for (var y = 0; y < outHeight; y++)
            {
                int y0 = (int)((long)y * height / outHeight), y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * height / outHeight));
                for (var x = 0; x < outWidth; x++)
                {
                    int x0 = (int)((long)x * width / outWidth), x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * width / outWidth));
                    float r = 0, g = 0, b = 0;
                    for (var sy = y0; sy < y1; sy++)
                    {
                        var row = sy * width * 4;
                        for (var sx = x0; sx < x1; sx++)
                        {
                            var i = row + sx * 4;
                            r += ToLinear[rgba[i]];
                            g += ToLinear[rgba[i + 1]];
                            b += ToLinear[rgba[i + 2]];
                        }
                    }
                    var scale = 1f / ((y1 - y0) * (x1 - x0));
                    r *= scale;
                    g *= scale;
                    b *= scale;
                    var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                    var target = (y * outWidth + x) * 4;
                    output[target] = Grade(luma + (r - luma) * Saturation);
                    output[target + 1] = Grade(luma + (g - luma) * Saturation);
                    output[target + 2] = Grade(luma + (b - luma) * Saturation);
                    output[target + 3] = 255;
                }
            }
            return output;
        }

        private static byte Grade(float linear) => Graded[(int)(Math.Clamp(linear, 0f, 1f) * (Graded.Length - 1) + 0.5f)];

        private static float[] BuildToLinear()
        {
            var table = new float[256];
            for (var i = 0; i < 256; i++)
            {
                var s = i / 255f;
                table[i] = s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
            }
            return table;
        }

        // Linear light in 4096 steps to the graded, encoded byte.
        private static byte[] BuildGraded()
        {
            var table = new byte[4096];
            float Sigmoid(float x) => 1f / (1f + MathF.Exp(-x));
            var low = Sigmoid(-Contrast * Pivot);
            var high = Sigmoid(Contrast * (1 - Pivot));
            for (var i = 0; i < table.Length; i++)
            {
                var linear = i / (table.Length - 1f);
                var encoded = linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(linear, 1 / 2.4f) - 0.055f;
                var curved = (Sigmoid(Contrast * (encoded - Pivot)) - low) / (high - low);
                table[i] = (byte)Math.Clamp((int)(curved * 255 + 0.5f), 0, 255);
            }
            return table;
        }
    }
}
