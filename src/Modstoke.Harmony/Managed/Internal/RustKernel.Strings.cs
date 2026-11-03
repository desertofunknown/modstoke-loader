// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Linq;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal static string JoinStrings<T>(IEnumerable<T> source, Func<T, string> converter, string delimiter)
    {
        // Keep Aggregate's source validation, enumeration, converter calls and
        // disposal on the CLR. Strings are immutable once each converter returns.
        var parts = source.Aggregate(new List<string>(), (values, item) =>
        {
            values.Add(converter(item));
            return values;
        });
        if (parts.Count == 0) return string.Empty;

        var lengths = new uint[parts.Count];
        var characterCount = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            lengths[i] = (uint)(parts[i]?.Length ?? 0);
            characterCount = checked(characterCount + (int)lengths[i]);
        }
        var text = new char[characterCount];
        var offset = 0;
        foreach (var part in parts)
        {
            if (part == null) continue;
            part.CopyTo(0, text, offset, part.Length);
            offset += part.Length;
        }
        uint length = 0;
        fixed (uint* sizes = lengths)
        fixed (char* characters = text)
        fixed (char* separator = delimiter)
            Check(StringExports.Join(sizes, (uint)lengths.Length, characters, (uint)text.Length,
                separator, (uint)(delimiter?.Length ?? 0), null, 0, &length), "string join length");

        return string.Create(checked((int)length), (lengths, text, delimiter), static (output, inputs) =>
        {
            uint written = 0;
            fixed (uint* sizes = inputs.lengths)
            fixed (char* characters = inputs.text)
            fixed (char* separator = inputs.delimiter)
            fixed (char* destination = output)
                Check(StringExports.Join(sizes, (uint)inputs.lengths.Length, characters, (uint)inputs.text.Length,
                    separator, (uint)(inputs.delimiter?.Length ?? 0), destination, (uint)output.Length, &written), "string joining");
            if (written != output.Length) throw new RustBackendException("Invalid joined string length.");
        });
    }

    internal static string StringLiteral(string text, string quote)
    {
        // Preserve ToLiteral's null-input failure before resolving the native backend.
        var count = text.Length;
        uint length = 0;
        fixed (char* source = text)
        fixed (char* delimiter = quote)
        {
            var quoteCount = (uint)(quote?.Length ?? 0);
            Check(StringExports.Literal(source, (uint)count, delimiter, quoteCount,
                null, 0, &length), "string literal length");
        }
        return string.Create(checked((int)length), (text, quote), static (output, inputs) =>
        {
            uint written = 0;
            fixed (char* source = inputs.text)
            fixed (char* delimiter = inputs.quote)
            fixed (char* destination = output)
                Check(StringExports.Literal(source, (uint)inputs.text.Length, delimiter,
                    (uint)(inputs.quote?.Length ?? 0), destination, (uint)output.Length, &written), "string literal escaping");
            if (written != output.Length) throw new RustBackendException("Invalid string literal length.");
        });
    }

    private static class StringExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<uint*, uint, char*, uint, char*, uint, char*, uint, uint*, uint> Join =
            (delegate* unmanaged[Cdecl]<uint*, uint, char*, uint, char*, uint, char*, uint, uint*, uint>)Export("mh_string_join_v1");
        internal static readonly delegate* unmanaged[Cdecl]<char*, uint, char*, uint, char*, uint, uint*, uint> Literal =
            (delegate* unmanaged[Cdecl]<char*, uint, char*, uint, char*, uint, uint*, uint>)Export("mh_string_literal_v1");
    }
}
