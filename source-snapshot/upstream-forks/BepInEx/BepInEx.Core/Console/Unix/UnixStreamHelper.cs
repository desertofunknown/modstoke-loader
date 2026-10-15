using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace BepInEx.Unix;

internal static class UnixStreamHelper
{
    private static readonly string[] LibcCandidates =
    {
        "libc.so.6",
        "libc",
        "/usr/lib/libSystem.dylib"
    };

    private static IntPtr libc;

    public delegate int dupDelegate(int fd);

    public delegate int fcloseDelegate(IntPtr stream);

    public delegate IntPtr fdopenDelegate(int fd, string mode);

    public delegate int fflushDelegate(IntPtr stream);

    public delegate IntPtr freadDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);

    public delegate int fwriteDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);

    public delegate int isattyDelegate(int fd);

    public static dupDelegate dup;

    public static fdopenDelegate fdopen;

    public static freadDelegate fread;

    public static fwriteDelegate fwrite;

    public static fcloseDelegate fclose;

    public static fflushDelegate fflush;

    public static isattyDelegate isatty;

    static UnixStreamHelper()
    {
        libc = OpenLibc();
        dup = LoadFunction<dupDelegate>("dup");
        fdopen = LoadFunction<fdopenDelegate>("fdopen");
        fread = LoadFunction<freadDelegate>("fread");
        fwrite = LoadFunction<fwriteDelegate>("fwrite");
        fclose = LoadFunction<fcloseDelegate>("fclose");
        fflush = LoadFunction<fflushDelegate>("fflush");
        isatty = LoadFunction<isattyDelegate>("isatty");
    }

    public static Stream CreateDuplicateStream(int fileDescriptor)
    {
        var newFd = dup(fileDescriptor);

        return new UnixStream(newFd, FileAccess.Write);
    }

    private static IntPtr OpenLibc()
    {
        foreach (var candidate in LibcCandidates)
        {
            IntPtr handle;
            if (DynDll.TryOpenLibrary(candidate, out handle))
            {
                return handle;
            }
        }

        throw new DllNotFoundException("Could not load libc from any known POSIX library name.");
    }

    private static TDelegate LoadFunction<TDelegate>(string exportName)
        where TDelegate : Delegate
    {
        var pointer = DynDll.GetExport(libc, exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(pointer);
    }
}
