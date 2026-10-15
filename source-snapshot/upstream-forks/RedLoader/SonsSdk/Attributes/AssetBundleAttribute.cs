using System.Reflection;
using RedLoader;
using RedLoader.Utils;
using UnityEngine;
using Color = System.Drawing.Color;

namespace SonsSdk.Attributes;

[AttributeUsage(AttributeTargets.Class)]
public class AssetBundleAttribute : Attribute
{
    public string AssetBundleName { get; }
    
    public ModBase Mod { get; internal set; }

    public AssetBundleAttribute(string assetBundleName)
    {
        AssetBundleName = assetBundleName;
    }

    public bool IsLoaded()
    {
        return (bool)LoadedBundle;
    }

    internal bool LoadBundle(Assembly assembly)
    {
        if (BundleLoader != null)
            return BundleLoader(this, assembly);

        if (IsLoaded())
        {
            return true;
        }

        var absolutePath = LoaderEnvironment.GetModDataPath(assembly) / AssetBundleName;
        if (!absolutePath.FileExists())
        {
            RLog.Error($"Couldn't find asset bundle {absolutePath.Path}");
            return false;
        }

        LoadedBundle = AssetBundle.LoadFromFile(absolutePath.Path);
        if (!LoadedBundle)
        {
            RLog.Error($"Couldn't load asset bundle {absolutePath.Path}");
            return false;
        }

        var mappedAssets = 0;
        foreach (var assetReference in AssetReferences)
        {
            if (assetReference.LoadAsset())
            {
                mappedAssets++;
            }
        }
        
        RLog.Msg(Color.Orange, $"Loaded bundle {AssetBundleName}; mapped {mappedAssets}/{AssetReferences.Count} assets");

        return true;
    }

    internal bool LoadBundle() => LoadBundle(Mod.ModAssembly);

    internal AssetBundle LoadedBundle;

    internal Func<AssetBundleAttribute, Assembly, bool> BundleLoader;

    internal List<AssetReferenceAttribute> AssetReferences = new();
}

[AttributeUsage(AttributeTargets.Property)]
public class AssetReferenceAttribute : Attribute
{
    public string AssetName { get; internal set; }

    internal PropertyInfo Property { get; set; }

    internal AssetBundleAttribute AssetBundle { get; set; }

    public AssetReferenceAttribute(string assetName = null)
    {
        AssetName = assetName;
    }

    internal bool LoadAsset()
    {
        if (!AssetBundle.IsLoaded())
        {
            AssetLoaders.ReportAssetError($"Asset bundle {AssetBundle.AssetBundleName} is not loaded.");
            return false;
        }

        if (string.IsNullOrEmpty(AssetName))
        {
            AssetName = Property.Name;
        }

        return AssetLoaders.TryMapAsset(AssetBundle.LoadedBundle, Property, AssetName, AssetBundle.AssetBundleName);
    }
}

internal static class AssetBundleAttributeLoader
{
    public static List<AssetBundleAttribute> GetAllTypes(ModBase mod)
    {
        var melonAssembly = mod.ModAssembly;

        var assetBundles = new List<AssetBundleAttribute>();

        foreach (var type in melonAssembly.GetTypes())
        {
            var assetBundleAttribute = type.GetCustomAttribute<AssetBundleAttribute>();
            if (assetBundleAttribute == null)
            {
                continue;
            }

            foreach (var prop in type.GetProperties())
            {
                var assetReferenceAttribute = prop.GetCustomAttribute<AssetReferenceAttribute>();
                if (assetReferenceAttribute == null)
                {
                    continue;
                }

                assetReferenceAttribute.AssetBundle = assetBundleAttribute;
                assetReferenceAttribute.Property = prop;

                assetBundleAttribute.AssetReferences.Add(assetReferenceAttribute);
            }

            assetBundles.Add(assetBundleAttribute);
        }

        return assetBundles;
    }
}
