// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Host event stream: each event goes to stdout and TCP through HostOutput and is
// appended to Runtime/events.ndjson by one background writer.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    internal static class HostEvents
    {
        private const int QueueLimit = 4096;
        private const int MaxLineBytes = 1024 * 1024;
        private const long RotateBytes = 4L * 1024 * 1024;
        private const int MaxChatChars = 512;

        private static readonly UTF8Encoding Utf8 = new(false);
        private static readonly byte[] NewLine = { (byte)'\n' };
        private static readonly object Gate = new();
        private static readonly Queue<string> Lines = new();
        private static readonly Dictionary<Guid, (string Name, ulong SteamId)> Joined = new();
        private static Thread _writer;
        private static int _writing;
        private static bool _stopping;
        private static long _dropped;
        private static int _reportedDrop;
        private static int _reportedWriteFailure;
        private static int _reportedRotateFailure;
        private static FileStream _file;
        private static long _length;
        private static bool _subscribed;

        internal static long Dropped => Interlocked.Read(ref _dropped);

        internal static void Initialize()
        {
            if (!Host.Enabled)
                return;

            lock (Gate)
            {
                if (_writer == null)
                {
                    _stopping = false;
                    _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Modstoke host events" };
                    _writer.Start();
                }
            }

            if (!Host.IsServer || _subscribed)
                return;
            ServerSessions.Opened += OnSessionSeen;
            ServerSessions.RegistrationChanged += OnSessionSeen;
            ServerSessions.Closed += OnSessionClosed;
            ServerChat.Forwarded = OnChat;
            _subscribed = true;
        }

        internal static void Shutdown()
        {
            if (_subscribed)
            {
                ServerSessions.Opened -= OnSessionSeen;
                ServerSessions.RegistrationChanged -= OnSessionSeen;
                ServerSessions.Closed -= OnSessionClosed;
                ServerChat.Forwarded = null;
                _subscribed = false;
            }

            Flush(1000);
            lock (Gate)
            {
                _stopping = true;
                Monitor.PulseAll(Gate);
            }
        }

        internal static void Emit(string type, object fields = null)
        {
            if (!Host.Enabled || string.IsNullOrEmpty(type))
                return;
            try
            {
                var line = Format(type, fields);
                Enqueue(line);
                HostOutput.Broadcast(line);
            }
            catch (Exception error)
            {
                try
                {
                    RuntimeEntryPoint.LogSource?.LogWarning("Host event '" + type + "' was not written: "
                        + error.GetType().FullName + ": " + error.Message);
                }
                catch (Exception) { }
            }
        }

        internal static bool Flush(int timeoutMs)
        {
            var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
            lock (Gate)
            {
                while (Lines.Count > 0 || _writing > 0)
                {
                    var remaining = deadline - Environment.TickCount64;
                    if (_writer == null || remaining <= 0)
                        return false;
                    Monitor.Wait(Gate, (int)Math.Min(remaining, int.MaxValue));
                }
                return true;
            }
        }

        private static string Format(string type, object fields)
        {
            var now = HostOutput.Utc(DateTimeOffset.UtcNow);
            var node = new JsonObject { ["event"] = type, ["ts"] = now };
            if (fields != null)
            {
                foreach (var (key, value) in HostOutput.ToObject(fields))
                {
                    // Reply keys stay out so an event can never be read as a reply.
                    if (key is "event" or "ts" or "id" or "ok" or "command")
                        continue;
                    node[key] = value?.DeepClone();
                }
            }

            var line = node.ToJsonString(HostOutput.Json);
            if (Utf8.GetByteCount(line) <= MaxLineBytes)
                return line;
            return new JsonObject { ["event"] = type, ["ts"] = now, ["truncated"] = true }.ToJsonString(HostOutput.Json);
        }

        private static void Enqueue(string line)
        {
            lock (Gate)
            {
                if (Lines.Count >= QueueLimit)
                {
                    Lines.Dequeue();
                    Interlocked.Increment(ref _dropped);
                    if (Interlocked.Exchange(ref _reportedDrop, 1) == 0)
                        RuntimeEntryPoint.LogSource?.LogWarning("The host event file is behind; the oldest events are being dropped.");
                }
                Lines.Enqueue(line);
                Monitor.PulseAll(Gate);
            }
        }

        private static void WriteLoop()
        {
            while (true)
            {
                string[] batch;
                lock (Gate)
                {
                    while (Lines.Count == 0 && !_stopping)
                        Monitor.Wait(Gate);
                    if (Lines.Count == 0)
                        break;
                    batch = Lines.ToArray();
                    Lines.Clear();
                    _writing = batch.Length;
                }

                Write(batch);

                lock (Gate)
                {
                    _writing = 0;
                    Monitor.PulseAll(Gate);
                }
            }

            CloseFile();
            lock (Gate)
            {
                _writer = null;
                Monitor.PulseAll(Gate);
            }
        }

        private static void Write(string[] batch)
        {
            try
            {
                foreach (var line in batch)
                {
                    var bytes = Utf8.GetBytes(line);
                    var file = OpenFile();
                    if (_length > 0 && _length + bytes.Length + 1 > RotateBytes)
                        file = Rotate();
                    file.Write(bytes);
                    file.Write(NewLine);
                    _length += bytes.Length + 1;
                }
                _file?.Flush();
            }
            catch (Exception error)
            {
                CloseFile();
                if (Interlocked.Exchange(ref _reportedWriteFailure, 1) == 0)
                {
                    try
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning("Writing " + LoaderPaths.HostEventsPath + " failed: "
                            + error.GetType().FullName + ": " + error.Message);
                    }
                    catch (Exception) { }
                }
            }
        }

        // If the old file cannot be moved aside, the current one starts over so events keep being written.
        private static FileStream Rotate()
        {
            CloseFile();
            var path = LoaderPaths.HostEventsPath;
            try
            {
                File.Move(path, LoaderPaths.HostEventsPreviousPath, true);
                return OpenFile();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (Interlocked.Exchange(ref _reportedRotateFailure, 1) == 0)
                {
                    try
                    {
                        RuntimeEntryPoint.LogSource?.LogWarning("Rotating " + path + " failed, so it was cleared: "
                            + error.Message);
                    }
                    catch (Exception) { }
                }
            }
            _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _length = 0;
            return _file;
        }

        private static FileStream OpenFile()
        {
            if (_file != null)
                return _file;
            var path = LoaderPaths.HostEventsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _length = _file.Length;
            return _file;
        }

        private static void CloseFile()
        {
            var file = _file;
            _file = null;
            _length = 0;
            try
            {
                file?.Dispose();
            }
            catch (Exception) { }
        }

        private static void OnSessionSeen(ServerSession session)
        {
            if (session == null || Joined.ContainsKey(session.SessionId))
                return;
            if (!ServerSessions.TryReadIdentity(session, out var identity, out _) || identity.GameSteamId == 0)
                return;

            Joined[session.SessionId] = (identity.DisplayName, identity.GameSteamId);
            Emit("join", new
            {
                sessionId = session.SessionId.ToString(),
                name = identity.DisplayName,
                steamId = identity.GameSteamId.ToString()
            });
        }

        private static void OnSessionClosed(ServerSession session)
        {
            if (session == null || !Joined.Remove(session.SessionId, out var player))
                return;
            Emit("leave", new
            {
                sessionId = session.SessionId.ToString(),
                name = player.Name,
                steamId = player.SteamId.ToString()
            });
        }

        private static void OnChat(ChatRequest request)
        {
            // Server-local events carry no connection; on a dedicated server they are not player chat.
            if (request == null || request.SourceConnectionId == null)
                return;

            var session = request.SourceSession;
            string name = null;
            var steamId = request.RegisteredSteamId ?? 0;
            if (session != null && Joined.TryGetValue(session.SessionId, out var player))
            {
                name = player.Name;
                steamId = player.SteamId;
            }

            Emit("chat", new
            {
                channel = request.Origin == ChatRequestOrigin.ClientCommand ? "command" : "game",
                sessionId = session?.SessionId.ToString(),
                name,
                steamId = steamId == 0 ? null : steamId.ToString(),
                text = Bound(request.Message, MaxChatChars)
            });
        }

        private static string Bound(string text, int limit)
        {
            if (text == null || text.Length <= limit)
                return text;
            var length = char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit;
            return text.Substring(0, length);
        }
    }
}
