// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned console output redirect for early IL2CPP preloader logging.
// This preserves BepInEx/RedLoader behavior without calling the upstream
// ConsoleSetOutFix runtime patch.

using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace OpenACAI.Endnight.Loader.Bootstrap
{
    internal static class OpenACAIConsoleOutputRedirector
    {
        internal static OpenACAIConsoleOutputRedirectState Apply(Func<string, string, Type> loadRequiredType, Action<string> trace)
        {
            var state = new OpenACAIConsoleOutputRedirectState
            {
                OpenAcaiOwned = true,
                BepInExConsoleSetOutFixUsed = false,
                ConsoleOutTypeBefore = Console.Out?.GetType().FullName ?? "",
                RuntimeMajor = Environment.Version.Major,
                FrameworkDescription = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
            };

            var logSource = CreateConsoleLogSource(loadRequiredType);
            state.ConsoleLogSourceCreated = logSource != null;
            state.ConsoleLogSourceType = logSource?.GetType().FullName ?? "";

            var existing = Console.Out as OpenACAIConsoleLoggedTextWriter;
            if (existing != null)
            {
                existing.LogSource = logSource;
                state.WriterInstalled = true;
                state.WriterReused = true;
                state.ConsoleOutTypeAfter = Console.Out?.GetType().FullName ?? "";
                state.Detail = "OpenACAI console output redirect was already installed; refreshed the console log source.";
                trace?.Invoke("OpenACAI console output redirect initialized: " + state.Detail);
                return state;
            }

            var writer = new OpenACAIConsoleLoggedTextWriter
            {
                Parent = Console.Out ?? TextWriter.Null,
                LogSource = logSource
            };

            Console.SetOut(writer);
            state.WriterInstallAttempted = true;
            state.WriterInstalled = ContainsWriter(Console.Out, writer);
            state.WriterWrappedByRuntime = state.WriterInstalled && !ReferenceEquals(Console.Out, writer);
            state.ConsoleOutTypeAfter = Console.Out?.GetType().FullName ?? "";

            state.SetOutPatchSkipped = Environment.Version.Major >= 10;
            state.SetOutPatchSkipReason = state.SetOutPatchSkipped
                ? "OpenACAI does not detour Console.SetOut on .NET 10 or newer; early framework-method detours are gated out."
                : "OpenACAI bootstrap does not patch Console.SetOut in this runtime build.";
            state.Detail = "OpenACAI installed a console logging TextWriter"
                + (state.ConsoleLogSourceCreated ? " with a compatibility Console log source." : " without a compatibility Console log source.");
            trace?.Invoke("OpenACAI console output redirect initialized: " + state.Detail);
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
            if (current == null || expected == null)
            {
                return false;
            }

            if (ReferenceEquals(current, expected))
            {
                return true;
            }

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

    internal sealed class OpenACAIConsoleOutputRedirectState
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

    internal sealed class OpenACAIConsoleLoggedTextWriter : TextWriter
    {
        [ThreadStatic]
        private static bool _logging;

        public override Encoding Encoding => Encoding.UTF8;

        internal TextWriter Parent { get; set; } = TextWriter.Null;

        internal object LogSource { get; set; }

        public override void Flush()
        {
            Parent?.Flush();
        }

        public override void Write(char value)
        {
            Write(value.ToString());
        }

        public override void Write(char[] buffer, int index, int count)
        {
            Write(buffer == null ? null : new string(buffer, index, count));
        }

        public override void Write(string value)
        {
            Log(value);
            Parent?.Write(value);
        }

        public override void WriteLine()
        {
            WriteLine("");
        }

        public override void WriteLine(string value)
        {
            Log(value);
            Parent?.WriteLine(value);
        }

        private void Log(string value)
        {
            if (string.IsNullOrEmpty(value) || LogSource == null || _logging)
            {
                return;
            }

            try
            {
                _logging = true;
                var method = LogSource.GetType().GetMethod(
                    "LogInfo",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(object) },
                    null);
                method?.Invoke(LogSource, new object[] { value });
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
