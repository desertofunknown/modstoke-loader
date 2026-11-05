// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Lets server mods publish events and player fields to the dedicated-server host agent.</summary>
    public static class ServerHosting
    {
        private const int MaxTypeLength = 64;
        private const int MaxFields = 32;
        private const int MaxKeyLength = 64;
        private const int MaxStringLength = 1024;
        private const int MaxArrayEntries = 64;
        private const long FailureReportIntervalMs = 60_000;

        // Each event type gets a burst of 100 and then 20 per second, so one noisy mod cannot crowd out the rest.
        private const double EventBurst = 100;
        private const double EventsPerSecond = 20;
        private const int MaxEventTypes = 512;

        private static readonly Regex TypePattern = new("^[a-z][a-z0-9-]*(\\.[a-z0-9-]+)+$",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        private static readonly Dictionary<MethodInfo, long> ReportedFailures = new();
        private static readonly Dictionary<string, (double Tokens, long At)> Buckets = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Command> Commands = new(StringComparer.OrdinalIgnoreCase);
        private static long _rateLimited;

        /// <summary>True when this process reports to a host agent, which is always the case on a dedicated server.</summary>
        public static bool IsActive => Host.Enabled;

        internal static long RateLimited => System.Threading.Interlocked.Read(ref _rateLimited);

        /// <summary>
        /// Adds a host command such as <c>admin.kick</c>. The handler runs on the game thread, must not block, and returns
        /// exactly one reply; outcomes that arrive later go out as events carrying the request id as <c>correlationId</c>.
        /// Returns false when the name is invalid or already taken.
        /// </summary>
        public static bool RegisterCommand(string name, string description, Func<ServerHostRequest, ServerHostReply> handler)
        {
            if (handler == null || name == null || name.Length > MaxTypeLength || !TypePattern.IsMatch(name))
                return false;
            lock (Commands)
            {
                if (Commands.ContainsKey(name))
                    return false;
                Commands[name] = new Command(name, description ?? "", handler);
            }
            return true;
        }

        /// <summary>Removes a command added by <see cref="RegisterCommand"/>, for example when the mod unloads.</summary>
        public static bool UnregisterCommand(string name)
        {
            lock (Commands)
                return name != null && Commands.Remove(name);
        }

        internal static bool HasCommand(string name)
        {
            lock (Commands)
                return name != null && Commands.ContainsKey(name);
        }

        internal static List<(string Name, string Description)> CommandCatalog()
        {
            lock (Commands)
            {
                var list = new List<(string, string)>();
                foreach (var command in Commands.Values)
                    list.Add((command.Name, command.Description));
                return list;
            }
        }

        internal static ServerHostReply Run(ServerHostRequest request)
        {
            Command command;
            lock (Commands)
                Commands.TryGetValue(request.Command, out command);
            if (command == null)
                return ServerHostReply.Fail("unknown-command", "\"" + request.Command + "\" is not a command.");
            try
            {
                return command.Handler(request) ?? ServerHostReply.Fail("failed", "The command gave no reply.");
            }
            catch (Exception error)
            {
                ReportFailure(command.Handler.Method, error);
                return ServerHostReply.Fail("failed", "Command failed: " + error.Message);
            }
        }

        internal static JsonObject CopyFields(IReadOnlyDictionary<string, object> fields)
        {
            var copy = new JsonObject();
            if (fields == null)
                return copy;
            foreach (var (key, value) in fields)
            {
                if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key is "id" or "ok" or "command" or "error" or "detail"
                    || copy.ContainsKey(key) || !TryCopy(value, out var node))
                    continue;
                copy[key] = node;
            }
            return copy;
        }

        private static bool TryTakeToken(string type)
        {
            var now = Environment.TickCount64;
            lock (Buckets)
            {
                var known = Buckets.TryGetValue(type, out var bucket);
                if (!known && Buckets.Count >= MaxEventTypes)
                    return false;
                var (tokens, at) = known ? bucket : (EventBurst, now);
                tokens = Math.Min(EventBurst, tokens + (now - at) / 1000.0 * EventsPerSecond);
                if (tokens < 1)
                {
                    Buckets[type] = (tokens, now);
                    return false;
                }
                Buckets[type] = (tokens - 1, now);
                return true;
            }
        }

        private sealed record Command(string Name, string Description, Func<ServerHostRequest, ServerHostReply> Handler);

        /// <summary>
        /// Publishes an event such as <c>admin.kick</c> to the host's event stream from any thread. Values may be
        /// strings, booleans, numbers, null or string arrays; returns false when hosting is inactive or the event is invalid.
        /// </summary>
        public static bool Emit(string type, IReadOnlyDictionary<string, object> fields)
        {
            if (!Host.Enabled || type == null || type.Length > MaxTypeLength || !TypePattern.IsMatch(type))
                return false;
            if (fields != null && fields.Count > MaxFields)
                return false;

            var copy = new JsonObject();
            if (fields != null)
            {
                foreach (var (key, value) in fields)
                {
                    if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || key is "event" or "ts" or "id" or "ok" or "command"
                        || copy.ContainsKey(key) || !TryCopy(value, out var node))
                        return false;
                    copy[key] = node;
                }
            }

            if (!TryTakeToken(type))
            {
                System.Threading.Interlocked.Increment(ref _rateLimited);
                return false;
            }
            HostEvents.Emit(type, copy);
            return true;
        }

        /// <summary>
        /// Raised on the game thread for each player in a <c>players</c> query, after the built-in fields.
        /// Handlers may add or replace keys; values must serialize to JSON.
        /// </summary>
        public static event Action<ServerSession, IDictionary<string, object>> PlayerFields;

        internal static void AddPlayerFields(ServerSession session, IDictionary<string, object> fields)
        {
            var handlers = PlayerFields;
            if (handlers == null)
                return;

            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(session, fields);
                }
                catch (Exception error)
                {
                    ReportFailure(handler.Method, error);
                }
            }
        }

        private static bool TryCopy(object value, out JsonNode node)
        {
            node = null;
            switch (value)
            {
                case null:
                    return true;
                case string text:
                    if (text.Length > MaxStringLength)
                        return false;
                    node = JsonValue.Create(text);
                    return true;
                case bool flag:
                    node = JsonValue.Create(flag);
                    return true;
                case float single when float.IsFinite(single):
                    node = JsonValue.Create(single);
                    return true;
                case double number when double.IsFinite(number):
                    node = JsonValue.Create(number);
                    return true;
                case byte or sbyte or short or ushort or int or uint or long:
                    node = JsonValue.Create(Convert.ToInt64(value));
                    return true;
                case ulong large:
                    node = JsonValue.Create(large);
                    return true;
                case decimal exact:
                    node = JsonValue.Create(exact);
                    return true;
                case string[] entries:
                    if (entries.Length > MaxArrayEntries)
                        return false;
                    var array = new JsonArray();
                    foreach (var entry in entries)
                    {
                        if (entry != null && entry.Length > MaxStringLength)
                            return false;
                        array.Add(entry == null ? null : JsonValue.Create(entry));
                    }
                    node = array;
                    return true;
                default:
                    return false;
            }
        }

        private static void ReportFailure(MethodInfo method, Exception error)
        {
            var now = Environment.TickCount64;
            lock (ReportedFailures)
            {
                if (ReportedFailures.TryGetValue(method, out var last) && now - last < FailureReportIntervalMs)
                    return;
                ReportedFailures[method] = now;
            }
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server hosting player-fields handler "
                    + method.DeclaringType?.FullName + "." + method.Name + " failed: " + error);
            }
            catch (Exception) { }
        }
    }

    /// <summary>A host command request, as the host agent sent it.</summary>
    public sealed class ServerHostRequest
    {
        internal ServerHostRequest(string id, string command, string arguments, string origin, bool confirmed,
            string actorLabel, string actorSteamId)
        {
            Id = id;
            Command = command;
            Arguments = arguments ?? "";
            Origin = origin;
            Confirmed = confirmed;
            ActorLabel = actorLabel;
            ActorSteamId = actorSteamId;
        }

        /// <summary>The request id, or null when the sender did not ask for a machine reply.</summary>
        public string Id { get; }
        public string Command { get; }
        /// <summary>Everything after the command, with the host flags (--json, --id, --actor, --actor-steam) removed.</summary>
        public string Arguments { get; }
        /// <summary>stdin, tcp, pipe, game or other. stdin and tcp come from the server host and carry owner rights.</summary>
        public string Origin { get; }
        public bool Confirmed { get; }
        /// <summary>The panel user the host agent named with --actor. Asserted by the host, not authenticated.</summary>
        public string ActorLabel { get; }
        /// <summary>The SteamID64 the host agent named with --actor-steam, for restricting staff by role. Never grants rights.</summary>
        public string ActorSteamId { get; }
    }

    /// <summary>The single reply to a host command.</summary>
    public sealed class ServerHostReply
    {
        private ServerHostReply(bool ok, string error, string message, IReadOnlyDictionary<string, object> data)
        {
            IsOk = ok;
            Error = error;
            Message = message;
            Data = data;
        }

        public bool IsOk { get; }
        public string Error { get; }
        public string Message { get; }
        public IReadOnlyDictionary<string, object> Data { get; }

        /// <summary>Success. Data values follow the same rules as <see cref="ServerHosting.Emit"/>.</summary>
        public static ServerHostReply Ok(IReadOnlyDictionary<string, object> data = null, string message = null) =>
            new(true, null, message, data);

        /// <summary>Failure with one of the protocol error codes, such as invalid-arguments, refused or failed.</summary>
        public static ServerHostReply Fail(string error, string detail, IReadOnlyDictionary<string, object> data = null) =>
            new(false, string.IsNullOrWhiteSpace(error) ? "failed" : error, detail, data);
    }
}
