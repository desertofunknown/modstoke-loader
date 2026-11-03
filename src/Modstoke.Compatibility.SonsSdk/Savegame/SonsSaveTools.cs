// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System.Reflection;

namespace SonsSdk;

internal interface ISdkSaveDriver
{
    void Register<T>(ICustomSaveable<T> saveable);
    void Unregister<T>(ICustomSaveable<T> saveable);
    void Initialize();
    void RegisterSerializer();
    string Serialize<T>(CustomRegisteredSerializers<T> registration);
    void Deserialize<T>(CustomRegisteredSerializers<T> registration, string json);
    ModSaveData Serialize();
    void Deserialize(ref string data);
}

public static class SonsSaveTools
{
    internal static Dictionary<string, ICustomRegisteredSerializer> CustomSaveables = new();
    private static readonly Lazy<ISdkSaveDriver> SaveDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkSave", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkSaveDriver>)method.CreateDelegate(typeof(Func<ISdkSaveDriver>)))();
    });
    internal static ISdkSaveDriver Driver => SaveDriver.Value;

    public static void Register<T>(ICustomSaveable<T> saveable)
    {
        Driver.Register(saveable);
    }

    public static void Unregister<T>(ICustomSaveable<T> saveable)
    {
        Driver.Unregister(saveable);
    }

    public static void Init()
    {
        RedloaderModSerializer.Init();
    }
}
