// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using BepInEx;
using Microsoft.Win32.SafeHandles;
using Modstoke.Diagnostics;

namespace Modstoke.Loader
{
    /// <summary>
    /// Writes "@modstoke " machine lines for the hosting agent. Console.Out is redirected into the log, so
    /// lines go to the original standard output from one writer thread; the game thread only queues them.
    /// </summary>
    internal static class HostOutput
    {
        internal const string Prefix = "@modstoke ";
        internal const int Protocol = 1;
        private const int QueueLimit = 2048;
        private const int LineLimit = 1024 * 1024;
        private const string OriginalOutputKey = "Modstoke.OriginalStandardOutput";
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly object Sync = new object();
        private static readonly LinkedList<(string Line, bool Event)> Queue = new LinkedList<(string, bool)>();
        private static readonly ManualResetEventSlim Idle = new ManualResetEventSlim(true);
        private static Thread _writer;
        private static int _inFlight;
        private static long _dropped;
        private static bool _tooLargeReported;
        private static bool _writeFailureReported;
        private static volatile bool _disabled;
        private static TextWriter _standardWriter;
        private static Stream _standardStream;

        internal static JsonSerializerOptions Json { get; } = CreateOptions();

        internal static int PendingCount
        {
            get { lock (Sync) return Queue.Count + _inFlight; }
        }

        internal static long DroppedEvents => Interlocked.Read(ref _dropped);

        internal static string Utc(DateTimeOffset value) =>
            value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        internal static JsonObject ToObject(object value)
        {
            if (value == null)
                return new JsonObject();
            if (value is JsonObject json)
                return json;
            var node = JsonSerializer.SerializeToNode(value, value.GetType(), Json);
            return node as JsonObject ?? new JsonObject { ["value"] = node };
        }

        internal static bool WantsReply(ConsoleSubmission s) => s.RequestId != null || s.Json;

        internal static void Reply(ConsoleSubmission request, bool ok, object body = null) =>
            Route(request.ReplyChannel, request.Origin, Envelope(request.RequestId, request.Command, ok, body));

        internal static void Reply(string requestId, int channel, string command, bool ok, object body) =>
            Route(channel, ConsoleOrigin.ServerConsole, Envelope(requestId, command, ok, body));

        internal static void Broadcast(string json)
        {
            if (string.IsNullOrEmpty(json))
                return;
            var line = Prefix + json;
            if (TooLarge(line))
            {
                if (!_tooLargeReported)
                {
                    _tooLargeReported = true;
                    RuntimeEntryPoint.LogSource?.LogWarning("A host event over 1 MiB was not written.");
                }
                return;
            }
            if (Host.IsServer)
                Enqueue(line, true);
            HostTcp.Broadcast(line);
        }

        internal static bool Flush(int timeoutMs)
        {
            if (_writer == null)
                return true;
            try
            {
                return Idle.Wait(Math.Max(0, timeoutMs));
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        internal static void WriteFileAtomic(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                if (File.Exists(path))
                    File.Replace(temporary, path, null);
                else
                    File.Move(temporary, path);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (Exception) { /* Preserve the original write failure. */ }
            }
        }

        internal static void Initialize()
        {
            if (Host.IsServer)
                lock (Sync)
                    StartWriter();
        }

        private static string Envelope(string requestId, string command, bool ok, object body)
        {
            var reply = new JsonObject
            {
                ["id"] = requestId,
                ["ok"] = ok,
                ["command"] = command ?? ""
            };
            foreach (var (key, value) in ToObject(body))
                if (key is not ("id" or "ok" or "command"))
                    reply[key] = value?.DeepClone();
            var line = Prefix + reply.ToJsonString(Json);
            if (!TooLarge(line))
                return line;
            return Prefix + new JsonObject
            {
                ["id"] = requestId,
                ["ok"] = false,
                ["command"] = command ?? "",
                ["error"] = "too-large",
                ["detail"] = "The reply was larger than 1 MiB."
            }.ToJsonString(Json);
        }

        private static bool TooLarge(string line) =>
            line.Length > LineLimit / 3 && Utf8.GetByteCount(line) > LineLimit;

        private static void Route(int channel, ConsoleOrigin origin, string line)
        {
            if (channel > 0)
                HostTcp.Send(channel, line);
            else if (Host.IsServer && origin == ConsoleOrigin.ServerConsole)
                Enqueue(line, false);
            else
                ConsoleHub.Append("Info", "Host", line);
        }

        private static void Enqueue(string line, bool isEvent)
        {
            if (_disabled)
                return;
            lock (Sync)
            {
                StartWriter();
                // Replies are never dropped; the oldest event makes room, or the new event is lost.
                if (Queue.Count >= QueueLimit)
                {
                    var oldest = Queue.First;
                    while (oldest != null && !oldest.Value.Event)
                        oldest = oldest.Next;
                    if (oldest == null && isEvent)
                    {
                        Interlocked.Increment(ref _dropped);
                        return;
                    }
                    if (oldest != null)
                    {
                        Queue.Remove(oldest);
                        Interlocked.Increment(ref _dropped);
                    }
                }
                Queue.AddLast((line, isEvent));
                Idle.Reset();
                Monitor.Pulse(Sync);
            }
        }

        private static void StartWriter()
        {
            if (_writer != null)
                return;
            _writer = new Thread(Write) { IsBackground = true, Name = "Modstoke host output" };
            _writer.Start();
        }

        private static void Write()
        {
            while (true)
            {
                string line;
                lock (Sync)
                {
                    while (Queue.Count == 0)
                    {
                        _inFlight = 0;
                        Idle.Set();
                        Monitor.Wait(Sync);
                    }
                    line = Queue.First.Value.Line;
                    Queue.RemoveFirst();
                    _inFlight = 1;
                }
                if (!_disabled)
                    WriteLine(line);
            }
        }

        private static void WriteLine(string line)
        {
            try
            {
                if (_standardWriter == null && _standardStream == null)
                    ResolveTarget();
                // One write per line, so other writers on the same handle cannot land inside it.
                var stream = _standardStream ?? (_standardWriter as StreamWriter)?.BaseStream;
                if (stream == null)
                {
                    lock (_standardWriter)
                    {
                        _standardWriter.Write(line + "\n");
                        _standardWriter.Flush();
                    }
                    return;
                }
                var bytes = Utf8.GetBytes(line + "\n");
                lock (stream)
                {
                    _standardWriter?.Flush();
                    stream.Write(bytes);
                    stream.Flush();
                }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The agent's pipe is gone; a broken pipe must not take the server down with it.
                _disabled = true;
                RuntimeEntryPoint.LogSource?.LogWarning("Host output to standard output stopped: " + error.Message);
            }
            catch (Exception error)
            {
                if (_writeFailureReported)
                    return;
                _writeFailureReported = true;
                RuntimeEntryPoint.LogSource?.LogWarning("A host output line was not written: " + error.Message);
            }
        }

        // The preloader records the stdout handle the process started with before the log console replaces it.
        // Without it, the console driver keeps that handle as its standard output writer.
        private static void ResolveTarget()
        {
            var original = AppDomain.CurrentDomain.GetData(OriginalOutputKey) is IntPtr handle ? handle : IntPtr.Zero;
            if (original != IntPtr.Zero && original != new IntPtr(-1))
            {
                try
                {
                    _standardStream = new FileStream(new SafeFileHandle(original, ownsHandle: false), FileAccess.Write, 1);
                    return;
                }
                catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
                {
                    // Not a writable handle any more; use what the console driver kept.
                }
            }
            try
            {
                _standardWriter = OriginalStandardOutput();
            }
            catch (Exception)
            {
                // No BepInEx console manager; fall back to the process's standard output.
            }
            if (_standardWriter == null)
                _standardStream = Console.OpenStandardOutput();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TextWriter OriginalStandardOutput()
        {
            var standard = ConsoleManager.ConsoleActive ? ConsoleManager.StandardOutStream : null;
            return standard != null && !ReferenceEquals(standard, TextWriter.Null)
                && !ReferenceEquals(standard, ConsoleManager.ConsoleStream)
                && standard.GetType().Name != "ConsoleLoggedTextWriter" ? standard : null;
        }

        private static JsonSerializerOptions CreateOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                MaxDepth = 32
            };
            options.MakeReadOnly();
            return options;
        }
    }
}
