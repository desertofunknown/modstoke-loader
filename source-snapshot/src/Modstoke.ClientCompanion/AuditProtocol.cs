using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Modstoke.ClientCompanion;

internal static class AuditProtocol
{
    // These wire values match the current Admin host receiver. Changing them requires a paired host change.
    internal const string NoticeMessage = "OpenACAI Admin: the host requested an inventory audit.";
    internal const string CommandName = "openacai.audit";
    private const int MaxChunks = 12;
    private const int MaxPayloadCharacters = 160;
    private const int MaxCommandBytes = 256;

    internal static bool TryBuild(Guid noticeId, IReadOnlyDictionary<int, int> items,
        string version, out List<string> commands)
    {
        commands = new List<string>();
        if (noticeId == Guid.Empty || items == null || !ValidVersion(version)) return false;
        var ids = new List<int>(items.Keys);
        ids.Sort();
        var payloads = new List<string>();
        var chunk = new StringBuilder();
        foreach (int id in ids)
        {
            int count = items[id];
            if (id <= 0 || count <= 0) return false;
            string pair = id.ToString(CultureInfo.InvariantCulture) + ":" + count.ToString(CultureInfo.InvariantCulture);
            if (pair.Length > MaxPayloadCharacters) return false;
            if (chunk.Length > 0 && chunk.Length + 1 + pair.Length > MaxPayloadCharacters)
            {
                payloads.Add(chunk.ToString());
                chunk.Clear();
                if (payloads.Count >= MaxChunks) return false;
            }
            if (chunk.Length > 0) chunk.Append(',');
            chunk.Append(pair);
        }
        payloads.Add(chunk.Length == 0 ? "-" : chunk.ToString());
        for (int i = 0; i < payloads.Count; i++)
        {
            string command = Command(noticeId, i, payloads.Count, version, payloads[i]);
            if (Encoding.UTF8.GetByteCount(command) > MaxCommandBytes)
            {
                commands.Clear();
                return false;
            }
            commands.Add(command);
        }
        return true;
    }

    internal static string Failure(Guid noticeId, string failure, string version)
    {
        if (noticeId == Guid.Empty || !ValidVersion(version) ||
            (failure != "!unavailable" && failure != "!too-large"))
            throw new ArgumentException("Invalid audit failure frame.");
        return Command(noticeId, 0, 1, version, failure);
    }

    private static string Command(Guid id, int index, int total, string version, string payload) =>
        "/" + CommandName + " " + id.ToString("N") + " " + index.ToString(CultureInfo.InvariantCulture) +
        " " + total.ToString(CultureInfo.InvariantCulture) + " " + version + " " + payload;

    private static bool ValidVersion(string version)
    {
        if (String.IsNullOrEmpty(version) || version.Length > 24) return false;
        foreach (char value in version)
            if (!((value >= 'a' && value <= 'z') || (value >= 'A' && value <= 'Z') ||
                  (value >= '0' && value <= '9') || value == '.' || value == '-')) return false;
        return true;
    }
}
