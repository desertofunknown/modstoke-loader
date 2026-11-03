// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;

namespace Modstoke.Examples.WorldConditions;

public static class Mod
{
    private static WorldConditionsProvider? _provider;
    private static bool _settingsRegistrationAttempted;

    public static void Initialize()
    {
        if (_provider != null)
            return;
        if (LoaderConfig.TryGetProviderSnapshot(WorldConditionsProvider.SettingsId, out _))
            throw new InvalidOperationException("World Conditions settings are already registered.");

        var provider = new WorldConditionsProvider();
        if (!MenuRegistry.TryRegisterProvider(provider))
            throw new InvalidOperationException("World Conditions menu is already registered.");

        _provider = provider;
        try
        {
            // Registration can publish callbacks before returning. Keep cleanup available if it throws.
            _settingsRegistrationAttempted = true;
            if (!LoaderConfig.RegisterOrUpdateProvider(provider))
                throw new InvalidOperationException("Could not register World Conditions settings.");
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    public static void Shutdown()
    {
        var provider = _provider;
        if (provider == null)
            return;

        _provider = null;
        provider.Deactivate();
        try
        {
            if (_settingsRegistrationAttempted)
            {
                _settingsRegistrationAttempted = false;
                LoaderConfig.TryUnregisterProvider(WorldConditionsProvider.SettingsId);
            }
        }
        finally
        {
            MenuRegistry.TryUnregisterProvider(WorldConditionsProvider.MenuId);
        }
    }
}
