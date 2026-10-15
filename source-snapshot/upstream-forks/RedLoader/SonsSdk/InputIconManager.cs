using Sons.Gui.Input;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SonsSdk;

internal interface ISdkInputIconDriver
{
    IconAssetDatabase IconDatabase { get; }
    GameObject Get(string path, out string displayName, out bool isDefaultPrefab);
    GameObject Add(string path, Transform parent);
    GameObject CreateKeybindIcon(InputAction action, DynamicInputIcon icon, GameObject previous);
}

public class InputIconManager
{
    internal static IconAssetDatabase _iconDatabase;

    public static IconAssetDatabase IconDatabase
    {
        get
        {
            var driver = SdkEvents.BeginLifecycleSetup();
            if (driver != null)
                return driver.InputIcons.IconDatabase;

            if(!_iconDatabase)
                Initialize();
            
            return _iconDatabase;
        }
    }

    private static void Initialize()
    {
        _iconDatabase = Resources.FindObjectsOfTypeAll<IconAssetDatabase>().First();
    }

    public static GameObject Get(InputAction inputAction, out string displayName, out bool isDefaultPrefab)
    {
        if (inputAction.controls.Count == 0)
        {
            displayName = null;
            isDefaultPrefab = false;
            return null;
        }
        
        var path = inputAction.controls[0].path;
        return Get(path, out displayName, out isDefaultPrefab);
    }
    
    public static GameObject Get(string path, out string displayName, out bool isDefaultPrefab)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.InputIcons.Get(path, out displayName, out isDefaultPrefab);

        if (string.IsNullOrEmpty(path))
        {
            displayName = null;
            isDefaultPrefab = false;
            return null;
        }
        
        var icon = IconDatabase.GetIcon(path, out isDefaultPrefab);
        if (icon != null)
        {
            var inputControl = InputSystem.FindControl(path);
            displayName = inputControl != null ? inputControl.displayName : "NA";
            var newIcon = UnityEngine.Object.Instantiate(icon);
            try
            {
                if (isDefaultPrefab)
                {
                    var tmpText = newIcon.GetComponentInChildren<TMP_Text>(true);
                    if (tmpText != null)
                        tmpText.text = displayName;
                }

                return newIcon;
            }
            catch
            {
                RetireIcon(newIcon);
                throw;
            }
        }
        
        displayName = null;
        isDefaultPrefab = false;
        return null;
    }

    public static GameObject AddInputIcon(string path, SUiElement element)
    {
        return AddInputIcon(path, element.Root.transform);
    }
    
    public static GameObject AddInputIcon(string path, Transform transform)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.InputIcons.Add(path, transform);

        var inputIcon = Get(path, out _, out _);
        if (!inputIcon)
        {
            return null;
        }
        
        try
        {
            inputIcon.SetActive(true);
            inputIcon.transform.SetParent(transform, false);
            inputIcon.GetComponent<RectTransform>().anchoredPosition = new(-40, 0);
            return inputIcon;
        }
        catch
        {
            RetireIcon(inputIcon);
            throw;
        }
    }

    internal static void RetireIcon(GameObject icon, GameObject template = null)
    {
        try { if (icon) icon.SetActive(false); }
        catch (Exception exception) { ReportCleanupFailure(exception); }

        try { if (icon && icon != template) UnityEngine.Object.Destroy(icon); }
        catch (Exception exception) { ReportCleanupFailure(exception); }
    }

    private static void ReportCleanupFailure(Exception exception)
    {
        try { RedLoader.RLog.Warning("Failed to clean up an input icon: " + exception); }
        catch { /* Preserve the original operation failure. */ }
    }
}
