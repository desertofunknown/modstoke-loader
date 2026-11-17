// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Sons.Gui;
using SonsSdk;
using TMPro;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    /// <summary>What a dialog's status mark says: nothing, a step that can't be undone, a failure, or a success.</summary>
    internal enum DialogTone { Plain, Caution, Problem, Done }

    /// <summary>One answer a system dialog offers.</summary>
    internal sealed class DialogChoice
    {
        internal DialogChoice(Action run, bool back, Func<bool> enabled = null)
        {
            Run = run;
            Back = back;
            Enabled = enabled;
        }

        internal string Label { get; set; } = "";
        internal Action Run { get; }
        /// <summary>The answer Escape and the controller's Back give.</summary>
        internal bool Back { get; }
        internal Func<bool> Enabled { get; }
    }

    /// <summary>
    /// A dialog the front end draws in its own design for someone else: the game, or a mod through the SonsSdk shim.
    /// Its text and answers are read from that owner every frame, as the game's localization can arrive late.
    /// </summary>
    internal abstract class SystemDialog
    {
        protected readonly List<DialogChoice> Answers = new();

        internal string Title { get; private protected set; } = "";
        internal string Body { get; private protected set; } = "";
        /// <summary>The label over the dialog's text field, or null when it has none.</summary>
        internal string FieldLabel { get; private protected set; }
        internal IReadOnlyList<DialogChoice> Choices => Answers;

        /// <summary>Which parts the dialog has; when they change, its screen is built again rather than retexted.</summary>
        internal int Shape { get; private protected set; } = -1;

        internal DialogTone Tone { get; private protected set; }
        /// <summary>Focus starts on the way out, for a question whose other answer can't be undone.</summary>
        internal bool Cautious { get; private protected set; }
        /// <summary>The dialog stands for work going on, such as a connection being made.</summary>
        internal bool Busy { get; private protected set; }
        /// <summary>How dark the screen behind the dialog goes.</summary>
        internal float Dim { get; private protected set; } = 0.45f;

        /// <summary>Reads the owner's current text and answers; true when anything shown changed.</summary>
        internal abstract bool Refresh();

        /// <summary>What has been typed into the field, or null when the dialog has none.</summary>
        internal virtual string Typed => null;

        internal virtual bool Secret => false;

        /// <summary>The most characters the field takes, or 0 for no limit.</summary>
        internal virtual int MaxLength => 0;

        internal virtual void Type(string text) { }
    }

    /// <summary>
    /// One of the game's own modal dialogs. It stays open, out of sight, so the game keeps its input state, result
    /// and callbacks; the text is read off its interface after the game has localized it, and an answer presses the
    /// game's own button.
    /// </summary>
    internal sealed class GameDialog : SystemDialog
    {
        private const int FieldShown = 1 << 4, RejoinShown = 1 << 5;
        private readonly DynamicModalDialogController _controller;
        private readonly DynamicModalDialogGui _gui;
        private readonly Button[] _buttons;
        private readonly LinkTextGui[] _labels;
        private readonly LinkTextGui _title;
        private readonly TMP_Text _fieldLabel;
        private readonly TMP_InputField _field;
        // The dropped connection's dialog, or a failed rejoin's, offers Rejoin first.
        private bool _rejoin;

        internal GameDialog(ModalDialogManager manager, DynamicModalDialogController controller, int shows)
        {
            _controller = controller;
            Shows = shows;
            _gui = controller._gui ?? throw new InvalidOperationException("The game's dialog has no interface.");
            // Answers in the order they read: the dialog's options, then its way back.
            _buttons = new[] { _gui.Option1Button, _gui.Option2Button, _gui.Option3Button, _gui.BackButton };
            _labels = new[] { Link(_gui.Option1TextString), Link(_gui.Option2TextString), Link(_gui.Option3TextString), Link(_gui.BackTextString) };
            _title = Link(_gui.TitleTextString);
            _fieldLabel = _gui.InputLabelString != null ? _gui.InputLabelString.GetComponent<TMP_Text>() : null;
            _field = _gui.InputField;
            Describe(manager);
            Dim = controller._backgroundType switch
            {
                DynamicModalDialogController.BackgroundType.OPAQUE => 0.76f,
                DynamicModalDialogController.BackgroundType.CLEAR => 0f,
                _ => 0.45f
            };
        }

        /// <summary>The show this dialog stands for: the game can show the same dialog again with other text.</summary>
        internal int Shows { get; }

        internal bool Is(DynamicModalDialogController controller, int shows) => controller.Pointer == _controller.Pointer && shows == Shows;

        internal override bool Refresh()
        {
            var shape = 0;
            for (var i = 0; i < _buttons.Length; i++)
                if (Live(_buttons[i]) && _buttons[i].gameObject.activeSelf) shape |= 1 << i;
            if (Live(_field) && Live(_gui.InputGoup) && _gui.InputGoup.activeSelf) shape |= FieldShown;
            var rejoin = _rejoin && Rejoin.Offered;
            if (rejoin) shape |= RejoinShown;
            var content = _gui.ContentText;
            var title = Read(_title);
            var body = Live(content) && content.gameObject.activeSelf ? content.text ?? "" : "";
            var fieldLabel = (shape & FieldShown) == 0 ? null : Live(_fieldLabel) ? _fieldLabel.text ?? "" : "";
            var changed = shape != Shape || title != Title || body != Body || fieldLabel != FieldLabel;
            Title = title;
            Body = body;
            FieldLabel = fieldLabel;
            if (shape != Shape)
            {
                Shape = shape;
                Answers.Clear();
                // Beside Rejoin, the game's only answer is the way out when it has no Back of its own.
                var outFirst = rejoin && (shape & 1 << 3) == 0;
                DialogChoice leave = null;
                for (var i = 0; i < _buttons.Length; i++)
                {
                    if ((shape & 1 << i) == 0) continue;
                    var button = _buttons[i];
                    var choice = new DialogChoice(() => button.onClick.Invoke(), i == 3 || outFirst && leave == null,
                        () => Live(button) && button.interactable);
                    if (choice.Back) leave = choice;
                    Answers.Add(choice);
                }
                // The game's dialog is answered first, so it closes as the game expects, then the rejoin begins.
                if (rejoin)
                    Answers.Insert(0, new DialogChoice(() =>
                    {
                        leave?.Run();
                        Rejoin.Start();
                    }, false));
            }
            var answer = 0;
            if (rejoin)
            {
                var label = Rejoin.Label;
                if (Answers[0].Label != label) changed = true;
                Answers[answer++].Label = label;
            }
            for (var i = 0; i < _buttons.Length; i++)
            {
                if ((shape & 1 << i) == 0) continue;
                var label = Read(_labels[i]);
                if (Answers[answer].Label != label) changed = true;
                Answers[answer++].Label = label;
            }
            return changed;
        }

        internal override string Typed => (Shape & FieldShown) != 0 && Live(_field) ? _field.text ?? "" : null;

        internal override bool Secret => Live(_field) && _field.m_InputType == TMP_InputField.InputType.Password;

        internal override int MaxLength => Live(_field) ? _field.m_CharacterLimit : 0;

        // Through the field's own setter, as the game fills it, so its character limit and validation still apply.
        internal override void Type(string text)
        {
            if (Live(_field)) _field.SetText(text, true);
        }

        // Which dialog this is decides its mark and where focus starts; the manager holds one controller per dialog.
        private void Describe(ModalDialogManager manager)
        {
            if (manager == null || !manager) return;
            if (Any(manager.NewSaveConfirmDialog, manager.OverwriteSaveConfirmDialog, manager.DeleteSaveConfirmDialog,
                    manager.RecoverSaveBackupConfirmDialog, manager.BanConfirmDialog, manager.QuitGameConfirmDialog,
                    manager.ResetSettingsConfirmDialog, manager.ResetKeyboardBindingsConfirmDialog,
                    manager.ResetGamepadBindingsConfirmDialog))
            {
                Tone = DialogTone.Caution;
                Cautious = true;
            }
            else if (Any(manager.LegacySaveWarningDialog)) Tone = DialogTone.Caution;
            else if (Any(manager.KickedFromGameDialog, manager.BannedFromGameDialog, manager.ServerShutdownDialog,
                    manager.WrongPasswordDialog, manager.VersionMismatchDialog, manager.HostLeftLobbyDialog, manager.LobbyFullDialog,
                    manager.DedicatedGameFullDialog, manager.DedicatedGameNotFoundDialog, manager.IpAddressMalformedDialog,
                    manager.CantOverwriteSaveDialog, manager.CantRecoverSaveBackupDialog, manager.BackupRecoverFailureDialog))
                Tone = DialogTone.Problem;
            else if (Any(manager.BackupRecoverSuccessDialog)) Tone = DialogTone.Done;
            else if (Any(manager.GameStartingDialog, manager.FetchingServerDetailsDialog)) Busy = true;
            // A refusal means the server won't take the player back. The dropped connection's own dialog, or the failure of
            // a rejoin, carries the offer; a failed join to any other server doesn't.
            if (Any(manager.KickedFromGameDialog, manager.BannedFromGameDialog, manager.WrongPasswordDialog,
                    manager.VersionMismatchDialog, manager.LobbyFullDialog, manager.DedicatedGameFullDialog))
                Rejoin.Refused();
            else if (Any(manager.ServerShutdownDialog, manager.DedicatedGameNotFoundDialog))
                _rejoin = Rejoin.Claim(Any(manager.ServerShutdownDialog));
        }

        private bool Any(params DynamicModalDialogController[] dialogs)
        {
            foreach (var dialog in dialogs)
                if (dialog != null && dialog.Pointer == _controller.Pointer) return true;
            return false;
        }

        private static LinkTextGui Link(UnityEngine.Localization.Components.LocalizeStringEvent localized)
            => Live(localized) ? localized.GetComponent<LinkTextGui>() : null;

        private static string Read(LinkTextGui text) => Live(text) ? text._textValue ?? "" : "";

        private static bool Live(UnityEngine.Object value) => value != null && value;
    }

    /// <summary>
    /// A mod's dialog through the SonsSdk shim. The game-styled copy the shim builds stays hidden and holds the
    /// callbacks; an answer goes through it, so the mod sees exactly what it did before.
    /// </summary>
    internal sealed class ModDialog : SystemDialog
    {
        private readonly string[] _options = new string[3];
        private string _cancel;
        private bool _changed = true;

        internal ModDialog(GenericModalDialog dialog, string title, string message)
        {
            Dialog = dialog;
            Title = title ?? "";
            Body = message ?? "";
        }

        internal GenericModalDialog Dialog { get; }

        internal void SetOption(int index, string text)
        {
            _options[index] = text ?? "";
            _changed = true;
        }

        internal override bool Refresh()
        {
            // The shim's Cancel is always there; its label is Modstoke's, in the player's language.
            var cancel = FrontEnd.Text["common.cancel"];
            if (!_changed && cancel == _cancel) return false;
            _changed = false;
            _cancel = cancel;
            var shape = 0;
            for (var i = 0; i < _options.Length; i++)
                if (_options[i] != null) shape |= 1 << i;
            if (shape != Shape)
            {
                Shape = shape;
                Answers.Clear();
                if (_options[0] != null) Answers.Add(new DialogChoice(Dialog.Option1Clicked, false));
                if (_options[1] != null) Answers.Add(new DialogChoice(Dialog.Option2Clicked, false));
                if (_options[2] != null) Answers.Add(new DialogChoice(Dialog.Option3Clicked, false));
                Answers.Add(new DialogChoice(Dialog.CancelClicked, true));
            }
            var answer = 0;
            foreach (var option in _options)
                if (option != null) Answers[answer++].Label = option;
            Answers[answer].Label = cancel;
            return true;
        }
    }

    /// <summary>A notice of Modstoke's own, such as why a server turned this game away, with one answer that closes it.</summary>
    internal sealed class NoticeDialog : SystemDialog
    {
        private string _ok;

        internal NoticeDialog(string title, string body, DialogTone tone, Action closed)
        {
            Title = title ?? "";
            Body = body ?? "";
            Tone = tone;
            Shape = 0;
            Answers.Add(new DialogChoice(closed, false));
        }

        internal override bool Refresh()
        {
            var ok = FrontEnd.Text["dialog.ok"];
            if (ok == _ok) return false;
            _ok = Answers[0].Label = ok;
            return true;
        }
    }
}
