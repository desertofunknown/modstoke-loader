// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using Modstoke.Loader;

namespace Modstoke.Sons;

public static partial class MapLayers
{
    private const int FormatVersion = 1;
    private const int MinWriteIntervalMs = 30000;
    private const int ChangeDelayMs = 10000;
    private const int LoadDelayMs = 20000;
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };
    private static readonly EnumerationOptions TopLevel = new() { AttributesToSkip = FileAttributes.ReparsePoint };
    private static readonly uint[] CrcTable = CreateCrcTable();
    private static Timer _timer;
    private static long _dueAt;
    private static long _lastWrite;
    private static bool _writing;
    private static DateTimeOffset? _writtenUtc;
    private static string _lastProblem;
    private static Dictionary<string, float[]> _saved;

    private static string MapDirectory => Path.Combine(LoaderPaths.UserDataDirectory, "map");

    // Writes are coalesced: an earlier pending write is never pushed back, and writes stay 30 s apart.
    private static void RequestWrite(int delayMs)
    {
        lock (Sync)
        {
            if (!_hosting || _timer == null)
                return;
            var now = Environment.TickCount64;
            var due = Math.Max(now + delayMs, _lastWrite == 0 ? 0 : _lastWrite + MinWriteIntervalMs);
            if (_dueAt != 0 && _dueAt <= due)
                return;
            _dueAt = due;
            _timer.Change(Math.Max(0, due - now), Timeout.Infinite);
        }
    }

    private static void CancelWrite()
    {
        lock (Sync)
        {
            _dueAt = 0;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    // Timer thread. Only managed copies are read here; nothing calls into Unity.
    private static void WriteScheduled()
    {
        List<Snapshot> layers;
        lock (Sync)
        {
            if (_dueAt == 0)
                return;
            if (_writing)
            {
                _dueAt = Environment.TickCount64 + ChangeDelayMs;
                _timer.Change(ChangeDelayMs, Timeout.Infinite);
                return;
            }
            _dueAt = 0;
            if (!_hosting)
                return;
            _writing = true;
            layers = new List<Snapshot>(Layers.Count);
            foreach (var layer in Layers.Values)
                layers.Add(new Snapshot(layer));
        }

        var problem = default(string);
        try
        {
            Write(layers);
        }
        catch (Exception error)
        {
            // A timer callback must not throw; anything left pending is retried by the next request.
            problem = error.GetType().Name + ": " + error.Message;
        }
        finally
        {
            lock (Sync)
            {
                _writing = false;
                _lastWrite = Environment.TickCount64;
                if (problem == null)
                    _writtenUtc = DateTimeOffset.UtcNow;
            }
        }
        // Reported once per distinct failure; the next save or change retries.
        if (problem != null && problem != Interlocked.Exchange(ref _lastProblem, problem))
            RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK map layers could not be written: " + problem);
        else if (problem == null)
            Volatile.Write(ref _lastProblem, null);
    }

    // Referenced files first and layers.json last, each replaced atomically, so a reader never sees
    // layers.json point at a file that is not there yet.
    private static void Write(List<Snapshot> layers)
    {
        var root = MapDirectory;
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            foreach (var (id, model, surfaces) in layer.Models)
            {
                if (surfaces == null)
                    continue;
                HostOutput.WriteFileAtomic(Path.Combine(root, "models", layer.Folder, id + ".glb"), GlbWriter.Write(surfaces));
                MarkWritten(model, surfaces);
            }
            var placements = Path.Combine(root, "placements", layer.Folder + ".json");
            HostOutput.WriteFileAtomic(placements, Placements(layer.Placements));
            keep.Add(placements);
            foreach (var (id, patch, surfaces) in layer.Patches)
            {
                var mesh = Path.Combine(root, "terrain", layer.Folder, id + ".glb");
                var footprint = Path.Combine(root, "terrain", layer.Folder, id + ".png");
                keep.Add(mesh);
                keep.Add(footprint);
                if (surfaces == null)
                    continue;
                HostOutput.WriteFileAtomic(mesh, GlbWriter.Write(surfaces));
                HostOutput.WriteFileAtomic(footprint, Footprint(surfaces, patch.Bounds));
                MarkWritten(patch, surfaces);
            }
        }
        var index = Index(layers);
        HostOutput.WriteFileAtomic(Path.Combine(root, "layers.json"), index);
        Prune(Path.Combine(root, "placements"), keep);
        var terrain = Path.Combine(root, "terrain");
        if (Directory.Exists(terrain))
        {
            foreach (var folder in Directory.EnumerateDirectories(terrain, "*", TopLevel))
                Prune(folder, keep);
        }
    }

    private static void MarkWritten(Model model, IReadOnlyList<MapSurface> surfaces)
    {
        lock (Sync)
        {
            if (ReferenceEquals(model.Surfaces, surfaces))
                model.Surfaces = null;
        }
    }

    private static byte[] Index(List<Snapshot> layers)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteNumber("version", FormatVersion);
            json.WriteString("written", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WriteStartArray("layers");
            foreach (var layer in layers)
            {
                json.WriteStartObject();
                json.WriteString("mod", layer.ModId);
                json.WriteString("version", layer.Version);
                json.WriteStartArray("models");
                foreach (var (id, model, _) in layer.Models)
                {
                    json.WriteStartObject();
                    json.WriteString("id", id);
                    json.WriteString("file", "models/" + layer.Folder + "/" + id + ".glb");
                    WriteBounds(json, model.Bounds);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteString("placements", "placements/" + layer.Folder + ".json");
                json.WriteStartArray("terrain");
                foreach (var (id, patch, _) in layer.Patches)
                {
                    json.WriteStartObject();
                    json.WriteString("id", id);
                    json.WriteString("file", "terrain/" + layer.Folder + "/" + id + ".glb");
                    WriteBounds(json, patch.Bounds);
                    json.WriteString("footprint", "terrain/" + layer.Folder + "/" + id + ".png");
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static void WriteBounds(Utf8JsonWriter json, float[] bounds)
    {
        json.WriteStartArray("bounds");
        foreach (var value in bounds)
            json.WriteNumberValue(value);
        json.WriteEndArray();
    }

    private static byte[] Placements(MapPlacement[] placements)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartArray();
            foreach (var placement in placements)
            {
                json.WriteStartObject();
                json.WriteString("id", placement.Id);
                json.WriteString("model", placement.Model);
                json.WriteStartArray("position");
                json.WriteNumberValue(placement.Position.x);
                json.WriteNumberValue(placement.Position.y);
                json.WriteNumberValue(placement.Position.z);
                json.WriteEndArray();
                json.WriteStartArray("rotation");
                json.WriteNumberValue(placement.Rotation.x);
                json.WriteNumberValue(placement.Rotation.y);
                json.WriteNumberValue(placement.Rotation.z);
                json.WriteNumberValue(placement.Rotation.w);
                json.WriteEndArray();
                json.WriteStartArray("scale");
                json.WriteNumberValue(placement.Scale.x);
                json.WriteNumberValue(placement.Scale.y);
                json.WriteNumberValue(placement.Scale.z);
                json.WriteEndArray();
                if (placement.Label != null)
                    json.WriteString("label", placement.Label);
                if (placement.Kind != null)
                    json.WriteString("kind", placement.Kind);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        }
        return buffer.ToArray();
    }

    // Files this module wrote earlier that the current layers no longer reference.
    private static void Prune(string directory, HashSet<string> keep)
    {
        if (!Directory.Exists(directory))
            return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", TopLevel))
        {
            if (!keep.Contains(file) && (file.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                File.Delete(file);
        }
    }

    // Called under Sync. Reads the last layers.json once per session to find models a version already wrote.
    private static bool TryReadSavedModel(Layer layer, string modelId, out float[] bounds)
    {
        _saved ??= ReadSaved();
        return _saved.TryGetValue(layer.Folder + "/" + layer.Version + "/" + modelId, out bounds)
            && File.Exists(Path.Combine(MapDirectory, "models", layer.Folder, modelId + ".glb"));
    }

    private static Dictionary<string, float[]> ReadSaved()
    {
        var saved = new Dictionary<string, float[]>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(MapDirectory, "layers.json");
            if (!File.Exists(path))
                return saved;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != FormatVersion || !root.TryGetProperty("layers", out var layers)
                || layers.ValueKind != JsonValueKind.Array)
                return saved;
            foreach (var layer in layers.EnumerateArray())
            {
                if (!layer.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.String
                    || !layer.TryGetProperty("version", out var modVersion) || modVersion.ValueKind != JsonValueKind.String
                    || !layer.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var model in models.EnumerateArray())
                {
                    if (!model.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                        || !model.TryGetProperty("bounds", out var values) || values.ValueKind != JsonValueKind.Array
                        || values.GetArrayLength() != 6)
                        continue;
                    var bounds = new float[6];
                    var index = 0;
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var number) || !float.IsFinite(number))
                            break;
                        bounds[index++] = number;
                    }
                    if (index == 6)
                        saved[mod.GetString().ToLowerInvariant() + "/" + modVersion.GetString() + "/" + id.GetString()] = bounds;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or FormatException or InvalidOperationException)
        {
            // An unreadable index only means every model is exported again.
            saved.Clear();
        }
        return saved;
    }

    // 1 m per pixel over the patch's X and Z bounds in glTF space: column 0 is west, row 0 north.
    // White marks where the panel hides the base terrain: pixel centres the mesh covers from above.
    private static byte[] Footprint(IReadOnlyList<MapSurface> surfaces, float[] bounds)
    {
        float minX = bounds[0], minZ = bounds[2];
        var width = Math.Max(1, (int)MathF.Ceiling(bounds[3] - minX));
        var height = Math.Max(1, (int)MathF.Ceiling(bounds[5] - minZ));
        var mask = new byte[width * height];
        foreach (var surface in surfaces)
        {
            var p = surface.Positions;
            var indices = surface.Indices;
            for (var i = 0; i + 2 < indices.Length; i += 3)
            {
                float ax = p[indices[i]].x - minX, az = -p[indices[i]].z - minZ;
                float bx = p[indices[i + 1]].x - minX, bz = -p[indices[i + 1]].z - minZ;
                float cx = p[indices[i + 2]].x - minX, cz = -p[indices[i + 2]].z - minZ;
                var area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
                if (MathF.Abs(area) < 1e-6f)
                    continue;
                var x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)) - .5f));
                var x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)) - .5f));
                var z0 = Math.Max(0, (int)MathF.Floor(MathF.Min(az, MathF.Min(bz, cz)) - .5f));
                var z1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(az, MathF.Max(bz, cz)) - .5f));
                for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                {
                    float px = x + .5f, pz = z + .5f;
                    var w0 = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
                    var w1 = (cx - bx) * (pz - bz) - (cz - bz) * (px - bx);
                    var w2 = (ax - cx) * (pz - cz) - (az - cz) * (px - cx);
                    if (area > 0 ? w0 >= 0 && w1 >= 0 && w2 >= 0 : w0 <= 0 && w1 <= 0 && w2 <= 0)
                        mask[x + z * width] = 255;
                }
            }
        }
        return Png(mask, width, height);
    }

    // 8-bit greyscale PNG with unfiltered rows.
    private static byte[] Png(byte[] pixels, int width, int height)
    {
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
            Buffer.BlockCopy(pixels, y * width, raw, y * (width + 1) + 1, width);
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, true))
            zlib.Write(raw);

        var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void Chunk(MemoryStream png, string type, byte[] data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        png.Write(word);
        var name = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        png.Write(name);
        png.Write(data);
        var crc = ~Crc(Crc(0xFFFFFFFFu, name), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, crc);
        png.Write(word);
    }

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var value in data)
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    // What one write needs from a layer, copied under Sync. Null surfaces mean the file is already current.
    private sealed class Snapshot
    {
        internal Snapshot(Layer layer)
        {
            ModId = layer.ModId;
            Folder = layer.Folder;
            Version = layer.Version;
            foreach (var (id, model) in layer.Models)
                Models.Add((id, model, model.Surfaces));
            Placements = new MapPlacement[layer.Placements.Count];
            layer.Placements.Values.CopyTo(Placements, 0);
            foreach (var (id, patch) in layer.Patches)
                Patches.Add((id, patch, patch.Surfaces));
        }

        internal readonly string ModId;
        internal readonly string Folder;
        internal readonly string Version;
        internal readonly List<(string Id, Model Model, IReadOnlyList<MapSurface> Surfaces)> Models = new();
        internal readonly MapPlacement[] Placements;
        internal readonly List<(string Id, Model Patch, IReadOnlyList<MapSurface> Surfaces)> Patches = new();
    }
}
