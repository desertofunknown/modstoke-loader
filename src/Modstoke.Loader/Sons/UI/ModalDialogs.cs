// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using RedLoader.Utils;
using global::Sons.Gui;
using SonsSdk;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Dialog = SonsSdk.GenericModalDialog;
using Object = UnityEngine.Object;

namespace Modstoke.Loader
{
    internal sealed class ModalDialogs : ISdkModalDialogDriver
    {
        internal static readonly ModalDialogs Instance = new();

        private ModalDialogs() { }

        public Dialog Show(string title, string message)
        {
            Setup();
            Open(Dialog.Instance, title, message);
            RLog.Debug("Shown dialog");
            return Dialog.Instance;
        }

        public void Close()
        {
            if (Dialog.Instance)
                Close(Dialog.Instance);
        }

        public void Open(Dialog dialog, string title, string message)
        {
            dialog.ClearCallbacks();
            dialog.ResetGui();
            dialog.SetData(title, message);
            dialog.gameObject.SetActive(true);
            dialog._canvas.overrideSorting = true;
        }

        public void Close(Dialog dialog)
        {
            dialog.ClearCallbacks();
            dialog.gameObject.SetActive(false);
        }

        public void Complete(Dialog dialog, Action callback)
        {
            // A callback may open another dialog on this shared instance.
            Close(dialog);
            callback?.Invoke();
        }

        public void SetOption1(Dialog dialog, string text, Action action)
        {
            dialog._option1TextObject.SetText(text);
            dialog._option1Button.gameObject.SetActive(true);
            dialog.OnOption1 += action;
        }

        public void SetOption2(Dialog dialog, string text, Action action)
        {
            dialog._option2TextObject.SetText(text);
            dialog._option2Button.gameObject.SetActive(true);
            dialog.OnOption2 += action;
        }

        public void SetOption3(Dialog dialog, string text, Action action)
        {
            dialog._option3TextObject.SetText(text);
            dialog._option3Button.gameObject.SetActive(true);
            dialog.OnOption3 += action;
        }

        public void SetCancel(Dialog dialog, Action action)
        {
            dialog.OnCancel += action;
        }

        public void Setup()
        {
            if (Dialog.Instance)
                return;

            var manager = ModalDialogManager._instance;
            var prefab = manager != null && manager ? manager.GameStartingDialog : null;
            if (prefab == null || !prefab || prefab._gui == null || SUI.SUI.SUIViewport == null)
                throw new InvalidOperationException("The game's modal dialog or the SDK's viewport is not loaded yet.");
            var gui = prefab._gui;
            GameObject staging = null;
            GameObject newGui = null;
            try
            {
                // Keep copied native callbacks inactive until the SDK owns the clone.
                staging = new GameObject("SUI Modal Staging");
                staging.SetActive(false);
                newGui = Object.Instantiate(gui.gameObject, staging.transform, true);
                newGui.SetActive(false);
                newGui.SetParent(SUI.SUI.SUIViewport.transform);

                gui = newGui.GetComponent<DynamicModalDialogGui>();
                var dialog = newGui.AddComponent<Dialog>();

                dialog._titleTextObject = gui.TitleTextString.GetComponent<LinkTextGui>();
                Object.DestroyImmediate(gui.TitleTextString);

                dialog._contentTextObject = gui.ContentTextString.GetComponent<TextMeshProUGUI>();
                Object.DestroyImmediate(gui.ContentTextString);

                dialog._cancelTextObject = gui.BackTextString.GetComponent<LinkTextGui>();
                Object.DestroyImmediate(gui.BackTextString);

                dialog._option1TextObject = gui.Option1TextString.GetComponent<LinkTextGui>();
                Object.DestroyImmediate(gui.Option1TextString);

                dialog._option2TextObject = gui.Option2TextString.GetComponent<LinkTextGui>();
                Object.DestroyImmediate(gui.Option2TextString);

                dialog._option3TextObject = gui.Option3TextString.GetComponent<LinkTextGui>();
                Object.DestroyImmediate(gui.Option3TextString);

                dialog._option1Button = gui.Option1Button;
                dialog._option1Button.onClick = new Button.ButtonClickedEvent();
                dialog._option1Button.onClick.AddListener((UnityAction)dialog.Option1Clicked);
                dialog._option2Button = gui.Option2Button;
                dialog._option2Button.onClick = new Button.ButtonClickedEvent();
                dialog._option2Button.onClick.AddListener((UnityAction)dialog.Option2Clicked);
                dialog._option3Button = gui.Option3Button;
                dialog._option3Button.onClick = new Button.ButtonClickedEvent();
                dialog._option3Button.onClick.AddListener((UnityAction)dialog.Option3Clicked);

                dialog._cancelButton = gui.BackButton;
                dialog._cancelButton.gameObject.SetActive(true);
                dialog._cancelButton.onClick = new Button.ButtonClickedEvent();
                dialog._cancelButton.onClick.AddListener((UnityAction)dialog.CancelClicked);

                dialog._background = gui.Background;
                dialog._inputField = gui.InputField;
                dialog._gamepadMenuHelper = gui.GamepadMenuHelper;
                var eventSystem = gui.EventSystem;
                if (eventSystem && eventSystem.transform.IsChildOf(newGui.transform))
                {
                    // Its GameObject can also own the modal root or gamepad helper.
                    foreach (var inputModule in eventSystem.GetComponents<BaseInputModule>())
                        Object.DestroyImmediate(inputModule);
                    Object.DestroyImmediate(eventSystem);
                }
                dialog._canvas = newGui.GetComponent<Canvas>();
                newGui.transform.localScale = Vector3.one;

                var rect = newGui.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                Object.DestroyImmediate(gui);

                // Do not expose a dialog whose native setup has not finished.
                Dialog.Instance = dialog;
            }
            catch
            {
                DiscardFailedSetup(newGui);
                throw;
            }
            finally
            {
                DiscardFailedSetup(staging);
            }
        }

        private static void DiscardFailedSetup(GameObject gameObject)
        {
            if (!gameObject)
                return;

            try { gameObject.SetActive(false); }
            catch (Exception exception) { ReportCleanupFailure(exception); }

            try { Object.Destroy(gameObject); }
            catch (Exception exception) { ReportCleanupFailure(exception); }
        }

        private static void ReportCleanupFailure(Exception exception)
        {
            try { RLog.Warning("Failed to clean up an incomplete modal dialog: " + exception); }
            catch { /* Preserve the original setup failure. */ }
        }
    }
}
