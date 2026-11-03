// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using RedLoader;
using SUI;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace Modstoke.Loader
{
    internal sealed class SdkPanelBlur : ISdkPanelBlurDriver
    {
        internal static readonly SdkPanelBlur Instance = new();
        private GameObject _blurObject;

        private SdkPanelBlur() { }

        internal static ISdkPanelBlurDriver Create() => Instance;

        public Material GetForColor(Color color)
        {
            if (PanelBlur.BlurMaterials.TryGetValue(color, out var material) && material)
                return material;

            var shader = Shader.Find("Sons/UI/SampleBlurHLSL");
            if (!shader)
                throw new InvalidOperationException("The Sons UI blur shader is unavailable.");

            material = new Material(shader);
            try
            {
                material.color = color;
                PanelBlur.BlurMaterials[color] = material;
                return material;
            }
            catch
            {
                try { UnityEngine.Object.Destroy(material); }
                catch (Exception exception) { ReportCleanupFailure(exception); }
                throw;
            }
        }

        public void SetRadius(float radius)
        {
            if (!_blurObject)
            {
                PanelBlur.CurrentBlurPass = null;
                return;
            }
            if (PanelBlur.CurrentBlurPass == null)
                return;

            PanelBlur.CurrentBlurPass.radius = radius;
        }

        public void SetupBlur()
        {
            if (!LocalPlayer.Inventory)
                return;

            var parent = LocalPlayer.MainCamTr;
            if (!parent)
                return;
            if (_blurObject && _blurObject.transform.parent == parent && PanelBlur.CurrentBlurPass != null)
                return;

            var source = LocalPlayer.Inventory.CameraController.Camera.transform.Find("SlightBlurPass");
            if (!source)
            {
                RLog.Error("Failed to find SlightBlurPass!");
                return;
            }

            var clone = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
            try
            {
                var volume = clone.GetComponent<CustomPassVolume>();
                if (!volume || volume.customPasses == null || volume.customPasses.Count == 0)
                    throw new InvalidOperationException("The blur volume does not contain a custom pass.");
                var blurPass = volume.customPasses._items[0].Cast<UIBlurPass>();
                blurPass.radius = 25;
                var previous = _blurObject;
                _blurObject = clone;
                PanelBlur.CurrentBlurPass = blurPass;
                if (previous)
                    DiscardFailedSetup(previous);
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
