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
    internal static readonly Dictionary<Color, Material> BlurMaterials = new();
    internal static UIBlurPass CurrentBlurPass;

    public static Material GetForColor(Color color)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Blur.GetForColor(color);

        if (BlurMaterials.TryGetValue(color, out var blurMat))
            return blurMat;

        var newMat = new Material(Shader.Find("Sons/UI/SampleBlurHLSL"));
        newMat.color = color;
        BlurMaterials[color] = newMat;
        return newMat;
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

        if (CurrentBlurPass == null)
            return;

        CurrentBlurPass.radius = radius;
    }
    
    internal static void SetupBlur()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Blur.SetupBlur();
            return;
        }

        if (!LocalPlayer.Inventory)
            return;

        var invBlur = LocalPlayer.Inventory.CameraController.Camera.transform.Find("SlightBlurPass");
        
        if(!invBlur)
        {
            RLog.Error("Failed to find SlightBlurPass!");
            return;
        }

        var go = UnityEngine.Object.Instantiate(invBlur.gameObject, LocalPlayer.MainCamTr, false);

        try
        {
            var volume = go.GetComponent<CustomPassVolume>();
            var blurPass = volume.customPasses._items[0].Cast<UIBlurPass>();
            blurPass.radius = 25;
            CurrentBlurPass = blurPass;
        }
        catch (Exception e)
        {
            DiscardFailedSetup(go);
            RLog.Error($"Failed to initialize UIBlurPass! {e}");
        }
    }

    private static void DiscardFailedSetup(GameObject gameObject)
    {
        try { gameObject.SetActive(false); }
        catch (Exception exception) { ReportCleanupFailure(exception); }

        try { UnityEngine.Object.Destroy(gameObject); }
        catch (Exception exception) { ReportCleanupFailure(exception); }
    }

    private static void ReportCleanupFailure(Exception exception)
    {
        try { RLog.Warning("Failed to clean up an incomplete panel blur: " + exception); }
        catch { /* Preserve the initialization diagnostic. */ }
    }
}
