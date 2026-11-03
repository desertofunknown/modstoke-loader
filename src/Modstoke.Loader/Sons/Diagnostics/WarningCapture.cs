// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using UnityEngine;

namespace Modstoke.Loader
{
    internal static partial class WarningCapture
    {
        private static readonly object Gate = new object();
        private static BlockingCollection<object> _events;
        private static Thread _writer;
        private static string _directory;
        private static uint _mainThread;
        private static int _active;
        private static long _sequence;
        private static long _dropped;
        private static long _captureErrors;
        private static string _writerError;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        internal static void Initialize(Action<string> warning)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--modstoke-warning-capture");
            if (index < 0)
                return;
            try
            {
                if (index + 1 >= args.Length)
                    throw new InvalidDataException("Missing warning capture directory.");
                string root = Path.GetFullPath(Path.Combine(LoaderPaths.UserDataDirectory, "WarningCaptures"));
                string directory = LoaderPaths.MapMovedLegacyPath(Path.GetFullPath(args[index + 1]));
                if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !Directory.Exists(directory))
                    throw new InvalidDataException("Warning capture must use an existing session under " + root);
                // A request can be consumed by only one game process, even if Steam retries a launch.
                File.Move(Path.Combine(directory, "request.json"), Path.Combine(directory, "accepted.json"));
                _directory = directory;
                var stream = new StreamWriter(new FileStream(Path.Combine(directory, "warnings.jsonl"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
                _events = new BlockingCollection<object>(256);
                _writer = new Thread(() => WriteEvents(stream)) { IsBackground = true, Name = "Modstoke warning capture" };
                Volatile.Write(ref _active, 1);
                _writer.Start();
                AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop("process-exit");
                Enqueue(new { kind = "started", utc = DateTime.UtcNow, processId = Environment.ProcessId,
                    stopwatchFrequency = Stopwatch.Frequency,
                    note = "Observations only. Player.log remains authoritative through native shutdown. Native stacks may be incomplete or unsymbolized." });
                Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.Full);
                Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.Full);
                InstallNativeContexts();
            }
            catch (Exception error)
            {
                Enqueue(new { kind = "setup-error", utc = DateTime.UtcNow, error = error.ToString() });
                warning("Warning capture setup: " + error.Message);
            }
        }

        internal static void Record(string message, string unityStack, LogType type)
        {
            if (Volatile.Read(ref _active) == 0 || string.IsNullOrEmpty(message) || !IsTarget(message))
                return;
            try
            {
                uint thread = GetCurrentThreadId();
                object[] nativeStack = CaptureNativeStack();
                var nativeContext = CaptureCurrentContext(message, thread == Volatile.Read(ref _mainThread));
                Enqueue(new { kind = "warning", sequence = Interlocked.Increment(ref _sequence),
                    utc = DateTime.UtcNow, ticks = Stopwatch.GetTimestamp(), nativeThread = thread,
                    type = type.ToString(), message, unityStack, nativeContext, nativeStack });
            }
            catch
            {
                Interlocked.Increment(ref _captureErrors);
            }
        }

        internal static void MarkMainThread()
        {
            if (Volatile.Read(ref _active) == 0) return;
            Volatile.Write(ref _mainThread, GetCurrentThreadId());
            Enqueue(new { kind = "unity-main-thread", nativeThread = _mainThread });
        }

        private static bool IsTarget(string message) =>
            message.IndexOf("GameSettingsManager", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("ClosestPoint can only", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("create a Convex Mesh", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("JobTempAlloc", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("leaked allocation", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Enqueue(object item)
        {
            lock (Gate)
            {
                if (Volatile.Read(ref _active) != 0 && !_events.TryAdd(item))
                    Interlocked.Increment(ref _dropped);
            }
        }

        internal static void Stop(string reason)
        {
            lock (Gate)
            {
                if (Interlocked.Exchange(ref _active, 0) == 0)
                    return;
                _events.CompleteAdding();
            }
            // Do not detach live native trampolines during shutdown. They remain forwarding-only.
            bool drained = _writer != null && _writer.Join(TimeSpan.FromSeconds(3));
            try
            {
                File.WriteAllText(Path.Combine(_directory, "managed-stop.json"), JsonSerializer.Serialize(new {
                    reason, utc = DateTime.UtcNow, writerDrained = drained, observed = Interlocked.Read(ref _sequence),
                    dropped = Interlocked.Read(ref _dropped), captureErrors = Interlocked.Read(ref _captureErrors),
                    writerError = Volatile.Read(ref _writerError),
                    note = "Managed observation ended. Read Player.log for subsequent native allocation diagnostics."
                }));
            }
            catch { }
        }

        private static void WriteEvents(StreamWriter stream)
        {
            try
            {
                using (stream)
                {
                    while (!_events.IsCompleted)
                    {
                        if (_events.TryTake(out object item, 1000))
                            stream.WriteLine(JsonSerializer.Serialize(item, JsonOptions));
                        stream.Flush();
                    }
                }
            }
            catch (Exception error)
            {
                Volatile.Write(ref _writerError, error.ToString());
                // Never report writer failures through Unity's recursive logging path.
            }
        }

        private static object[] CaptureNativeStack()
        {
            var frames = new IntPtr[96];
            int count = RtlCaptureStackBackTrace(0, (uint)frames.Length, frames, IntPtr.Zero);
            var result = new object[count];
            StringBuilder buffer = null;
            for (int i = 0; i < count; i++)
            {
                string path = null;
                string rva = null;
                if (GetModuleHandleEx(0x4 | 0x2, frames[i], out IntPtr module))
                {
                    buffer ??= new StringBuilder(1024);
                    buffer.Clear();
                    if (GetModuleFileName(module, buffer, buffer.Capacity) != 0)
                        path = buffer.ToString();
                    rva = "0x" + (frames[i].ToInt64() - module.ToInt64()).ToString("X");
                }
                result[i] = new { address = "0x" + frames[i].ToInt64().ToString("X"), module = path, rva };
            }
            return result;
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", EntryPoint = "RtlCaptureStackBackTrace")]
        private static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, [Out] IntPtr[] frames, IntPtr hash);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetModuleHandleEx(uint flags, IntPtr address, out IntPtr module);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleFileNameW")]
        private static extern uint GetModuleFileName(IntPtr module, StringBuilder path, int capacity);
    }
}
