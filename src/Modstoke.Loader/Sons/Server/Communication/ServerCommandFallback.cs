// SPDX-License-Identifier: GPL-3.0-or-later
// Compatibility networking adapted from SonsSdk.
// Copyright (c) RedLoader contributors. Copyright (C) 2026 Alex Cooper.

using SonsSdk.Networking;

namespace Modstoke.Loader;

// SonsSdk's NetUtils loads this type and Create by name before a lifecycle driver is installed.
internal static class ServerCommandFallback
{
    internal static ISdkServerCommandDriver Create() => global::Modstoke.Loader.ServerCommands.Instance;
}
