// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Diagnostics;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    /// <summary>Console commands for server hosting panels. Each one answers in plain text or, on request, JSON.</summary>
    internal static class HostCommands
    {
        private static readonly char[] Spaces = { ' ', '\t' };

        internal static bool IsHostCommand(string name) => name is "status" or "version" or "players" or "mods"
            or "requirements" or "save" or "stop" or "config" or "reload" or "broadcast" or "saves" or "perf"
            || name == "game" && !Host.IsServer || ServerHosting.HasCommand(name);

        // status and mods are already listed as built-ins.
        internal static IEnumerable<ConsoleCommandInfo> Catalog()
        {
            yield return Listed("version", "Show the Loader, game and runtime versions.");
            if (Host.IsServer)
                yield return Listed("players", "List connected players and where they are.");
            yield return Listed("requirements", "requirements generate: write server-requirements.json from the installed mods.");
            if (Host.IsServer)
            {
                yield return Listed("save", "Save the world now.");
                yield return Listed("stop", "Save the world and shut the server down. stop --no-save skips the save.");
                yield return Listed("broadcast", "broadcast <text>: send a chat message from the server to every player.");
                yield return Listed("saves", "List the save slots, their in-game day, size and which one is running.");
            }
            yield return Listed("config", "config check | config get: check the known config files, or show dedicatedserver.cfg.");
            yield return Listed("reload", "Apply config changes that do not need a restart.");
            yield return Listed("perf", "perf [--seconds N]: time each mod's per-frame work for N seconds (default 10).");
            if (!Host.IsServer)
                yield return Listed("game", "game continue [latest|<slot>] | new <mode> | inventory open|close | equip <item id> | quit: drive a single player game for test runs (desktop console).");
            foreach (var (name, description) in ServerHosting.CommandCatalog())
                yield return Listed(name, description);
        }

        private static ConsoleCommandInfo Listed(string name, string description) =>
            new ConsoleCommandInfo(name, description, true, false, true);

        internal static bool TryExecute(ConsoleSubmission s)
        {
            if (!IsHostCommand(s.Command))
                return false;
            if (!TryReadFlags(s.Arguments, out var json, out var id, out var actor, out var actorSteam, out var rest))
            {
                Respond(s with { Json = true }, false,
                    "A request id or actor is 1 to 64 letters, digits, '.', '_', ':', '@' or '-'; --actor-steam takes a SteamID64.",
                    error: "invalid-id");
                return true;
            }
            var request = s with { RequestId = s.RequestId ?? id, Json = s.Json || json, Arguments = rest };
            if (request.Command is "status" or "mods" && !HostOutput.WantsReply(request))
                return false;
            try
            {
                if (ServerHosting.HasCommand(request.Command))
                    RunModCommand(request, actor, actorSteam);
                else
                    Run(request);
            }
            catch (Exception error)
            {
                Respond(request, false, "Command failed: " + error.Message);
            }
            return true;
        }

        private static void Run(ConsoleSubmission request)
        {
            var command = request.Command;
            if (command is "save" or "stop" or "players" or "broadcast" && !Host.IsServer)
            {
                Respond(request, false, "\"" + command + "\" is only available on a dedicated server.", error: "unavailable");
                return;
            }
            if (command is "save" or "stop" or "reload" or "broadcast" or "game"
                && request.Origin is not (ConsoleOrigin.ServerConsole or ConsoleOrigin.DesktopConsole))
            {
                Respond(request, false, "\"" + command + "\" can only be run from the server or desktop console.",
                    error: "unavailable");
                return;
            }
            var first = FirstWord(request.Arguments);
            switch (command)
            {
                case "status":
                    HostStatus.RunStatus(request);
                    break;
                case "version":
                    HostStatus.RunVersion(request);
                    break;
                case "players":
                    HostPlayers.Run(request);
                    break;
                case "mods":
                    HostMods.RunMods(request);
                    break;
                case "requirements" when first == "generate":
                    HostMods.RunRequirements(request);
                    break;
                case "requirements":
                    Usage(request, "requirements generate [--name <text>]");
                    break;
                case "save":
                    HostSave.RunSave(request);
                    break;
                case "stop" when request.Arguments.Length == 0 || request.Arguments == "--no-save":
                    HostSave.RunStop(request);
                    break;
                case "stop":
                    Usage(request, "stop [--no-save]");
                    break;
                case "config" when first == "check":
                    HostConfig.RunConfig(request);
                    break;
                case "config" when first == "get":
                    HostConfig.RunGet(request);
                    break;
                case "config":
                    Usage(request, "config check | config get");
                    break;
                case "broadcast":
                    RunBroadcast(request);
                    break;
                case "saves":
                    HostWorld.RunSaves(request);
                    break;
                case "perf":
                    HostPerf.RunPerf(request);
                    break;
                case "reload":
                    HostConfig.RunReload(request);
                    break;
                case "game":
                    HostGame.Run(request);
                    break;
            }
        }

        // For restart countdowns and scheduled messages when no moderation mod is loaded. Runs on the game thread.
        private static void RunBroadcast(ConsoleSubmission request)
        {
            var text = request.Arguments.Trim();
            if (text.Length == 0)
            {
                Usage(request, "broadcast <text>");
                return;
            }
            if (!ServerChat.IsPlainTextValid(text))
            {
                Respond(request, false, "Chat text must be one line of at most " + ServerChat.MaxMessageLength
                    + " characters, without < or >.", error: "invalid-arguments");
                return;
            }

            int queued = 0, failed = 0;
            var unavailable = false;
            foreach (var session in ServerSessions.Sessions)
            {
                var result = ServerChat.SendToSession(session, text);
                if (result == ChatSendResult.Queued)
                    queued++;
                else
                {
                    failed++;
                    unavailable |= result is ChatSendResult.Unavailable or ChatSendResult.NotServer;
                }
            }
            if (queued == 0 && unavailable)
            {
                Respond(request, false, "Server chat is not available right now.", new { recipients = 0, failed }, "not-ready");
                return;
            }
            HostEvents.Emit("chat", new { channel = "server", name = "Server", text, recipients = queued });
            Respond(request, true, "Sent to " + queued + (queued == 1 ? " player." : " players."),
                new { recipients = queued, failed });
        }

        private static void RunModCommand(ConsoleSubmission request, string actor, string actorSteam)
        {
            var reply = ServerHosting.Run(new ServerHostRequest(request.RequestId, request.Command, request.Arguments,
                OriginName(request), request.Confirmed, actor, actorSteam));
            var body = ServerHosting.CopyFields(reply.Data);
            if (reply.IsOk)
                Respond(request, true, reply.Message ?? request.Command + " done.", body);
            else
                Respond(request, false, reply.Message ?? request.Command + " failed.", body, reply.Error);
        }

        // TCP clients submit as the server console; their reply channel tells them apart.
        private static string OriginName(ConsoleSubmission request) => request.ReplyChannel > 0 ? "tcp" : request.Origin switch
        {
            ConsoleOrigin.ServerConsole => "stdin",
            ConsoleOrigin.DesktopConsole => "pipe",
            ConsoleOrigin.InGameConsole => "game",
            _ => "other"
        };

        /// <summary>Answers a host request once. Safe from any thread.</summary>
        internal static void Respond(ConsoleSubmission request, bool ok, string human, object body = null,
            string error = null)
        {
            human ??= "";
            var reply = HostOutput.WantsReply(request);
            if (reply)
            {
                if (ok)
                {
                    HostOutput.Reply(request, true, body);
                }
                else
                {
                    var failure = HostOutput.ToObject(body).DeepClone().AsObject();
                    failure["error"] = error ?? "failed";
                    failure["detail"] = human;
                    HostOutput.Reply(request, false, failure);
                }
            }
            if (!reply || request.Command is "save" or "stop" or "reload" or "requirements")
                RuntimeConsole.ReportHuman(request, ok ? "Info" : "Warning", human);
        }

        private static void Usage(ConsoleSubmission request, string usage) =>
            Respond(request, false, "Usage: " + usage, error: "invalid-arguments");

        private static string FirstWord(string arguments)
        {
            var split = arguments.IndexOfAny(Spaces);
            return split < 0 ? arguments : arguments[..split];
        }

        // Removes --json, --id <id>, --actor <label> and --actor-steam <steamid64>; the rest is rejoined with single spaces.
        private static bool TryReadFlags(string arguments, out bool json, out string id, out string actor,
            out string actorSteam, out string rest)
        {
            json = false;
            id = null;
            actor = null;
            actorSteam = null;
            rest = "";
            var kept = new List<string>();
            var tokens = (arguments ?? "").Split(Spaces, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "--json")
                {
                    json = true;
                }
                else if (tokens[i] == "--id")
                {
                    if (i + 1 >= tokens.Length || !HostLine.IsId(tokens[i + 1]))
                        return false;
                    id = tokens[++i];
                }
                else if (tokens[i] == "--actor")
                {
                    if (i + 1 >= tokens.Length || !IsActor(tokens[i + 1]))
                        return false;
                    actor = tokens[++i];
                }
                else if (tokens[i] == "--actor-steam")
                {
                    if (i + 1 >= tokens.Length || !IsSteamId(tokens[i + 1]))
                        return false;
                    actorSteam = tokens[++i];
                }
                else
                {
                    kept.Add(tokens[i]);
                }
            }
            rest = string.Join(" ", kept);
            return true;
        }

        private static bool IsActor(string value)
        {
            if (value.Length is 0 or > 64)
                return false;
            foreach (var c in value)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or ':' or '@' or '-'))
                    return false;
            }
            return true;
        }

        private static bool IsSteamId(string value) =>
            value.Length == 17 && value.StartsWith("7656", StringComparison.Ordinal) && ulong.TryParse(value, out _);
    }
}
