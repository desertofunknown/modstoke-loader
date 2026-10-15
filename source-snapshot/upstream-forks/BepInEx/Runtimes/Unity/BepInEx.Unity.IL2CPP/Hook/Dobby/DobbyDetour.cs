using System;
using System.Runtime.CompilerServices;

namespace BepInEx.Unity.IL2CPP.Hook.Dobby;

internal class DobbyDetour : BaseNativeDetour<DobbyDetour>
{
    public DobbyDetour(nint originalMethodPtr, Delegate detourMethod) : base(originalMethodPtr, detourMethod) { }

    protected override void ApplyImpl() => EnsureSuccess(DobbyLib.Commit(OriginalMethodPtr));

    protected override unsafe void PrepareImpl()
    {
        nint trampolinePtr = 0;
        EnsureSuccess(DobbyLib.Prepare(OriginalMethodPtr, DetourMethodPtr, &trampolinePtr));
        TrampolinePtr = trampolinePtr;
    }

    protected override void UndoImpl() => EnsureSuccess(DobbyLib.Destroy(OriginalMethodPtr));

    protected override void FreeImpl() { }

    private static void EnsureSuccess(int result, [CallerArgumentExpression("result")] string methodName = null)
    {
        if (result == 0) return;
        throw new InvalidOperationException($"{methodName} failed with result {result}");
    }
}
