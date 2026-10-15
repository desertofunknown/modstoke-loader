// SPDX-License-Identifier: GPL-3.0-or-later
// Log categories and levels adapted from BepInEx's IL2CPPUnityLogSource.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using Logger = BepInEx.Logging.Logger;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAIUnityLogSource : ILogSource
    {
        private static readonly OpenACAIUnityLogSource Instance = new OpenACAIUnityLogSource();
        private readonly object _sync = new object();
        private Application.LogCallback _callback;
        private Action<string, string, LogType> _compatibilityEndpoint;
        private bool _initialized;
        private bool _enabled;
        private bool _defaultRegistered;
        private bool _disposed;
        private bool _subscriptionAttempted;
        private bool _subscriptionCompleted;
        private bool _detached;
        private int _forwarding;
        private long _messageCount;
        private string _status = "not-attached";
        private string _setupError = "";
        private string _callbackError = "";
        private string _detachError = "";
        [ThreadStatic] private static bool _dispatching;

        private OpenACAIUnityLogSource() { }

        public string SourceName => "Unity";
        public event EventHandler<LogEventArgs> LogEvent;

        internal static void Install(Action<string> addStartupWarning)
        {
            var source = Instance;
            try
            {
                lock (source._sync)
                {
                    if (source._initialized || source._disposed)
                        return;
                    source._initialized = true;
                    source._enabled = ConfigFile.CoreConfig.Bind("Logging", "UnityLogListening", true,
                        "Enables showing unity log messages in the BepInEx logging system.").Value;
                    if (!source._enabled)
                    {
                        if (source._compatibilityEndpoint == null)
                            source._status = "disabled";
                        return;
                    }
                    if (source._compatibilityEndpoint == null)
                    {
                        Logger.Sources.Add(source);
                        Volatile.Write(ref source._defaultRegistered, true);
                    }
                }
                source.EnsureCapture();
            }
            catch (Exception error)
            {
                source.Dispose();
                source.DetachCallback();
                source._status = "failed";
                source._setupError = error.Message;
                addStartupWarning?.Invoke("Unity log capture could not be attached: " + error.Message);
            }
        }

        internal static void RegisterCompatibility(Action<string, string, LogType> endpoint)
        {
            var source = Instance;
            lock (source._sync)
            {
                Interlocked.Exchange(ref source._compatibilityEndpoint, endpoint);
                if (source._defaultRegistered)
                {
                    Volatile.Write(ref source._defaultRegistered, false);
                    Logger.Sources.Remove(source);
                }
                if (source._forwarding != 0)
                    source._status = "compatibility";
            }
            source.EnsureCapture();
        }

        private void EnsureCapture()
        {
            lock (_sync)
            {
                if (_subscriptionAttempted || _disposed)
                    return;
                // Retain this exact converted delegate for removal, including failed attachment.
                _callback = new Action<string, string, LogType>(OnMessage);
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                _subscriptionAttempted = true;
                Volatile.Write(ref _forwarding, 1);
            }
            try
            {
                // Unity may dispatch synchronously; do not hold the gate while attaching capture.
                Application.add_logMessageReceivedThreaded(_callback);
                lock (_sync)
                {
                    if (!_disposed)
                        _status = _compatibilityEndpoint == null ? "owned" : "compatibility";
                }
            }
            catch (Exception error)
            {
                Dispose();
                lock (_sync)
                {
                    _status = "failed";
                    _setupError = error.Message;
                }
                throw;
            }
            finally
            {
                lock (_sync)
                {
                    _subscriptionCompleted = true;
                    // Shutdown can race attachment. Removal must follow the completed add attempt.
                    if (_disposed)
                        DetachCallback();
                }
            }
        }

        private void OnMessage(string message, string stackTrace, LogType type)
        {
            if (Volatile.Read(ref _forwarding) == 0 || _dispatching)
                return;
            _dispatching = true;
            try
            {
                Interlocked.Increment(ref _messageCount);
                var endpoint = Volatile.Read(ref _compatibilityEndpoint);
                if (endpoint != null)
                {
                    endpoint(message, stackTrace, type);
                    return;
                }
                if (!Volatile.Read(ref _defaultRegistered))
                    return;
                var level = type switch
                {
                    LogType.Error => LogLevel.Error,
                    LogType.Assert => LogLevel.Debug,
                    LogType.Warning => LogLevel.Warning,
                    LogType.Exception => LogLevel.Error,
                    _ => LogLevel.Message
                };
                if ((Logger.ListenedLogLevels & level) == LogLevel.None)
                    return;
                var text = message ?? "";
                if (type == LogType.Exception && !string.IsNullOrWhiteSpace(stackTrace))
                    text += Environment.NewLine + stackTrace;
                LogEvent?.Invoke(this, new LogEventArgs(text, level, this));
            }
            catch (Exception error)
            {
                // Unity can call concurrently or during another log write. Do not log from this catch.
                try { Volatile.Write(ref _callbackError, error.Message); }
                catch { Volatile.Write(ref _callbackError, "Unity log callback failed."); }
            }
            finally
            {
                _dispatching = false;
            }
        }

        // Called on the Unity thread while its domain is still available.
        internal static void Shutdown()
        {
            Instance.Dispose();
            Instance.DetachCallback();
        }

        private void DetachCallback()
        {
            lock (_sync)
            {
                if (!_subscriptionCompleted || _detached)
                    return;
                try
                {
                    Application.remove_logMessageReceivedThreaded(_callback);
                    _detached = true;
                    _detachError = "";
                }
                catch (Exception error)
                {
                    _detachError = error.Message;
                    // Keep the callback rooted and inert if Unity could not remove it.
                }
                if (_status == "stopped")
                    _status = _detached ? "detached" : "detach-failed";
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                Volatile.Write(ref _forwarding, 0);
                Volatile.Write(ref _defaultRegistered, false);
                Logger.Sources.Remove(this);
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                if (_subscriptionAttempted && _status != "failed" &&
                    _status != "detached" && _status != "detach-failed")
                    _status = "stopped";
            }
        }

        // Process exit may run off-thread or after Unity teardown; managed cleanup only.
        private void OnProcessExit(object sender, EventArgs args) => Dispose();

        internal static object CreateState() => new
        {
            enabled = Instance._enabled,
            status = Volatile.Read(ref Instance._status),
            callback = "logMessageReceivedThreaded",
            compatibilityEndpointSelected = Volatile.Read(ref Instance._compatibilityEndpoint) != null,
            forwarding = Volatile.Read(ref Instance._forwarding) != 0,
            receivedMessageCount = Interlocked.Read(ref Instance._messageCount),
            setupError = Volatile.Read(ref Instance._setupError),
            callbackError = Volatile.Read(ref Instance._callbackError),
            detachError = Volatile.Read(ref Instance._detachError)
        };
    }
}
