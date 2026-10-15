using System;
using System.Collections;
using Il2CppObject = Il2CppSystem.Object;

namespace BepInEx.Unity.IL2CPP.Utils.Collections;

internal interface IManagedEnumeratorDriver
{
    Il2CppObject Current { get; }
    bool MoveNext();
    void Reset();
}

// Driver selection must not trigger the native wrapper's type registration.
internal static class ManagedEnumeratorDriverFactory
{
    private static Func<IEnumerator, IManagedEnumeratorDriver> _factory;

    internal static void SetFactory(Func<IEnumerator, IManagedEnumeratorDriver> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (ReferenceEquals(_factory, factory))
            return;
        if (_factory != null)
            throw new InvalidOperationException("Coroutine adapter ownership has already been selected.");

        _factory = factory;
    }

    internal static IManagedEnumeratorDriver Create(IEnumerator routine)
    {
        var factory = _factory;
        if (factory == null)
            return null;

        return factory(routine)
            ?? throw new InvalidOperationException("The coroutine adapter factory returned no driver.");
    }
}
