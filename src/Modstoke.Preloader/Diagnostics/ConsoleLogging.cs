// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Modstoke.Diagnostics;

namespace Modstoke.Preloader
{
    internal sealed class ConsoleLogging : ILogListener
    {
        private static readonly ConsoleLogging Instance = new();
        private static readonly ConfigEntry<string> ConsoleMode = ConfigFile.CoreConfig.Bind(
            "Logging.Console", "Mode", "Desktop",
            "Console window: Desktop opens the Modstoke dashboard; Native uses the Windows console. " +
            "Console applications such as a dedicated server always use their own console. " +
            "Enabled and --enable-console still control automatic opening. Shared log capture remains enabled.");
        // Whoever launched a console application reads its console and standard output, so the dashboard must not take them.
        private static readonly Lazy<bool> ConsoleApplication = new(IsConsoleApplication);
        [ThreadStatic] private static bool _capturing;
        private static int _attached;
        private static long _failures;
        private static string _lastError = "";
        private static string _modeDetail = "";

        public LogLevel LogLevelFilter => LogLevel.All;

        internal static bool DesktopActive => OperatingSystem.IsWindows()
            && ConsoleManager.Driver is WindowsConsoleDriver driver && driver.DesktopActive;

        internal static bool DesktopRequested
        {
            get
            {
                if (ConsoleApplication.Value)
                {
                    Volatile.Write(ref _modeDetail, "The game is a console application; using its own console.");
                    return false;
                }
                var mode = ConsoleMode.Value?.Trim();
                if (string.Equals(mode, "Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    Volatile.Write(ref _modeDetail, "Desktop requested.");
                    return true;
                }
                Volatile.Write(ref _modeDetail, string.Equals(mode, "Native", StringComparison.OrdinalIgnoreCase)
                    ? "Native requested." : "Invalid Logging.Console.Mode; using Native. Expected Desktop or Native.");
                return false;
            }
        }

        internal static void Attach()
        {
            if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0)
                return;
            try
            {
                Logger.Listeners.Add(Instance);
                RegisterShutdown();
                ConsoleHost.Start(Paths.GameRootPath);
                SetPhase("preloader", "Preparing the game runtime.");
            }
            catch (Exception error)
            {
                RecordFailure("attach shared console", error);
            }
        }

        internal static void RegisterShutdown()
        {
            // The preloader registers its disk flush after capture attaches. Keep IPC alive until that flush finishes.
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (_capturing || Volatile.Read(ref _attached) == 0)
                return;
            try
            {
                _capturing = true;
                // Source names and payload formatting can invoke plugin code. Never do either under a shared lock.
                var source = eventArgs.Source?.SourceName ?? "Unknown";
                var message = eventArgs.Data?.ToString() ?? "";
                ConsoleHub.Append(eventArgs.Level.ToString(), source, message);
            }
            catch (Exception error)
            {
                RecordFailure("capture log event", error);
            }
            finally { _capturing = false; }
        }

        internal static void SetPhase(string phase, string summary)
        {
            try { ConsoleHub.SetPhase(phase, summary); }
            catch (Exception error) { RecordFailure("update console phase", error); }
        }

        internal static void Append(string level, string source, string message)
        {
            if (_capturing)
                return;
            try
            {
                _capturing = true;
                ConsoleHub.Append(level, source, message);
            }
            catch (Exception error) { RecordFailure("capture console output", error); }
            finally { _capturing = false; }
        }

        private static bool IsConsoleApplication()
        {
            try
            {
                using var image = File.OpenRead(Environment.ProcessPath ?? "");
                using var reader = new PEReader(image);
                return reader.PEHeaders.PEHeader?.Subsystem == Subsystem.WindowsCui;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException
                or ArgumentException)
            {
                RecordFailure("read the process subsystem", error);
                return false;
            }
        }

        private static void RecordFailure(string operation, Exception error)
        {
            // Do not send listener failures back through the logger or format a failed payload again.
            Interlocked.Increment(ref _failures);
            Volatile.Write(ref _lastError, operation + ": " + error.GetType().FullName);
        }

        internal static object CreateState() => new
        {
            attached = Volatile.Read(ref _attached) != 0,
            requestedMode = ConsoleMode.Value,
            modeDetail = Volatile.Read(ref _modeDetail),
            desktopActive = DesktopActive,
            failures = Interlocked.Read(ref _failures),
            lastError = Volatile.Read(ref _lastError)
        };

        private static void OnProcessExit(object sender, EventArgs args) => Shutdown();

        internal static void Shutdown()
        {
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            try { Instance.Dispose(); }
            finally
            {
                try { ConsoleHost.Stop(); }
                catch (Exception error) { RecordFailure("stop console host", error); }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _attached, 0) == 0)
                return;
            try { Logger.Listeners.Remove(this); }
            catch (Exception error) { RecordFailure("detach shared console", error); }
        }

        internal sealed class OutputWriter : TextWriter
        {
            private const int MaxPendingChars = 1024;
            // Single-character writes are joined per line so they do not each take a history entry.
            [ThreadStatic] private static StringBuilder _pendingChars;

            public override Encoding Encoding => Encoding.UTF8;

            // This route never calls Console.Out or BepInEx: both can lead back to this writer.
            public override void Write(char value)
            {
                var pending = _pendingChars ??= new StringBuilder();
                pending.Append(value);
                if (value == '\n' || pending.Length >= MaxPendingChars)
                    Write("");
            }
            public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
            public override void Write(ReadOnlySpan<char> buffer) => Write(buffer.ToString());
            public override void Write(string value)
            {
                var pending = _pendingChars;
                if (pending?.Length > 0)
                {
                    value = pending + value;
                    pending.Clear();
                }
                if (!string.IsNullOrEmpty(value))
                    Append("Info", "Console", value);
            }
            public override void WriteLine(string value) => Write(value + NewLine);
            public override void WriteLine(ReadOnlySpan<char> buffer) => WriteLine(buffer.ToString());
            public override void WriteLine(char value) => WriteLine(value.ToString());
            public override void WriteLine(char[] buffer) => WriteLine(buffer == null ? "" : new string(buffer));
            public override void WriteLine(char[] buffer, int index, int count) => WriteLine(new string(buffer, index, count));
            public override void WriteLine() => Write(NewLine);
            public override void Flush() => Write("");
        }
    }
}
