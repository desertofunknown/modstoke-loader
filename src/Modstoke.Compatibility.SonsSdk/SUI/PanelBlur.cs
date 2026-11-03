using System.Reflection;
using RedLoader;
using SonsSdk;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace SUI;

internal interface ISdkPanelBlurDriver
{
    Material GetForColor(Color color);
    void SetRadius(float radius);
    void SetupBlur();
}

public static class PanelBlur
{
    private static readonly Lazy<ISdkPanelBlurDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.SdkPanelBlur", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkPanelBlurDriver>)method.CreateDelegate(typeof(Func<ISdkPanelBlurDriver>)))();
    });
    internal static ISdkPanelBlurDriver Fallback => FallbackDriver.Value;

    internal static readonly Dictionary<Color, Material> BlurMaterials = new();
    internal static UIBlurPass CurrentBlurPass;

    public static Material GetForColor(Color color)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blur.GetForColor(color);

        return Fallback.GetForColor(color);
    }

    public static Material GetForShade(float shade)
    {
        return GetForColor(new(shade, shade, shade));
    }

    internal static void SetRadius(float radius)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blur.SetRadius(radius);
            return;
        }

        Fallback.SetRadius(radius);
    }
    
    internal static void SetupBlur()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blur.SetupBlur();
            return;
        }

        Fallback.SetupBlur();
    }

}
