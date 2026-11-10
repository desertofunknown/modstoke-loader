// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Bolt;
using Il2CppInterop.Runtime;
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
            report.AppendLine("ItemInstanceBoltDataToken round trip");
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

        private static string RoundTrip(int itemId)
        {
            try
            {
                var token = new ItemInstanceBoltDataToken { ItemInstance = new ItemInstance(itemId) };
                var packet = new UdpPacket(new byte[4096]);
                token.Write(packet);
                var written = packet.Position;
                packet.Position = 0;
                var copy = new ItemInstanceBoltDataToken();
                copy.Read(packet);
                var read = copy.ItemInstance?._itemID;
                return read == itemId
                    ? "kept (" + written + " bits written)"
                    : "changed to " + (read?.ToString() ?? "null") + " (" + written + " bits written)";
            }
            catch (Exception error)
            {
                return "could not test: " + error.GetType().Name + ": " + error.Message;
            }
        }
    }
}
