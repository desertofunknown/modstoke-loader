// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    internal enum TypeNameKind : uint
    {
        Text, Alias, GenericName, Namespace, Nullable, ArrayRank, Nested, Separator, GenericEnd, GenericStart
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TypeNamePart
    {
        internal TypeNameKind Kind;
        internal uint Start, Length, Value;
    }

    // A single batch keeps reflection and its ordering on the CLR side; no native callbacks.
    internal sealed class TypeNameDescription
    {
        private readonly List<TypeNamePart> parts = new();
        private readonly List<char> text = new();

        internal void Add(TypeNameKind kind, string value = null, uint argument = 0)
        {
            // Match StringBuilder's range failure before visiting subsequent CLR metadata.
            if (kind == TypeNameKind.GenericName)
            {
                if (value == null && argument != 0) throw new ArgumentNullException(nameof(value));
                if (argument > (uint)(value?.Length ?? 0)) throw new ArgumentOutOfRangeException("count");
            }
            parts.Add(new TypeNamePart { Kind = kind, Start = (uint)text.Count,
                Length = (uint)(value?.Length ?? 0), Value = argument });
            if (value != null) text.AddRange(value);
        }

        internal string Render()
        {
            // Reflection has finished and neither list changes during rendering. Pin the
            // existing storage only for each call; native code never retains these spans.
            var descriptors = CollectionsMarshal.AsSpan(parts);
            var characters = CollectionsMarshal.AsSpan(text);
            uint length = 0;
            fixed (TypeNamePart* input = descriptors)
            fixed (char* source = characters)
                Check(TypeNameExports.Render(input, (uint)descriptors.Length, source,
                    (uint)characters.Length, null, 0, &length), "type name length");

            return string.Create(checked((int)length), this, static (output, description) =>
            {
                var sourceParts = CollectionsMarshal.AsSpan(description.parts);
                var sourceText = CollectionsMarshal.AsSpan(description.text);
                uint written = 0;
                fixed (TypeNamePart* input = sourceParts)
                fixed (char* source = sourceText)
                fixed (char* destination = output)
                    Check(TypeNameExports.Render(input, (uint)sourceParts.Length, source,
                        (uint)sourceText.Length, destination, (uint)output.Length, &written), "type name rendering");
                if (written != output.Length) throw new RustBackendException("Invalid rendered type name length.");
            });
        }
    }

    private static class TypeNameExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<TypeNamePart*, uint, char*, uint, char*, uint, uint*, uint> Render =
            (delegate* unmanaged[Cdecl]<TypeNamePart*, uint, char*, uint, char*, uint, uint*, uint>)Export("mh_typename_render_v1");
    }
}
