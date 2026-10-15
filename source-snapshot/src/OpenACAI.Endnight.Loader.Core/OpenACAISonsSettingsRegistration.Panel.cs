// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using Sons.Ai.Vail;
using SonsSdk;
using SUI;
using UnityEngine;
using UnityEngine.UI;
using static SUI.SUI;
using SettingsPanel = SonsGameManager.ModSettingsUi;

namespace OpenACAI.Endnight.Loader
{
    internal sealed partial class OpenACAISonsSettingsRegistration
    {
        public void CreatePanel()
        {
            if (SettingsPanel._initialized)
                return;

            var panel = RegisterNewPanel(SettingsPanel.MOD_SETTINGS_NAME)
                .OverrideSorting(200)
                .Background(SettingsPanel.MainBg).Material(GameResources.GetMaterial(MaterialAssetMap.DarkGreyBackground))
                .Dock(EDockType.Fill).Size(-400, -100);
            panel.Active(false);

            SettingsPanel.Raycaster = panel.Root.GetComponent<GraphicRaycaster>();
            panel.Add(SLabel.Bind(SettingsPanel.Title).Dock(EDockType.Top)
                .Size(0, 60).Position(0, -30)
                .FontColor(Color.white.WithAlpha(0.3f)).Font(EFont.RobotoLight).FontSpacing(3).Active(false));

            SettingsPanel._mainContainer = SScrollContainer.Margin(100, 100, 100, 160).As<SScrollContainerOptions>();
            SettingsPanel._mainContainer.ContainerObject.Padding(2, 4, 0, 0);
            panel.Add(SettingsPanel._mainContainer);

            panel.Add(SBgButton
                .Background(SettingsPanel.ButtonBg).Background("#990508").Ppu(3)
                .Pivot(0, 0).Anchor(AnchorType.BottomLeft).Position(80, 80).Size(300, 60)
                .RichText("\uF060 Back").FontColor("#ff5856").UpperCase().FontSize(20).Notify(SettingsPanel.Close));

            panel.Add(SBgButton
                .Background(SettingsPanel.ButtonBg).Background("#796C4E").Ppu(3)
                .Pivot(0, 0).Anchor(AnchorType.BottomLeft).Position(390, 80).Size(200, 60)
                .RichText("Revert \uf0e2").FontColor("#c3ba8b").UpperCase().FontSize(20).Notify(SettingsPanel.RevertSettings));

            // Keep SDK callback identities and their existing subscription order.
            SettingsPanel.SubscribeEvents();
            SettingsPanel._initialized = true;
        }

        public void UpdatePanel()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                ClosePanel(returnToOwner: true);
        }

        public bool TryOpenPanel(string id, Action<bool> onClosed)
        {
            if (!SettingsPanel._initialized || SettingsRegistry.GetEntry(id) is not { } entry)
                return false;

            if (SettingsPanel._currentEntry != null)
            {
                ClosePanel(returnToOwner: false);
                if (SettingsPanel._currentEntry != null)
                    return false;
            }

            RLog.Debug($"Found panel for {id}");
            try
            {
                entry.ParentTo(SettingsPanel._mainContainer.ContainerObject.RectTransform);
                SettingsPanel.Title.Value = $"MOD SETTINGS <color=#ea2f4e>[{id}]</color>";
                TogglePanel(SettingsPanel.MOD_SETTINGS_NAME, true);
                TooltipProvider.RaycastFor(SettingsPanel.Raycaster);
                SettingsPanel._currentEntry = entry;
                SettingsPanel._onClosed = onClosed;
                return true;
            }
            catch
            {
                try
                {
                    entry.Unparent();
                    TogglePanel(SettingsPanel.MOD_SETTINGS_NAME, false);
                    TooltipProvider.StopRaycasting();
                }
                catch
                {
                    // Preserve the original opening failure if UI cleanup also fails.
                }

                throw;
            }
        }

        public void ClosePanel(bool returnToOwner)
        {
            var entry = SettingsPanel._currentEntry;
            var onClosed = SettingsPanel._onClosed;
            SettingsPanel._currentEntry = null;
            SettingsPanel._onClosed = null;
            var completed = false;
            var showRestartPrompt = false;

            try
            {
                entry?.Unparent();
                TogglePanel(SettingsPanel.MOD_SETTINGS_NAME, false);
                TooltipProvider.StopRaycasting();

                if (entry != null)
                {
                    entry.Save();
                    entry.InvokeCallbacks();
                    showRestartPrompt = entry.ChangesNeedRestart && entry.CheckForChanges();
                }

                if (showRestartPrompt)
                {
                    GenericModalDialog.ShowDialog("Restart", "You need to restart the game for the changes to take effect.")
                        .SetOption1("Ok", () => { });
                }

                completed = true;
            }
            finally
            {
                onClosed?.Invoke(returnToOwner && completed && !showRestartPrompt && SettingsPanel._currentEntry == null);
            }
        }

        public void RevertPanel()
        {
            SettingsPanel._currentEntry?.RevertSettings();
        }
    }
}
