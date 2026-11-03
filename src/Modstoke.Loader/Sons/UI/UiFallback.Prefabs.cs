// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility shared UI handling adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

namespace Modstoke.Loader;

internal sealed partial class UiFallback
{
    private static void InitializePrefabs() => UiPrefabs.Instance.Initialize();
}
