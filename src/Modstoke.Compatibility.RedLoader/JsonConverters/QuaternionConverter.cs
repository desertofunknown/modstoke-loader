using System;
using Alt.Json;
using UnityEngine;

namespace RedLoader.JsonConverters
{
    internal class QuaternionConverter : JsonConverter<Quaternion>
    {
        public override void WriteJson(JsonWriter writer, Quaternion value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new[] { value.x, value.y, value.z, value.w });
        }

        public override Quaternion ReadJson(JsonReader reader, Type objectType, Quaternion existingValue, bool hasExistingValue,
                                            JsonSerializer serializer)
        {
            var components = serializer.Deserialize<float[]>(reader);
            return new Quaternion(components[0], components[1], components[2], components[3]);
        }
    }
}
