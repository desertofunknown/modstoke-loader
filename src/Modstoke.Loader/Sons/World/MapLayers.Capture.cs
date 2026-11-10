// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Modstoke.Sons;

public static partial class MapLayers
{
    private const int MaxTextureSize = 2048;

    // Game thread. Everything is copied into managed arrays here, so writing never touches Unity.
    private static List<MapSurface> Capture(GameObject root)
    {
        var merged = new Dictionary<int, MergedSurface>();
        var top = root.transform;
        var toRoot = top.worldToLocalMatrix;
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
        {
            var renderer = filter.GetComponent<MeshRenderer>();
            var mesh = filter.sharedMesh;
            if (!renderer || !renderer.enabled || !mesh || !Shown(filter.transform, top))
                continue;
            var shared = renderer.sharedMaterials;
            var materials = new Material[shared.Length];
            for (var i = 0; i < materials.Length; i++)
                materials[i] = shared[i];
            Add(merged, mesh, materials, toRoot * filter.transform.localToWorldMatrix);
        }
        return Merge(merged);
    }

    // Active below the root. The root itself may be inactive, as a model built only for export usually is.
    private static bool Shown(Transform transform, Transform root)
    {
        for (; transform && transform != root; transform = transform.parent)
        {
            if (!transform.gameObject.activeSelf)
                return false;
        }
        return true;
    }

    private static List<MapSurface> Capture(Mesh mesh, Material[] materials)
    {
        var merged = new Dictionary<int, MergedSurface>();
        Add(merged, mesh, materials, Matrix4x4.identity);
        return Merge(merged);
    }

    private static void Add(Dictionary<int, MergedSurface> merged, Mesh mesh, Material[] materials, Matrix4x4 m)
    {
        // Unity gives assets loaded from files positive instance IDs and objects made at runtime negative ones.
        // That keeps the game's own meshes out of the export.
        if (mesh.GetInstanceID() > 0 || !mesh.isReadable)
            return;
        var vertices = mesh.vertices;
        var count = vertices.Length;
        if (count == 0)
            return;
        var normals = mesh.normals;
        var uvs = mesh.uv;
        var colors = mesh.colors;
        var hasNormals = normals.Length == count;
        var hasUvs = uvs.Length == count;
        var hasColors = colors.Length == count;

        // Normals use the inverse transpose, here the cofactor matrix; a mirrored transform also flips winding.
        float c00 = m.m11 * m.m22 - m.m12 * m.m21, c01 = m.m12 * m.m20 - m.m10 * m.m22, c02 = m.m10 * m.m21 - m.m11 * m.m20;
        float c10 = m.m02 * m.m21 - m.m01 * m.m22, c11 = m.m00 * m.m22 - m.m02 * m.m20, c12 = m.m01 * m.m20 - m.m00 * m.m21;
        float c20 = m.m01 * m.m12 - m.m02 * m.m11, c21 = m.m02 * m.m10 - m.m00 * m.m12, c22 = m.m00 * m.m11 - m.m01 * m.m10;
        var det = m.m00 * c00 + m.m01 * c01 + m.m02 * c02;
        var mirrored = det < 0;
        var positions = new Vector3[count];
        var turned = hasNormals ? new Vector3[count] : null;
        for (var i = 0; i < count; i++)
        {
            var v = vertices[i];
            positions[i].x = m.m00 * v.x + m.m01 * v.y + m.m02 * v.z + m.m03;
            positions[i].y = m.m10 * v.x + m.m11 * v.y + m.m12 * v.z + m.m13;
            positions[i].z = m.m20 * v.x + m.m21 * v.y + m.m22 * v.z + m.m23;
            if (!hasNormals)
                continue;
            var n = normals[i];
            float x = c00 * n.x + c01 * n.y + c02 * n.z, y = c10 * n.x + c11 * n.y + c12 * n.z,
                z = c20 * n.x + c21 * n.y + c22 * n.z;
            var length = MathF.Sqrt(x * x + y * y + z * z);
            if (length > 0)
                length = mirrored ? -length : length;
            else
                (y, length) = (1, 1);
            turned[i].x = x / length;
            turned[i].y = y / length;
            turned[i].z = z / length;
        }

        var remap = new int[count];
        var submeshes = Math.Min(mesh.subMeshCount, materials.Length);
        for (var s = 0; s < submeshes; s++)
        {
            if (mesh.GetTopology(s) != MeshTopology.Triangles)
                continue;
            var material = materials[s];
            var key = material ? material.GetInstanceID() : 0;
            if (!merged.TryGetValue(key, out var surface))
                merged.Add(key, surface = new MergedSurface(material));
            Array.Fill(remap, -1);
            var triangles = mesh.GetTriangles(s);
            surface.BeginPart(hasNormals, hasUvs, hasColors);
            for (var i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                if (mirrored)
                    (b, c) = (c, b);
                surface.Indices.Add(Vertex(a));
                surface.Indices.Add(Vertex(b));
                surface.Indices.Add(Vertex(c));
            }

            int Vertex(int index)
            {
                if (remap[index] >= 0)
                    return remap[index];
                remap[index] = surface.Positions.Count;
                surface.Positions.Add(positions[index]);
                if (hasNormals) surface.Normals.Add(turned[index]);
                if (hasUvs) surface.Uvs.Add(uvs[index]);
                if (hasColors) surface.Colors.Add(colors[index]);
                return remap[index];
            }
        }
    }

    private static List<MapSurface> Merge(Dictionary<int, MergedSurface> merged)
    {
        var surfaces = new List<MapSurface>();
        var textures = new Dictionary<int, byte[]>();
        foreach (var surface in merged.Values)
        {
            if (surface.Indices.Count == 0)
                continue;
            ReadMaterial(surface.Material, textures, out var color, out var metallic, out var roughness, out var texture);
            surfaces.Add(new MapSurface(surface.Positions.ToArray(), surface.Indices.ToArray(), color,
                surface.AllNormals ? surface.Normals.ToArray() : null,
                surface.AllUvs && texture != null ? surface.Uvs.ToArray() : null,
                surface.AllColors ? surface.Colors.ToArray() : null, metallic, roughness)
            {
                Texture = surface.AllUvs ? texture : null
            });
        }
        return surfaces;
    }

    // HDRP Lit names first, then the built-in ones.
    private static void ReadMaterial(Material material, Dictionary<int, byte[]> textures, out Color color,
        out float metallic, out float roughness, out byte[] texture)
    {
        color = Color.white;
        metallic = 0;
        roughness = 1;
        texture = null;
        if (!material)
            return;
        if (material.HasProperty("_BaseColor"))
            color = material.GetColor("_BaseColor");
        else if (material.HasProperty("_Color"))
            color = material.GetColor("_Color");
        if (material.HasProperty("_Metallic"))
            metallic = material.GetFloat("_Metallic");
        if (material.HasProperty("_Smoothness"))
            roughness = 1 - material.GetFloat("_Smoothness");
        else if (material.HasProperty("_Glossiness"))
            roughness = 1 - material.GetFloat("_Glossiness");
        var map = material.HasProperty("_BaseColorMap") ? material.GetTexture("_BaseColorMap")
            : material.HasProperty("_MainTex") ? material.GetTexture("_MainTex") : null;
        var image = map ? map.TryCast<Texture2D>() : null;
        if (!image || image.GetInstanceID() > 0 || !image.isReadable
            || Math.Max(image.width, image.height) > MaxTextureSize)
            return;
        var id = image.GetInstanceID();
        if (textures.TryGetValue(id, out texture))
            return;
        try
        {
            var png = ImageConversion.EncodeToPNG(image);
            texture = new byte[png.Length];
            for (var i = 0; i < texture.Length; i++)
                texture[i] = png[i];
        }
        catch (Exception)
        {
            // Compressed formats cannot be encoded; the material keeps its colour.
            texture = null;
        }
        textures[id] = texture;
    }

    // Geometry of one material, gathered across the mesh filters of a model.
    private sealed class MergedSurface(Material material)
    {
        internal readonly Material Material = material;
        internal readonly List<Vector3> Positions = new();
        internal readonly List<Vector3> Normals = new();
        internal readonly List<Vector2> Uvs = new();
        internal readonly List<Color> Colors = new();
        internal readonly List<int> Indices = new();
        internal bool AllNormals = true, AllUvs = true, AllColors = true;

        // An attribute is kept only when every part has it.
        internal void BeginPart(bool normals, bool uvs, bool colors)
        {
            AllNormals &= normals;
            AllUvs &= uvs;
            AllColors &= colors;
        }
    }
}
