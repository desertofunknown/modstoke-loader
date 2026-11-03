// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Security.Cryptography;

namespace Modstoke.Sons
{
    public static partial class ClientEnrollment
    {
        private static void OnReceived(ControlMessage message)
        {
            if (!OnOwnerThread || !_selected || _faulted)
                return;
            var entry = Observe(message.Peer);
            if (entry == null)
                return;
            var now = Now;
            Refresh(entry, now);
            if (entry.State == EnrollmentState.Retired)
                return;
            if (entry.State == EnrollmentState.HostNotParticipating)
            {
                ModAdmission.HostParticipating(entry.Peer);
                // Gameplay already sent while released was acknowledged and then dropped by the host's
                // gate. Reconnect rather than continue desynced; otherwise negotiate as if on time.
                if (entry.SentWhileReleased)
                {
                    Retire(entry, EnrollmentEndReason.HostAnsweredLate);
                    return;
                }
                entry.State = EnrollmentState.Pending;
                entry.Deadline = now + NegotiationSeconds;
            }
            var payload = message.PayloadSpan;
            if (ModAdmissionWire.MatchesMagic(payload))
            {
                if (!ModAdmission.Receive(message.Peer, payload))
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (!EnrollmentWire.TryRead(payload, out var frame))
            {
                Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (frame.Kind == EnrollmentMessageKind.Rejected)
            {
                Retire(entry, MatchesChallenge(entry, frame)
                    ? EnrollmentEndReason.RemoteRejected
                    : EnrollmentEndReason.InvalidMessage);
                return;
            }
            if (entry.Peer.ServerSession != null)
                ReceiveResponse(entry, frame, now);
            else
                ReceiveServerMessage(entry, frame, now);
        }

        private static void ReceiveResponse(Enrollment entry, EnrollmentFrame frame, double now)
        {
            if (frame.Kind != EnrollmentMessageKind.Response || !MatchesChallenge(entry, frame))
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
                // An identical retransmission cannot reset the attempt budget or change its claims.
                if (frame.OfferedCapabilities != entry.OfferedCapabilities)
                    Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            entry.OfferedCapabilities = frame.OfferedCapabilities;
            entry.State = EnrollmentState.Enrolled;
            ModAdmission.Enrolled(entry.Peer);
            entry.Outgoing = EnrollmentMessageKind.Accepted;
            entry.Attempts = 0;
            entry.NextSend = now;
            SendNext(entry, now);
        }

        private static void ReceiveServerMessage(Enrollment entry, EnrollmentFrame frame, double now)
        {
            if (frame.Kind == EnrollmentMessageKind.Challenge)
            {
                if (entry.Nonce != null)
                {
                    if (!MatchesChallenge(entry, frame))
                        Retire(entry, EnrollmentEndReason.InvalidMessage);
                    return;
                }
                entry.Nonce = frame.Nonce.ToArray();
                entry.RequiredCapabilities = frame.RequiredCapabilities;
                if ((_localCapabilities & entry.RequiredCapabilities) != entry.RequiredCapabilities)
                {
                    RejectCapabilities(entry);
                    return;
                }
                entry.OfferedCapabilities = _localCapabilities;
                entry.Outgoing = EnrollmentMessageKind.Response;
                entry.NextSend = now;
                SendNext(entry, now);
                return;
            }
            if (frame.Kind != EnrollmentMessageKind.Accepted || !MatchesChallenge(entry, frame)
                || frame.OfferedCapabilities != entry.OfferedCapabilities)
            {
                Retire(entry, EnrollmentEndReason.InvalidMessage);
                return;
            }
            entry.State = EnrollmentState.Enrolled;
            ModAdmission.Enrolled(entry.Peer);
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

        private static void SendNext(Enrollment entry, double now)
        {
            if (entry.State == EnrollmentState.Retired || entry.Nonce == null
                || now >= entry.Deadline || now < entry.NextSend || entry.Attempts >= MaxAttemptsPerMessage
                || (entry.Peer.ServerSession == null && entry.State == EnrollmentState.Enrolled))
                return;
            if (entry.State == EnrollmentState.Enrolled && entry.Attempts > 0
                && ModAdmission.Selected)
                return; // The reliable channel owns retransmission after admission negotiation begins.
            entry.Attempts++;
            entry.NextSend = now + RetrySeconds;
            var offered = entry.Outgoing == EnrollmentMessageKind.Challenge ? 0 : entry.OfferedCapabilities;
            var payload = EnrollmentWire.Write(entry.Outgoing, entry.Nonce, entry.RequiredCapabilities, offered);
            if (ControlTransport.Send(entry.Peer, payload) != ControlResult.Success)
                Retire(entry, EnrollmentEndReason.SendFailed);
        }
    }
}
