// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using RedLoader.Utils;

namespace Modstoke.Loader
{
    /// <summary>
    /// Dedicated-server hosting support: machine replies, the status and event files, graceful save and stop.
    /// Everything here is off on a game client unless MODSTOKE_HOST_STATUS=1 asks for the status file.
    /// </summary>
    internal static class Host
    {
        private const int PostsPerTick = 32;
        private static readonly Lazy<bool> Server = new Lazy<bool>(() =>
            LoaderEnvironment.IsDedicatedServer || GameBuild.IsDedicatedServer);
        private static readonly Lazy<bool> Active = new Lazy<bool>(() =>
            Server.Value || Environment.GetEnvironmentVariable("MODSTOKE_HOST_STATUS") == "1");
        private static readonly Lazy<DateTimeOffset?> ProcessStart = new Lazy<DateTimeOffset?>(ReadProcessStart);
        private static readonly ConcurrentQueue<Action> Posted = new ConcurrentQueue<Action>();
        private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);
        private static int _mainThread;
        private static DateTimeOffset _initializedUtc = DateTimeOffset.UtcNow;
        private static bool _initialized;

        internal static bool Initialized => _initialized;
        internal static bool IsServer => Server.Value;
        internal static bool Enabled => Active.Value;
        internal static DateTimeOffset StartedUtc => ProcessStart.Value ?? _initializedUtc;
        internal static bool IsMainThread => _mainThread != 0 && Environment.CurrentManagedThreadId == _mainThread;

        internal static void Post(Action action)
        {
            if (action != null)
                Posted.Enqueue(action);
        }

        internal static void Initialize()
        {
            if (_initialized)
                return;
            _initialized = true;
            _mainThread = Environment.CurrentManagedThreadId;
            _initializedUtc = DateTimeOffset.UtcNow;
            if (!Enabled)
                return;
            Run("HostOutput", HostOutput.Initialize);
            Run("HostEvents", HostEvents.Initialize);
            Run("HostCrash", HostCrash.Initialize);
            Run("HostStatus", HostStatus.Initialize);
            Run("HostSave", HostSave.Initialize);
            Run("HostConfig", HostConfig.Initialize);
            if (IsServer)
                Run("HostTcp", HostTcp.Start);
            Run("HostMods", () =>
            {
                foreach (var load in HostMods.LoadResults)
                    if (load.Status == "failed")
                        HostEvents.Emit("mod-error", new { id = load.Id, name = load.Name, loader = load.Loader, error = load.Error });
            });
        }

        internal static void Tick()
        {
            if (!_initialized)
                return;
            for (int i = 0; i < PostsPerTick && Posted.TryDequeue(out var action); i++)
            {
                try
                {
                    action();
                }
                catch (Exception error)
                {
                    Warn(action.Method.Name, "Host work " + action.Method.Name + " failed: " + error.Message);
                }
            }
            if (!Enabled)
                return;
            Run("HostPerf.Frame", HostPerf.Frame);
            Run("HostStatus.Tick", HostStatus.Tick);
            Run("HostSave.Tick", HostSave.Tick);
            if (IsServer)
                Run("HostWorld.Tick", HostWorld.Tick);
        }

        internal static void Shutdown()
        {
            if (!_initialized || !Enabled)
                return;
            Run("HostSave.OnQuit", HostSave.OnQuit);
            Run("HostStatus.Shutdown", HostStatus.Shutdown);
            Run("HostEvents.Shutdown", HostEvents.Shutdown);
            Run("HostTcp.Stop", HostTcp.Stop);
            Run("HostPerf.Shutdown", HostPerf.Shutdown);
            Run("HostOutput.Flush", () => HostOutput.Flush(1000));
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                Warn(name, name + " failed: " + error.Message);
            }
        }

        private static void Warn(string key, string message)
        {
            lock (Reported)
                if (!Reported.Add(key))
                    return;
            RuntimeEntryPoint.LogSource?.LogWarning(message);
        }

        private static DateTimeOffset? ReadProcessStart()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception error) when (error is InvalidOperationException or NotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }
}
