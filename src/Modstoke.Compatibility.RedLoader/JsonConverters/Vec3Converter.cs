using System;
using Alt.Json;
using UnityEngine;

namespace RedLoader.JsonConverters
{
    internal class Vec3Converter : JsonConverter<Vector3>
    {
        public override void WriteJson(JsonWriter writer, Vector3 value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new[] { value.x, value.y, value.z });
        }

        public override Vector3 ReadJson(JsonReader reader, Type objectType, Vector3 existingValue, bool hasExistingValue,
            JsonSerializer serializer)
        {
            var components = serializer.Deserialize<float[]>(reader);
            return new Vector3(components[0], components[1], components[2]);
        }
    }
}
