using Tomlet;
using Tomlet.Models;
using UnityEngine;

namespace RedLoader.Support.Preferences
{
    internal static class UnityMappers
    {
        internal static void RegisterMappers()
        {
            TomletMain.RegisterMapper(WriteColor, ReadColor);
            TomletMain.RegisterMapper(WriteVector2, ReadVector2);
            TomletMain.RegisterMapper(WriteVector3, ReadVector3);
            TomletMain.RegisterMapper(WriteVector4, ReadVector4);
            TomletMain.RegisterMapper(WriteQuaternion, ReadQuaternion);
        }

        private static Color ReadColor(TomlValue value)
        {
            float[] floats = ConfigSystem.Mapper.ReadArray<float>(value);
            if (floats == null || floats.Length != 4)
                return default;
            // Color channels use a 255-based scale in RedLoader configuration.
            return new Color(floats[0] / 255f, floats[1] / 255f, floats[2] / 255f, floats[3] / 255f);
        }

        private static TomlValue WriteColor(Color value)
        {
            float[] floats = new[] { value.r * 255, value.g * 255, value.b * 255, value.a * 255 };
            return ConfigSystem.Mapper.WriteArray(floats);
        }

        private static Vector2 ReadVector2(TomlValue value)
        {
            float[] floats = ConfigSystem.Mapper.ReadArray<float>(value);
            if (floats == null || floats.Length != 2)
                return default;
            return new Vector2(floats[0], floats[1]);
        }

        private static TomlValue WriteVector2(Vector2 value)
        {
            float[] floats = new[] { value.x, value.y };
            return ConfigSystem.Mapper.WriteArray(floats);
        }

        private static Vector3 ReadVector3(TomlValue value)
        {
            float[] floats = ConfigSystem.Mapper.ReadArray<float>(value);
            if (floats == null || floats.Length != 3)
                return default;
            return new Vector3(floats[0], floats[1], floats[2]);
        }

        private static TomlValue WriteVector3(Vector3 value)
        {
            float[] floats = new[] { value.x, value.y, value.z };
            return ConfigSystem.Mapper.WriteArray(floats);
        }

        private static Vector4 ReadVector4(TomlValue value)
        {
            float[] floats = ConfigSystem.Mapper.ReadArray<float>(value);
            if (floats == null || floats.Length != 4)
                return default;
            return new Vector4(floats[0], floats[1], floats[2], floats[3]);
        }

        private static TomlValue WriteVector4(Vector4 value)
        {
            float[] floats = new[] { value.x, value.y, value.z, value.w };
            return ConfigSystem.Mapper.WriteArray(floats);
        }

        private static Quaternion ReadQuaternion(TomlValue value)
        {
            float[] floats = ConfigSystem.Mapper.ReadArray<float>(value);
            if (floats == null || floats.Length != 4)
                return default;
            return new Quaternion(floats[0], floats[1], floats[2], floats[3]);
        }

        private static TomlValue WriteQuaternion(Quaternion value)
        {
            float[] floats = new[] { value.x, value.y, value.z, value.w };
            return ConfigSystem.Mapper.WriteArray(floats);
        }
    }
}
