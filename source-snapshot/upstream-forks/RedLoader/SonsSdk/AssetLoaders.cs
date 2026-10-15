using System.Reflection;
using Endnight.Extensions;
using Il2CppInterop.Runtime;
using Il2CppSystem.Linq;
using RedLoader;
using RedLoader.Utils;
using SonsSdk.Attributes;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using Color = System.Drawing.Color;
using Object = UnityEngine.Object;

namespace SonsSdk;

/// <summary>
/// Utilities for addressables
/// </summary>
public static class AssetLoaders
{
    /// <summary>
    /// Load an asset from addressables
    /// </summary>
    /// <param name="name"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T LoadAsset<T>(string name) where T : Object
    {
        return Addressables.LoadAssetAsync<T>(name).WaitForCompletion();
    }

    /// <summary>
    /// Load a gameobject from addressables
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static GameObject LoadPrefab(string name)
    {
        return Addressables.LoadAssetAsync<GameObject>(name).WaitForCompletion();
    }

    /// <summary>
    /// Load and instantiate a gameobject from addressables
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public static GameObject InstantiatePrefab(string name)
    {
        return Object.Instantiate(Addressables.LoadAssetAsync<GameObject>(name).WaitForCompletion());
    }

    /// <summary>
    /// Load an asset bundle from the calling assembly. The name will automatically be prefixed with the assembly name.
    /// </summary>
    /// <param name="assembly">The assembly to load the bundle from</param>
    /// <param name="name">The path of the resource you wish to load</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <example>LoadFromAssembly("Resources.bundle")</example>
    public static AssetBundle LoadBundleFromAssembly(Assembly assembly, string name)
    {
        return AssetBundle.LoadFromMemory(LoadDataFromAssembly(assembly, name));
    }

    /// <summary>
    /// Load data from the calling assembly. The name will automatically be prefixed with the assembly name.
    /// </summary>
    /// <param name="assembly">The assembly to get the data from</param>
    /// <param name="name">The path of the resource you wish to load</param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    /// <example>LoadFromAssembly("Resources.bundle")</example>
    public static byte[] LoadDataFromAssembly(Assembly assembly, string name)
    {
        var ns = assembly.GetName().Name;
        var stream = assembly.GetManifestResourceStream(ns + "." + name);
        if(stream == null)
            throw new Exception("Failed to load data from assembly. Stream is null.");
        
        var bytes = new byte[stream.Length];
        _ = stream.Read(bytes, 0, bytes.Length);
        return bytes;
    }
    
    /// <summary>
    /// Load a texture from a byte buffer
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    public static Texture2D LoadTexture(byte[] data)
    {
        var tex = new Texture2D(2, 2);
        tex.LoadImage(data);
        return tex;
    }
    
    /// <summary>
    /// Load a texture from a file
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static Texture2D LoadTexture(string path)
    {
        return LoadTexture(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Load a texture from a file in the calling assembly. The name will automatically be prefixed with the assembly name.
    /// </summary>
    /// <param name="assembly">The assembly to load the texture from</param>
    /// <param name="path"></param>
    /// <example>LoadTextureFromAssembly("Resources.MyTexture.png")</example>
    /// <returns></returns>
    public static Texture2D LoadTextureFromAssembly(Assembly assembly, string path)
    {
        return LoadTexture(AssetLoaders.LoadDataFromAssembly(assembly, path));
    }

    /// <summary>
    /// Maps the contents of an asset bundle to a static class. The name of the property must match the name of the asset in the bundle.
    /// Individual mapping failures are logged; remaining properties are still processed.
    /// </summary>
    /// <param name="bundleData">The loaded bundle</param>
    /// <param name="mapFileType">The class to map the asset bundle contents to</param>
    /// <exception cref="InvalidOperationException">The asset bundle could not be loaded.</exception>
    public static void MapBundleToFile(byte[] bundleData, Type mapFileType)
    {
        var bundle = AssetBundle.LoadFromMemory(bundleData);
        if (!bundle)
        {
            throw new InvalidOperationException($"Couldn't load the asset bundle for {mapFileType.FullName}.");
        }

        foreach (var prop in mapFileType.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
        {
            TryMapAsset(bundle, prop, prop.Name, mapFileType.FullName);
        }
    }

    internal static bool TryMapAsset(AssetBundle bundle, PropertyInfo property, string assetName, string bundleName)
    {
        var context = $"asset {assetName} from {bundleName} into {property.DeclaringType?.FullName}.{property.Name}";
        try
        {
            if (property.SetMethod == null || !property.SetMethod.IsStatic || property.GetIndexParameters().Length != 0)
            {
                ReportAssetError($"Couldn't map {context}: the property must have a static setter and no index parameters.");
                return false;
            }

            if (!typeof(Object).IsAssignableFrom(property.PropertyType))
            {
                ReportAssetError($"Couldn't map {context}: the property type must derive from UnityEngine.Object.");
                return false;
            }

            var asset = bundle.LoadAsset(assetName, Il2CppType.From(property.PropertyType));
            if (!asset)
            {
                ReportAssetError($"Couldn't map {context}: the bundle did not return a usable asset.");
                return false;
            }

            asset.hideFlags = HideFlags.HideAndDontSave;
            var actualType = property.PropertyType == typeof(Object)
                ? asset
                : DynamicInitializerStore.GetInitializer(property.PropertyType)(asset.Pointer);

            property.SetValue(null, actualType);
            return true;
        }
        catch (Exception exception)
        {
            ReportAssetError($"Couldn't map {context}.", exception);
            return false;
        }
    }

    internal static void ReportAssetError(string message, Exception exception = null)
    {
        try
        {
            RLog.Error(exception == null ? message : $"{message}\n{exception}");
        }
        catch
        {
            // A mod's logging callback must not stop other mods from loading their assets.
        }
    }
    
    /// <inheritdoc cref="MapBundleToFile"/>
    public static void MapBundleToFile<T>(byte[] bundleData)
    {
        MapBundleToFile(bundleData, typeof(T));
    }

    public static void PrintAllAddressables()
    {
        var writer = new FileWriter("Addressables");

        var map = Addressables.ResourceLocators.First().Cast<ResourceLocationMap>();
        map.Keys.ForEach(new Action<Il2CppSystem.Object>(x =>
        {
            var str = x.Cast<Il2CppSystem.String>();

            try
            {
                var obj = Addressables.LoadAsset<Object>(str).WaitForCompletion();
                var objType = obj.GetIl2CppType().Name;
                writer.Add($"[{objType}] {obj.name} ");
            }
            catch (Exception e)
            { }

            //Extensions.Log(str);
            writer.Line($"<{str}>");
        }));

        writer.Save();

        RLog.Msg(Color.Chartreuse, "============== FINISHED WRITING ADDRESSABLES ==============");
    }
}
