using System;
using Alt.Json;
using UnityEngine;

namespace RedLoader.JsonConverters
{
    internal class Vec4Converter : JsonConverter<Vector4>
    {
        public override void WriteJson(JsonWriter writer, Vector4 value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new[] { value.x, value.y, value.z, value.w });
        }

        public override Vector4 ReadJson(JsonReader reader, Type objectType, Vector4 existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            var components = serializer.Deserialize<float[]>(reader);
            return new Vector4(components[0], components[1], components[2], components[3]);
        }
    }
}
