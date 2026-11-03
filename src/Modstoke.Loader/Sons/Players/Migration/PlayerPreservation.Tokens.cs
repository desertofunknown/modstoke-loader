// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bolt;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;

namespace Modstoke.Sons
{
    public static partial class PlayerPreservation
    {
        private const int MaximumTokenModuleBytes = 32768;
        private sealed record ItemTokenData(int ItemId, string Modules);

        private static ItemTokenData CaptureItemToken(IProtocolToken token, string field)
        {
            if (token == null) return null;
            Require(!token.WasCollected && IL2CPP.il2cpp_object_get_class(token.Pointer)
                == Il2CppClassPointerStore<ItemInstanceBoltDataToken>.NativeClassPtr,
                field + " has an unregistered protocol token type.");
            var native = token.Cast<ItemInstanceBoltDataToken>();
            var item = native.ItemInstance;
            Require(item != null && !item.WasCollected && item._itemID > 0,
                field + " has no live item instance.");
            string modules = CanonicalModules(PlayerInventoryBaseline.CaptureModules(item));
            var restored = PlayerInventoryBaseline.RestoreInstance(item._itemID, modules);
            Require(restored._itemID == item._itemID
                && CanonicalModules(PlayerInventoryBaseline.CaptureModules(restored)) == modules,
                field + " cannot independently round-trip its item data.");
            return new ItemTokenData(item._itemID, modules);
        }

        private static IProtocolToken RestoreItemToken(ItemTokenData data)
        {
            if (data == null) return null;
            var item = PlayerInventoryBaseline.RestoreInstance(data.ItemId, data.Modules);
            var native = new ItemInstanceBoltDataToken { ItemInstance = item };
            var token = native.Cast<IProtocolToken>();
            Require(CaptureItemToken(token, "Restored item token") == data,
                "Restored protocol token differs from its captured item data.");
            return token;
        }

        private static void WriteItemToken(BinaryWriter writer, ItemTokenData data)
        {
            writer.Write((byte)(data == null ? 0 : 1));
            if (data == null) return;
            Require(data.ItemId > 0, "Item token ID is invalid.");
            var bytes = Utf8.GetBytes(CanonicalModules(data.Modules));
            Require(writer.BaseStream.Position + 8L + bytes.Length <= MaximumPayloadBytes,
                "Item tokens exceed the player state payload bound.");
            writer.Write(data.ItemId);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static ItemTokenData ReadItemToken(BinaryReader reader)
        {
            byte kind = reader.ReadByte();
            if (kind == 0) return null;
            Require(kind == 1, "Item token discriminator is unsupported.");
            int id = reader.ReadInt32(), length = reader.ReadInt32();
            Require(id > 0 && length > 0 && length <= MaximumTokenModuleBytes
                && length <= reader.BaseStream.Length - reader.BaseStream.Position,
                "Item token length or item ID is invalid.");
            string modules = Utf8.GetString(reader.ReadBytes(length));
            Require(CanonicalModules(modules) == modules, "Item token module data is not canonical.");
            return new ItemTokenData(id, modules);
        }

        private static string CanonicalModules(string json)
        {
            Require(json != null && Utf8.GetByteCount(json) <= MaximumTokenModuleBytes,
                "Item token module data exceeds its bound.");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            Require(document.RootElement.ValueKind == JsonValueKind.Array
                && document.RootElement.GetArrayLength() <= 32, "Item token module data is malformed.");
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(document.RootElement, writer);
            Require(stream.Length <= MaximumTokenModuleBytes, "Canonical item token module data exceeds its bound.");
            return Utf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
        }

        private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var properties = new System.Collections.Generic.SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                    Require(!property.Name.StartsWith('$') && properties.TryAdd(property.Name, property.Value),
                        "Item token contains duplicate properties or type metadata.");
                writer.WriteStartObject();
                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var child in element.EnumerateArray()) WriteCanonical(child, writer);
                writer.WriteEndArray();
            }
            else element.WriteTo(writer);
        }
    }
}
