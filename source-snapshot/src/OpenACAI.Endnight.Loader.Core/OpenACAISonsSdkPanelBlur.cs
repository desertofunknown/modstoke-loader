// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using SUI;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsSdkPanelBlur : ISdkPanelBlurDriver
    {
        internal static readonly OpenACAISonsSdkPanelBlur Instance = new();

        private OpenACAISonsSdkPanelBlur() { }

        public Material GetForColor(Color color)
        {
            if (PanelBlur.BlurMaterials.TryGetValue(color, out var material))
                return material;

            material = new Material(Shader.Find("Sons/UI/SampleBlurHLSL"));
            material.color = color;
            PanelBlur.BlurMaterials[color] = material;
            return material;
        }

        public void SetRadius(float radius)
        {
            if (PanelBlur.CurrentBlurPass == null)
                return;

            PanelBlur.CurrentBlurPass.radius = radius;
        }

        public void SetupBlur()
        {
            if (!LocalPlayer.Inventory)
                return;

            var source = LocalPlayer.Inventory.CameraController.Camera.transform.Find("SlightBlurPass");
            if (!source)
            {
                RLog.Error("Failed to find SlightBlurPass!");
                return;
            }

            var clone = UnityEngine.Object.Instantiate(source.gameObject, LocalPlayer.MainCamTr, false);
            try
            {
                var volume = clone.GetComponent<CustomPassVolume>();
                var blurPass = volume.customPasses._items[0].Cast<UIBlurPass>();
                blurPass.radius = 25;
                PanelBlur.CurrentBlurPass = blurPass;
            }
            catch (Exception exception)
            {
                DiscardFailedSetup(clone);
                RLog.Error($"Failed to initialize UIBlurPass! {exception}");
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
}
