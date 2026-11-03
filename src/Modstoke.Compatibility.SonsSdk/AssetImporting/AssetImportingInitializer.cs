using SonsSdk.AssetImporting.GLTF;
using UnityGLTF;

namespace SonsSdk.AssetImporting;

public class AssetImportingInitializer
{
    private static readonly Lazy<Action> InitializeOwned = new(() =>
    {
        var type = System.Reflection.Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkResources", true);
        var method = type.GetMethod("RegisterAssetImporters", System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "RegisterAssetImporters");
        return (Action)method.CreateDelegate(typeof(Action));
    });

    public static void Init() => InitializeOwned.Value();
}
