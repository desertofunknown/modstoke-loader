// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Modstoke.Localization
{
    /// <summary>
    /// The text of one language file. A file is a JSON object of key to text; nested objects join their keys
    /// with dots, and an object made only of plural categories ("one", "few", "many", "other"...) is one
    /// counted text. Keys starting with "_" are notes for translators, and // comments are allowed.
    /// </summary>
    internal sealed class StringCatalog
    {
        private static readonly JsonDocumentOptions Options = new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        internal readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, Dictionary<string, string>> Counted = new(StringComparer.Ordinal);

        /// <summary>Adds a file's text over what is already here; <paramref name="problem"/> hears what was skipped.</summary>
        internal void Merge(string json, Action<string> problem)
        {
            using var document = JsonDocument.Parse(json, Options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("A language file is one JSON object of key to text.");
            Read(document.RootElement, "", problem);
        }

        private void Read(JsonElement group, string prefix, Action<string> problem)
        {
            foreach (var entry in group.EnumerateObject())
            {
                if (entry.Name.StartsWith("_", StringComparison.Ordinal)) continue;
                var key = prefix + entry.Name;
                switch (entry.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        Texts[key] = entry.Value.GetString();
                        Counted.Remove(key);
                        break;
                    case JsonValueKind.Object when IsCounted(entry.Value):
                        Counted[key] = entry.Value.EnumerateObject()
                            .ToDictionary(form => form.Name, form => form.Value.GetString(), StringComparer.Ordinal);
                        Texts.Remove(key);
                        break;
                    case JsonValueKind.Object:
                        Read(entry.Value, key + ".", problem);
                        break;
                    default:
                        problem?.Invoke("\"" + key + "\" is not text and was skipped.");
                        break;
                }
            }
        }

        // A counted text needs the "other" form, which every language has, and nothing but plural forms.
        private static bool IsCounted(JsonElement group)
        {
            var hasOther = false;
            foreach (var form in group.EnumerateObject())
            {
                if (!PluralRules.IsCategory(form.Name) || form.Value.ValueKind != JsonValueKind.String) return false;
                hasOther |= form.Name == PluralRules.Other;
            }
            return hasOther;
        }
    }
}
