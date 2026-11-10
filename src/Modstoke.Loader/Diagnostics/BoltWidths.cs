// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Bolt;
using Il2CppInterop.Runtime;
using Modstoke.Sons;
using Sons.Inventory;
using UdpKit;

namespace Modstoke.Loader
{
    /// <summary>
    /// Reports how many bits Bolt gives each networked item-id field, and whether item tokens keep large ids.
    /// Mod items above the native 2047 limit can only travel through fields wide enough to hold them.
    /// </summary>
    internal static class BoltWidths
    {
        private static readonly int[] ProbeIds = { 1999, 2047, 2048, 3000, 70000 };
        private const int RifleItemId = 361;

        private static readonly string[] ProbeEvents =
        {
            "PlayerAddItem", "PlayerRemoveItem", "ItemRemoveFromPlayer", "ItemStorageAddItemEvent",
            "ItemStorageRemoveItemEvent", "DestroyPickUp", "SpawnLootItemEvent", "RackAdd", "PlaceTrophy",
            "FireCookEvent", "TrapRefillEvent", "AttachItemToZiplineEvent", "PlayerGiveActorItem",
            "PlayerRequestActorItem", "AddItemToDoor"
        };

        internal static string Run()
        {
            var report = new StringBuilder();
            report.AppendLine("Bolt integer fields that look like item ids (bits; max value)");
            var rows = new List<string>();
            try
            {
                foreach (var entry in Factory._factoriesByKey)
                {
                    var meta = entry.Value?.TryCast<NetworkObj_Meta>();
                    if (meta == null)
                        continue;
                    var owner = Il2CppType.TypeFromPointer(entry.Value.TryCast<IFactory>().ObjectClass)?.Name ?? entry.Key.IdString;
                    var properties = meta.Properties;
                    if (properties == null)
                        continue;
                    for (int i = 0; i < properties.Length; i++)
                    {
                        var info = properties[i];
                        var integer = info?.Property?.TryCast<NetworkProperty_Integer>();
                        if (integer == null)
                            continue;
                        var name = integer.PropertyName ?? "";
                        var path = info.Paths is { Length: > 0 } paths ? string.Join("/", paths) : name;
                        if (!LooksLikeItemId(name) && !LooksLikeItemId(path))
                            continue;
                        var bits = integer.Compression.BitsRequired;
                        var max = bits is > 0 and < 32 ? ((1L << bits) - 1).ToString() : "full int";
                        rows.Add(owner + "." + path + ": " + bits + " bits; " + max);
                    }
                }
            }
            catch (Exception error)
            {
                rows.Add("Reading Bolt metadata failed: " + error.Message);
            }
            rows.Sort(StringComparer.Ordinal);
            foreach (var row in rows)
                report.AppendLine("  " + row);

            report.AppendLine();
            report.AppendLine("Item events (carrier " + (ItemIdCarrier.Active ? "active" : "inactive") + "): value kept by the setter; then the id written into storage directly, read back and sent through Write/Read");
            var events = typeof(PlayerAddItem).Assembly;
            foreach (var name in ProbeEvents)
                report.AppendLine("  " + name + ": " + ProbeEvent(events.GetType(name)));

            report.AppendLine();
            report.AppendLine("ItemInstanceBoltDataToken round trip (a rifle instance carrying each id)");
            foreach (var id in ProbeIds)
                report.AppendLine("  " + id + ": " + RoundTrip(id));

            var text = report.ToString();
            try
            {
                var path = Path.Combine(LoaderPaths.UserDataDirectory, "bolt-widths.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
                text += Environment.NewLine + "Written to " + path;
            }
            catch (Exception error)
            {
                text += Environment.NewLine + "Could not write the report: " + error.Message;
            }
            return text;
        }

        private static bool LooksLikeItemId(string name) =>
            name.IndexOf("item", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("cloth", StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("mod", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string ProbeEvent(Type type)
        {
            Event sent = null, received = null;
            try
            {
                // Declared only: Bolt's NetworkObj base has an indexer that reflection also calls "Item".
                const BindingFlags declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                var property = type?.GetProperty("ItemId", declared) ?? type?.GetProperty("Item", declared);
                var create = type?.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                if (property == null || create == null)
                    return "not found";

                sent = (Event)create.Invoke(null, null);
                received = (Event)create.Invoke(null, null);
                var integer = FindInteger(sent.Meta, property.Name);
                if (integer == null)
                    return "no integer property " + property.Name;
                var results = new List<string>();
                foreach (var id in ProbeIds)
                {
                    // The carrier treats the probe id as a reserved mod id, so 'setter' shows what mod items get.
                    ItemIdCarrier.ProbeItemId = id;
                    try { property.SetValue(sent, id); }
                    finally { ItemIdCarrier.ProbeItemId = 0; }
                    var kept = (int)property.GetValue(sent);
                    // SetDynamic leaves event storage unchanged, so the id goes straight into the storage slot.
                    property.SetValue(sent, 5);
                    var direct = WriteStored(sent, integer, id, 5);
                    var stored = (int)property.GetValue(sent);
                    var packet = new UdpPacket(new byte[64]);
                    integer.Write(null, sent, sent.Storage, packet);
                    var bits = packet.Position;
                    packet.Position = 0;
                    integer.Read(null, received, received.Storage, packet);
                    results.Add(id + ": setter " + kept + ", direct " + (direct ? "written" : "refused") + ", getter "
                        + stored + ", received " + (int)property.GetValue(received) + " (" + bits + "b)");
                }
                return string.Join("; ", results);
            }
            catch (Exception error)
            {
                var cause = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
                return "could not test: " + cause.GetType().Name + ": " + FirstLine(cause.Message);
            }
            finally
            {
                // Probe events are never sent, so their storage goes straight back to the pool.
                try { sent?.FreeStorage(); received?.FreeStorage(); }
                catch { }
            }
        }

        // Writes NetworkValue.Int0 of the property's slot in place. The slot must already hold the value the getter
        // reported, which confirms the computed address before anything is written.
        private static unsafe bool WriteStored(Event target, NetworkProperty property, int value, int current)
        {
            var values = target.Storage?.Values;
            var index = target.OffsetStorage + property.OffsetStorage;
            var type = Il2CppClassPointerStore<NetworkValue>.NativeClassPtr;
            var field = IL2CPP.il2cpp_class_get_field_from_name(type, "Int0");
            if (values == null || index < 0 || index >= values.Length || field == IntPtr.Zero)
                return false;
            var size = IL2CPP.il2cpp_class_array_element_size(type);
            // Field offsets count the boxed object header; array elements are stored without it.
            var offset = (int)IL2CPP.il2cpp_field_get_offset(field) - 2 * IntPtr.Size;
            if (size < sizeof(int) || size > 512 || offset < 0 || offset > size - sizeof(int))
                return false;
            var slot = (int*)((byte*)values.Pointer + 4 * IntPtr.Size + (long)index * size + offset);
            if (*slot != current)
                return false;
            *slot = value;
            return true;
        }

        private static NetworkProperty_Integer FindInteger(NetworkObj_Meta meta, string name)
        {
            var properties = meta?.Properties;
            for (int i = 0; properties != null && i < properties.Length; i++)
            {
                var integer = properties[i]?.Property?.TryCast<NetworkProperty_Integer>();
                if (integer != null && integer.PropertyName == name)
                    return integer;
            }
            return null;
        }

        // A real item gives the token a valid instance to write; only the id is swapped for the probe value.
        private static string RoundTrip(int itemId)
        {
            try
            {
                var instance = new ItemInstance(RifleItemId) { _itemID = itemId };
                var token = new ItemInstanceBoltDataToken { ItemInstance = instance };
                var packet = new UdpPacket(new byte[4096]);
                token.Write(packet);
                var written = packet.Position;
                packet.Position = 0;
                var rawShort = packet.ReadShort();
                packet.Position = 0;
                var rawInt = packet.ReadInt();
                packet.Position = 0;
                string read;
                try
                {
                    var copy = new ItemInstanceBoltDataToken();
                    copy.Read(packet);
                    read = copy.ItemInstance?._itemID.ToString() ?? "null";
                }
                catch (Exception error)
                {
                    read = "read failed (" + error.GetType().Name + ")";
                }
                return "read back " + read + "; " + written + " bits; first 16 bits " + rawShort + ", first 32 bits " + rawInt;
            }
            catch (Exception error)
            {
                return "could not test: " + error.GetType().Name + ": " + FirstLine(error.Message);
            }
        }

        private static string FirstLine(string text)
        {
            var end = text?.IndexOfAny(new[] { '\r', '\n' }) ?? -1;
            return end < 0 ? text : text.Substring(0, end);
        }
    }
}
