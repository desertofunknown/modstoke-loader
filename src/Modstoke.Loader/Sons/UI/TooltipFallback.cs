// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility tooltip fallback adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using SonsSdk;
using UnityEngine.UI;

namespace Modstoke.Loader
{
    internal sealed class TooltipFallback : ISdkTooltipDriver
    {
        private static readonly TooltipFallback Driver = new();

        internal static ISdkTooltipDriver Create() => Driver;

        private TooltipFallback() { }

        public void Setup() => Tooltips.Instance.Setup();
        public void Show(string text) => Tooltips.Instance.Show(text);
        public void Hide() => Tooltips.Instance.Hide();
        public void RaycastFor(GraphicRaycaster raycaster) => Tooltips.Instance.RaycastFor(raycaster);
        public void StopRaycasting() => Tooltips.Instance.StopRaycasting();
        public void Update() => Tooltips.Instance.Update();
    }
}
