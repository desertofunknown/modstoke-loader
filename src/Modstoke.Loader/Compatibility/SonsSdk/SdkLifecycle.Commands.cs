// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Loader
{
    internal sealed partial class SdkLifecycle
    {
        public void RegisterCommands(Type targetType, object instance)
            => SdkCommandFallback.Create().RegisterCommands(targetType, instance);

        public void RegisterCommand(string command, Func<string, bool> callback)
            => SdkCommandFallback.Create().RegisterCommand(command, callback);
    }
}
