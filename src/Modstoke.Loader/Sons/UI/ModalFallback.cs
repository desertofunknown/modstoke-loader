// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility modal dialog fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using SonsSdk;
using Dialog = SonsSdk.GenericModalDialog;

namespace Modstoke.Loader
{
    internal sealed class ModalFallback : ISdkModalDialogDriver
    {
        private static readonly ModalFallback Driver = new();

        internal static ISdkModalDialogDriver Create() => Driver;

        private ModalFallback() { }

        public Dialog Show(string title, string message) =>
            ModalDialogs.Instance.Show(title, message);

        public void Close() => ModalDialogs.Instance.Close();

        public void Open(Dialog dialog, string title, string message) =>
            ModalDialogs.Instance.Open(dialog, title, message);

        public void Close(Dialog dialog) => ModalDialogs.Instance.Close(dialog);

        public void Complete(Dialog dialog, Action callback) =>
            ModalDialogs.Instance.Complete(dialog, callback);

        public void SetOption1(Dialog dialog, string text, Action action) =>
            ModalDialogs.Instance.SetOption1(dialog, text, action);

        public void SetOption2(Dialog dialog, string text, Action action) =>
            ModalDialogs.Instance.SetOption2(dialog, text, action);

        public void SetOption3(Dialog dialog, string text, Action action) =>
            ModalDialogs.Instance.SetOption3(dialog, text, action);

        public void SetCancel(Dialog dialog, Action action) =>
            ModalDialogs.Instance.SetCancel(dialog, action);

        public void Setup() => ModalDialogs.Instance.Setup();
    }
}
