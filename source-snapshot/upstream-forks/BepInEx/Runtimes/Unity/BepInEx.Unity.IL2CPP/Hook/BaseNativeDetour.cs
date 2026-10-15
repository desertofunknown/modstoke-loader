using System;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using MonoMod.RuntimeDetour;

namespace BepInEx.Unity.IL2CPP.Hook;

internal abstract class BaseNativeDetour<T> : INativeDetour where T : BaseNativeDetour<T>
{
    protected static readonly ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(typeof(T).Name);

    protected BaseNativeDetour(nint originalMethodPtr, Delegate detourMethod)
    {
        if (originalMethodPtr == 0)
            throw new ArgumentOutOfRangeException(nameof(originalMethodPtr), "Original method pointer cannot be zero.");
        if (detourMethod == null)
            throw new ArgumentNullException(nameof(detourMethod));

        OriginalMethodPtr = originalMethodPtr;
        DetourMethod = detourMethod;
        DetourMethodPtr = Marshal.GetFunctionPointerForDelegate(detourMethod);

        if (DetourMethodPtr == 0)
            throw new InvalidOperationException("Could not get a native function pointer for the detour delegate.");
    }

    public bool IsPrepared { get; protected set; }
    protected MethodBase TrampolineMethod { get; set; }
    protected Delegate DetourMethod { get; set; }

    public nint OriginalMethodPtr { get; }
    public nint DetourMethodPtr { get; }
    public nint TrampolinePtr { get; protected set; }
    public DetourConfig Config { get; }
    public bool IsValid { get; private set; } = true;
    public bool IsApplied { get; private set; }

    public void Dispose()
    {
        if (!IsValid) return;

        try
        {
            Undo();
        }
        finally
        {
            Free();
        }
    }

    public void Apply()
    {
        if (IsApplied) return;
        if (!IsValid) throw new ObjectDisposedException(GetType().Name);

        Prepare();
        try
        {
            ApplyImpl();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to apply native detour from 0x{OriginalMethodPtr:X2} to 0x{DetourMethodPtr:X2}");
            Logger.LogError(ex);

            try
            {
                Free();
            }
            catch (Exception cleanupEx)
            {
                Logger.LogWarning($"Failed to clean up native detour after apply failure: {cleanupEx}");
            }

            throw;
        }

        Logger.Log(LogLevel.Debug,
                   $"Original: {OriginalMethodPtr:X}, Trampoline: {TrampolinePtr:X}, diff: {Math.Abs(OriginalMethodPtr - TrampolinePtr):X}");

        IsApplied = true;
    }

    public void Undo()
    {
        if (!IsValid || !IsApplied || !IsPrepared) return;

        try
        {
            UndoImpl();
        }
        finally
        {
            IsApplied = false;
        }
    }

    public void Free()
    {
        if (!IsValid) return;

        try
        {
            if (IsApplied)
            {
                try
                {
                    Undo();
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"Failed to undo native detour before freeing it: {ex}");
                }
            }

            FreeImpl();
        }
        finally
        {
            IsPrepared = false;
            IsValid = false;
        }
    }

    public MethodBase GenerateTrampoline(MethodBase signature = null)
    {
        if (TrampolineMethod == null)
        {
            Prepare();
            TrampolineMethod = signature;
        }

        return TrampolineMethod;
    }

    public TDelegate GenerateTrampoline<TDelegate>() where TDelegate : Delegate
    {
        if (!typeof(Delegate).IsAssignableFrom(typeof(TDelegate)))
            throw new InvalidOperationException($"Type {typeof(TDelegate)} not a delegate type.");

        Prepare();

        return Marshal.GetDelegateForFunctionPointer<TDelegate>(TrampolinePtr);
    }

    protected abstract void ApplyImpl();

    private void Prepare()
    {
        if (IsPrepared) return;
        if (!IsValid) throw new ObjectDisposedException(GetType().Name);

        Logger.LogDebug($"Preparing detour from 0x{OriginalMethodPtr:X2} to 0x{DetourMethodPtr:X2}");
        PrepareImpl();
        if (TrampolinePtr == 0)
            throw new InvalidOperationException("Native detour provider returned a zero trampoline pointer.");

        Logger.LogDebug($"Prepared detour; Trampoline: 0x{TrampolinePtr:X2}");
        IsPrepared = true;
    }

    protected abstract void PrepareImpl();

    protected abstract void UndoImpl();

    protected abstract void FreeImpl();
}
