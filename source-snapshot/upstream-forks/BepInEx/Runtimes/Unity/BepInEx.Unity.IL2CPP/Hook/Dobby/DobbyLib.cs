using System.Runtime.InteropServices;
using System.Reflection;

namespace BepInEx.Unity.IL2CPP.Hook.Dobby;

internal static unsafe class DobbyLib
{
    public static bool IsAvailable()
    {
        if (!NativeLibrary.TryLoad("dobby", typeof(DobbyLib).Assembly, null, out var handle))
            return false;

        try
        {
            return NativeLibrary.TryGetExport(handle, "DobbyPrepare", out _)
                   && NativeLibrary.TryGetExport(handle, "DobbyCommit", out _)
                   && NativeLibrary.TryGetExport(handle, "DobbyDestroy", out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [DllImport("dobby", EntryPoint = "DobbyHook", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Hook(nint target, nint replacement, nint* originalCall);

    [DllImport("dobby", EntryPoint = "DobbyPrepare", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Prepare(nint target, nint replacement, nint* originalCall);

    [DllImport("dobby", EntryPoint = "DobbyCommit", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Commit(nint target);

    [DllImport("dobby", EntryPoint = "DobbyDestroy", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Destroy(nint target);
}
