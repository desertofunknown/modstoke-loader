// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Modstoke.Diagnostics;

namespace Modstoke.Loader
{
    /// <summary>
    /// Loopback line protocol for a hosting agent that sits outside Wine and cannot share the server's stdin.
    /// Off unless MODSTOKE_HOST_PORT and MODSTOKE_HOST_TOKEN are both set on a dedicated server.
    /// </summary>
    internal static class HostTcp
    {
        private const string PortVariable = "MODSTOKE_HOST_PORT";
        private const string TokenVariable = "MODSTOKE_HOST_TOKEN";
        private const int MinTokenLength = 16;
        private const int MaxClients = 2;
        private const int MaxLineBytes = HostLine.MaxLength;
        private const int SendLimit = 1024;
        private const int AuthTimeoutMs = 10000;
        private const int SendTimeoutMs = 10000;
        private const int StopDrainMs = 1000;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        private static readonly object Sync = new object();
        private static readonly List<Client> Clients = new List<Client>();
        private static TcpListener _listener;
        private static Thread _acceptor;
        private static byte[] _tokenHash;
        private static int _port;
        private static int _nextId;
        private static volatile bool _stopped;

        internal static int? Port
        {
            get
            {
                var port = Volatile.Read(ref _port);
                return port == 0 ? null : port;
            }
        }

        internal static int ClientCount
        {
            get { lock (Sync) return Clients.Count(client => client.Authenticated); }
        }

        internal static int PendingCount
        {
            get { lock (Sync) return Clients.Sum(client => client.Queued); }
        }

        internal static void Start()
        {
            if (!Host.IsServer || _acceptor != null)
                return;
            var portText = Environment.GetEnvironmentVariable(PortVariable);
            var token = Environment.GetEnvironmentVariable(TokenVariable);
            var hasPort = !string.IsNullOrWhiteSpace(portText);
            var hasToken = !string.IsNullOrEmpty(token);
            if (!hasPort && !hasToken)
                return;
            if (!hasPort || !hasToken)
            {
                Info("Host TCP is off: set both " + PortVariable + " and " + TokenVariable + " to enable it.");
                return;
            }
            if (!int.TryParse(portText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || port < 1 || port > 65535)
            {
                Warn("Host TCP is off: " + PortVariable + " must be a port from 1 to 65535.");
                return;
            }
            if (token.Length < MinTokenLength)
            {
                Warn("Host TCP is off: " + TokenVariable + " must be at least " + MinTokenLength + " characters.");
                return;
            }

            _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            // Child processes, such as the desktop console and Wine helpers, must not inherit it.
            Environment.SetEnvironmentVariable(TokenVariable, null);
            _stopped = false;
            _acceptor = new Thread(() => Accept(port)) { IsBackground = true, Name = "Modstoke host TCP" };
            _acceptor.Start();
        }

        internal static void Stop()
        {
            _stopped = true;
            TcpListener listener;
            Client[] clients;
            lock (Sync)
            {
                listener = _listener;
                _listener = null;
                clients = Clients.ToArray();
            }
            try { listener?.Stop(); }
            catch (SocketException) { /* Already closed. */ }
            // Lets each sender put out the stop reply and the last events before the sockets close.
            foreach (var client in clients)
                client.Finish("server stopping");
            var deadline = Environment.TickCount64 + StopDrainMs;
            foreach (var client in clients)
            {
                client.WaitSent((int)Math.Max(0, deadline - Environment.TickCount64));
                client.Close("server stopping");
            }
            Volatile.Write(ref _port, 0);
        }

        internal static void Send(int channel, string line)
        {
            if (channel <= 0 || string.IsNullOrEmpty(line))
                return;
            Client target;
            lock (Sync)
                target = Clients.FirstOrDefault(client => client.Id == channel);
            // A reply for a client that already left has nowhere to go.
            target?.Send(line);
        }

        internal static void Broadcast(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;
            Client[] clients;
            lock (Sync)
            {
                if (Clients.Count == 0)
                    return;
                clients = Clients.Where(client => client.Authenticated).ToArray();
            }
            foreach (var client in clients)
                client.Send(line);
        }

        private static void Accept(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try
            {
                listener.Start();
            }
            catch (SocketException error)
            {
                Warn("Host TCP could not listen on 127.0.0.1:" + port + ": " + error.Message);
                return;
            }
            lock (Sync)
            {
                if (_stopped)
                {
                    listener.Stop();
                    return;
                }
                _listener = listener;
            }
            Volatile.Write(ref _port, port);
            Info("Host TCP listening on 127.0.0.1:" + port + ".");

            while (!_stopped)
            {
                TcpClient socket;
                try
                {
                    socket = listener.AcceptTcpClient();
                }
                catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
                catch (SocketException)
                {
                    if (_stopped)
                        return;
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    Admit(socket);
                }
                catch (Exception error)
                {
                    Warn("Host TCP client failed: " + error.Message);
                    try { socket.Dispose(); }
                    catch (Exception) { /* Refused either way. */ }
                }
            }
        }

        private static void Admit(TcpClient socket)
        {
            Client client = null;
            Client replaced = null;
            lock (Sync)
            {
                // Unauthenticated sockets count too, but a new connection replaces the oldest of them, so
                // another local process cannot hold the agent out by reconnecting.
                if (!_stopped && Clients.Count >= MaxClients)
                {
                    replaced = Clients.FirstOrDefault(item => !item.Authenticated);
                    if (replaced != null)
                        Clients.Remove(replaced);
                }
                if (!_stopped && Clients.Count < MaxClients)
                {
                    client = new Client(Interlocked.Increment(ref _nextId), socket);
                    Clients.Add(client);
                }
            }
            replaced?.Close("replaced");
            if (client == null)
            {
                try { socket.Dispose(); }
                catch (Exception) { /* Refused either way. */ }
                return;
            }
            Info("Host TCP client " + client.Id + " connected.");
            try
            {
                client.Start();
            }
            catch (Exception)
            {
                client.Close("error");
                throw;
            }
        }

        private static void Remove(Client client)
        {
            lock (Sync)
                Clients.Remove(client);
        }

        private static bool TokenMatches(string candidate)
        {
            var expected = _tokenHash;
            if (expected == null || candidate == null)
                return false;
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), expected);
        }

        private static string FirstWord(string line)
        {
            var words = (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var index = 0;
            if (index < words.Length && words[index].StartsWith('#'))
                index++;
            if (index < words.Length && string.Equals(words[index], "confirm", StringComparison.OrdinalIgnoreCase))
                index++;
            return index < words.Length ? words[index] : "";
        }

        private static void Info(string message) => RuntimeEntryPoint.LogSource?.LogInfo(message);

        private static void Warn(string message) => RuntimeEntryPoint.LogSource?.LogWarning(message);

        private sealed class Client
        {
            private const int Pending = 0, Accepted = 1, Rejected = 2;
            private readonly TcpClient _socket;
            private readonly NetworkStream _stream;
            private readonly BlockingCollection<string> _outgoing =
                new BlockingCollection<string>(new ConcurrentQueue<string>(), SendLimit);
            private Timer _authTimer;
            private Thread _writer;
            private int _auth;
            private int _closed;
            private volatile string _finishReason;

            internal Client(int id, TcpClient socket)
            {
                Id = id;
                _socket = socket;
                // A peer that stops reading must not keep its slot behind a blocked send.
                socket.SendTimeout = SendTimeoutMs;
                socket.NoDelay = true;
                _stream = socket.GetStream();
            }

            internal int Id { get; }
            internal bool Authenticated => Volatile.Read(ref _auth) == Accepted;
            internal int Queued => _outgoing.Count;

            internal void Start()
            {
                _authTimer = new Timer(_ =>
                {
                    try
                    {
                        Reject("authentication timed out", "Send the host token as the first line within 10 seconds.");
                    }
                    catch (Exception)
                    {
                        Close("error");
                    }
                }, null, AuthTimeoutMs, Timeout.Infinite);
                _writer = new Thread(Write) { IsBackground = true, Name = "Modstoke host TCP send " + Id };
                _writer.Start();
                new Thread(Read) { IsBackground = true, Name = "Modstoke host TCP receive " + Id }.Start();
            }

            internal void WaitSent(int timeoutMs)
            {
                var writer = _writer;
                if (writer != null && writer != Thread.CurrentThread)
                    writer.Join(Math.Max(0, timeoutMs));
            }

            internal void Send(string line)
            {
                try
                {
                    if (_outgoing.TryAdd(line))
                        return;
                }
                catch (InvalidOperationException)
                {
                    return; // Closing.
                }
                Close("send queue full");
            }

            internal void Close(string reason)
            {
                if (Interlocked.Exchange(ref _closed, 1) != 0)
                    return;
                _authTimer?.Dispose();
                try { _outgoing.CompleteAdding(); }
                catch (ObjectDisposedException) { /* Already done. */ }
                try { _socket.Dispose(); }
                catch (Exception) { /* The connection is gone either way. */ }
                Remove(this);
                Info("Host TCP client " + Id + " disconnected (" + reason + ").");
            }

            // Queues a last reply, then lets the sender drain it before the socket closes.
            internal void Finish(string reason)
            {
                _finishReason = reason;
                try { _outgoing.CompleteAdding(); }
                catch (ObjectDisposedException) { /* Already closing. */ }
            }

            private void Reject(string reason, string detail)
            {
                if (Interlocked.CompareExchange(ref _auth, Rejected, Pending) != Pending)
                    return;
                HostOutput.Reply(null, Id, "auth", false, new { error = "unauthorized", detail });
                Finish(reason);
            }

            private void Write()
            {
                var reason = "connection closed";
                try
                {
                    foreach (var line in _outgoing.GetConsumingEnumerable())
                    {
                        var bytes = Encoding.UTF8.GetBytes(line.EndsWith('\n') ? line : line + "\n");
                        _stream.Write(bytes, 0, bytes.Length);
                    }
                    reason = _finishReason ?? reason;
                    try { _socket.Client.Shutdown(SocketShutdown.Send); }
                    catch (Exception) { /* Closing anyway. */ }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException
                    or SocketException or InvalidOperationException)
                {
                    reason = "send failed";
                }
                catch (Exception)
                {
                    reason = "error";
                }
                Close(reason);
            }

            private void Read()
            {
                var buffer = new byte[4096];
                var line = new byte[MaxLineBytes + 1];
                var length = 0;
                try
                {
                    while (_finishReason == null)
                    {
                        var read = _stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0)
                            break;
                        for (var i = 0; i < read && _finishReason == null; i++)
                        {
                            if (buffer[i] != (byte)'\n')
                            {
                                if (length == line.Length)
                                {
                                    HostOutput.Reply(null, Id, "", false, new
                                    {
                                        error = "too-large",
                                        detail = "Lines are limited to " + MaxLineBytes + " bytes."
                                    });
                                    Finish("line too long");
                                    return;
                                }
                                line[length++] = buffer[i];
                                continue;
                            }
                            var count = length > 0 && line[length - 1] == (byte)'\r' ? length - 1 : length;
                            length = 0;
                            if (count > MaxLineBytes)
                            {
                                HostOutput.Reply(null, Id, "", false, new
                                {
                                    error = "too-large",
                                    detail = "Lines are limited to " + MaxLineBytes + " bytes."
                                });
                                Finish("line too long");
                                return;
                            }
                            Handle(line, count);
                        }
                    }
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException
                    or SocketException or InvalidOperationException)
                {
                    // Reset by the peer or closed from this side.
                }
                catch (Exception)
                {
                    Close("error");
                    return;
                }
                if (_finishReason == null)
                    Close("connection closed");
            }

            private void Handle(byte[] bytes, int count)
            {
                string text;
                try
                {
                    text = Utf8.GetString(bytes, 0, count);
                }
                catch (DecoderFallbackException)
                {
                    if (!Authenticated)
                        Reject("authentication failed", "The host token was not accepted.");
                    else
                        HostOutput.Reply(null, Id, "", false, new
                        {
                            error = "invalid-arguments",
                            detail = "Lines must be UTF-8."
                        });
                    return;
                }

                if (!Authenticated)
                {
                    if (!TokenMatches(text))
                    {
                        Reject("authentication failed", "The host token was not accepted.");
                        return;
                    }
                    if (Interlocked.CompareExchange(ref _auth, Accepted, Pending) != Pending)
                        return;
                    _authTimer?.Dispose();
                    HostOutput.Reply(null, Id, "auth", true, new { protocol = HostOutput.Protocol });
                    Info("Host TCP client " + Id + " authenticated.");
                    return;
                }

                if (text.Trim().Length == 0)
                    return;
                if (!HostLine.TryParse(text, out var command, out var confirmed, out var requestId, out var error))
                {
                    HostOutput.Reply(requestId, Id, FirstWord(text), false, new { error, detail = Describe(error) });
                    return;
                }
                if (!ConsoleHub.TrySubmitLine(command, confirmed, ConsoleOrigin.ServerConsole, requestId, true, Id,
                    out var detail, out var reason))
                {
                    HostOutput.Reply(requestId, Id, FirstWord(command), false, new { error = reason, detail });
                }
            }

            private static string Describe(string error) => error switch
            {
                "invalid-id" => "A request id is 1 to 64 letters, digits, '.', '_', ':' or '-'.",
                "too-large" => "Lines are limited to " + MaxLineBytes + " characters.",
                _ => "A command is required."
            };
        }
    }
}
