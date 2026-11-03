// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Exchanges native-mod compatibility claims. A decoded report is untrusted remote metadata,
    /// not a local runtime observation, proof of integrity, or authorization to admit a peer.
    /// </summary>
    internal static class NativeModReport
    {
        private const int MaxBytes = ModAdmissionWire.MaxDocumentBytes;
        private const int MaxEntries = 512;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        internal static bool TryCapture(out byte[] utf8, out LoadedModsSnapshot snapshot,
            out string error)
        {
            utf8 = null;
            snapshot = null;
            error = "Native mod report capture failed.";
            try
            {
                if (LoadedMods.TryCapture(out var source) != LoadedModsReadStatus.Success)
                    return false;

                var entries = new List<LoadedModEntry>();
                foreach (var entry in source.Entries)
                {
                    if (entry.Loader != "modstoke") continue;
                    if (entries.Count == MaxEntries) return false;
                    ValidateEntry(entry.Id, entry.Version, entry.State, entry.MetadataValid);
                    entries.Add(entry);
                }

                var captured = CreateSnapshot(entries, source.NativeLoadCompleted, source.NativeExecutionEnabled,
                    source.NativeStopped, source.Truncated, source.HasUnreadableEntries);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteBoolean("nativeLoadCompleted", captured.NativeLoadCompleted);
                    writer.WriteBoolean("nativeExecutionEnabled", captured.NativeExecutionEnabled);
                    writer.WriteBoolean("nativeStopped", captured.NativeStopped);
                    writer.WriteBoolean("truncated", captured.Truncated);
                    writer.WriteBoolean("hasUnreadableEntries", captured.HasUnreadableEntries);
                    writer.WriteStartArray("mods");
                    foreach (var entry in captured.Entries)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", entry.Id);
                        writer.WriteString("version", entry.Version);
                        writer.WriteNumber("state", (int)entry.State);
                        writer.WriteBoolean("metadataValid", entry.MetadataValid);
                        writer.WriteBoolean("ambiguous", entry.Ambiguous);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                if (stream.Length > MaxBytes) return false;
                utf8 = stream.ToArray();
                snapshot = captured;
                error = "";
                return true;
            }
            catch (Exception)
            {
                // Never expose local paths, exception details, or mod-provided text.
                return false;
            }
        }

        internal static bool TryRead(ReadOnlySpan<byte> utf8, out LoadedModsSnapshot snapshot,
            out string error)
        {
            snapshot = null;
            error = "Invalid native mod report.";
            if (utf8.Length == 0 || utf8.Length > MaxBytes) return false;
            try
            {
                StrictUtf8.GetCharCount(utf8);
                using var document = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions
                {
                    MaxDepth = 8,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow
                });
                var fields = ReadFields(document.RootElement, "schemaVersion", "nativeLoadCompleted",
                    "nativeExecutionEnabled", "nativeStopped", "truncated", "hasUnreadableEntries", "mods");
                if (ReadInt(fields["schemaVersion"]) != 1) return false;
                var loadCompleted = ReadBool(fields["nativeLoadCompleted"]);
                var executionEnabled = ReadBool(fields["nativeExecutionEnabled"]);
                var stopped = ReadBool(fields["nativeStopped"]);
                var truncated = ReadBool(fields["truncated"]);
                var unreadable = ReadBool(fields["hasUnreadableEntries"]);
                var mods = fields["mods"];
                if (mods.ValueKind != JsonValueKind.Array || mods.GetArrayLength() > MaxEntries) return false;
                var entries = new List<LoadedModEntry>(mods.GetArrayLength());
                foreach (var mod in mods.EnumerateArray())
                {
                    var values = ReadFields(mod, "id", "version", "state", "metadataValid", "ambiguous");
                    var id = ReadString(values["id"]);
                    var version = ReadString(values["version"]);
                    var state = (ModActivationState)ReadInt(values["state"]);
                    var metadataValid = ReadBool(values["metadataValid"]);
                    var ambiguous = ReadBool(values["ambiguous"]);
                    ValidateEntry(id, version, state, metadataValid);
                    entries.Add(new LoadedModEntry("modstoke", id, version, state,
                        false, false, metadataValid, ambiguous));
                }
                snapshot = CreateSnapshot(entries, loadCompleted, executionEnabled, stopped, truncated, unreadable);
                error = "";
                return true;
            }
            catch (Exception)
            {
                // Treat malformed encoding and syntax alike; never reflect remote data into errors.
                return false;
            }
        }

        private static Dictionary<string, JsonElement> ReadFields(JsonElement element, params string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new FormatException();
            var fields = new Dictionary<string, JsonElement>(names.Length, StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                var name = property.Name;
                if (Array.IndexOf(names, name) < 0 || !fields.TryAdd(name, property.Value))
                    throw new FormatException();
            }
            if (fields.Count != names.Length) throw new FormatException();
            return fields;
        }

        private static int ReadInt(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
                throw new FormatException();
            return value;
        }

        private static bool ReadBool(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
                throw new FormatException();
            return element.GetBoolean();
        }

        private static string ReadString(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.String) throw new FormatException();
            return element.GetString();
        }

        private static void ValidateEntry(string id, string version, ModActivationState state,
            bool metadataValid)
        {
            ValidateString(id, 256);
            ValidateString(version, 128);
            if (state != ModActivationState.NativeNotActivated
                && state != ModActivationState.NativeDeferred
                && state != ModActivationState.NativeActive
                && state != ModActivationState.NativeFailed
                && state != ModActivationState.NativeStopped)
                throw new FormatException();
            if (metadataValid && (!LoadedMods.ValidText(id, 256)
                || !LoadedMods.ValidText(version, 128)))
                throw new FormatException();
        }

        private static void ValidateString(string value, int maxScalars)
        {
            if (value == null || value.Length > maxScalars * 2) throw new FormatException();
            // EnumerateRunes replaces lone surrogates, so reject them before counting scalars.
            StrictUtf8.GetByteCount(value);
            var count = 0;
            foreach (var rune in value.EnumerateRunes())
            {
                if (++count > maxScalars) throw new FormatException();
            }
        }

        private static LoadedModsSnapshot CreateSnapshot(List<LoadedModEntry> entries,
            bool loadCompleted, bool executionEnabled, bool stopped, bool truncated, bool unreadable)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.Id)) duplicates.Add(entry.Id);
            }
            var copied = new LoadedModEntry[entries.Count];
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                copied[index] = new LoadedModEntry("modstoke", entry.Id, entry.Version, entry.State,
                    false, false, entry.MetadataValid, entry.Ambiguous || duplicates.Contains(entry.Id));
            }
            return new LoadedModsSnapshot(Array.AsReadOnly(copied), loadCompleted,
                executionEnabled, stopped, truncated, unreadable);
        }
    }
}
