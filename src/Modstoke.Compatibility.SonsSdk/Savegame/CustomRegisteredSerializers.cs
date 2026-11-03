// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

namespace SonsSdk;

internal class CustomRegisteredSerializers<T> : ICustomRegisteredSerializer
{
    public ICustomSaveable<T> Serializer;

    public string Name => Serializer.Name;
    
    public bool IncludeInPlayerSave => Serializer.IncludeInPlayerSave;

    public bool Is(object serializer)
    {
        return Serializer == serializer;
    }

    public string Serialize()
    {
        return SonsSaveTools.Driver.Serialize(this);
    }

    public void Deserialize(string json)
    {
        SonsSaveTools.Driver.Deserialize(this, json);
    }

    public bool IsType(Type type)
    {
        return Serializer.GetType() == type;
    }

    public bool IsSerializedType(Type type)
    {
        return typeof(T) == type;
    }

    public CustomRegisteredSerializers(ICustomSaveable<T> serializer)
    {
        Serializer = serializer;
    }
}
