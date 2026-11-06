// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modstoke.Diagnostics;
using Modstoke.Sons;

namespace Modstoke.Loader
{
    internal static class HostPlayers
    {
        private const int HangGliderItemId = 626;

        // A Modstoke client sends its handshake as soon as it connects and repeats it after 3 and 9 s while
        // unanswered. Admin waits the same 20 s before it shows a client as having none.
        internal static readonly TimeSpan ClientKindSettle = TimeSpan.FromSeconds(20);

        internal static void Run(ConsoleSubmission request)
        {
            if (!Host.IsServer)
            {
                HostCommands.Respond(request, false, "Player queries need a dedicated server.", error: "unavailable");
                return;
            }
            if (!ServerSessions.HooksInstalled)
            {
                HostCommands.Respond(request, false, "Player sessions are not being tracked yet.", error: "not-ready");
                return;
            }

            int? max = null;
            var players = new List<JsonNode>();
            var human = new StringBuilder();
            try
            {
                if (ServerWorld.TryReadPopulation(out var population, out _))
                    max = population.RuntimeMaximumPlayers > 0 ? population.RuntimeMaximumPlayers : population.ReplicatedMaximumPlayers;

                foreach (var session in ServerSessions.Sessions)
                {
                    if (session == null || !session.IsCurrent)
                        continue;

                    var fields = Read(session);
                    if (human.Length > 0)
                        human.Append('\n');
                    human.Append(Describe(fields));

                    var builtIn = JsonSerializer.SerializeToNode(fields, HostOutput.Json);
                    ServerHosting.AddPlayerFields(session, fields);
                    players.Add(Serialize(fields, builtIn));
                }
            }
            catch (Exception error)
            {
                HostCommands.Respond(request, false, "Reading players failed: " + error.GetType().Name + ": " + error.Message);
                return;
            }

            HostCommands.Respond(request, true,
                players.Count == 0 ? "No players are connected." : human.ToString(),
                new JsonObject
                {
                    ["count"] = players.Count,
                    ["max"] = max,
                    ["players"] = new JsonArray(players.ToArray())
                });
        }

        private static Dictionary<string, object> Read(ServerSession session)
        {
            string name = null;
            var registered = session.RegisteredSteamId ?? 0;
            var steamId = registered != 0 ? registered.ToString() : null;
            string role = null;
            if (ServerSessions.TryReadIdentity(session, out var identity, out _))
            {
                name = identity.DisplayName;
                if (identity.GameSteamId != 0)
                    steamId = identity.GameSteamId.ToString();
                role = identity.StoredRole?.ToString();
            }

            int? pingMs = null;
            if (ServerSessions.TryReadConnection(session, out var connection, out _)
                && float.IsFinite(connection.NetworkRoundTripSeconds) && connection.NetworkRoundTripSeconds >= 0)
                pingMs = (int)Math.Round(connection.NetworkRoundTripSeconds * 1000);

            object position = null;
            PlayerMovementSnapshot movement = null;
            if (ServerWorld.TryCapturePlayerBinding(session, out var binding, out _))
            {
                if (ServerWorld.TryReadPlayerPose(binding, out var pose, out _))
                {
                    position = new Dictionary<string, object>
                    {
                        ["x"] = Math.Round((double)pose.Position.X, 2),
                        ["y"] = Math.Round((double)pose.Position.Y, 2),
                        ["z"] = Math.Round((double)pose.Position.Z, 2)
                    };
                }
                ServerWorld.TryReadPlayerMovement(binding, out movement, out _);
            }

            string lifeState = null;
            string life = null;
            bool? gliding = null;
            if (ServerWorld.TryReadPlayerState(session, out var state, out _))
            {
                lifeState = state.LifeState?.ToString();
                life = DescribeLife(state.LifeState);
                gliding = state.HeldOnlyItemId == HangGliderItemId || state.HeldItemIds.Contains(HangGliderItemId);
            }

            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["sessionId"] = session.SessionId.ToString(),
                ["connectionId"] = session.ConnectionId,
                ["name"] = name,
                ["steamId"] = steamId,
                ["clientKind"] = ReadClientKind(session, out _),
                ["role"] = role,
                ["joinedAt"] = session.FirstObservedAt.ToUnixTimeMilliseconds(),
                ["pingMs"] = pingMs,
                ["pos"] = position,
                // Earlier name for pos, kept for existing readers.
                ["position"] = position,
                // The SDK names no map regions; flags.inCave is the only area it can tell.
                ["area"] = null,
                ["life"] = life,
                // Raw PlayerLifeState name, which Admin reads.
                ["lifeState"] = lifeState,
                ["flags"] = new Dictionary<string, object>
                {
                    ["gliding"] = gliding,
                    ["inCave"] = movement == null ? null : movement.RawCaveMask != 0,
                    ["inWater"] = movement?.InWater,
                    ["inVehicle"] = movement == null ? null : movement.DrivingGolfCart || movement.PassengerGolfCart || movement.OnRaft,
                    ["onZipline"] = movement?.ZipLineAttached
                }
            };
        }

        // "modstoke" once the client's handshake or capability enrollment arrived. "vanilla" means neither came
        // within ClientKindSettle of connecting; a modded client that withholds its handshake looks the same.
        // Settled is false only while waiting could still change the answer.
        internal static string ReadClientKind(ServerSession session, out bool settled)
        {
            if (ServerSessions.TryReadClientSignals(session, out var signals, out _)
                && (signals.Hello != null || signals.Enrollment?.State == EnrollmentState.Enrolled))
            {
                settled = true;
                return "modstoke";
            }
            // Without the notice hooks no handshake can be received, so waiting tells nothing.
            var listening = ServerNotices.HooksInstalled;
            settled = !listening || DateTimeOffset.UtcNow - session.FirstObservedAt >= ClientKindSettle;
            return settled && listening && signals != null ? "vanilla" : "unknown";
        }

        // Dying is the downed state another player can revive from.
        private static string DescribeLife(PlayerLifeState? state) => state switch
        {
            PlayerLifeState.Alive => "alive",
            PlayerLifeState.Dying => "downed",
            PlayerLifeState.Dead => "dead",
            _ => null
        };

        private static JsonNode Serialize(Dictionary<string, object> fields, JsonNode builtIn)
        {
            try
            {
                return JsonSerializer.SerializeToNode(fields, HostOutput.Json);
            }
            catch (Exception error)
            {
                builtIn["fieldsError"] = error.GetType().Name + ": " + error.Message;
                return builtIn;
            }
        }

        private static string Describe(Dictionary<string, object> fields)
        {
            var line = new StringBuilder();
            line.Append(fields["name"] as string ?? "Unknown player");
            if (fields["steamId"] is string steamId)
                line.Append(" (").Append(steamId).Append(')');
            line.Append(" · ").Append(fields["pingMs"] is int ping ? ping.ToString(CultureInfo.InvariantCulture) : "?").Append(" ms");
            if (fields["pos"] is Dictionary<string, object> position)
            {
                line.Append(" · ").Append(Coordinate(position["x"])).Append(", ")
                    .Append(Coordinate(position["y"])).Append(", ").Append(Coordinate(position["z"]));
            }
            return line.ToString();
        }

        private static string Coordinate(object value) =>
            ((double)value).ToString("0.#", CultureInfo.InvariantCulture);
    }
}
