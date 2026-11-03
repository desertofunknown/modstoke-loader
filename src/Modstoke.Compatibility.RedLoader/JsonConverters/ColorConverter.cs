using System;
using Alt.Json;
using UnityEngine;

namespace RedLoader.JsonConverters
{
    internal class ColorConverter : JsonConverter<Color>
    {
        public override void WriteJson(JsonWriter writer, Color value, JsonSerializer serializer)
        {
            serializer.Serialize(writer, new[] { value.r, value.g, value.b, value.a });
        }

        public override Color ReadJson(JsonReader reader, Type objectType, Color existingValue, bool hasExistingValue, JsonSerializer serializer)
        {
            var components = serializer.Deserialize<float[]>(reader);
            return new Color(components[0], components[1], components[2], components[3]);
        }
    }
}
