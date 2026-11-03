// SPDX-License-Identifier: LGPL-2.1-only
// Copyright (C) 2026 Alex Cooper.

namespace SonsSdk;

/// <summary>Supplies a payload codec while retaining the normal mod save registration and carrier.</summary>
/// <remarks>Existing ICustomSaveable implementations keep their RedLoader serializer and converters.</remarks>
public interface ICustomJsonSaveable<T> : ICustomSaveable<T>
{
    /// <summary>Encodes a payload using the same schema accepted by Deserialize.</summary>
    string Serialize(T value);

    /// <summary>Decodes current and previously saved payloads before Load is called.</summary>
    T Deserialize(string json);
}
