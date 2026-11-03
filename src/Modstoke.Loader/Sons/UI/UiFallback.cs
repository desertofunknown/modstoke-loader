// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility shared UI handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using RedLoader.Utils;
using SonsSdk;
using SUI;
using Sui = SUI.SUI;

namespace Modstoke.Loader;

internal sealed partial class UiFallback : ISdkUiFallbackDriver
{
    private static readonly UiFallback Driver = new();

    internal static ISdkUiFallbackDriver Create() => Driver;

    public void InitPrefabs(ISdkLifecycleDriver driver)
    {
        var sw = TimingLogger.StartNew("SUI.InitPrefabs");

        if (Sui.IsInitialized)
            return;

        Sui.InitBundleContentForFallback();

        if (driver != null)
        {
            driver.UiPrefabs.Initialize();
        }
        else
        {
            InitializePrefabs();
        }

        Sui.PrintSpritesForFallback();

        Sui.CreateViewport();

        Sui.PublishInitialized();

        SdkEvents.OnSonsSceneInitialized.Subscribe(Sui.OnSonsScene);

        sw.Stop();
    }
}
