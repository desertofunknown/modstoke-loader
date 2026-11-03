// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using Il2CppInterop.Runtime.Injection;
using Object = Il2CppSystem.Object;

namespace SonsSdk;

public class RedloaderModSerializer : Object
{
    public static RedloaderModSerializer Instance;

    private string _lastData;
    
    internal static void Init()
    {
        SonsSaveTools.Driver.Initialize();
    }

    internal static void RegisterSerializer()
    {
        SonsSaveTools.Driver.RegisterSerializer();
    }
    
    public string SerializedName => "Mod";
    public bool UniqueFile => true;
    public bool ShouldSerialize => true;
    public bool IncludeInPlayerSave => true;
    public int ExecutionOrder => 6969;
    Il2CppSystem.Func<string, ModSaveData> DeserializeOverrideAction => (Il2CppSystem.Func<string, ModSaveData>)DeserializeOverride;
    
    ModSaveData OnSerialize()
    {
        return SonsSaveTools.Driver.Serialize();
    }
    
    private ModSaveData DeserializeOverride(string data)
    {
        _lastData = data;
        return null;
    }

    // DeserializeOverride captures the raw JSON and returns null to the native serializer.
    void OnDeserialize(ModSaveData _)
    {
        SonsSaveTools.Driver.Deserialize(ref _lastData);
    }

    public RedloaderModSerializer(IntPtr ptr) : base(ptr)
    { }

    public RedloaderModSerializer() : base(ClassInjector.DerivedConstructorPointer<RedloaderModSerializer>())
    {
        ClassInjector.DerivedConstructorBody(this);
    }
}
