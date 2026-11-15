// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ClientEnrollment
    {
        private static void OnReceived(ControlMessage message)
        {
            if (!OnOwnerThread || _faulted)
                return;
            var payload = message.PayloadSpan;
            if (!Selected)
            {
                AnswerWithoutSelection(message.Peer, payload);
                return;
            }
            var entry = Observe(message.Peer);
            if (entry == null)
                return;
            Refresh(entry);
            Receive(entry, payload, Now);
        }

        private static void Receive(Enrollment entry, ReadOnlySpan<byte> payload, double now)
        {
            if (entry.State == EnrollmentState.Retired)
                return;
            if (entry.State == EnrollmentState.HostNotParticipating)
            {
                if (EnrollmentWire.TryRead(payload, out var repeated) && IsNotParticipating(entry, repeated))
                    return;
                ModAdmission.HostParticipating(entry.Peer);
                _clientReleased = default;
                // Gameplay sent while released reached a host that was still holding this connection, which refuses
                // it for that. Leave with the reason rather than negotiate on a connection already out of step.
                if (_clientSentWhileReleased)
                {
                    Retire(entry, EnrollmentEndReason.HostAnsweredLate);
                    KickReason.Expect("The server answered Modstoke too late, after your game had started playing.",
                        "Rejoin to play.");
                    return;
                }
                entry.State = EnrollmentState.Pending;
                entry.Deadline = _clock + NegotiationSeconds;
            }
            // The host sends these only after Accepted, but the stream channel resends a lost frame by itself, so one
            // can overtake Accepted. Once the client has answered the challenge, keep them for Accepted to release.
            if (entry.Peer.ServerSession == null && entry.State == EnrollmentState.Pending && entry.Nonce != null
                && (ModAdmissionWire.MatchesMagic(payload) || ContentAgreement.MatchesMagic(payload)
                    || EnrollmentWire.IsKind(payload, EnrollmentMessageKind.Admitted)))
            {
                entry.AfterAccepted ??= new List<byte[]>();
                if (entry.AfterAccepted.Count >= MaxAfterAccepted)
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                else
                    entry.AfterAccepted.Add(payload.ToArray());
                return;
            }
            if (ModAdmissionWire.MatchesMagic(payload))
            {
                if (!ModAdmission.Receive(entry.Peer, payload))
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (ContentAgreement.MatchesMagic(payload))
            {
                if (entry.State != EnrollmentState.Enrolled)
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                else if (!ContentAgreement.IsCurrentVersion(payload))
                    Retire(entry, EnrollmentEndReason.ProtocolMismatch);
                else if (!ContentAgreement.Receive(entry.Peer, payload))
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (EnrollmentWire.MatchesMagic(payload) && EnrollmentWire.ReadVersion(payload) != ProtocolVersion)
            {
                Retire(entry, EnrollmentEndReason.ProtocolMismatch);
                return;
            }
            if (!EnrollmentWire.TryRead(payload, out var frame))
            {
                Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (frame.Kind == EnrollmentMessageKind.Rejected)
            {
                ReceiveRejected(entry, frame);
                return;
            }
            if (entry.Peer.ServerSession != null)
                ReceiveClientMessage(entry, frame, now);
            else
                ReceiveServerMessage(entry, frame, now);
        }

        // A host that runs no check this session still owes a client's hello an answer, or the client would hold
        // its gameplay for the whole negotiation window.
        private static void AnswerWithoutSelection(ControlPeer peer, ReadOnlySpan<byte> payload)
        {
            if (peer?.ServerSession == null || !EnrollmentWire.TryRead(payload, out var frame)
                || frame.Kind != EnrollmentMessageKind.Hello)
                return;
            var reply = EnrollmentWire.Write(EnrollmentMessageKind.Rejected, frame.Nonce, 0, 0,
                EnrollmentRejection.NotParticipating);
            if (ControlTransport.Send(peer, reply) == ControlResult.Success)
                RuntimeEntryPoint.LogSource?.LogInfo("Connection " + peer.ConnectionId
                    + " opened Modstoke negotiation; this session runs no mod check, so it plays as the game does.");
        }

        private static void ReceiveRejected(Enrollment entry, EnrollmentFrame frame)
        {
            if (entry.State == EnrollmentState.Pending && IsNotParticipating(entry, frame))
            {
                ReleaseClient(entry, "The server runs Modstoke but checks nothing for this session; playing as the "
                    + "game does after " + Milliseconds(entry.OpenedAt) + " ms.");
                return;
            }
            var matches = MatchesChallenge(entry, frame);
            if (matches)
                entry.RemoteRejection = frame.Rejection;
            Retire(entry, matches ? EnrollmentEndReason.RemoteRejected : EnrollmentEndReason.InvalidMessage);
        }

        // The host's answer to this client's hello when it checks nothing this session.
        private static bool IsNotParticipating(Enrollment entry, EnrollmentFrame frame) =>
            entry.Peer.ServerSession == null && entry.Nonce == null && frame.Kind == EnrollmentMessageKind.Rejected
                && frame.Rejection == EnrollmentRejection.NotParticipating
                && CryptographicOperations.FixedTimeEquals(entry.HelloNonce, frame.Nonce);

        private static void ReceiveClientMessage(Enrollment entry, EnrollmentFrame frame, double now)
        {
            if (frame.Kind == EnrollmentMessageKind.Hello)
            {
                // A repeated hello changes nothing; the challenge already went out on the reliable stream.
                if (entry.State != EnrollmentState.Pending || entry.HelloAt != 0)
                    return;
                entry.HelloAt = now;
                // Only a claim until the response; kept so a refusal can say which part of the check is missing.
                entry.OfferedCapabilities = frame.OfferedCapabilities;
                Send(entry, EnrollmentMessageKind.Challenge);
                return;
            }
            if (frame.Kind != EnrollmentMessageKind.Response || entry.HelloAt == 0 || !MatchesChallenge(entry, frame))
            {
                Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if ((frame.OfferedCapabilities & entry.RequiredCapabilities) != entry.RequiredCapabilities)
            {
                RejectCapabilities(entry);
                return;
            }
            if (entry.State == EnrollmentState.Enrolled)
            {
                // An identical retransmission cannot change its claims.
                if (frame.OfferedCapabilities != entry.OfferedCapabilities)
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            entry.OfferedCapabilities = frame.OfferedCapabilities;
            entry.State = EnrollmentState.Enrolled;
            entry.EnrolledAt = now;
            // Accepted goes first: everything the checks below send must reach an enrolled client.
            if (!Send(entry, EnrollmentMessageKind.Accepted))
                return;
            ModAdmission.Enrolled(entry.Peer);
            ContentAgreement.Enrolled(entry.Peer, frame.OfferedCapabilities);
            Advance(entry);
        }

        private static void ReceiveServerMessage(Enrollment entry, EnrollmentFrame frame, double now)
        {
            switch (frame.Kind)
            {
                case EnrollmentMessageKind.Challenge:
                    if (entry.Nonce != null)
                    {
                        if (!MatchesChallenge(entry, frame))
                            Retire(entry, EnrollmentEndReason.InvalidMessage);
                        return;
                    }
                    entry.Nonce = frame.Nonce.ToArray();
                    entry.RequiredCapabilities = frame.RequiredCapabilities;
                    if ((LocalCapabilities & entry.RequiredCapabilities) != entry.RequiredCapabilities)
                    {
                        RejectCapabilities(entry);
                        return;
                    }
                    entry.OfferedCapabilities = LocalCapabilities;
                    Send(entry, EnrollmentMessageKind.Response);
                    return;
                case EnrollmentMessageKind.Accepted:
                case EnrollmentMessageKind.Admitted:
                    if (!MatchesChallenge(entry, frame) || frame.OfferedCapabilities != entry.OfferedCapabilities
                        || (frame.Kind == EnrollmentMessageKind.Admitted && entry.State != EnrollmentState.Enrolled))
                        Retire(entry, EnrollmentEndReason.InvalidMessage);
                    else if (frame.Kind == EnrollmentMessageKind.Accepted && entry.State == EnrollmentState.Pending)
                    {
                        entry.State = EnrollmentState.Enrolled;
                        entry.EnrolledAt = now;
                        // The host's checks run from here; mod admission has its own longer window when it asked for it.
                        entry.Deadline = _clock + AdmissionSeconds
                            + ((entry.RequiredCapabilities & ModAdmission.ProtocolCapability) != 0 ? ModAdmission.AdmissionSeconds : 0);
                        ModAdmission.Enrolled(entry.Peer);
                        var overtaken = entry.AfterAccepted;
                        entry.AfterAccepted = null;
                        if (overtaken != null)
                            foreach (var payload in overtaken) Receive(entry, payload, now);
                    }
                    else if (frame.Kind == EnrollmentMessageKind.Admitted && !entry.Admitted)
                    {
                        entry.Admitted = true;
                        RuntimeEntryPoint.LogSource?.LogInfo("The server admitted this connection after "
                            + Milliseconds(entry.OpenedAt) + " ms (enrolled at "
                            + (long)((entry.EnrolledAt - entry.OpenedAt) * 1000) + " ms); "
                            + _clientHeldSends + " game sends were held until then.");
                    }
                    return;
                default:
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                    return;
            }
        }

        private static bool MatchesChallenge(Enrollment entry, EnrollmentFrame frame) =>
            entry.Nonce != null && frame.RequiredCapabilities == entry.RequiredCapabilities
                && CryptographicOperations.FixedTimeEquals(entry.Nonce, frame.Nonce);

        private static void RejectCapabilities(Enrollment entry)
        {
            try
            {
                var payload = EnrollmentWire.Write(EnrollmentMessageKind.Rejected,
                    entry.Nonce, entry.RequiredCapabilities, 0, EnrollmentRejection.IncompatibleCapabilities);
                ControlTransport.Send(entry.Peer, payload);
            }
            finally { Retire(entry, EnrollmentEndReason.IncompatibleCapabilities); }
        }

        private static bool Send(Enrollment entry, EnrollmentMessageKind kind)
        {
            var hello = kind == EnrollmentMessageKind.Hello;
            var offered = kind == EnrollmentMessageKind.Challenge ? 0 : hello ? LocalCapabilities : entry.OfferedCapabilities;
            var payload = EnrollmentWire.Write(kind, hello ? entry.HelloNonce : entry.Nonce,
                hello ? 0 : entry.RequiredCapabilities, offered);
            if (ControlTransport.Send(entry.Peer, payload) == ControlResult.Success)
                return true;
            Retire(entry, EnrollmentEndReason.SendFailed);
            return false;
        }
    }
}
