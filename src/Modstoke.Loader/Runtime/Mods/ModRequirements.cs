// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Modstoke.Loader
{
    /// <summary>Parses portable native package requirements without establishing runtime compatibility.</summary>
    public static class ModRequirements
    {
        public const int MaxDocumentBytes = 65536;
        public const int MaxPackages = 128;

        /// <summary>
        /// Accepts schema 1 JSON with exact, case-sensitive field names. IDs and versions are
        /// preserved verbatim. Failure returns a null document and a bounded error without input data.
        /// </summary>
        public static bool TryParse(string json, out ModRequirementsDocument document, out string error) =>
            ModRequirementsDocument.TryParse(json, out document, out error);
    }

    /// <summary>An immutable, validated preparation list; it is not an authorization or integrity claim.</summary>
    public sealed class ModRequirementsDocument
    {
        private const string Format = "modstoke.server-requirements";
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);
        private static readonly string[] DocumentFields = { "format", "schemaVersion", "name", "packages" };
        private static readonly string[] PackageFields = { "loader", "id", "version" };
        private readonly byte[] _canonicalUtf8;

        private ModRequirementsDocument(string name, ModRequirement[] packages, byte[] canonicalUtf8)
        {
            Name = name;
            Packages = Array.AsReadOnly(packages);
            _canonicalUtf8 = canonicalUtf8;
        }

        public string Name { get; }
        public IReadOnlyList<ModRequirement> Packages { get; }

        // Callers cannot mutate the document's retained transport representation.
        internal byte[] GetCanonicalUtf8() => (byte[])_canonicalUtf8.Clone();

        internal static bool TryParse(string json, out ModRequirementsDocument document, out string error)
        {
            document = null;
            error = "Invalid server requirements JSON.";
            if (json == null) return false;
            try
            {
                // UTF-8 cannot be shorter than the corresponding valid UTF-16 code-unit count.
                if (json.Length > ModRequirements.MaxDocumentBytes
                    || StrictUtf8.GetByteCount(json) > ModRequirements.MaxDocumentBytes)
                {
                    error = "Server requirements must be at most 64 KiB of UTF-8 JSON.";
                    return false;
                }
                using var parsed = JsonDocument.Parse(StrictUtf8.GetBytes(json), new JsonDocumentOptions { MaxDepth = 4 });
                if (!TryReadFields(parsed.RootElement, DocumentFields, out var fields, out error)) return false;
                if (fields["format"].ValueKind != JsonValueKind.String || fields["format"].GetString() != Format
                    || fields["schemaVersion"].ValueKind != JsonValueKind.Number
                    || !fields["schemaVersion"].TryGetUInt32(out var schema) || schema != 1)
                {
                    error = "Expected modstoke.server-requirements with schemaVersion 1.";
                    return false;
                }
                if (!TryReadText(fields["name"], 120, out var name))
                {
                    error = "Name must contain 1 to 120 Unicode scalars, without controls or edge whitespace.";
                    return false;
                }
                var packageArray = fields["packages"];
                if (packageArray.ValueKind != JsonValueKind.Array || packageArray.GetArrayLength() < 1
                    || packageArray.GetArrayLength() > ModRequirements.MaxPackages)
                {
                    error = "Server requirements must list between 1 and 128 packages.";
                    return false;
                }
                var packages = new ModRequirement[packageArray.GetArrayLength()];
                var packageIndex = 0;
                var nativeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var lowercaseIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var package in packageArray.EnumerateArray())
                {
                    if (!TryReadFields(package, PackageFields, out var packageFields, out error)) return false;
                    if (packageFields["loader"].ValueKind != JsonValueKind.String
                        || packageFields["loader"].GetString() != "modstoke")
                    {
                        error = "Only the modstoke native package loader is supported.";
                        return false;
                    }
                    if (!TryReadText(packageFields["id"], 256, out var id))
                    {
                        error = "Package ID must contain 1 to 256 Unicode scalars, without controls or edge whitespace.";
                        return false;
                    }
                    if (!TryReadText(packageFields["version"], 128, out var version))
                    {
                        error = "Package version must contain 1 to 128 Unicode scalars, without controls or edge whitespace.";
                        return false;
                    }
                    // Honor native host identity and also reject invariant-lowercase collisions.
                    // Rust's Unicode lowercase can expand/contextually map characters differently;
                    // this additional check does not claim identical Manager Unicode behavior.
                    if (!nativeIds.Add(id) || !lowercaseIds.Add(id.ToLowerInvariant()))
                    {
                        error = "Duplicate package ID (case-insensitive).";
                        return false;
                    }
                    packages[packageIndex++] = new ModRequirement(id, version);
                }
                var canonicalUtf8 = WriteCanonicalUtf8(name, packages);
                if (canonicalUtf8.Length > ModRequirements.MaxDocumentBytes)
                {
                    error = "The canonical requirements exceed 64 KiB. Use a smaller package list.";
                    return false;
                }
                document = new ModRequirementsDocument(name, packages, canonicalUtf8);
                error = null;
                return true;
            }
            catch (EncoderFallbackException) { error = "Server requirements contain invalid Unicode."; }
            catch (JsonException) { error = "Invalid server requirements JSON."; }
            catch (InvalidOperationException) { error = "Invalid server requirements JSON or Unicode."; }
            return false;
        }

        private static bool TryReadFields(JsonElement element, string[] allowed,
            out Dictionary<string, JsonElement> fields, out string error)
        {
            fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            error = "Server requirements must contain objects with exactly the expected fields.";
            if (element.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (Array.IndexOf(allowed, name) < 0 || !fields.TryAdd(name, property.Value))
                {
                    error = "Server requirements contain an unknown or duplicate field.";
                    return false;
                }
            }
            if (fields.Count != allowed.Length) return false;
            error = null;
            return true;
        }

        private static bool TryReadText(JsonElement element, int maxScalars, out string value)
        {
            value = null;
            if (element.ValueKind != JsonValueKind.String) return false;
            var text = element.GetString();
            if (string.IsNullOrEmpty(text) || text.Length > maxScalars * 2
                || char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])) return false;
            var scalarCount = 0;
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (char.IsControl(current) || ++scalarCount > maxScalars) return false;
                if (char.IsHighSurrogate(current))
                {
                    if (++index >= text.Length || !char.IsLowSurrogate(text[index])) return false;
                }
                else if (char.IsLowSurrogate(current)) return false;
            }
            value = text;
            return true;
        }

        private static byte[] WriteCanonicalUtf8(string name, ModRequirement[] packages)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
                { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                writer.WriteString("format", Format);
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("name", name);
                writer.WriteStartArray("packages");
                foreach (var package in packages)
                {
                    writer.WriteStartObject();
                    writer.WriteString("loader", "modstoke");
                    writer.WriteString("id", package.Id);
                    writer.WriteString("version", package.Version);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return stream.ToArray();
        }
    }

    /// <summary>An exact native package identity and version from a validated requirements document.</summary>
    public sealed class ModRequirement
    {
        internal ModRequirement(string id, string version) { Id = id; Version = version; }
        public string Id { get; }
        public string Version { get; }
    }
}
