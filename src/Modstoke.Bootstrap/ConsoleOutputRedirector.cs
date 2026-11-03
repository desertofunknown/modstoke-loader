// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Console output redirect for early IL2CPP preloader logging.
// This preserves BepInEx/RedLoader behavior without calling the upstream
// ConsoleSetOutFix runtime patch.

using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Modstoke.Bootstrap
{
    internal static class ConsoleOutputRedirector
    {
        private static readonly object Sync = new object();
        private static ConsoleLoggedTextWriter _writer;
        private static object _consoleLogSource;

        internal static ConsoleOutputRedirectState Apply(Func<string, string, Type> loadRequiredType, Action<string> trace)
        {
            lock (Sync)
                return ApplyCore(loadRequiredType, trace);
        }

        private static ConsoleOutputRedirectState ApplyCore(Func<string, string, Type> loadRequiredType, Action<string> trace)
        {
            var state = new ConsoleOutputRedirectState
            {
                OpenAcaiOwned = true,
                BepInExConsoleSetOutFixUsed = false,
                ConsoleOutTypeBefore = Console.Out?.GetType().FullName ?? "",
                RuntimeMajor = Environment.Version.Major,
                FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                SetOutPatchSkipped = Environment.Version.Major >= 10,
                SetOutPatchSkipReason = Environment.Version.Major >= 10
                    ? "Modstoke does not detour Console.SetOut on .NET 10 or newer; early framework-method detours are gated out."
                    : "Modstoke bootstrap does not patch Console.SetOut in this runtime build."
            };

            var logSource = _consoleLogSource ??= CreateConsoleLogSource(loadRequiredType);
            state.ConsoleLogSourceCreated = logSource != null;
            state.ConsoleLogSourceType = logSource?.GetType().FullName ?? "";

            // Console.SetOut normally wraps the supplied writer in a synchronized TextWriter.
            var existing = _writer != null && ContainsWriter(Console.Out, _writer) ? _writer : null;
            if (existing != null)
            {
                if (!ReferenceEquals(existing.LogSource, logSource))
                    existing.LogSource = logSource;
                state.WriterInstalled = true;
                state.WriterReused = true;
                state.WriterWrappedByRuntime = !ReferenceEquals(Console.Out, existing);
                state.ConsoleOutTypeAfter = Console.Out?.GetType().FullName ?? "";
                state.Detail = "Modstoke console output redirect was already installed; reused the console log source.";
            }
            else
            {
                var writer = new ConsoleLoggedTextWriter
                {
                    Parent = Console.Out ?? TextWriter.Null,
                    LogSource = logSource
                };

                state.WriterInstallAttempted = true;
                Console.SetOut(writer);
                _writer = writer;
                state.WriterInstalled = ContainsWriter(Console.Out, writer);
                state.WriterWrappedByRuntime = state.WriterInstalled && !ReferenceEquals(Console.Out, writer);
                state.ConsoleOutTypeAfter = Console.Out?.GetType().FullName ?? "";

                state.Detail = "Modstoke installed a console logging TextWriter"
                    + (state.ConsoleLogSourceCreated ? " with a compatibility Console log source." : " without a compatibility Console log source.");
            }

            trace?.Invoke("Modstoke console output redirect initialized: " + state.Detail);
            return state;
        }

        private static object CreateConsoleLogSource(Func<string, string, Type> loadRequiredType)
        {
            if (loadRequiredType == null)
            {
                return null;
            }

            var loggerType = loadRequiredType("BepInEx.Core", "BepInEx.Logging.Logger");
            var method = loggerType.GetMethod(
                "CreateLogSource",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null);
            if (method == null)
            {
                throw new MissingMethodException(loggerType.FullName, "CreateLogSource");
            }

            return method.Invoke(null, new object[] { "Console" });
        }

        private static bool ContainsWriter(TextWriter current, TextWriter expected)
        {
            return ContainsWriter(current, expected, 0);
        }

        private static bool ContainsWriter(object current, TextWriter expected, int depth)
        {
            if (current == null || expected == null || depth > 4)
            {
                return false;
            }

            if (ReferenceEquals(current, expected))
            {
                return true;
            }

            var type = current.GetType();
            while (type != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (!typeof(TextWriter).IsAssignableFrom(field.FieldType))
                    {
                        continue;
                    }

                    object nested;
                    try
                    {
                        nested = field.GetValue(current);
                    }
                    catch
                    {
                        continue;
                    }

                    if (ReferenceEquals(nested, expected) || ContainsWriter(nested, expected, depth + 1))
                    {
                        return true;
                    }
                }

                type = type.BaseType;
            }

            return false;
        }
    }

    internal sealed class ConsoleOutputRedirectState
    {
        internal bool OpenAcaiOwned { get; set; }
        internal bool BepInExConsoleSetOutFixUsed { get; set; }
        internal bool ConsoleLogSourceCreated { get; set; }
        internal string ConsoleLogSourceType { get; set; } = "";
        internal bool WriterInstallAttempted { get; set; }
        internal bool WriterInstalled { get; set; }
        internal bool WriterWrappedByRuntime { get; set; }
        internal bool WriterReused { get; set; }
        internal string ConsoleOutTypeBefore { get; set; } = "";
        internal string ConsoleOutTypeAfter { get; set; } = "";
        internal bool SetOutPatchSkipped { get; set; }
        internal string SetOutPatchSkipReason { get; set; } = "";
        internal int RuntimeMajor { get; set; }
        internal string FrameworkDescription { get; set; } = "";
        internal string Detail { get; set; } = "";
    }

    internal sealed class ConsoleLoggedTextWriter : TextWriter
    {
        private const int MaxPendingChars = 1024;

        [ThreadStatic]
        private static bool _logging;
        [ThreadStatic]
        private static StringBuilder _pendingChars;
        private object _logSource;
        private Action<object> _logInfo;

        public override Encoding Encoding => Encoding.UTF8;

        internal TextWriter Parent { get; set; } = TextWriter.Null;

        internal object LogSource
        {
            get => _logSource;
            set
            {
                Action<object> logInfo = null;
                try
                {
                    var method = value?.GetType().GetMethod("LogInfo",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(object) }, null);
                    if (method != null)
                        logInfo = (Action<object>)method.CreateDelegate(typeof(Action<object>), value);
                }
                catch
                {
                    // An incompatible log source must not prevent forwarding to the original writer.
                }
                _logSource = value;
                Volatile.Write(ref _logInfo, logInfo);
            }
        }

        public override void Flush()
        {
            Parent?.Flush();
        }

        public override void Write(char value)
        {
            Parent?.Write(value);
            // Join character writes into one log event per line instead of one per character.
            var pending = _pendingChars ??= new StringBuilder();
            if (value == '\n' || value == '\r')
            {
                Log(TakePending());
                return;
            }

            pending.Append(value);
            if (pending.Length >= MaxPendingChars)
                Log(TakePending());
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Write(buffer == null ? null : new string(buffer, index, count));
        }

        public override void Write(string value)
        {
            Log(TakePending() + value);
            Parent?.Write(value);
        }

        public override void WriteLine()
        {
            WriteLine("");
        }

        public override void WriteLine(string value)
        {
            Log(TakePending() + value);
            Parent?.WriteLine(value);
        }

        private static string TakePending()
        {
            var pending = _pendingChars;
            if (pending == null || pending.Length == 0)
                return "";
            var text = pending.ToString();
            pending.Clear();
            return text;
        }

        private void Log(string value)
        {
            var logInfo = Volatile.Read(ref _logInfo);
            if (string.IsNullOrEmpty(value) || logInfo == null || _logging)
            {
                return;
            }

            try
            {
                _logging = true;
                logInfo(value);
            }
            catch
            {
                // Console output forwarding must never block preloader startup.
            }
            finally
            {
                _logging = false;
            }
        }
    }
}
