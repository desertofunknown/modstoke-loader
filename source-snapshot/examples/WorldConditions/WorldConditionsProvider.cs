// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using OpenACAI.Loader;
using OpenACAI.Sons;

namespace OpenACAI.Examples.WorldConditions;

internal sealed class WorldConditionsProvider : IOpenACAIMenuContentProvider, IOpenACAICommandProvider,
    IOpenACAIConfigProvider
{
    internal const string MenuId = "openacai.example.worldConditions";
    internal const string SettingsId = MenuId + ".settings";
    private const string RefreshId = "refresh";
    private const string ShowTicksKey = "showClockTicks";
    private readonly object _sync = new();
    private bool _active = true;
    private bool _showClockTicks;
    private bool _hasRead;
    private OpenACAISonsClockSnapshot? _clock;
    private OpenACAISonsWeatherSnapshot? _weather;
    private OpenACAISonsEnvironmentReadStatus _clockStatus;
    private OpenACAISonsEnvironmentReadStatus _weatherStatus;

    public string Id => MenuId;
    string IOpenACAIConfigProvider.Id => SettingsId;
    public string DisplayName => "World Conditions";
    string IOpenACAIConfigProvider.DisplayName => "World Conditions Settings";
    public string Kind => OpenACAIMenuProviderKinds.Status;
    public int Priority => 500;
    public string Source => "World Conditions Example";
    public string Description => "Local clock and weather";
    public bool RequiresAuthorization => false;
    public bool Enabled { get { lock (_sync) return _active; } }

    public IReadOnlyList<OpenACAICommandSnapshot> GetCommands() =>
        new[] { new OpenACAICommandSnapshot(RefreshId, "Refresh", "Read local clock and weather.",
            "", Enabled, requiresConfirmation: false) };

    public bool TryInvokeCommand(string commandId, out OpenACAICommandResultSnapshot result)
    {
        if (!string.Equals(commandId, RefreshId, StringComparison.Ordinal))
        {
            result = OpenACAICommandResultSnapshot.Failure("Unknown command.");
            return false;
        }
        if (!Enabled)
        {
            result = OpenACAICommandResultSnapshot.Failure("The example has stopped.");
            return false;
        }

        // SDK reads enforce the game thread. No fallback or background retry is used.
        var clockRead = OpenACAISonsEnvironmentReads.TryReadClock(out var clock, out var clockStatus);
        var weatherRead = OpenACAISonsEnvironmentReads.TryReadWeather(out var weather, out var weatherStatus);
        lock (_sync)
        {
            if (!_active)
            {
                result = OpenACAICommandResultSnapshot.Failure("The example has stopped.");
                return false;
            }
            _hasRead = true;
            _clock = clock;
            _weather = weather;
            _clockStatus = clockStatus;
            _weatherStatus = weatherStatus;
        }

        result = clockRead && weatherRead
            ? OpenACAICommandResultSnapshot.Success("Local clock and weather captured.")
            : OpenACAICommandResultSnapshot.Failure($"Clock: {clockStatus}; weather: {weatherStatus}.");
        return result.Succeeded;
    }

    public OpenACAIMenuContentSnapshot CreateContent()
    {
        lock (_sync)
        {
            var clockRows = new List<OpenACAIMenuRowSnapshot>();
            if (_clock == null)
                clockRows.Add(Row("Clock", _hasRead ? _clockStatus.ToString() : "Not captured"));
            else
            {
                clockRows.Add(Row("Game time", _clock.GameTime.ToString("c", CultureInfo.InvariantCulture)));
                clockRows.Add(Row("Effective time", _clock.EffectiveTime.ToString("c", CultureInfo.InvariantCulture)));
                clockRows.Add(Row("Override active", _clock.IsOverrideActive.ToString()));
                clockRows.Add(Row("Network-managed flag", _clock.IsNetworkManaged.ToString()));
                if (_showClockTicks)
                {
                    clockRows.Add(Row("Game ticks", _clock.GameTime.Ticks.ToString(CultureInfo.InvariantCulture)));
                    clockRows.Add(Row("Effective ticks", _clock.EffectiveTime.Ticks.ToString(CultureInfo.InvariantCulture)));
                }
                clockRows.Add(Row("Observed", FormatTimestamp(_clock.ObservedAt)));
            }

            var weatherRows = new List<OpenACAIMenuRowSnapshot>();
            if (_weather == null)
                weatherRows.Add(Row("Weather", _hasRead ? _weatherStatus.ToString() : "Not captured"));
            else
            {
                weatherRows.Add(Row("Precipitation", _weather.Precipitation?.ToString()
                    ?? Unknown(_weather.RawPrecipitation)));
                weatherRows.Add(Row("Cloud phase", _weather.CloudState?.ToString()
                    ?? Unknown(_weather.RawCloudState)));
                weatherRows.Add(Row("Snow flag", _weather.IsSnowing.ToString()));
                weatherRows.Add(Row("Rain blocked", _weather.IsRainBlocked.ToString()));
                weatherRows.Add(Row("Observed", FormatTimestamp(_weather.ObservedAt)));
            }

            return new OpenACAIMenuContentSnapshot(DisplayName, "Last local observations",
                new[] { new OpenACAIMenuSectionSnapshot("Clock", clockRows),
                    new OpenACAIMenuSectionSnapshot("Weather", weatherRows) });
        }
    }

    public IReadOnlyList<OpenACAIConfigValueSnapshot> GetSettings()
    {
        lock (_sync)
        {
            return new[] { new OpenACAIConfigValueSnapshot("Display", ShowTicksKey, "Show clock ticks",
                OpenACAIConfigValueKinds.Boolean, _showClockTicks.ToString(), "False", "",
                canEdit: _active, requiresRestart: false) };
        }
    }

    public bool TrySetValue(string key, string value, out string status)
    {
        lock (_sync)
        {
            if (!_active)
            {
                status = "The example has stopped.";
                return false;
            }
            if (!string.Equals(key, ShowTicksKey, StringComparison.Ordinal))
            {
                status = "Unknown setting.";
                return false;
            }
            if (!bool.TryParse(value, out var showTicks))
            {
                status = "A Boolean value is required.";
                return false;
            }
            _showClockTicks = showTicks;
            status = "Display setting updated for this session.";
            return true;
        }
    }

    internal void Deactivate()
    {
        lock (_sync)
        {
            _active = false;
            _clock = null;
            _weather = null;
        }
    }

    private static OpenACAIMenuRowSnapshot Row(string label, string value) => new(label, value, "", "");
    private static string Unknown(int value) => "Unknown (" + value.ToString(CultureInfo.InvariantCulture) + ")";
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
}
