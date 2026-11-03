// SPDX-License-Identifier: GPL-3.0-or-later
// Native logging contract adapted from BepInEx's IL2CPPLogSource.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Il2CppInterop.Runtime;

namespace Modstoke.Preloader
{
    internal sealed class IL2CPPLogSource : ILogSource
    {
        // IL2CPP exposes a single setter, not a subscription or a previous-callback getter.
        // Keep both the source and its callback alive even after forwarding stops.
        internal static readonly IL2CPPLogSource Instance = new IL2CPPLogSource();
        private readonly object _sync = new object();
        private readonly NativeLogCallback _callback;
        private Action<string> _compatibilityEndpoint;
        private bool _defaultRegistered;
        private bool _attempted;
        private bool _registered;
        private bool _disposed;
        private string _lastError = "";
        [ThreadStatic] private static bool _dispatching;

        private IL2CPPLogSource() => _callback = OnMessage;

        public string SourceName => "IL2CPP";
        public event EventHandler<LogEventArgs> LogEvent;

        internal void Attach()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_compatibilityEndpoint == null && !_defaultRegistered)
                {
                    Logger.Sources.Add(this);
                    Volatile.Write(ref _defaultRegistered, true);
                }
            }
            EnsureCapture();
        }

        internal static void RegisterCompatibility(Action<string> endpoint)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            var source = Instance;
            lock (source._sync)
            {
                ObjectDisposedException.ThrowIf(source._disposed, source);
                Interlocked.Exchange(ref source._compatibilityEndpoint, endpoint);
                if (source._defaultRegistered)
                {
                    Volatile.Write(ref source._defaultRegistered, false);
                    Logger.Sources.Remove(source);
                }
            }
            source.EnsureCapture();
        }

        private void EnsureCapture()
        {
            lock (_sync)
            {
                if (_attempted || _disposed)
                    return;
                _attempted = true;
            }
            try
            {
                IL2CPP.il2cpp_register_log_callback(Marshal.GetFunctionPointerForDelegate(_callback));
                Volatile.Write(ref _registered, true);
            }
            catch (Exception error)
            {
                RecordError(error);
                try { Dispose(); }
                catch { /* Keep the native registration failure as the startup diagnostic. */ }
                throw;
            }
        }

        private void OnMessage(IntPtr message)
        {
            if (Volatile.Read(ref _disposed) || _dispatching || message == IntPtr.Zero)
                return;
            _dispatching = true;
            try
            {
                // Copy native text during the call; IL2CPP may reuse its message buffer on return.
                var text = Marshal.PtrToStringAnsi(message)?.Trim() ?? "";
                var endpoint = Volatile.Read(ref _compatibilityEndpoint);
                if (endpoint != null)
                    endpoint(text);
                else if (Volatile.Read(ref _defaultRegistered))
                    LogEvent?.Invoke(this, new LogEventArgs(text, LogLevel.Message, this));
            }
            catch (Exception error)
            {
                // A third-party listener must not throw through the unmanaged callback.
                RecordError(error);
            }
            finally
            {
                _dispatching = false;
            }
        }

        private void RecordError(Exception error)
        {
            try { Volatile.Write(ref _lastError, error.Message); }
            catch { Volatile.Write(ref _lastError, "Native log callback failed."); }
        }

        internal object CreateState() => new
        {
            registered = Volatile.Read(ref _registered),
            compatibilityEndpointSelected = Volatile.Read(ref _compatibilityEndpoint) != null,
            forwarding = Volatile.Read(ref _registered) && !Volatile.Read(ref _disposed),
            callbackLifetime = "process",
            callbackError = Volatile.Read(ref _lastError)
        };

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                Volatile.Write(ref _disposed, true);
                Volatile.Write(ref _defaultRegistered, false);
                Interlocked.Exchange(ref _compatibilityEndpoint, null);
                LogEvent = null;
                Logger.Sources.Remove(this);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NativeLogCallback(IntPtr message);
    }
}
