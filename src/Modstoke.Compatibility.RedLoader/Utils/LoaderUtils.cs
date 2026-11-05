using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Alt.Json;
using MonoMod.Cil;
using MonoMod.Utils;
using HarmonyLib;
using RedLoader.JsonConverters;
using RedLoader.Utils;
using ColorConverter = RedLoader.JsonConverters.ColorConverter;

#pragma warning disable 0618

namespace RedLoader
{
    public static class LoaderUtils
    {
        private static readonly Random RandomNumGen = new();
        private static readonly MethodInfo StackFrameGetMethod = typeof(StackFrame).GetMethod("GetMethod", BindingFlags.Instance | BindingFlags.Public);
        
        public static T Clamp<T>(T value, T min, T max) where T : IComparable<T>
        {
            if (value.CompareTo(min) < 0)
                return min;
            if (value.CompareTo(max) > 0)
                return max;
            return value;
        }

        public static string HashCode { get; private set; }
        
        private static JsonSerializerSettings _jsonSerializerSettings;
        public static JsonSerializerSettings JsonSerializerSettings
        {
            get
            {
                if (_jsonSerializerSettings == null)
                {
                    _jsonSerializerSettings = new JsonSerializerSettings();
                    _jsonSerializerSettings.Converters.Add(new Vec2Converter());
                    _jsonSerializerSettings.Converters.Add(new Vec3Converter());
                    _jsonSerializerSettings.Converters.Add(new Vec4Converter());
                    _jsonSerializerSettings.Converters.Add(new ColorConverter());
                    _jsonSerializerSettings.Converters.Add(new QuaternionConverter());
                }

                return _jsonSerializerSettings;
            }
        }

        public static int RandomInt()
        {
            lock (RandomNumGen)
                return RandomNumGen.Next();
        }

        public static int RandomInt(int max)
        {
            lock (RandomNumGen)
                return RandomNumGen.Next(max);
        }

        public static int RandomInt(int min, int max)
        {
            lock (RandomNumGen)
                return RandomNumGen.Next(min, max);
        }

        public static double RandomDouble()
        {
            lock (RandomNumGen)
                return RandomNumGen.NextDouble();
        }

        public static string RandomString(int length)
        {
            StringBuilder builder = new();
            for (int i = 0; i < length; i++)
                builder.Append(Convert.ToChar(Convert.ToInt32(Math.Floor(25 * RandomDouble())) + 65));
            return builder.ToString();
        }

        public static bool IsCompatible(SemanticVersioning.Version version)
        {
            var range = new SemanticVersioning.Range($"<={LoaderEnvironment.RedloaderVersion}");
            return range.IsSatisfied(version);
        }
        
        public static bool IsCompatible(string version)
        {
            return IsCompatible(SemanticVersioning.Version.Parse(version));
        }

        public static PlatformID GetPlatform => Environment.OSVersion.Platform;

        public static bool IsUnix => GetPlatform is PlatformID.Unix;
        public static bool IsWindows => GetPlatform is PlatformID.Win32NT or PlatformID.Win32S or PlatformID.Win32Windows or PlatformID.WinCE;
        public static bool IsMac => GetPlatform is PlatformID.MacOSX;

        public static T PullAttributeFromAssembly<T>(Assembly asm, bool inherit = false) where T : Attribute
        {
            T[] attributetbl = PullAttributesFromAssembly<T>(asm, inherit);
            if ((attributetbl == null) || (attributetbl.Length <= 0))
                return null;
            return attributetbl[0];
        }

        public static T[] PullAttributesFromAssembly<T>(Assembly asm, bool inherit = false) where T : Attribute
        {
            Attribute[] att_tbl = Attribute.GetCustomAttributes(asm, inherit);

            if ((att_tbl == null) || (att_tbl.Length <= 0))
                return null;

            Type requestedType = typeof(T);
            string requestedAssemblyName = requestedType.Assembly.GetName().Name;
            List<T> output = new();
            foreach (Attribute att in att_tbl)
            {
                Type attType = att.GetType();
                string attAssemblyName = attType.Assembly.GetName().Name;

                if ((attType == requestedType)
                    || IsTypeEqualToFullName(attType, requestedType.FullName)
                    || ((attAssemblyName.Equals("RedLoader")
                        || attAssemblyName.Equals("RedLoader.ModHandler"))
                        && (requestedAssemblyName.Equals("RedLoader")
                        || requestedAssemblyName.Equals("RedLoader.ModHandler"))
                        && IsTypeEqualToName(attType, requestedType.Name)))
                    output.Add(att as T);
            }

            return output.ToArray();
        }

        public static bool IsTypeEqualToName(Type type1, string type2)
            => type1.Name == type2 || (type1 != typeof(object) && IsTypeEqualToName(type1.BaseType, type2));

        public static bool IsTypeEqualToFullName(Type type1, string type2)
            => type1.FullName == type2 || (type1 != typeof(object) && IsTypeEqualToFullName(type1.BaseType, type2));

        public static string MakePlural(this string str, int amount)
            => amount == 1 ? str : $"{str}s";

        public static IEnumerable<Type> GetValidTypes(this Assembly asm)
            => GetValidTypes(asm, null);

        public static IEnumerable<Type> GetValidTypes(this Assembly asm, Func<Type, bool> predicate)
        {
            IEnumerable<Type> returnval = Enumerable.Empty<Type>();
            try { returnval = asm.GetTypes().AsEnumerable(); }
            catch (ReflectionTypeLoadException ex) 
            {
                RLog.Error($"Failed to load all types in assembly {asm.FullName} due to: {ex.Message}", ex);
                returnval = ex.Types; 
            }

            return returnval.Where(x => (x != null) && (predicate == null || predicate(x)));
        }
        
        public static Color ColorFromString(string color)
        {
            if (string.IsNullOrEmpty(color))
                return RLog.DefaultMelonColor;
            if (color.StartsWith("#"))
                color = color.Substring(1);
            if (color.Length != 6)
                return RLog.DefaultMelonColor;
            var r = Convert.ToInt32(color.Substring(0, 2), 16);
            var g = Convert.ToInt32(color.Substring(2, 2), 16);
            var b = Convert.ToInt32(color.Substring(4, 2), 16);
            return Color.FromArgb(r, g, b);
        }
        
        public static UnityEngine.Color UnityColorFromString(string color)
        {
            if (string.IsNullOrEmpty(color))
                return UnityEngine.Color.white;

            if (color.StartsWith("#"))
                color = color.Substring(1);

            if (color.Length == 3 || color.Length == 4) 
            {
                var rChar = color[0];
                var gChar = color[1];
                var bChar = color[2];
                var aChar = color.Length == 4 ? color[3] : 'F';
                color = $"{rChar}{rChar}{gChar}{gChar}{bChar}{bChar}{aChar}{aChar}";
            }
            else if (color.Length != 6 && color.Length != 8)
            {
                return UnityEngine.Color.white;
            }

            var r = Convert.ToInt32(color.Substring(0, 2), 16);
            var g = Convert.ToInt32(color.Substring(2, 2), 16);
            var b = Convert.ToInt32(color.Substring(4, 2), 16);
            var a = color.Length == 8 ? Convert.ToInt32(color.Substring(6, 2), 16) : 255;

            return new UnityEngine.Color((float)r/255, (float)g/255, (float)b/255, (float)a/255);
        }
        
        public static string JsonSerialize<T>(T obj)
        {
            return JsonConvert.SerializeObject(obj, JsonSerializerSettings);
        }

        public static T JsonDeserialize<T>(string data)
        {
            return JsonConvert.DeserializeObject<T>(data, JsonSerializerSettings);
        }

        public static string ByteStr(byte[] bytes)
        {
            var str = new StringBuilder();
            foreach (var b in bytes)
            {
                str.Append(b.ToString("X")).Append(' ');
            }

            str.Remove(str.Length - 1, 1);

            return str.ToString();
        }
        
        public static int HashString(string str)
        {
            var hash = 5381;
            foreach (var c in str)
            {
                hash = (hash * 33) ^ c;
            }
            return hash;
        }

        public static bool IsNotImplemented(this MethodBase methodBase)
        {
            if (methodBase == null)
                throw new ArgumentNullException(nameof(methodBase));

            DynamicMethodDefinition method = methodBase.ToNewDynamicMethodDefinition();
            ILContext ilcontext = new(method.Definition);
            ILCursor ilcursor = new(ilcontext);

            bool returnval = (ilcursor.Instrs.Count == 2)
                && (ilcursor.Instrs[1].OpCode.Code == Mono.Cecil.Cil.Code.Throw);

            ilcontext.Dispose();
            method.Dispose();
            return returnval;
        }

        public static HarmonyMethod ToNewHarmonyMethod(this MethodInfo methodInfo)
        {
            if (methodInfo == null)
                throw new ArgumentNullException(nameof(methodInfo));
            return new HarmonyMethod(methodInfo);
        }


        public static DynamicMethodDefinition ToNewDynamicMethodDefinition(this MethodBase methodBase)
        {
            if (methodBase == null)
                throw new ArgumentNullException(nameof(methodBase));
            return new DynamicMethodDefinition(methodBase);
        }

        private static FieldInfo AppDomainSetup_application_base;
        public static void SetApplicationBase(this AppDomainSetup _this, string value)
        {
            if (AppDomainSetup_application_base == null)
                AppDomainSetup_application_base = typeof(AppDomainSetup).GetField("application_base", BindingFlags.NonPublic | BindingFlags.Instance);
            if (AppDomainSetup_application_base != null)
                AppDomainSetup_application_base.SetValue(_this, value);
        }

        private static FieldInfo HashAlgorithm_HashSizeValue;
        public static void SetHashSizeValue(this HashAlgorithm _this, int value)
        {
            if (HashAlgorithm_HashSizeValue == null)
                HashAlgorithm_HashSizeValue = typeof(HashAlgorithm).GetField("HashSizeValue", BindingFlags.Public | BindingFlags.Instance);
            if (HashAlgorithm_HashSizeValue != null)
                HashAlgorithm_HashSizeValue.SetValue(_this, value);
        }

        // Modified Version of System.IO.Path.HasExtension from .NET Framework's mscorlib.dll
        public static bool ContainsExtension(this string path)
        {
            if (path != null)
            {
                path.CheckInvalidPathChars();
                int num = path.Length;
                while (--num >= 0)
                {
                    char c = path[num];
                    if (c == '.')
                        return num != path.Length - 1;
                    if (c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar || c == Path.VolumeSeparatorChar)
                        break;
                }
            }
            return false;
        }

        // Modified Version of System.IO.Path.CheckInvalidPathChars from .NET Framework's mscorlib.dll
        private static void CheckInvalidPathChars(this string path)
        {
            foreach (int num in path)
                if (num == 34 || num == 60 || num == 62 || num == 124 || num < 32)
                    throw new ArgumentException("Argument_InvalidPathChars", nameof(path));
        }

        public static void GetDelegate<T>(this IntPtr ptr, out T output) where T : Delegate
            => output = GetDelegate<T>(ptr);
        public static T GetDelegate<T>(this IntPtr ptr) where T : Delegate
            => GetDelegate(ptr, typeof(T)) as T;
        public static Delegate GetDelegate(this IntPtr ptr, Type type)
        {
            if (ptr == IntPtr.Zero)
                throw new ArgumentNullException(nameof(ptr));
            Delegate del = Marshal.GetDelegateForFunctionPointer(ptr, type);
            if (del == null)
                throw new Exception($"Unable to Get Delegate of Type {type.FullName} for Function Pointer!");
            return del;
        }
        public static IntPtr GetFunctionPointer(this Delegate del)
            => Marshal.GetFunctionPointerForDelegate(del);
        
        public static IntPtr GetNativeLibraryExport(this IntPtr ptr, string name)
            => NativeLibrary.GetExport(ptr, name);

        #if !NET6_0
        [MethodImpl(MethodImplOptions.InternalCall)]
        public extern static bool IsGame32Bit();
#else
        public static bool IsGame32Bit() => !Environment.Is64BitProcess;
#endif


        public static bool IsGameIl2Cpp() => Directory.Exists(LoaderEnvironment.Il2CppDataDirectory);

        public static bool IsOldMono() => File.Exists(LoaderEnvironment.UnityGameDataDirectory + "\\Mono\\mono.dll") || 
                                          File.Exists(LoaderEnvironment.UnityGameDataDirectory + "\\Mono\\libmono.so");

        public static string GetFileProductName(string filepath)
        {
            var fileInfo = FileVersionInfo.GetVersionInfo(filepath);
            if (fileInfo != null)
                return fileInfo.ProductName;
            return null;
        }
        
        public static void ShowMessageBox(string message, string title = "RedLoader", uint type = 0x00000000)
        {
            if (CorePreferences.DisableNotifications.Value)
                return;
            if (LoaderEnvironment.IsDedicatedServer)
            {
                RLog.Warning($"{title}: {message}");
                return;
            }
            
            MessageBox(0, message, title, type);
        }
        
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr MessageBox(int hWnd, String text, String caption, uint type);
    }
}
