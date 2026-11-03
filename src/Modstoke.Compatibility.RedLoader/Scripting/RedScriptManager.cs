using System;
using System.Reflection;

namespace RedLoader.Scripting;

internal interface IRedScriptLifecycle
{
    void Init();
    void Update();
}

internal class RedScriptManager
{
    private static readonly Lazy<IRedScriptLifecycle> Lifecycle = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.RedScriptLifecycle", throwOnError: true);
        var method = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
            binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return method.CreateDelegate<Func<IRedScriptLifecycle>>()();
    });

    public static void Init() => Lifecycle.Value.Init();

    public static void Update() => Lifecycle.Value.Update();
}
