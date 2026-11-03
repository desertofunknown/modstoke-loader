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
/// Utilities for loading addressable and embedded assets.
/// </summary>
public static class AssetLoaders
{
    /// <summary>
    /// Synchronously load an asset from addressables.
    /// </summary>
    public static T LoadAsset<T>(string name) where T : Object
    {
        return Addressables.LoadAssetAsync<T>(name).WaitForCompletion();
    }

    /// <summary>
    /// Synchronously load a GameObject from addressables.
    /// </summary>
    public static GameObject LoadPrefab(string name)
    {
        return LoadAsset<GameObject>(name);
    }

    /// <summary>
    /// Synchronously load and instantiate a GameObject from addressables.
    /// </summary>
    public static GameObject InstantiatePrefab(string name)
    {
        return Object.Instantiate(LoadPrefab(name));
    }

    /// <summary>
    /// Load an embedded asset bundle from the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly to load the bundle from</param>
    /// <param name="name">The resource name, without the assembly-name prefix.</param>
    /// <exception cref="Exception">The embedded resource was not found.</exception>
    /// <example>LoadBundleFromAssembly(assembly, "Resources.bundle")</example>
    public static AssetBundle LoadBundleFromAssembly(Assembly assembly, string name)
    {
        return AssetBundle.LoadFromMemory(LoadDataFromAssembly(assembly, name));
    }

    /// <summary>
    /// Load embedded resource data from the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly to get the data from</param>
    /// <param name="name">The resource name, without the assembly-name prefix.</param>
    /// <exception cref="Exception">The embedded resource was not found.</exception>
    /// <exception cref="EndOfStreamException">The stream ended before all resource data was read.</exception>
    /// <example>LoadDataFromAssembly(assembly, "Resources.bundle")</example>
    public static byte[] LoadDataFromAssembly(Assembly assembly, string name)
    {
        var ns = assembly.GetName().Name;
        using var stream = assembly.GetManifestResourceStream(ns + "." + name);
        if(stream == null)
            throw new Exception("Failed to load data from assembly. Stream is null.");
        
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0)
                throw new EndOfStreamException($"Incomplete embedded resource: {ns}.{name}.");
            offset += count;
        }
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
    /// Load a texture from an embedded resource in the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly to load the texture from</param>
    /// <param name="path">The resource name, without the assembly-name prefix.</param>
    /// <example>LoadTextureFromAssembly(assembly, "Resources.MyTexture.png")</example>
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
            var mappedAsset = property.PropertyType == typeof(Object)
                ? asset
                : DynamicInitializerStore.GetInitializer(property.PropertyType)(asset.Pointer);

            property.SetValue(null, mappedAsset);
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
            catch (Exception)
            { }

            writer.Line($"<{str}>");
        }));

        writer.Save();

        RLog.Msg(Color.Chartreuse, "============== FINISHED WRITING ADDRESSABLES ==============");
    }
}
