using System;
using System.Collections;
using Il2CppObject = Il2CppSystem.Object;

namespace RedLoader.Unity.IL2CPP.Utils.Collections;

internal interface IManagedEnumeratorDriver
{
    Il2CppObject Current { get; }
    bool MoveNext();
    void Reset();
}

// Keep driver selection separate from the native wrapper's lazy type registration.
internal static class ManagedEnumeratorDriverFactory
{
    private static Func<IEnumerator, IManagedEnumeratorDriver> _factory;
    private static bool _enumeratorCreated;

    internal static void SetFactory(Func<IEnumerator, IManagedEnumeratorDriver> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (ReferenceEquals(_factory, factory))
            return;
        if (_factory != null || _enumeratorCreated)
            throw new InvalidOperationException("Coroutine adapter ownership must be selected before creating an enumerator.");

        _factory = factory;
    }

    internal static IManagedEnumeratorDriver Create(IEnumerator routine)
    {
        _enumeratorCreated = true;
        var factory = _factory;
        if (factory == null)
            return null;

        return factory(routine)
            ?? throw new InvalidOperationException("The coroutine adapter factory returned no driver.");
    }
}
