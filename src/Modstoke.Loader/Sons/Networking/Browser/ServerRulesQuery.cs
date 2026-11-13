// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Modstoke.Sons
{
    /// <summary>
    /// Asks a Steam game server for its rules with the standard server query (A2S_RULES) on its query port. Steam
    /// answers it for dedicated servers with the values set through SteamGameServer.SetKeyValue. The game's own
    /// rules callback cannot be built from managed code under IL2CPP, so this runs over a plain UDP socket.
    /// </summary>
    internal static class ServerRulesQuery
    {
        private const byte RulesRequest = 0x56, ChallengeReply = 0x41, RulesReply = 0x45;
        private const int MaxPackets = 32;

        /// <summary>The server's rules, or null when it did not answer in time or the answer was malformed.</summary>
        internal static async Task<Dictionary<string, string>> RunAsync(IPAddress address, int port, TimeSpan timeout)
        {
            using var cancel = new CancellationTokenSource(timeout);
            using var socket = new UdpClient(address.AddressFamily);
            var server = new IPEndPoint(address, port);
            try
            {
                var challenge = -1;
                // A server answers the first request with a challenge, then the rules once it is sent back.
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    await socket.SendAsync(Request(challenge), server, cancel.Token).ConfigureAwait(false);
                    var payload = await ReceiveAsync(socket, server, cancel.Token).ConfigureAwait(false);
                    if (payload == null || payload.Length < 5) return null;
                    if (payload[4] == ChallengeReply && payload.Length >= 9)
                    {
                        challenge = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(5));
                        continue;
                    }
                    return payload[4] == RulesReply ? Parse(payload) : null;
                }
                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (SocketException)
            {
                return null;
            }
        }

        private static byte[] Request(int challenge)
        {
            var request = new byte[9];
            BinaryPrimitives.WriteInt32LittleEndian(request, -1);
            request[4] = RulesRequest;
            BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(5), challenge);
            return request;
        }

        // A long answer arrives split into numbered packets that share an id; this joins them into one payload.
        private static async Task<byte[]> ReceiveAsync(UdpClient socket, IPEndPoint server, CancellationToken cancel)
        {
            Dictionary<int, byte[]> parts = null;
            var total = 0;
            while (true)
            {
                var packet = await socket.ReceiveAsync(cancel).ConfigureAwait(false);
                if (!packet.RemoteEndPoint.Address.Equals(server.Address) || packet.Buffer.Length < 5) continue;
                var data = packet.Buffer;
                var header = BinaryPrimitives.ReadInt32LittleEndian(data);
                if (header == -1) return data;
                if (header != -2 || data.Length < 12) return null;
                var id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
                // A compressed answer sets the id's top bit; servers that publish short rules never need it.
                if (id < 0) return null;
                total = data[8];
                var number = data[9];
                if (total == 0 || total > MaxPackets || number >= total) return null;
                parts ??= new Dictionary<int, byte[]>();
                parts[number] = data.AsSpan(12).ToArray();
                if (parts.Count < total) continue;
                return parts.OrderBy(part => part.Key).SelectMany(part => part.Value).ToArray();
            }
        }

        private static Dictionary<string, string> Parse(byte[] payload)
        {
            if (payload.Length < 7) return null;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(5));
            var rules = new Dictionary<string, string>(StringComparer.Ordinal);
            var at = 7;
            for (var i = 0; i < count; i++)
            {
                if (!ReadString(payload, ref at, out var key) || !ReadString(payload, ref at, out var value)) break;
                rules[key] = value;
            }
            return rules;
        }

        private static bool ReadString(byte[] payload, ref int at, out string text)
        {
            text = null;
            var end = Array.IndexOf(payload, (byte)0, at);
            if (end < 0) return false;
            text = Encoding.UTF8.GetString(payload, at, end - at);
            at = end + 1;
            return true;
        }
    }
}
