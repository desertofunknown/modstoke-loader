using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace RedLoader.Unix;

internal static class UnixStreamHelper
{
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
        var libc = OpenLibc();
        dup = LoadExport<dupDelegate>(libc, nameof(dup));
        fdopen = LoadExport<fdopenDelegate>(libc, nameof(fdopen));
        fread = LoadExport<freadDelegate>(libc, nameof(fread));
        fwrite = LoadExport<fwriteDelegate>(libc, nameof(fwrite));
        fclose = LoadExport<fcloseDelegate>(libc, nameof(fclose));
        fflush = LoadExport<fflushDelegate>(libc, nameof(fflush));
        isatty = LoadExport<isattyDelegate>(libc, nameof(isatty));
    }

    public static Stream CreateDuplicateStream(int fileDescriptor)
    {
        var newFd = dup(fileDescriptor);

        return new UnixStream(newFd, FileAccess.Write);
    }

    private static IntPtr OpenLibc()
    {
        foreach (var candidate in new[] { "libc.so.6", "libc", "/usr/lib/libSystem.dylib" })
            if (DynDll.TryOpenLibrary(candidate, out var handle))
                return handle;

        throw new DllNotFoundException("Could not load libc for RedLoader Unix console stream redirection.");
    }

    private static TDelegate LoadExport<TDelegate>(IntPtr libc, string exportName)
        where TDelegate : Delegate
    {
        var pointer = DynDll.GetExport(libc, exportName);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(pointer);
    }
}
