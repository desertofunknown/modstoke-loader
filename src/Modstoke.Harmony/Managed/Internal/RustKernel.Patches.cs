// Copyright (c) 2026 Alex Cooper. MIT license.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PatchOwnerText
    {
        internal uint Start, Length, IsNull;
    }

    private static class PatchesNative
    {
        internal static readonly delegate* unmanaged[Cdecl]<PatchOwnerText*, uint, char*, uint, uint*, uint, uint*, uint> Owners =
            (delegate* unmanaged[Cdecl]<PatchOwnerText*, uint, char*, uint, uint*, uint, uint*, uint>)Export("mh_patches_owners_v1");
    }

    internal static ReadOnlyCollection<string> CollectPatchOwners(Patches patches)
    {
        // Read the public Patch instances on every query, in the original group order.
        // Keep strings rooted and preserve the first string instance for each owner.
        var owners = new List<string>();
        foreach (var patch in patches.Prefixes) owners.Add(patch.owner);
        foreach (var patch in patches.Postfixes) owners.Add(patch.owner);
        foreach (var patch in patches.Transpilers) owners.Add(patch.owner);
        foreach (var patch in patches.Finalizers) owners.Add(patch.owner);
        foreach (var patch in patches.ILManipulators) owners.Add(patch.owner);

        var descriptors = new PatchOwnerText[owners.Count];
        var characterCount = 0;
        for (var i = 0; i < owners.Count; i++)
        {
            var owner = owners[i];
            descriptors[i] = new PatchOwnerText
            {
                Start = (uint)characterCount,
                Length = (uint)(owner?.Length ?? 0),
                IsNull = owner is null ? 1u : 0u,
            };
            characterCount = checked(characterCount + (owner?.Length ?? 0));
        }
        var characters = new char[characterCount];
        for (var i = 0; i < owners.Count; i++)
            owners[i]?.CopyTo(0, characters, (int)descriptors[i].Start, (int)descriptors[i].Length);

        var ordinals = new uint[owners.Count];
        uint count = 0;
        fixed (PatchOwnerText* nativeOwners = descriptors)
        fixed (char* nativeCharacters = characters)
        fixed (uint* output = ordinals)
            Check(PatchesNative.Owners(nativeOwners, (uint)descriptors.Length, nativeCharacters,
                (uint)characters.Length, output, (uint)ordinals.Length, &count), "patch owners");
        if (count > ordinals.Length)
            throw new RustBackendException("Modstoke Harmony received an invalid patch owner count.");
        var result = new List<string>((int)count);
        for (var i = 0; i < count; i++)
        {
            var ordinal = ordinals[i];
            if (ordinal >= owners.Count || (i != 0 && ordinal <= ordinals[i - 1]))
                throw new RustBackendException("Modstoke Harmony received an invalid patch owner ordinal.");
            result.Add(owners[(int)ordinal]);
        }
        return result.AsReadOnly();
    }
}
