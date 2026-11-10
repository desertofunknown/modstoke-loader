// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Modstoke.Loader;
using UnityEngine;
using global::Sons.Gameplay.GameSetup;
using global::Sons.Save;

namespace Modstoke.Sons;

/// <summary>Models, placements and terrain patches that mods publish for the hosting panel's island map.</summary>
/// <remarks>
/// The files go to UserData/map when a world loads, after world saves and shortly after a change, on a worker
/// thread and at most every 30 seconds. Only meshes and textures a mod created at runtime are exported; anything
/// loaded from game or bundle files is skipped. Multiplayer clients accept calls but never write.
/// </remarks>
public static partial class MapLayers
{
    public const int MaxModels = 64;
    public const int MaxPlacements = 2048;
    public const int MaxTerrainPatches = 1024;
    public const int MaxSurfaces = 64;
    public const int MaxVertices = 1 << 20;
    public const float MaxPatchSize = 1024;
    private const int MaxIdLength = 64;
    private const int MaxTextLength = 64;
    private const float MaxCoordinate = 100000;
    private static readonly object Sync = new();
    // Keyed by the lowercased manifest ID, which is also the mod's folder name in the map directory.
    private static readonly Dictionary<string, Layer> Layers = new(StringComparer.Ordinal);
    private static int _ownerThread;
    private static bool _hosting;

    /// <summary>Last write failure, without file contents; null after a successful write.</summary>
    public static string LastProblem => Volatile.Read(ref _lastProblem);

    public static DateTimeOffset? LastWrittenUtc
    {
        get { lock (Sync) return _writtenUtc; }
    }

    internal static void Initialize()
    {
        if (_ownerThread != 0)
            return;
        _ownerThread = Environment.CurrentManagedThreadId;
        _timer = new Timer(_ => WriteScheduled());
        SaveLifecycle.Lifecycle += OnSaveLifecycle;
        GameRuntime.SceneChanged += OnSceneChanged;
    }

    /// <summary>Exports the runtime-created meshes under <paramref name="root"/>, in its local space.</summary>
    /// <remarks>
    /// Game thread only. Disabled renderers and meshes loaded from files are left out. A model is captured once:
    /// later calls with the same ID succeed without reading the object again.
    /// </remarks>
    public static bool TryRegisterModel(string modId, string modelId, GameObject root, out MapLayerStatus status)
    {
        if (!OnThread) { status = MapLayerStatus.WrongThread; return false; }
        if (!root) { status = MapLayerStatus.InvalidGeometry; return false; }
        if (!BeginModel(modId, modelId, out status))
            return status == MapLayerStatus.Success;
        return RegisterModel(modId, modelId, Capture(root), out status);
    }

    /// <summary>Exports one mesh, with one material per submesh, in the mesh's own space. Game thread only.</summary>
    public static bool TryRegisterModel(string modId, string modelId, Mesh mesh, Material[] materials,
        out MapLayerStatus status)
    {
        if (!OnThread) { status = MapLayerStatus.WrongThread; return false; }
        if (!mesh) { status = MapLayerStatus.InvalidGeometry; return false; }
        if (!BeginModel(modId, modelId, out status))
            return status == MapLayerStatus.Success;
        return RegisterModel(modId, modelId, Capture(mesh, materials ?? Array.Empty<Material>()), out status);
    }

    /// <summary>Exports geometry the mod built itself. Any thread.</summary>
    /// <remarks>The arrays are kept until written; do not change them afterwards.</remarks>
    public static bool TryRegisterModel(string modId, string modelId, IReadOnlyList<MapSurface> surfaces,
        out MapLayerStatus status)
    {
        if (!BeginModel(modId, modelId, out status))
            return status == MapLayerStatus.Success;
        return RegisterModel(modId, modelId, surfaces, out status);
    }

    /// <summary>
    /// Registers a model this mod version already exported in an earlier session, so the mod can skip building
    /// it. False with <see cref="MapLayerStatus.NotFound"/> when it has to be registered again.
    /// </summary>
    public static bool TryRegisterSavedModel(string modId, string modelId, out MapLayerStatus status)
    {
        if (!BeginModel(modId, modelId, out status))
            return status == MapLayerStatus.Success;
        lock (Sync)
        {
            status = FindLayer(modId, true, out var layer);
            if (status != MapLayerStatus.Success)
                return false;
            if (layer.Models.ContainsKey(modelId))
                return true;
            if (!TryReadSavedModel(layer, modelId, out var bounds))
            {
                status = MapLayerStatus.NotFound;
                return false;
            }
            layer.Models.Add(modelId, new Model(bounds, null));
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    /// <summary>Adds or moves an instance of a registered model, in Unity world space.</summary>
    /// <remarks>
    /// Any thread. A placement that moved less than 10 cm and turned less than about two degrees keeps its
    /// previous value, so settling or hovering objects don't cause writes.
    /// </remarks>
    public static bool TrySetPlacement(string modId, MapPlacement placement, out MapLayerStatus status)
    {
        if (placement == null || !ValidId(placement.Id) || !ValidId(placement.Model)
            || !ValidText(placement.Label) || !ValidText(placement.Kind))
        {
            status = MapLayerStatus.InvalidId;
            return false;
        }
        if (!InRange(placement.Position) || !Finite(placement.Scale) || !TryNormalize(placement.Rotation, out var rotation))
        {
            status = MapLayerStatus.InvalidGeometry;
            return false;
        }
        lock (Sync)
        {
            status = FindLayer(modId, false, out var layer);
            if (status == MapLayerStatus.NotFound
                || (status == MapLayerStatus.Success && !layer.Models.ContainsKey(placement.Model)))
                status = MapLayerStatus.UnknownModel;
            if (status != MapLayerStatus.Success)
                return false;
            if (layer.Placements.TryGetValue(placement.Id, out var current) && Same(current, placement, rotation))
                return true;
            if (current == null && layer.Placements.Count == MaxPlacements)
            {
                status = MapLayerStatus.LimitReached;
                return false;
            }
            layer.Placements[placement.Id] = new MapPlacement(placement.Id, placement.Model, placement.Position,
                rotation, placement.Scale, placement.Label, placement.Kind);
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    public static bool RemovePlacement(string modId, string placementId)
    {
        lock (Sync)
        {
            if (FindLayer(modId, false, out var layer) != MapLayerStatus.Success || placementId == null
                || !layer.Placements.Remove(placementId))
                return false;
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    /// <summary>Replaces an edited area of ground with a world-space mesh. Any thread.</summary>
    /// <remarks>
    /// The footprint mask, which tells the panel where to hide the base terrain, is the mesh's own coverage seen
    /// from above. The arrays are kept until written; do not change them afterwards.
    /// </remarks>
    public static bool TrySetTerrainPatch(string modId, string patchId, IReadOnlyList<MapSurface> surfaces,
        out MapLayerStatus status)
    {
        if (!ValidId(patchId))
        {
            status = MapLayerStatus.InvalidId;
            return false;
        }
        if (!TryMeasure(surfaces, out var bounds) || bounds[3] - bounds[0] > MaxPatchSize || bounds[5] - bounds[2] > MaxPatchSize)
        {
            status = MapLayerStatus.InvalidGeometry;
            return false;
        }
        lock (Sync)
        {
            status = FindLayer(modId, true, out var layer);
            if (status != MapLayerStatus.Success)
                return false;
            if (!layer.Patches.ContainsKey(patchId) && layer.Patches.Count == MaxTerrainPatches)
            {
                status = MapLayerStatus.LimitReached;
                return false;
            }
            layer.Patches[patchId] = new Model(bounds, surfaces);
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    public static bool RemoveTerrainPatch(string modId, string patchId)
    {
        lock (Sync)
        {
            if (FindLayer(modId, false, out var layer) != MapLayerStatus.Success || patchId == null
                || !layer.Patches.Remove(patchId))
                return false;
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    /// <summary>Withdraws everything a mod published, for its shutdown. Its model files stay for reuse.</summary>
    public static bool RemoveLayer(string modId)
    {
        lock (Sync)
        {
            if (string.IsNullOrEmpty(modId) || !Layers.Remove(modId.ToLowerInvariant()))
                return false;
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    private static bool OnThread => _ownerThread != 0 && Environment.CurrentManagedThreadId == _ownerThread;

    // False with Success when the model is already registered and nothing needs doing.
    private static bool BeginModel(string modId, string modelId, out MapLayerStatus status)
    {
        if (!ValidId(modelId))
        {
            status = MapLayerStatus.InvalidId;
            return false;
        }
        lock (Sync)
        {
            status = FindLayer(modId, true, out var layer);
            if (status != MapLayerStatus.Success)
                return false;
            if (layer.Models.ContainsKey(modelId))
                return false;
            if (layer.Models.Count == MaxModels)
            {
                status = MapLayerStatus.LimitReached;
                return false;
            }
        }
        return true;
    }

    private static bool RegisterModel(string modId, string modelId, IReadOnlyList<MapSurface> surfaces,
        out MapLayerStatus status)
    {
        if (surfaces is { Count: 0 })
        {
            status = MapLayerStatus.NoGeometry;
            return false;
        }
        if (!TryMeasure(surfaces, out var bounds))
        {
            status = MapLayerStatus.InvalidGeometry;
            return false;
        }
        lock (Sync)
        {
            status = FindLayer(modId, true, out var layer);
            if (status != MapLayerStatus.Success)
                return false;
            if (layer.Models.ContainsKey(modelId))
                return true;
            if (layer.Models.Count == MaxModels)
            {
                status = MapLayerStatus.LimitReached;
                return false;
            }
            // A GLB this mod version already wrote is kept rather than written again.
            layer.Models.Add(modelId, TryReadSavedModel(layer, modelId, out var saved)
                ? new Model(saved, null) : new Model(bounds, surfaces));
        }
        RequestWrite(ChangeDelayMs);
        return true;
    }

    // Called under Sync. Only Modstoke mods that started can publish; the version comes from their manifest.
    private static MapLayerStatus FindLayer(string modId, bool create, out Layer layer)
    {
        layer = null;
        if (string.IsNullOrWhiteSpace(modId))
            return MapLayerStatus.UnknownMod;
        var folder = modId.ToLowerInvariant();
        if (!ValidId(folder))
            return MapLayerStatus.InvalidId;
        if (Layers.TryGetValue(folder, out layer))
            return MapLayerStatus.Success;
        if (!create)
            return MapLayerStatus.NotFound;
        foreach (var mod in ModRuntime.State.Mods)
        {
            if (!string.Equals(mod.ModId, modId, StringComparison.OrdinalIgnoreCase) || !mod.EntrypointInvoked || mod.Failed)
                continue;
            layer = new Layer(mod.ModId, folder, mod.Version ?? "");
            Layers.Add(folder, layer);
            return MapLayerStatus.Success;
        }
        return MapLayerStatus.UnknownMod;
    }

    // Placements and patches belong to one world; models outlive it.
    private static void ClearWorld()
    {
        lock (Sync)
        {
            foreach (var layer in Layers.Values)
            {
                layer.Placements.Clear();
                layer.Patches.Clear();
            }
        }
    }

    private static void OnSaveLifecycle(SaveLifecycleEvent evt)
    {
        if (evt == null)
            return;
        if (evt.Operation == "Load" && evt.Phase == "Before")
        {
            lock (Sync)
                _hosting = false;
            CancelWrite();
            ClearWorld();
        }
        else if (evt.Operation == "Load" && evt.Phase == "After" && evt.Success == true)
            BeginWorld();
        else if (evt.Operation == "Save" && evt.Phase == "After" && evt.SavePlayerOnly != true)
            RequestWrite(0);
    }

    private static void OnSceneChanged(SceneEvent evt)
    {
        if (evt?.Phase != "Loaded")
            return;
        if (evt.SceneKind == SceneKind.Game)
            BeginWorld();
        else if (evt.SceneKind == SceneKind.Title)
        {
            lock (Sync)
                _hosting = false;
            CancelWrite();
            ClearWorld();
        }
    }

    // A new or loaded world: give mods time to restore their objects before the first write.
    private static void BeginWorld()
    {
        bool hosting;
        try
        {
            hosting = GameRuntime.Environment.IsDedicatedServer
                || GameSetupManager.GetSaveGameType() != SaveGameType.MultiplayerClient;
        }
        catch (Exception)
        {
            hosting = false;
        }
        lock (Sync)
            _hosting = hosting;
        RequestWrite(LoadDelayMs);
    }

    private static bool ValidId(string id)
    {
        // Matches what the panel serves: lowercase, no leading dot, no separators.
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || id[0] == '.')
            return false;
        foreach (var c in id)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.'))
                return false;
        }
        return true;
    }

    private static bool ValidText(string text)
    {
        if (text == null)
            return true;
        if (text.Length > MaxTextLength)
            return false;
        foreach (var c in text)
        {
            if (char.IsControl(c))
                return false;
        }
        return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);

    private static bool InRange(Vector3 v) => Finite(v)
        && Math.Abs(v.x) <= MaxCoordinate && Math.Abs(v.y) <= MaxCoordinate && Math.Abs(v.z) <= MaxCoordinate;

    // Struct fields only: Unity's vector methods are not safe off the game thread.
    private static bool TryNormalize(Quaternion q, out Quaternion normalized)
    {
        normalized = default;
        var length = MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (!float.IsFinite(length) || length < 1e-4f)
            return false;
        normalized.x = q.x / length;
        normalized.y = q.y / length;
        normalized.z = q.z / length;
        normalized.w = q.w / length;
        return true;
    }

    private static bool Same(MapPlacement current, MapPlacement next, Quaternion rotation)
    {
        float dx = next.Position.x - current.Position.x, dy = next.Position.y - current.Position.y,
            dz = next.Position.z - current.Position.z;
        var dot = current.Rotation.x * rotation.x + current.Rotation.y * rotation.y
            + current.Rotation.z * rotation.z + current.Rotation.w * rotation.w;
        return current.Model == next.Model && current.Label == next.Label && current.Kind == next.Kind
            && dx * dx + dy * dy + dz * dz < .01f && Math.Abs(dot) > .99985f
            && Math.Abs(next.Scale.x - current.Scale.x) < .001f && Math.Abs(next.Scale.y - current.Scale.y) < .001f
            && Math.Abs(next.Scale.z - current.Scale.z) < .001f;
    }

    // Checks every surface and returns the bounds in glTF space, the space the GLB and layers.json use.
    private static bool TryMeasure(IReadOnlyList<MapSurface> surfaces, out float[] bounds)
    {
        bounds = null;
        if (surfaces == null || surfaces.Count == 0 || surfaces.Count > MaxSurfaces)
            return false;
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        long vertices = 0;
        foreach (var surface in surfaces)
        {
            if (surface == null)
                return false;
            var positions = surface.Positions;
            var count = positions.Length;
            vertices += count;
            if (vertices > MaxVertices || (surface.Normals != null && surface.Normals.Length != count)
                || (surface.Uvs != null && surface.Uvs.Length != count) || (surface.Colors != null && surface.Colors.Length != count))
                return false;
            foreach (var index in surface.Indices)
            {
                if ((uint)index >= (uint)count)
                    return false;
            }
            foreach (var p in positions)
            {
                if (!InRange(p))
                    return false;
                minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x);
                minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
                minZ = Math.Min(minZ, -p.z); maxZ = Math.Max(maxZ, -p.z);
            }
        }
        bounds = new[] { minX, minY, minZ, maxX, maxY, maxZ };
        return true;
    }

    private sealed class Layer(string modId, string folder, string version)
    {
        internal readonly string ModId = modId;
        internal readonly string Folder = folder;
        internal readonly string Version = version;
        internal readonly Dictionary<string, Model> Models = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, MapPlacement> Placements = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Model> Patches = new(StringComparer.Ordinal);
    }

    // Surfaces stay until the file is written, then only the bounds are kept.
    private sealed class Model(float[] bounds, IReadOnlyList<MapSurface> surfaces)
    {
        internal readonly float[] Bounds = bounds;
        internal IReadOnlyList<MapSurface> Surfaces = surfaces;
    }
}

/// <summary>One primitive of a map model or terrain patch: Unity-space metres and one material.</summary>
public sealed class MapSurface
{
    /// <param name="baseColor">The material colour as Unity shows it (sRGB); it is written linear.</param>
    public MapSurface(Vector3[] positions, int[] indices, Color baseColor, Vector3[] normals = null,
        Vector2[] uvs = null, Color[] colors = null, float metallic = 0, float roughness = 1)
    {
        ArgumentNullException.ThrowIfNull(positions);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Length == 0 || indices.Length % 3 != 0)
            throw new ArgumentException("Indices must describe whole triangles.", nameof(indices));
        Positions = positions;
        Indices = indices;
        BaseColor = baseColor;
        Normals = normals;
        Uvs = uvs;
        Colors = colors;
        Metallic = Math.Clamp(float.IsFinite(metallic) ? metallic : 0, 0, 1);
        Roughness = Math.Clamp(float.IsFinite(roughness) ? roughness : 1, 0, 1);
    }

    internal Vector3[] Positions { get; }
    internal int[] Indices { get; }
    internal Color BaseColor { get; }
    internal Vector3[] Normals { get; }
    internal Vector2[] Uvs { get; }
    internal Color[] Colors { get; }
    internal float Metallic { get; }
    internal float Roughness { get; }
    // PNG bytes of the mod's own base-colour texture, captured from a material.
    internal byte[] Texture { get; init; }
}

/// <summary>An instance of a registered model, in Unity world space (the island is 4 km square around the origin).</summary>
public sealed class MapPlacement
{
    public MapPlacement(string id, string model, Vector3 position, Quaternion rotation, Vector3 scale,
        string label = null, string kind = null)
    {
        Id = id;
        Model = model;
        Position = position;
        Rotation = rotation;
        Scale = scale;
        Label = label;
        Kind = kind;
    }

    public string Id { get; }
    public string Model { get; }
    public Vector3 Position { get; }
    public Quaternion Rotation { get; }
    public Vector3 Scale { get; }
    public string Label { get; }
    public string Kind { get; }
}

public enum MapLayerStatus
{
    Success, NotFound, WrongThread, UnknownMod, InvalidId, UnknownModel, InvalidGeometry, NoGeometry, LimitReached
}
