using System.Collections;
using Pathfinding.Util;
using RedLoader;
using Sons.Ai.Vail;
using Sons.Gui.Input;
using SonsSdk;
using TheForest.Utils;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Color = System.Drawing.Color;
using InputSystem = Sons.Input.InputSystem;
using Object = UnityEngine.Object;

namespace SUI;

public class SKeybindOptions : SUiElement<SKeybindOptions>
{
    public RebindingInputOptionGui RebindingInputOptionGui;
    public DynamicInputIcon DynamicInputIcon;
    public Button RebindButton;
    
    private static RebindSession _activeRebind;
    private static long _rebindGeneration;
    private RebindSession _rebindSession;
    private KeybindConfigEntry _keybindConfig;
    private GameObject _instance;

    public SKeybindOptions(GameObject root) : base(root)
    {
        RebindingInputOptionGui = root.GetComponent<RebindingInputOptionGui>();
        DynamicInputIcon = root.GetComponent<DynamicInputIcon>();
        RebindButton = root.FindGet<Button>("InputSelectionPanel/Panel/InputButton01");
        TextObject = root.FindGet<TextMeshProUGUI>("LabelPanel/Label");
        
        RebindButton.onClick.AddListener((UnityAction)StartRebindOperation);
        
        root.SetActive(true);
    }
    
    private void StartRebindOperation()
    {
        if (_keybindConfig == null || !Root || !Root.activeInHierarchy ||
            _activeRebind != null || InputSystem.GetState(InputState.InputBinding))
            return;

        var session = new RebindSession(_keybindConfig.GetKeybind(), EventSystem.current, ++_rebindGeneration);
        _rebindSession = session;
        _activeRebind = session;
        try
        {
            session.BindingModeEntered = true;
            RebindingInputOptionGui.SetBindingMode(true);
            if (session.EventSystem && session.EventSystemWasActive)
            {
                session.EventSystemDisabled = true;
                session.EventSystem.gameObject.SetActive(false);
            }

            session.InputBindingEntered = true;
            InputSystem.SetState(InputState.InputBinding, true);
            session.ActionDisabled = true;
            session.Keybind.Action.Disable();

            session.Operation.WithAction(session.Keybind.Action).WithTimeout(5f).WithMatchingEventsBeingSuppressed()
                .WithExpectedControlType("Button")
                .OnApplyBinding((Il2CppSystem.Action<InputActionRebindingExtensions.RebindingOperation, string>)
                    ((_, path) => ApplyBinding(session, path)))
                .OnCancel((Il2CppSystem.Action<InputActionRebindingExtensions.RebindingOperation>)
                    (_ => FinishRebind(session, false)))
                .OnComplete((Il2CppSystem.Action<InputActionRebindingExtensions.RebindingOperation>)
                    (_ => FinishRebind(session, true)))
                .Start();
        }
        catch
        {
            FinishRebind(session, false, cancelOperation: true);
            throw;
        }
    }

    private void ApplyBinding(RebindSession session, string path)
    {
        if (!ReferenceEquals(_rebindSession, session))
            return;

        try
        {
            session.Keybind.Action.ApplyBindingOverride(path);
            session.AppliedPath = path;
            RLog.Msg(Color.Orange, $"Applied binding override to {session.Keybind.Action.name} [Path: {path}]");
        }
        catch
        {
            FinishRebind(session, false, cancelOperation: true);
            throw;
        }
    }

    internal void CancelRebind()
    {
        if (_rebindSession != null)
            FinishRebind(_rebindSession, false, cancelOperation: true);
    }

    private void FinishRebind(RebindSession session, bool completed, bool cancelOperation = false)
    {
        if (!ReferenceEquals(_rebindSession, session))
            return;

        // Detach before native cancellation can invoke its callback again.
        _rebindSession = null;
        try
        {
            if (cancelOperation)
                CleanupRebind(session.Operation.Cancel);
            CleanupRebind(session.Operation.Dispose);
            if (session.BindingModeEntered)
                CleanupRebind(() =>
                {
                    if (RebindingInputOptionGui)
                        RebindingInputOptionGui.SetBindingMode(false);
                });

            if (completed)
            {
                session.Keybind.SaveBindingPath(session.AppliedPath);
                if (Root)
                    CreateNewInstance();
            }
        }
        finally
        {
            if (session.ActionDisabled && session.ActionWasEnabled)
                CleanupRebind(session.Keybind.Action.Enable);
            if (session.EventSystemDisabled)
                CleanupRebind(() =>
                {
                    if (session.EventSystem)
                        session.EventSystem.gameObject.SetActive(true);
                });
            if (session.InputBindingEntered)
                CleanupRebind(() => InputSystem.SetState(InputState.InputBinding, false));
            if (ReferenceEquals(_activeRebind, session))
                _activeRebind = null;

            if (session.InputBindingEntered)
                CleanupRebind(() =>
                {
                    if (Root && Root.activeInHierarchy)
                        EnableEventSystemWithDelay(session).RunCoro();
                });
        }
    }

    private IEnumerator EnableEventSystemWithDelay(RebindSession session)
    {
        yield return new WaitForSeconds(0.55f);
        if (session.Generation != _rebindGeneration || _activeRebind != null ||
            !Root || !Root.activeInHierarchy || InputSystem.GetState(InputState.InputBinding))
            yield break;

        if (session.EventSystemDisabled && session.EventSystem)
            session.EventSystem.gameObject.SetActive(true);
        InputSystem.SetState(InputState.InputBinding, false);
        RLog.Msg(Color.Orange, "Event system activated");
    }

    private static void CleanupRebind(Action cleanup)
    {
        try { cleanup(); }
        catch (Exception exception)
        {
            try { RLog.Warning("Failed to restore keybinding input: " + exception); }
            catch { /* Preserve the original operation failure. */ }
        }
    }

    private sealed class RebindSession
    {
        internal readonly ModKeybind Keybind;
        internal readonly EventSystem EventSystem;
        internal readonly bool ActionWasEnabled;
        internal readonly bool EventSystemWasActive;
        internal readonly long Generation;
        internal readonly InputActionRebindingExtensions.RebindingOperation Operation;
        internal string AppliedPath;
        internal bool BindingModeEntered;
        internal bool EventSystemDisabled;
        internal bool InputBindingEntered;
        internal bool ActionDisabled;

        internal RebindSession(ModKeybind keybind, EventSystem eventSystem, long generation)
        {
            Keybind = keybind;
            EventSystem = eventSystem;
            Generation = generation;
            ActionWasEnabled = keybind.Action.enabled;
            EventSystemWasActive = eventSystem && eventSystem.gameObject.activeSelf;
            Operation = new InputActionRebindingExtensions.RebindingOperation();
        }
    }

    private GameObject CreateNewInstance()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            var replacement = driver.InputIcons.CreateKeybindIcon(_keybindConfig.GetAction(), DynamicInputIcon, _instance);
            if (replacement)
                _instance = replacement;
            return replacement;
        }

        var defaultInstance = DynamicInputIcon._defaultInstance;
        var replacementIcon = InputIconManager.Get(_keybindConfig.GetAction(), out _, out _);
        if (!replacementIcon)
            return null;

        GameObject activeIcon;
        try
        {
            if (!defaultInstance)
                throw new InvalidOperationException("The keybind icon template is missing.");

            var sourceRect = defaultInstance.GetComponent<RectTransform>();
            var targetRect = replacementIcon.GetComponent<RectTransform>();
            if (!sourceRect || !targetRect)
                throw new InvalidOperationException("Keybind icons require RectTransform components.");

            replacementIcon.transform.SetParent(defaultInstance.transform.parent, false);
            CopyRectTransform(sourceRect, targetRect);
            replacementIcon.transform.SetSiblingIndex(defaultInstance.transform.GetSiblingIndex());
            activeIcon = DynamicInputIcon._activeInstance;
        }
        catch
        {
            InputIconManager.RetireIcon(replacementIcon);
            throw;
        }

        var previous = _instance;
        _instance = replacementIcon;
        InputIconManager.RetireIcon(activeIcon, defaultInstance);
        if (previous != activeIcon)
            InputIconManager.RetireIcon(previous, defaultInstance);
        return replacementIcon;
    }
    
    private static void CopyRectTransform(RectTransform fromTransform, RectTransform toTransform)
    {
        toTransform.localScale = fromTransform.localScale;
        toTransform.localRotation = fromTransform.localRotation;
        toTransform.localPosition = fromTransform.localPosition;
        toTransform.anchorMin = fromTransform.anchorMin;
        toTransform.anchorMax = fromTransform.anchorMax;
        toTransform.anchoredPosition = fromTransform.anchoredPosition;
        toTransform.sizeDelta = fromTransform.sizeDelta;
        toTransform.pivot = fromTransform.pivot;
        toTransform.offsetMin = fromTransform.offsetMin;
        toTransform.offsetMax = fromTransform.offsetMax;
    }
    
    public void RevertToDefault()
    {
        if (_keybindConfig == null)
        {
            return;
        }
        
        CancelRebind();
        _keybindConfig.GetKeybind().RevertToDefault();
        CreateNewInstance();
    }

    public SKeybindOptions Config(KeybindConfigEntry config)
    {
        ArgumentNullException.ThrowIfNull(config);
        CancelRebind();
        _keybindConfig = config;
        CreateNewInstance();
        return this;
    }

    /// <summary>
    /// Forces a total height for the keybind container
    /// </summary>
    /// <param name="height"></param>
    /// <returns></returns>
    public SKeybindOptions BindingInputHeight(float height)
    {
        var layout = Root.FindGet<LayoutElement>("InputSelectionPanel/Panel/InputButton01/");
        layout.minHeight = -1;
        layout.preferredHeight = -1;
        layout = Root.FindGet<LayoutElement>("InputSelectionPanel");
        layout.minHeight = -1;
        layout.preferredHeight = height;
        var rect = Root.FindGet<RectTransform>("InputSelectionPanel/Panel/InputButton01/InputIcon");
        rect.sizeDelta = new(height-10, height-10);
        return this;
    }
}
