// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Modstoke.Sons
{
    public sealed partial class ModuleStackStore
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private static readonly string[] DocumentFields = { "Version", "Revision", "TotalCount", "Runs" };
        private static readonly string[] RunFields = { "Count", "ItemId", "NativeModulesJson", "SupplementalState", "Fingerprint" };
        private static readonly string[] StateFields = { "ContainerVolume", "LimbIsFresh", "LimbBloodReveal" };

        /// <summary>
        /// Pure managed, bounded version-1 parsing for host save staging or client readback.
        /// Validates all structure, exact snapshot fingerprints, counts and run order before
        /// returning immutable data. No mutable client store, native calls or trusted sender
        /// claim. Limits are caller policy and are never raised by the serialized document.
        /// </summary>
        public static bool TryParseSnapshot(string json, out ModuleStackSnapshot snapshot,
            out string detail, ModuleStackLimits limits = null)
        {
            snapshot = null;
            try
            {
                snapshot = ParseSnapshot(json, limits ?? ModuleStackLimits.Default);
                detail = "Snapshot data parsed; sender, world and native custody are not verified.";
                return true;
            }
            catch (Exception error) { detail = error.Message; return false; }
        }

        private static ModuleStackSnapshot ParseSnapshot(string json, ModuleStackLimits limits)
        {
            Require(json != null && json.Length <= limits.MaxSerializedBytes
                && Utf8.GetByteCount(json) <= limits.MaxSerializedBytes, "The serialized store exceeds the byte limit or is missing.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RequireObject(root, DocumentFields);
            Require(root.GetProperty("Version").GetInt32() == FormatVersion, "Unsupported module store format version.");
            long revision = root.GetProperty("Revision").GetInt64();
            int count = root.GetProperty("TotalCount").GetInt32();
            Require(revision >= 0 && count >= 0 && count <= limits.MaxCount, "Invalid revision or total count.");
            var inputRuns = root.GetProperty("Runs");
            Require(inputRuns.ValueKind == JsonValueKind.Array && inputRuns.GetArrayLength() <= limits.MaxRuns,
                "The run array is missing or exceeds the run limit.");
            var runs = new ModuleStackRun[inputRuns.GetArrayLength()];
            int total = 0, index = 0;
            foreach (var run in inputRuns.EnumerateArray())
            {
                RequireObject(run, RunFields);
                int amount = run.GetProperty("Count").GetInt32();
                Require(amount > 0 && amount <= limits.MaxCount - total, "Invalid run count or count limit exceeded.");
                var supplemental = run.GetProperty("SupplementalState");
                RequireObject(supplemental, StateFields);
                var volume = supplemental.GetProperty("ContainerVolume");
                var fresh = supplemental.GetProperty("LimbIsFresh");
                var blood = supplemental.GetProperty("LimbBloodReveal");
                var state = new PlayerInventoryModuleState(
                    volume.ValueKind == JsonValueKind.Null ? null : volume.GetSingle(),
                    fresh.ValueKind == JsonValueKind.Null ? null : fresh.GetBoolean(),
                    blood.ValueKind == JsonValueKind.Null ? null : blood.GetUInt32());
                var snapshot = ItemModuleSnapshot.FromSerialized(run.GetProperty("ItemId").GetInt32(),
                    run.GetProperty("NativeModulesJson").GetString(), state);
                Require(string.Equals(snapshot.Fingerprint, run.GetProperty("Fingerprint").GetString(), StringComparison.Ordinal),
                    "A snapshot fingerprint does not match its complete canonical data.");
                Require(index == 0 || !runs[index - 1].Snapshot.SameState(snapshot),
                    "Adjacent identical runs must be merged in a version-1 document.");
                runs[index++] = new(snapshot, amount);
                total += amount;
            }
            Require(total == count, "The run counts do not match TotalCount.");
            return CreateSnapshot(revision, total, runs, limits);
        }

        private static void RequireObject(JsonElement value, string[] fields)
        {
            Require(value.ValueKind == JsonValueKind.Object, "A required store object is missing.");
            int seen = 0;
            foreach (var property in value.EnumerateObject())
            {
                string name = property.Name;
                int index = Array.IndexOf(fields, name);
                if (index < 0) throw new InvalidOperationException("Unknown store property: " + name);
                int bit = 1 << index;
                if ((seen & bit) != 0) throw new InvalidOperationException("Duplicate store property: " + name);
                seen |= bit;
            }
            Require(seen == (1 << fields.Length) - 1, "Required store properties are missing.");
        }

        private static ModuleStackSnapshot CreateSnapshot(long revision, int totalCount,
            ModuleStackRun[] runs, ModuleStackLimits limits)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("Version", FormatVersion);
                writer.WriteNumber("Revision", revision);
                writer.WriteNumber("TotalCount", totalCount);
                writer.WriteStartArray("Runs");
                foreach (var run in runs)
                {
                    var snapshot = run.Snapshot;
                    writer.WriteStartObject();
                    writer.WriteNumber("Count", run.Count);
                    writer.WriteNumber("ItemId", snapshot.ItemId);
                    writer.WriteString("NativeModulesJson", snapshot.NativeModulesJson);
                    writer.WriteStartObject("SupplementalState");
                    var state = snapshot.SupplementalState;
                    if (state.ContainerVolume is { } volume) writer.WriteNumber("ContainerVolume", volume);
                    else writer.WriteNull("ContainerVolume");
                    if (state.LimbIsFresh is { } fresh) writer.WriteBoolean("LimbIsFresh", fresh);
                    else writer.WriteNull("LimbIsFresh");
                    if (state.LimbBloodReveal is { } blood) writer.WriteNumber("LimbBloodReveal", blood);
                    else writer.WriteNull("LimbBloodReveal");
                    writer.WriteEndObject();
                    writer.WriteString("Fingerprint", snapshot.Fingerprint);
                    writer.WriteEndObject();
                    // Stop oversized candidates before serializing the remaining runs.
                    writer.Flush();
                    Require(buffer.Length <= limits.MaxSerializedBytes, "The serialized store byte limit would be exceeded.");
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            Require(buffer.Length <= limits.MaxSerializedBytes, "The serialized store byte limit would be exceeded.");
            int size = checked((int)buffer.Length);
            return new(revision, totalCount, runs, Utf8.GetString(buffer.GetBuffer(), 0, size), size);
        }
    }
}
