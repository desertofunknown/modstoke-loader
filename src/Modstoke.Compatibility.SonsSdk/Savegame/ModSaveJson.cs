// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper.

using System.Text.Json;

namespace SonsSdk;

internal static class ModSaveJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    internal static string Serialize(List<NamedSaveData> entries)
    {
        // A legacy mod can replace the global contract, including the envelope format.
        if (Alt.Json.JsonConvert.DefaultSettings != null)
            return Alt.Json.JsonConvert.SerializeObject(entries);
        return JsonSerializer.Serialize(entries, Options);
    }

    internal static List<NamedSaveData> Deserialize(string json)
    {
        if (Alt.Json.JsonConvert.DefaultSettings != null)
            return Alt.Json.JsonConvert.DeserializeObject<List<NamedSaveData>>(json);
        try
        {
            return JsonSerializer.Deserialize<List<NamedSaveData>>(json, Options);
        }
        catch (JsonException)
        {
            // Retain support for older Newtonsoft-specific input without changing new saves.
            return Alt.Json.JsonConvert.DeserializeObject<List<NamedSaveData>>(json);
        }
    }
}
