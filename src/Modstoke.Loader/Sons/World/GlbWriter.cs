// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Modstoke.Sons;

// Binary glTF 2.0 with one mesh and one primitive per surface. Input is Unity's left-handed space: Z is
// negated, V flipped and triangle winding reversed for glTF's right-handed one. Exact duplicate vertices
// are merged and indices are 16-bit whenever a primitive allows it. Reads struct fields only, so it is
// safe off the game thread.
internal static class GlbWriter
{
    private const uint Magic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;
    private const int FloatComponent = 5126;
    private const int UShortComponent = 5123;
    private const int UIntComponent = 5125;
    private const int VertexTarget = 34962;
    private const int IndexTarget = 34963;

    internal static byte[] Write(IReadOnlyList<MapSurface> surfaces)
    {
        var data = new MemoryStream();
        var binary = new BinaryWriter(data);
        var views = new List<(int Offset, int Length, int Target)>();
        var accessors = new List<Accessor>();
        var primitives = new List<Primitive>();
        var materials = new List<MapSurface>();
        var images = new List<byte[]>();

        foreach (var surface in surfaces)
        {
            var mesh = Weld(surface);
            var primitive = new Primitive { Material = Material(materials, surface) };
            primitive.Position = accessors.Count;
            accessors.Add(new Accessor(View(views, binary, VertexTarget, () =>
            {
                foreach (var v in mesh.Vertices) { binary.Write(v.Px); binary.Write(v.Py); binary.Write(-v.Pz); }
            }), FloatComponent, mesh.Vertices.Count, "VEC3", Bounds(mesh.Vertices)));
            if (surface.Normals != null)
            {
                primitive.Normal = accessors.Count;
                accessors.Add(new Accessor(View(views, binary, VertexTarget, () =>
                {
                    foreach (var v in mesh.Vertices) { binary.Write(v.Nx); binary.Write(v.Ny); binary.Write(-v.Nz); }
                }), FloatComponent, mesh.Vertices.Count, "VEC3", null));
            }
            if (surface.Uvs != null)
            {
                primitive.Uv = accessors.Count;
                accessors.Add(new Accessor(View(views, binary, VertexTarget, () =>
                {
                    foreach (var v in mesh.Vertices) { binary.Write(v.U); binary.Write(1 - v.V); }
                }), FloatComponent, mesh.Vertices.Count, "VEC2", null));
            }
            if (surface.Colors != null)
            {
                primitive.Color = accessors.Count;
                accessors.Add(new Accessor(View(views, binary, VertexTarget, () =>
                {
                    foreach (var v in mesh.Vertices) { binary.Write(v.R); binary.Write(v.G); binary.Write(v.B); binary.Write(v.A); }
                }), FloatComponent, mesh.Vertices.Count, "VEC4", null));
            }
            var shortIndices = mesh.Vertices.Count <= ushort.MaxValue;
            primitive.Indices = accessors.Count;
            accessors.Add(new Accessor(View(views, binary, IndexTarget, () =>
            {
                for (var i = 0; i < mesh.Indices.Length; i += 3)
                {
                    WriteIndex(binary, mesh.Indices[i], shortIndices);
                    WriteIndex(binary, mesh.Indices[i + 2], shortIndices);
                    WriteIndex(binary, mesh.Indices[i + 1], shortIndices);
                }
            }), shortIndices ? UShortComponent : UIntComponent, mesh.Indices.Length, "SCALAR", null));
            primitives.Add(primitive);
        }

        var imageViews = new List<int>();
        foreach (var material in materials)
        {
            if (material.Texture == null || images.Contains(material.Texture))
                continue;
            images.Add(material.Texture);
            imageViews.Add(View(views, binary, 0, () => binary.Write(material.Texture)));
        }
        binary.Flush();

        var json = Json(views, accessors, primitives, materials, images, imageViews, (int)data.Length);
        var jsonLength = Align(json.Length);
        var binaryLength = Align((int)data.Length);
        var glb = new MemoryStream(12 + 8 + jsonLength + 8 + binaryLength);
        var output = new BinaryWriter(glb);
        output.Write(Magic);
        output.Write(2u);
        output.Write((uint)(12 + 8 + jsonLength + 8 + binaryLength));
        output.Write((uint)jsonLength);
        output.Write(JsonChunk);
        output.Write(json);
        for (var i = json.Length; i < jsonLength; i++)
            output.Write((byte)' ');
        output.Write((uint)binaryLength);
        output.Write(BinaryChunk);
        data.Position = 0;
        data.CopyTo(glb);
        for (var i = (int)data.Length; i < binaryLength; i++)
            output.Write((byte)0);
        output.Flush();
        return glb.ToArray();
    }

    private static byte[] Json(List<(int Offset, int Length, int Target)> views, List<Accessor> accessors,
        List<Primitive> primitives, List<MapSurface> materials, List<byte[]> images, List<int> imageViews, int bufferLength)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteStartObject("asset");
            json.WriteString("version", "2.0");
            json.WriteString("generator", "Modstoke Loader");
            json.WriteEndObject();
            json.WriteNumber("scene", 0);
            json.WriteStartArray("scenes");
            json.WriteStartObject();
            json.WriteStartArray("nodes");
            json.WriteNumberValue(0);
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteStartArray("nodes");
            json.WriteStartObject();
            json.WriteNumber("mesh", 0);
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteStartArray("meshes");
            json.WriteStartObject();
            json.WriteStartArray("primitives");
            foreach (var primitive in primitives)
            {
                json.WriteStartObject();
                json.WriteStartObject("attributes");
                json.WriteNumber("POSITION", primitive.Position);
                if (primitive.Normal >= 0) json.WriteNumber("NORMAL", primitive.Normal);
                if (primitive.Uv >= 0) json.WriteNumber("TEXCOORD_0", primitive.Uv);
                if (primitive.Color >= 0) json.WriteNumber("COLOR_0", primitive.Color);
                json.WriteEndObject();
                json.WriteNumber("indices", primitive.Indices);
                json.WriteNumber("material", primitive.Material);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteStartArray("materials");
            foreach (var material in materials)
            {
                json.WriteStartObject();
                json.WriteStartObject("pbrMetallicRoughness");
                json.WriteStartArray("baseColorFactor");
                json.WriteNumberValue(Linear(material.BaseColor.r));
                json.WriteNumberValue(Linear(material.BaseColor.g));
                json.WriteNumberValue(Linear(material.BaseColor.b));
                json.WriteNumberValue(Math.Clamp(material.BaseColor.a, 0, 1));
                json.WriteEndArray();
                json.WriteNumber("metallicFactor", material.Metallic);
                json.WriteNumber("roughnessFactor", material.Roughness);
                if (material.Texture != null)
                {
                    json.WriteStartObject("baseColorTexture");
                    json.WriteNumber("index", images.IndexOf(material.Texture));
                    json.WriteEndObject();
                }
                json.WriteEndObject();
                if (material.BaseColor.a < .999f)
                    json.WriteString("alphaMode", "BLEND");
                json.WriteEndObject();
            }
            json.WriteEndArray();

            if (images.Count > 0)
            {
                json.WriteStartArray("samplers");
                json.WriteStartObject();
                json.WriteNumber("magFilter", 9729);
                json.WriteNumber("minFilter", 9987);
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteStartArray("textures");
                for (var i = 0; i < images.Count; i++)
                {
                    json.WriteStartObject();
                    json.WriteNumber("sampler", 0);
                    json.WriteNumber("source", i);
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteStartArray("images");
                foreach (var view in imageViews)
                {
                    json.WriteStartObject();
                    json.WriteNumber("bufferView", view);
                    json.WriteString("mimeType", "image/png");
                    json.WriteEndObject();
                }
                json.WriteEndArray();
            }

            json.WriteStartArray("accessors");
            foreach (var accessor in accessors)
            {
                json.WriteStartObject();
                json.WriteNumber("bufferView", accessor.View);
                json.WriteNumber("componentType", accessor.Component);
                json.WriteNumber("count", accessor.Count);
                json.WriteString("type", accessor.Type);
                if (accessor.Bounds != null)
                {
                    json.WriteStartArray("min");
                    for (var i = 0; i < 3; i++) json.WriteNumberValue(accessor.Bounds[i]);
                    json.WriteEndArray();
                    json.WriteStartArray("max");
                    for (var i = 3; i < 6; i++) json.WriteNumberValue(accessor.Bounds[i]);
                    json.WriteEndArray();
                }
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("bufferViews");
            foreach (var (offset, length, target) in views)
            {
                json.WriteStartObject();
                json.WriteNumber("buffer", 0);
                json.WriteNumber("byteOffset", offset);
                json.WriteNumber("byteLength", length);
                if (target != 0) json.WriteNumber("target", target);
                json.WriteEndObject();
            }
            json.WriteEndArray();

            json.WriteStartArray("buffers");
            json.WriteStartObject();
            json.WriteNumber("byteLength", bufferLength);
            json.WriteEndObject();
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return buffer.ToArray();
    }

    // Each view starts on a four-byte boundary, as accessors require.
    private static int View(List<(int Offset, int Length, int Target)> views, BinaryWriter binary, int target, Action write)
    {
        binary.Flush();
        while (binary.BaseStream.Length % 4 != 0)
            binary.Write((byte)0);
        var offset = (int)binary.BaseStream.Length;
        write();
        binary.Flush();
        views.Add((offset, (int)binary.BaseStream.Length - offset, target));
        return views.Count - 1;
    }

    private static void WriteIndex(BinaryWriter binary, int index, bool shortIndex)
    {
        if (shortIndex)
            binary.Write((ushort)index);
        else
            binary.Write((uint)index);
    }

    // Surfaces with the same colour, finish and texture share a material.
    private static int Material(List<MapSurface> materials, MapSurface surface)
    {
        for (var i = 0; i < materials.Count; i++)
        {
            var other = materials[i];
            if (other.BaseColor.r == surface.BaseColor.r && other.BaseColor.g == surface.BaseColor.g
                && other.BaseColor.b == surface.BaseColor.b && other.BaseColor.a == surface.BaseColor.a
                && other.Metallic == surface.Metallic && other.Roughness == surface.Roughness
                && ReferenceEquals(other.Texture, surface.Texture))
                return i;
        }
        materials.Add(surface);
        return materials.Count - 1;
    }

    private static float Linear(float srgb)
    {
        srgb = Math.Clamp(srgb, 0, 1);
        return srgb <= .04045f ? srgb / 12.92f : MathF.Pow((srgb + .055f) / 1.055f, 2.4f);
    }

    private static float[] Bounds(List<Vertex> vertices)
    {
        var bounds = new[] { float.MaxValue, float.MaxValue, float.MaxValue, float.MinValue, float.MinValue, float.MinValue };
        foreach (var v in vertices)
        {
            bounds[0] = Math.Min(bounds[0], v.Px); bounds[3] = Math.Max(bounds[3], v.Px);
            bounds[1] = Math.Min(bounds[1], v.Py); bounds[4] = Math.Max(bounds[4], v.Py);
            bounds[2] = Math.Min(bounds[2], -v.Pz); bounds[5] = Math.Max(bounds[5], -v.Pz);
        }
        return bounds;
    }

    private static int Align(int length) => (length + 3) & ~3;

    private static (List<Vertex> Vertices, int[] Indices) Weld(MapSurface surface)
    {
        var lookup = new Dictionary<Vertex, int>();
        var vertices = new List<Vertex>();
        var indices = new int[surface.Indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var source = surface.Indices[i];
            var p = surface.Positions[source];
            var vertex = new Vertex { Px = p.x, Py = p.y, Pz = p.z, A = 1 };
            if (surface.Normals != null)
            {
                var n = surface.Normals[source];
                (vertex.Nx, vertex.Ny, vertex.Nz) = (n.x, n.y, n.z);
            }
            if (surface.Uvs != null)
                (vertex.U, vertex.V) = (surface.Uvs[source].x, surface.Uvs[source].y);
            if (surface.Colors != null)
            {
                var c = surface.Colors[source];
                (vertex.R, vertex.G, vertex.B, vertex.A) = (c.r, c.g, c.b, c.a);
            }
            if (!lookup.TryGetValue(vertex, out var index))
            {
                index = vertices.Count;
                vertices.Add(vertex);
                lookup.Add(vertex, index);
            }
            indices[i] = index;
        }
        return (vertices, indices);
    }

    private record struct Vertex(float Px, float Py, float Pz, float Nx, float Ny, float Nz,
        float U, float V, float R, float G, float B, float A);

    private sealed record Accessor(int View, int Component, int Count, string Type, float[] Bounds);

    private sealed class Primitive
    {
        internal int Position, Indices, Material;
        internal int Normal = -1, Uv = -1, Color = -1;
    }
}
