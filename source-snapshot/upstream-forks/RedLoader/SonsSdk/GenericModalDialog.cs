using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppInterop.Runtime.Runtime;
using Il2CppSystem;
using RedLoader;
using RedLoader.Utils;
using Sons.Gui;
using Sons.Input;
using SUI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Action = System.Action;
using Object = UnityEngine.Object;

namespace SonsSdk;

internal interface ISdkModalDialogDriver
{
    void Setup();
    GenericModalDialog Show(string title, string message);
    void Close();
    void Open(GenericModalDialog dialog, string title, string message);
    void Close(GenericModalDialog dialog);
    void Complete(GenericModalDialog dialog, Action callback);
    void SetOption1(GenericModalDialog dialog, string text, Action action);
    void SetOption2(GenericModalDialog dialog, string text, Action action);
    void SetOption3(GenericModalDialog dialog, string text, Action action);
    void SetCancel(GenericModalDialog dialog, Action action);
}

public class GenericModalDialog : MonoBehaviour
{
    static GenericModalDialog()
    {
        ClassInjector.RegisterTypeInIl2Cpp<GenericModalDialog>();
    }

    internal static GenericModalDialog Instance;
    
    internal LinkTextGui _titleTextObject;
    internal TextMeshProUGUI _contentTextObject;
    
    internal LinkTextGui _cancelTextObject;
    internal LinkTextGui _option1TextObject;
    internal LinkTextGui _option2TextObject;
    internal LinkTextGui _option3TextObject;

    internal Button _option1Button;
    internal Button _option2Button;
    internal Button _option3Button;
    internal Button _cancelButton;

    internal Image _background;

    internal TMP_InputField _inputField;
    
    internal InputGamepadMenuHelper _gamepadMenuHelper;
    internal Canvas _canvas;

    [HideFromIl2Cpp]
    internal event Action OnCancel;
    [HideFromIl2Cpp]
    internal event Action OnOption1;
    [HideFromIl2Cpp]
    internal event Action OnOption2;
    [HideFromIl2Cpp]
    internal event Action OnOption3;
    
    internal void Option1Clicked()
    {
        Complete(OnOption1);
    }
    
    internal void Option2Clicked()
    {
        Complete(OnOption2);
    }
    
    internal void Option3Clicked()
    {
        Complete(OnOption3);
    }
    
    internal void CancelClicked()
    {
        Complete(OnCancel);
    }

    [HideFromIl2Cpp]
    private void Complete(Action callback)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Complete(this, callback);
            return;
        }

        CloseInternal();
        callback?.Invoke();
    }

    internal void SetData(string title, string message)
    {
        _titleTextObject.SetText(title);
        _contentTextObject.text = message;
    }
    
    [HideFromIl2Cpp]
    public void SetOption1(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption1(this, text, action);
            return;
        }

        _option1TextObject.SetText(text);
        _option1Button.gameObject.SetActive(true);
        OnOption1 += action;
    }
    
    [HideFromIl2Cpp]
    public void SetOption2(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption2(this, text, action);
            return;
        }

        _option2TextObject.SetText(text);
        _option2Button.gameObject.SetActive(true);
        OnOption2 += action;
    }
    
    [HideFromIl2Cpp]
    public void SetOption3(string text, Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetOption3(this, text, action);
            return;
        }

        _option3TextObject.SetText(text);
        _option3Button.gameObject.SetActive(true);
        OnOption3 += action;
    }
    
    [HideFromIl2Cpp]
    public void SetCancel(Action action)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.SetCancel(this, action);
            return;
        }

        OnCancel += action;
    }

    private void CloseInternal()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Close(this);
            return;
        }

        ClearCallbacks();
        gameObject.SetActive(false);
    }

    [HideFromIl2Cpp]
    internal void ClearCallbacks()
    {
        OnCancel = null;
        OnOption1 = null;
        OnOption2 = null;
        OnOption3 = null;
    }
    
    private void OpenInternal(string title, string message)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Open(this, title, message);
            return;
        }

        ClearCallbacks();
        ResetGui();
        SetData(title, message);
        gameObject.SetActive(true);
        _canvas.overrideSorting = true;
    }

    internal void ResetGui()
    {
        _option1Button.gameObject.SetActive(false);
        _option2Button.gameObject.SetActive(false);
        _option3Button.gameObject.SetActive(false);
        _inputField.gameObject.SetActive(false);
        
        _cancelTextObject.SetText("Cancel");
    }

    public static GenericModalDialog ShowDialog(string title, string message)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Dialogs.Show(title, message);

        Setup();
        Instance.OpenInternal(title, message);
        RLog.Debug("Shown dialog");
        return Instance;
    }

    public static void Close()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Close();
            return;
        }

        if(!Instance)
            return;
        
        Instance.CloseInternal();
    }

    internal static void Setup()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Dialogs.Setup();
            return;
        }

        if (Instance)
            return;

        var prefab = ModalDialogManager._instance.GameStartingDialog;
        var gui = prefab._gui;

        var newGui = gui.gameObject.Instantiate();
        try
        {
            newGui.SetParent(SUI.SUI.SUIViewport.transform);
            newGui.SetActive(false);

            gui = newGui.GetComponent<DynamicModalDialogGui>();

            var dialog = newGui.AddComponent<GenericModalDialog>();

            dialog._titleTextObject = gui.TitleTextString.GetComponent<LinkTextGui>();
            Destroy(gui.TitleTextString);

            dialog._contentTextObject = gui.ContentTextString.GetComponent<TextMeshProUGUI>();
            Destroy(gui.ContentTextString);

            dialog._cancelTextObject = gui.BackTextString.GetComponent<LinkTextGui>();
            Destroy(gui.BackTextString);

            dialog._option1TextObject = gui.Option1TextString.GetComponent<LinkTextGui>();
            Destroy(gui.Option1TextString);

            dialog._option2TextObject = gui.Option2TextString.GetComponent<LinkTextGui>();
            Destroy(gui.Option2TextString);

            dialog._option3TextObject = gui.Option3TextString.GetComponent<LinkTextGui>();
            Destroy(gui.Option3TextString);

            dialog._option1Button = gui.Option1Button;
            dialog._option1Button.onClick.AddListener((UnityAction)dialog.Option1Clicked);

            dialog._option2Button = gui.Option2Button;
            dialog._option2Button.onClick.AddListener((UnityAction)dialog.Option2Clicked);

            dialog._option3Button = gui.Option3Button;
            dialog._option3Button.onClick.AddListener((UnityAction)dialog.Option3Clicked);

            dialog._cancelButton = gui.BackButton;
            dialog._cancelButton.gameObject.SetActive(true);
            dialog._cancelButton.onClick = new Button.ButtonClickedEvent();
            dialog._cancelButton.onClick.AddListener((UnityAction)dialog.CancelClicked);

            dialog._background = gui.Background;

            dialog._inputField = gui.InputField;

            Destroy(gui.EventSystem.gameObject);
            dialog._gamepadMenuHelper = gui.GamepadMenuHelper;

            dialog._canvas = newGui.GetComponent<Canvas>();

            newGui.transform.localScale = Vector3.one;

            var rect = newGui.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;

            Destroy(gui);

            // Do not expose a dialog whose native setup has not finished.
            Instance = dialog;
        }
        catch
        {
            DiscardFailedSetup(newGui);
            throw;
        }
    }

    private static void DiscardFailedSetup(GameObject gameObject)
    {
        try { gameObject.SetActive(false); }
        catch (System.Exception exception) { ReportCleanupFailure(exception); }

        try { Object.Destroy(gameObject); }
        catch (System.Exception exception) { ReportCleanupFailure(exception); }
    }

    private static void ReportCleanupFailure(System.Exception exception)
    {
        try { RLog.Warning("Failed to clean up an incomplete modal dialog: " + exception); }
        catch { /* Preserve the original setup failure. */ }
    }
}
