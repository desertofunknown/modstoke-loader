using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using Il2CppSystem;
using ArgumentNullException = System.ArgumentNullException;
using Il2CppIEnumerator = Il2CppSystem.Collections.IEnumerator;
using IntPtr = System.IntPtr;
using Type = System.Type;

namespace RedLoader.Unity.IL2CPP.Utils.Collections;

internal interface IManagedEnumeratorFallback
{
    Object Current(Il2CppManagedEnumerator wrapper);
    bool MoveNext(Il2CppManagedEnumerator wrapper);
    void Reset(Il2CppManagedEnumerator wrapper);
    System.Func<object, Object> GetValueBoxer(Type t);
    Object ManagedToIl2CppObject(object obj);
}

public class Il2CppManagedEnumerator : Object
{
    internal static readonly Dictionary<Type, System.Func<object, Object>> boxers = new();
    private static readonly System.Lazy<IManagedEnumeratorFallback> fallback = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.RedManagedEnumeratorFallback", throwOnError: true);
        var method = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new System.MissingMethodException(type.FullName, "Create");
        return method.CreateDelegate<System.Func<IManagedEnumeratorFallback>>()();
    });

    internal readonly IEnumerator enumerator;
    private readonly IManagedEnumeratorDriver driver;
    internal Object current;
    internal bool hasCurrent;

    static Il2CppManagedEnumerator()
    {
        ClassInjector.RegisterTypeInIl2Cpp<Il2CppManagedEnumerator>(new RegisterTypeOptions
        {
            Interfaces = new[] { typeof(Il2CppIEnumerator) }
        });
    }

    public Il2CppManagedEnumerator(IntPtr ptr) : base(ptr) { }

    public Il2CppManagedEnumerator(IEnumerator enumerator)
        : base(ClassInjector.DerivedConstructorPointer<Il2CppManagedEnumerator>())
    {
        this.enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        driver = ManagedEnumeratorDriverFactory.Create(enumerator);
        ClassInjector.DerivedConstructorBody(this);
    }

    public Object Current
    {
        get
        {
            if (driver != null)
                return driver.Current;

            return fallback.Value.Current(this);
        }
    }

    public bool MoveNext()
    {
        if (driver != null)
            return driver.MoveNext();

        return fallback.Value.MoveNext(this);
    }

    public void Reset()
    {
        if (driver != null)
        {
            driver.Reset();
            return;
        }

        fallback.Value.Reset(this);
    }

    internal static System.Func<object, Object> GetValueBoxer(Type t)
    {
        return fallback.Value.GetValueBoxer(t);
    }

    internal static Object ManagedToIl2CppObject(object obj)
    {
        return fallback.Value.ManagedToIl2CppObject(obj);
    }
}
