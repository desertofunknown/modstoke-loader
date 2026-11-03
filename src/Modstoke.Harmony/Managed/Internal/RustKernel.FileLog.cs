// Copyright (c) 2026 Alex Cooper. MIT license.
namespace HarmonyLib;

internal static unsafe partial class RustKernel
{
    private static class FileLogExports
    {
        internal static readonly delegate* unmanaged[Cdecl]<byte*, uint, char*, uint, uint*, uint> Row =
            (delegate* unmanaged[Cdecl]<byte*, uint, char*, uint, uint*, uint>)Export("mh_filelog_row_v1");
    }

    internal static string FormatFileLogRow(byte* bytes, int count)
    {
        var output = stackalloc char[28];
        uint written = 0;
        Check(FileLogExports.Row(bytes, (uint)count, output, 28, &written), "file log byte row");
        if (written > 28)
            throw new RustBackendException("Modstoke Harmony received an invalid file log row length.");
        return new string(output, 0, (int)written);
    }
}
