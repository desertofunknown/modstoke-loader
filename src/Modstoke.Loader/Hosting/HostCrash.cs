// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Modstoke.Diagnostics;
using Modstoke.Sons;
using UnityEngine;

namespace Modstoke.Loader
{
    /// <summary>
    /// Writes Runtime/crash-&lt;utc&gt;.json when an unhandled managed exception reaches the AppDomain.
    /// Unity exceptions and asserts are kept only as context for that report.
    /// </summary>
    internal static class HostCrash
    {
        internal const int TerminatingExitCode = 70;
        private const int UnityErrorLimit = 8;
        private const int UnityErrorLength = 1024;
        private const int LogLines = 200;
        private const int KeptReports = 10;

        private static readonly object UnityErrorsSync = new object();
        private static readonly Queue<string> UnityErrors = new Queue<string>();
        private static int _subscribed;
        private static int _handled;

        internal static bool Crashed => Volatile.Read(ref _handled) != 0;

        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        }

        // Called from Unity's threaded log callback: no IO, no logging, never throws.
        internal static void ObserveUnityLog(string message, string stackTrace, LogType type)
        {
            if (Volatile.Read(ref _subscribed) == 0 || (type != LogType.Exception && type != LogType.Assert))
                return;
            try
            {
                var text = FirstLines(message, 1);
                var stack = FirstLines(stackTrace, 3);
                if (stack.Length > 0)
                    text += "\n" + stack;
                if (text.Length > UnityErrorLength)
                    text = text[..UnityErrorLength];
                lock (UnityErrorsSync)
                {
                    if (UnityErrors.Count == UnityErrorLimit)
                        UnityErrors.Dequeue();
                    UnityErrors.Enqueue(text);
                }
            }
            catch (Exception)
            {
                // Context only.
            }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (!Host.Enabled || Interlocked.Exchange(ref _handled, 1) != 0)
                return;
            try
            {
                var exception = e.ExceptionObject as Exception;
                var now = DateTimeOffset.UtcNow;
                var path = LoaderPaths.CrashReportPath(now);
                var assembly = FailingAssembly(exception);
                var mod = FindMod(assembly);
                try
                {
                    var report = new
                    {
                        schema = 1,
                        at = HostOutput.Utc(now),
                        state = SafeState(),
                        uptimeSeconds = Math.Round((now - Host.StartedUtc).TotalSeconds, 1),
                        loader = new { version = RuntimeEntryPoint.Version },
                        game = Game(),
                        exception = new
                        {
                            type = exception?.GetType().FullName ?? e.ExceptionObject?.GetType().FullName,
                            message = exception?.Message,
                            text = exception?.ToString() ?? e.ExceptionObject?.ToString()
                        },
                        module = assembly?.GetName().Name,
                        mod = mod == null ? null : new { id = mod.Id, name = mod.Name, loader = mod.Loader },
                        unityErrors = UnityErrorSnapshot(),
                        log = RecentLog()
                    };
                    HostOutput.WriteFileAtomic(path, JsonSerializer.SerializeToUtf8Bytes(report,
                        new JsonSerializerOptions(HostOutput.Json)
                        {
                            WriteIndented = true,
                            DefaultIgnoreCondition = JsonIgnoreCondition.Never
                        }));
                    Prune();
                    Log("Unhandled exception. Crash report: " + path);
                }
                catch (Exception error)
                {
                    path = null;
                    Log("Crash report could not be written: " + error.Message);
                }

                HostEvents.Emit("crash", new
                {
                    file = path,
                    exceptionType = exception?.GetType().FullName,
                    message = exception?.Message,
                    mod = mod?.Id
                });
                HostEvents.Flush(1000);
                HostOutput.Flush(1000);
                HostStatus.WriteCrashedNow();
            }
            catch (Exception)
            {
                // Never add a second failure to the one taking the process down.
            }
            if (e.IsTerminating)
                Terminate(TerminatingExitCode);
        }

        /// <summary>Ends the process at once with the given exit code; Unity's shutdown does not run.</summary>
        internal static void Terminate(int exitCode)
        {
            try
            {
                TerminateProcess(GetCurrentProcess(), (uint)exitCode);
            }
            catch (Exception)
            {
                Environment.Exit(exitCode);
            }
        }

        // The first frame outside the runtime, Unity, the interop layer and the Loader itself.
        private static Assembly FailingAssembly(Exception exception)
        {
            if (exception == null)
                return null;
            try
            {
                var frames = new StackTrace(exception).GetFrames();
                foreach (var frame in frames)
                {
                    var assembly = frame?.GetMethod()?.Module.Assembly;
                    if (assembly != null && !assembly.IsDynamic && !IsPlatform(assembly))
                        return assembly;
                }
                return exception.TargetSite?.Module.Assembly;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool IsPlatform(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            return name == "mscorlib" || name == "netstandard" || name == "System" || name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name == "0Harmony"
                || name.StartsWith("MonoMod.", StringComparison.Ordinal)
                || name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("Il2Cpp", StringComparison.Ordinal)
                || name == typeof(HostCrash).Assembly.GetName().Name;
        }

        private static HostModLoad FindMod(Assembly assembly)
        {
            try { return assembly == null ? null : HostMods.FindByAssembly(assembly.Location); }
            catch (Exception) { return null; }
        }

        private static string SafeState()
        {
            try { return HostStatus.State; }
            catch (Exception) { return null; }
        }

        private static object Game()
        {
            try
            {
                var environment = GameRuntime.Environment;
                return new
                {
                    version = environment.SonsGameVersion,
                    unity = environment.UnityVersion,
                    executable = environment.GameExecutableName,
                    dedicatedServer = Host.IsServer
                };
            }
            catch (Exception)
            {
                return new { dedicatedServer = Host.IsServer };
            }
        }

        private static string[] UnityErrorSnapshot()
        {
            lock (UnityErrorsSync)
                return UnityErrors.ToArray();
        }

        private static object[] RecentLog()
        {
            try
            {
                var last = ConsoleHub.Read(long.MaxValue, 1).LastId;
                return ConsoleHub.Read(Math.Max(0, last - LogLines), LogLines).Entries
                    .Select(entry => (object)new
                    {
                        ts = HostOutput.Utc(entry.Timestamp),
                        level = entry.Level,
                        source = entry.Source,
                        message = entry.Message
                    })
                    .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<object>();
            }
        }

        private static void Prune()
        {
            try
            {
                var reports = Directory.GetFiles(LoaderPaths.RuntimeDirectory, "crash-*.json")
                    .Where(file => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(file => Path.GetFileName(file), StringComparer.Ordinal)
                    .Skip(KeptReports);
                foreach (var report in reports)
                    File.Delete(report);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Old reports are removed on the next crash.
            }
        }

        private static string FirstLines(string text, int count)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            var lines = text.Split('\n', count + 1);
            return string.Join("\n", lines.Take(count).Select(line => line.TrimEnd('\r')));
        }

        private static void Log(string message)
        {
            try { RuntimeEntryPoint.LogSource?.LogError(message); }
            catch (Exception) { }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    }
}
