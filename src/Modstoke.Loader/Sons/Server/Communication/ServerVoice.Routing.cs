// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Utilities;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Multiplayer;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class ServerVoice
    {
        [ThreadStatic] private static bool _forwarding;

        private static bool BeforeForward(VoiceChatBoltListener __instance, Il2CppStructArray<byte> __0,
            int __1, Bolt.NetworkId __2, bool __3, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!PolicyActive) return true;
            if (!OnOwnerThread()) return false;
            bool entered = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                if (_publishing || _forwarding || __instance == null || !ValidBuffer(__0, __1)) return false;
                var incoming = _incoming;
                VoiceParticipant source;
                Action<VoiceDeliveryRequest> handlers;
                DateTimeOffset receivedAt;
                if (incoming != null)
                {
                    if (!incoming.Receiving || incoming.Forwarded || incoming.Listener.Pointer != __instance.Pointer
                        || incoming.Source.NetworkId != __2.PackedValue || incoming.Size != __1 || incoming.Positional != __3) return false;
                    incoming.Forwarded = true;
                    incoming.CompressedBuffer = __0.Pointer;
                    source = incoming.Source;
                    handlers = incoming.Handlers;
                    receivedAt = incoming.ReceivedAt;
                }
                else
                {
                    // Only native capture from the actual listen host may begin a local forwarding scope.
                    source = CaptureHost();
                    handlers = Routing;
                    receivedAt = DateTimeOffset.UtcNow;
                    if (source == null || source.NetworkId != __2.PackedValue) return false;
                }
                if (handlers == null || !source.IsCurrent) return false;
                _forwarding = true;
                entered = true;
                // Preserve native talk timestamps and callbacks once per accepted forwarding invocation.
                __instance._talkingPlayers.UpdateTalkingPlayerInfo(__2);
                if (!source.IsCurrent) return false;
                float range = __instance._positionalVoiceAreaOfInterest;
                if (__3 && (!Single.IsFinite(range) || range < 0)) return false;
                var candidates = __3
                    ? VoiceChatBoltListener.GetOtherPlayersInAreaOfInterest(source.Binding.Player, range)
                    : PlayerLocation.GetPlayers();
                var recipients = CaptureRecipients(candidates, source);
                if (recipients == null) return false;
                var previous = _outgoing;
                var outgoing = new OutgoingVoice(__instance, __0.Pointer, __1, __3, source, handlers, receivedAt);
                _outgoing = outgoing;
                try
                {
                    foreach (var recipient in recipients)
                    {
                        if (!source.IsCurrent) break;
                        if (!recipient.IsCurrent) continue;
                        var connection = ServerSessions.ResolveCurrent(recipient.Session);
                        if (connection == null) continue;
                        outgoing.Recipient = recipient;
                        outgoing.Connection = connection.Pointer;
                        outgoing.SendEntered = false;
                        __instance.SendVoiceData(__0, __1, connection, source.NetworkId, __3);
                        outgoing.Recipient = null;
                    }
                }
                finally { _outgoing = previous; }
            }
            catch (Exception error) { ReportFailure(error); }
            finally { if (entered) _forwarding = false; }
            // The native loop dereferences LocalPlayer.Entity, even on dedicated servers.
            return false;
        }

        private static List<VoiceParticipant> CaptureRecipients(
            Il2CppSystem.Collections.Generic.IEnumerable<PlayerLocation> candidates, VoiceParticipant source)
        {
            if (candidates == null) return null;
            ServerSession localController = null;
            if (ServerWorld.TryCaptureLocalPlayerBinding(out var localBinding, out _)
                && ServerWorld.TryReadPlayerBinding(localBinding, out var local, out _))
                localController = local.ControllerSession;
            var recipients = new List<VoiceParticipant>();
            var sessions = new HashSet<Guid>();
            int visited = 0;
            var enumerator = candidates.GetEnumerator();
            try
            {
                var iterator = enumerator.Cast<Il2CppSystem.Collections.IEnumerator>();
                while (iterator.MoveNext())
                {
                    if (++visited > MaximumCandidates) return null;
                    var candidate = enumerator.Current;
                    if (candidate == null || candidate.WasCollected || !candidate) continue;
                    var entity = candidate.GetComponentInParent<BoltEntity>();
                    if (entity == null || !entity) entity = candidate.GetComponent<BoltEntity>();
                    if (entity == null || entity.WasCollected || !entity || !entity.isAttached || entity.networkId.PackedValue == source.NetworkId) continue;
                    var recipient = CaptureRemote(entity.source);
                    if (recipient == null || recipient.Binding.Player.Pointer != entity.Pointer
                        || ReferenceEquals(recipient.Session, localController) || ReferenceEquals(recipient.Session, source.Session)
                        || !sessions.Add(recipient.Session.SessionId)) continue;
                    recipients.Add(recipient);
                }
            }
            finally { enumerator.Cast<Il2CppSystem.IDisposable>().Dispose(); }
            return recipients;
        }

        private static bool BeforeSend(VoiceChatBoltListener __instance, Il2CppStructArray<byte> __0,
            int __1, BoltConnection __2, ulong __3, bool __4, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!PolicyActive) return true;
            if (!OnOwnerThread()) return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                var frame = _outgoing;
                if (_publishing || frame == null || frame.SendEntered || frame.Recipient == null || __instance == null
                    || __2 == null || !ValidBuffer(__0, __1) || frame.Listener.Pointer != __instance.Pointer
                    || frame.Buffer != __0.Pointer || frame.Size != __1 || frame.Connection != __2.Pointer
                    || frame.Source.NetworkId != __3 || frame.Positional != __4) return false;
                frame.SendEntered = true;
                var session = ServerSessions.ResolveSource(__2);
                if (!ReferenceEquals(session, frame.Recipient.Session)) return false;
                return Authorize(frame.Source, frame.Recipient, frame.Positional, frame.Size, frame.ReceivedAt, frame.Handlers);
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static bool BeforePlayback(CoopSteamVoicePlayer __instance, Il2CppStructArray<byte> __0,
            int __1, ulong __2, bool __3, Transform __4, bool __runOriginal, out float __7)
        {
            __7 = 0;
            if (!__runOriginal) return false;
            if (!PolicyActive) return true;
            if (!OnOwnerThread()) return false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                var frame = _incoming;
                // The native debug recording path also uses this method; it has no network-receive scope.
                if (frame == null) return true;
                if (_publishing || !frame.Receiving || frame.PlaybackAttempted || __instance == null
                    || !ValidBuffer(__0, __1) || frame.Size != __1 || frame.Source.NetworkId != __2 || frame.Positional != __3
                    || (frame.CompressedBuffer != IntPtr.Zero && frame.CompressedBuffer != __0.Pointer)
                    || frame.Listener._voicePlayer?.Pointer != __instance.Pointer || !frame.Source.IsCurrent) return false;
                frame.PlaybackAttempted = true;
                var transform = frame.Source.Binding.Player.transform;
                if (transform == null || !transform || __4 == null || !__4 || transform.Pointer != __4.Pointer) return false;
                var recipient = CaptureHost();
                return recipient != null && Authorize(frame.Source, recipient, __3, __1, frame.ReceivedAt, frame.Handlers);
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static bool Authorize(VoiceParticipant source, VoiceParticipant recipient,
            bool positional, int size, DateTimeOffset receivedAt, Action<VoiceDeliveryRequest> handlers)
        {
            if (_publishing || !source.IsCurrent || !recipient.IsCurrent) return false;
            var request = new VoiceDeliveryRequest(source, recipient, positional, size, receivedAt);
            _publishing = true;
            try
            {
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected) return false;
                }
                return source.IsCurrent && recipient.IsCurrent;
            }
            catch (Exception error) { ReportFailure(error); return false; }
            finally { request.Close(); _publishing = false; }
        }

        private sealed class OutgoingVoice
        {
            internal readonly VoiceChatBoltListener Listener;
            internal readonly IntPtr Buffer;
            internal readonly int Size;
            internal readonly bool Positional;
            internal readonly VoiceParticipant Source;
            internal readonly Action<VoiceDeliveryRequest> Handlers;
            internal readonly DateTimeOffset ReceivedAt;
            internal VoiceParticipant Recipient;
            internal IntPtr Connection;
            internal bool SendEntered;

            internal OutgoingVoice(VoiceChatBoltListener listener, IntPtr buffer, int size, bool positional,
                VoiceParticipant source, Action<VoiceDeliveryRequest> handlers, DateTimeOffset receivedAt)
            {
                Listener = listener; Buffer = buffer; Size = size; Positional = positional;
                Source = source; Handlers = handlers; ReceivedAt = receivedAt;
            }
        }
    }
}
