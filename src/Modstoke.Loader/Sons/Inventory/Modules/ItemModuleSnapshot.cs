// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using global::Sons.Inventory;
using NativeJson = Newtonsoft.Json.JsonConvert;

namespace Modstoke.Sons
{
    /// <summary>
    /// Immutable native module data for mod-owned storage. Capture is not an item transfer,
    /// ownership proof or grant. Stored timers are snapshots, not running native updaters.
    /// </summary>
    public sealed class ItemModuleSnapshot
    {
        public const int MaximumModuleBytes = 65536;
        private static readonly UTF8Encoding Utf8 = new(false, true);

        private ItemModuleSnapshot(int itemId, string modules,
            PlayerInventoryModuleState supplemental)
        {
            ItemId = itemId;
            NativeModulesJson = modules;
            SupplementalState = supplemental;
            var payload = JsonSerializer.Serialize(new { ItemId, NativeModulesJson, SupplementalState });
            Fingerprint = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(payload)));
        }

        public int ItemId { get; }
        public string NativeModulesJson { get; }
        public PlayerInventoryModuleState SupplementalState { get; }
        public string Fingerprint { get; }

        public static bool TryCapture(ItemInstance instance, out ItemModuleSnapshot snapshot,
            out string detail)
        {
            snapshot = null;
            detail = "Capture requires the initialized Unity thread and an actual native item with supported modules.";
            if (!Crafting.OnCraftExecutionThread) return false;
            try
            {
                if (instance is null || instance.WasCollected || instance._itemID <= 0)
                    throw new InvalidOperationException(detail);
                var data = instance.Data;
                var list = instance._modules;
                if (!Crafting.CraftExecutionLive(data) || data.Id != instance._itemID
                    || list is null || list.WasCollected || list.Count is < 1 or > 10)
                    throw new InvalidOperationException(detail);
                var modules = new ItemInstanceModule[list.Count];
                for (int i = 0; i < modules.Length; i++) modules[i] = list[i];
                var saved = instance.GetModuleSaveData();
                if (saved is null || saved.WasCollected || saved.Count != modules.Length)
                    throw new InvalidOperationException("Native saving omitted module state.");
                for (int i = 0; i < modules.Length; i++)
                    if (!ItemRegistration.NativeModuleRecordMatches(modules[i], saved[i]))
                        throw new InvalidOperationException("A module has no exact supported native save record.");
                var captured = FromSerialized(instance._itemID, NativeJson.SerializeObject(saved, Settings()),
                    PlayerInventoryBaseline.CaptureModuleState(instance));
                if (instance.WasCollected || instance.Data?.Pointer != data.Pointer
                    || instance._itemID != captured.ItemId || instance._modules?.Pointer != list.Pointer
                    || list.Count != modules.Length)
                    throw new InvalidOperationException("Native item identity changed during capture.");
                for (int i = 0; i < modules.Length; i++)
                    if (modules[i] is null || modules[i].WasCollected || list[i]?.Pointer != modules[i].Pointer)
                        throw new InvalidOperationException("Native module identity changed during capture.");
                snapshot = captured;
                detail = "Native module state copied; no inventory, storage or item custody changed.";
                return true;
            }
            catch (Exception error) { detail = error.Message; return false; }
        }

        // Deserialization validates data only. It does not authorize a client to add stock.
        internal static ItemModuleSnapshot FromSerialized(int itemId, string json,
            PlayerInventoryModuleState supplemental)
        {
            if (itemId <= 0 || json == null || json.Length > MaximumModuleBytes
                || Utf8.GetByteCount(json) > MaximumModuleBytes || supplemental == null)
                throw new ArgumentException("The module snapshot identity, size or supplemental state is invalid.");
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() is < 1 or > 10)
                throw new ArgumentException("A module snapshot requires one to ten native module records.");
            var records = new SortedDictionary<int, JsonElement>();
            foreach (var record in root.EnumerateArray())
            {
                if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("ModuleId", out var id)
                    || !id.TryGetInt32(out int number) || number is < 0 or > 9 || !records.TryAdd(number, record))
                    throw new ArgumentException("Module IDs must be supported and unique.");
            }
            bool volume = records.ContainsKey((int)ItemInstance.ItemInstanceModuleType.VolumeContainer);
            bool limb = records.ContainsKey((int)ItemInstance.ItemInstanceModuleType.DismemberedLimb);
            if (supplemental.ContainerVolume.HasValue != volume || supplemental.LimbIsFresh.HasValue != limb
                || supplemental.LimbBloodReveal.HasValue != limb
                || supplemental.ContainerVolume is { } amount && (!float.IsFinite(amount) || amount < 0))
                throw new ArgumentException("Supplemental state does not match the module shape.");
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var record in records.Values) WriteCanonical(writer, record);
                writer.WriteEndArray();
            }
            if (buffer.Length > MaximumModuleBytes) throw new ArgumentException("Canonical module data exceeds the size limit.");
            return new(itemId, Utf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length)), supplemental);
        }

        internal bool SameState(ItemModuleSnapshot other) => other != null
            && ItemId == other.ItemId && string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal)
            && string.Equals(NativeModulesJson, other.NativeModulesJson, StringComparison.Ordinal)
            && SupplementalState == other.SupplementalState;

        internal IReadOnlyList<ItemInstanceModule.SaveData> CreateNativeRecords()
        {
            if (!Crafting.OnCraftExecutionThread)
                throw new InvalidOperationException("Native records require the initialized Unity thread.");
            var records = NativeJson.DeserializeObject<Il2CppSystem.Collections.Generic.List<ItemInstanceModule.SaveData>>(
                NativeModulesJson, Settings());
            if (records is null || records.WasCollected || records.Count is < 1 or > 10)
                throw new InvalidOperationException("Native module records could not be reconstructed.");
            var readback = FromSerialized(ItemId, NativeJson.SerializeObject(records, Settings()), SupplementalState);
            if (!SameState(readback)) throw new InvalidOperationException("The native record reader changed saved module data.");
            var result = new ItemInstanceModule.SaveData[records.Count];
            for (int i = 0; i < result.Length; i++) result[i] = records[i];
            return result;
        }

        internal void RestoreSupplemental(ItemInstance instance, IReadOnlyList<ItemInstanceModule.SaveData> records)
        {
            // Native LoadFromSave can restart expired timers and omits these supplemental fields.
            // The caller has already proven the exact module shape and retained result custody.
            foreach (var record in records)
                if (record.ModuleId == (int)ItemInstance.ItemInstanceModuleType.Perishable)
                {
                    if (!instance.TryGetModule<PerishableItemInstanceModule>(out var perishable)
                        || perishable is null || perishable.WasCollected
                        || record.TryCast<PerishableItemInstanceModule.PerishableSaveData>() is not { } decay)
                        throw new InvalidOperationException("The perishable module changed during restoration.");
                    perishable._remainingSecondsUntilDecay = decay.TimeRemainingInState;
                    perishable._PauseDecay_k__BackingField = decay.PauseDecay;
                }
            if (SupplementalState.ContainerVolume is { } volume)
            {
                if (!instance.TryGetModule<VolumeContainerItemInstanceModule>(out var container)
                    || container is null || container.WasCollected)
                    throw new InvalidOperationException("The volume module changed during restoration.");
                container._CurrentVolume_k__BackingField = volume;
            }
            if (SupplementalState.LimbIsFresh is { } fresh)
            {
                if (!instance.TryGetModule<DismemberedLimbItemInstanceModule>(out var limb)
                    || limb is null || limb.WasCollected)
                    throw new InvalidOperationException("The limb module changed during restoration.");
                uint color = SupplementalState.LimbBloodReveal.Value;
                limb._isFresh = fresh;
                limb._bloodReveal = new UnityEngine.Color32((byte)(color & 255), (byte)((color >> 8) & 255),
                    (byte)((color >> 16) & 255), (byte)(color >> 24));
            }
        }

        private static Newtonsoft.Json.JsonSerializerSettings Settings()
        {
            var converters = new Il2CppSystem.Collections.Generic.List<Newtonsoft.Json.JsonConverter>();
            converters.Add(new ItemInstanceModuleSaveDataJsonConverter());
            return new Newtonsoft.Json.JsonSerializerSettings
            {
                TypeNameHandling = Newtonsoft.Json.TypeNameHandling.None,
                MetadataPropertyHandling = Newtonsoft.Json.MetadataPropertyHandling.Ignore,
                MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Error,
                Converters = converters.Cast<Il2CppSystem.Collections.Generic.IList<Newtonsoft.Json.JsonConverter>>()
            };
        }

        private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var properties = value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
                writer.WriteStartObject();
                string previous = null;
                foreach (var property in properties)
                {
                    var name = property.Name;
                    if (name.StartsWith('$') || name == previous)
                        throw new ArgumentException("JSON type metadata and duplicate properties are not allowed.");
                    previous = name;
                    writer.WritePropertyName(name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
            }
            else if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var child in value.EnumerateArray()) WriteCanonical(writer, child);
                writer.WriteEndArray();
            }
            else value.WriteTo(writer);
        }
    }
}
