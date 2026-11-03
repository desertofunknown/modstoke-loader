// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        private static bool _changingEntityControl;

        /// <summary>
        /// Revokes remote control of an attached, locally owned entity on the server's game thread.
        /// The caller owns authorization and must supply the exact expected live controller session.
        /// Never acquires ownership or assigns control. True means NoChange or Revoked; inspect Outcome.
        /// Revoked is a local observation after normal native return, not a remote acknowledgement.
        /// A false return with Unconfirmed can include partial native effects and must not be retried blindly.
        /// </summary>
        public static bool TryRevokeEntityControl(ulong networkId,
            ServerSession expectedControllerSession, out EntityControlResult result)
        {
            BoltEntity entity = null;
            Bolt.Entity attachment = null;
            BoltConnection connection = null;
            var invoked = false;
            var entered = false;
            result = new(networkId, EntityControlOutcome.Rejected, "Unavailable", false, null);
            try
            {
                var context = CheckContext();
                if (context != WorldReadStatus.Success)
                {
                    result = result with { Detail = context.ToString() };
                    return false;
                }
                if (_changingEntityControl)
                {
                    result = result with { Detail = "Entity control operation is already in progress." };
                    return false;
                }
                var support = HarmonySupport.CreateState();
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    result = result with { Detail = "Recognized native build and safe Harmony support are required." };
                    return false;
                }

                // Resolve the exact handle once; never substitute a connection found only by numeric ID.
                connection = ServerSessions.ResolveCurrent(expectedControllerSession);
                if (connection == null || connection.WasCollected)
                {
                    result = result with { Detail = "Expected controller session is unavailable." };
                    return false;
                }
                entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached)
                {
                    result = result with { Detail = "Entity is unavailable or detached." };
                    return false;
                }
                attachment = entity._entity;
                if (!MatchesEntityControlAttachment(entity, attachment, networkId))
                {
                    result = result with { Detail = "Entity attachment changed." };
                    return false;
                }
                if (!entity.isOwner || !attachment.IsOwner)
                {
                    result = result with { Detail = "Entity is not locally owned by the server." };
                    return false;
                }

                if (!expectedControllerSession.IsCurrent || connection.WasCollected
                    || CheckContext() != WorldReadStatus.Success
                    || !MatchesEntityControlAttachment(entity, attachment, networkId)
                    || !entity.isOwner || !attachment.IsOwner)
                {
                    result = result with { Detail = "Server, session, ownership or attachment changed before invocation." };
                    return false;
                }
                var controller = attachment.Controller;
                if (!expectedControllerSession.IsCurrent || connection.WasCollected
                    || (controller != null && (controller.WasCollected
                        || controller.Pointer != connection.Pointer || controller.ConnectionId != connection.ConnectionId)))
                {
                    result = result with { Detail = "Current controller does not match the expected live session." };
                    return false;
                }
                // No remote controller is not proof of local control. Do not call native code for this case.
                if (controller == null)
                {
                    result = result with
                    {
                        Outcome = EntityControlOutcome.NoChange,
                        Detail = "No remote controller is assigned; native revocation was not invoked."
                    };
                    return true;
                }

                _changingEntityControl = true;
                entered = true;
                invoked = true;
                // The owner-only native path (client 0x180945970, dedicated server 0x180836D20) handles queue, proxy and token cleanup.
                attachment.RevokeControl(null);

                // Native callbacks/patches can detach, replace or transfer the entity before returning.
                if (CheckContext() != WorldReadStatus.Success
                    || !expectedControllerSession.IsCurrent || connection.WasCollected
                    || !MatchesEntityControlAttachment(entity, attachment, networkId)
                    || !entity.isOwner || !attachment.IsOwner)
                {
                    result = new(networkId, EntityControlOutcome.Unconfirmed,
                        "Server, session, ownership or attachment changed during invocation.", true, null);
                    return false;
                }
                if (attachment.Controller != null)
                {
                    result = new(networkId, EntityControlOutcome.Unconfirmed,
                        "Native revocation returned but a remote controller is still assigned.", true, null);
                    return false;
                }

                result = new(networkId, EntityControlOutcome.Revoked,
                    "Native revocation returned and the same owned attachment has no remote controller.", true, null);
                return true;
            }
            catch (Exception error)
            {
                result = new(networkId, invoked ? EntityControlOutcome.Unconfirmed
                    : EntityControlOutcome.Rejected,
                    invoked ? "Invocation or post-call observation faulted; native effects may be partial."
                        : "Pre-call validation faulted; native revocation was not invoked.",
                    invoked, error.GetType().FullName);
                return false;
            }
            finally
            {
                if (entered) _changingEntityControl = false;
                // Retain the native wrappers through callbacks and all identity comparisons.
                GC.KeepAlive(entity);
                GC.KeepAlive(attachment);
                GC.KeepAlive(connection);
            }
        }

        /// <summary>
        /// Assigns control of a server-owned attachment to an exact live remote session. Null expected
        /// controller requires no remote controller. The caller owns authorization and readiness.
        /// An existing controller is revoked before assignment; this handoff is not atomic. Unconfirmed
        /// can leave the entity without a controller and must not be retried or rolled back blindly.
        /// True means NoChange or Assigned locally, not remote delivery or ownership migration.
        /// </summary>
        public static bool TryAssignEntityControl(ulong networkId,
            ServerSession expectedControllerSession, ServerSession recipientSession,
            out EntityControlResult result)
        {
            BoltEntity entity = null;
            Bolt.Entity attachment = null;
            BoltConnection expected = null;
            BoltConnection recipient = null;
            bool invoked = false, entered = false;
            result = new(networkId, EntityControlOutcome.Rejected, "Unavailable", false, null);
            try
            {
                var context = CheckContext();
                if (context != WorldReadStatus.Success)
                { result = result with { Detail = context.ToString() }; return false; }
                if (_changingEntityControl)
                { result = result with { Detail = "Entity control operation is already in progress." }; return false; }
                var support = HarmonySupport.CreateState();
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                { result = result with { Detail = "Recognized native build and safe Harmony support are required." }; return false; }

                recipient = ServerSessions.ResolveCurrent(recipientSession);
                if (!MatchesEntityControlSession(recipient, recipientSession))
                { result = result with { Detail = "Recipient session is unavailable." }; return false; }
                if (expectedControllerSession != null)
                {
                    expected = ReferenceEquals(expectedControllerSession, recipientSession) ? recipient
                        : ServerSessions.ResolveCurrent(expectedControllerSession);
                    if (!MatchesEntityControlSession(expected, expectedControllerSession))
                    { result = result with { Detail = "Expected controller session is unavailable." }; return false; }
                }
                entity = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
                if (entity is null || entity.WasCollected || !entity || !entity.isAttached)
                { result = result with { Detail = "Entity is unavailable or detached." }; return false; }
                attachment = entity._entity;
                if (!MatchesOwnedControlAttachment(entity, attachment, networkId))
                { result = result with { Detail = "Entity is not the same attached server-owned entity." }; return false; }
                var controller = attachment.Controller;
                if (expected == null ? controller != null : !SameControlConnection(controller, expected))
                { result = result with { Detail = "Current controller does not match the expected session." }; return false; }
                if (CheckContext() != WorldReadStatus.Success
                    || !MatchesEntityControlSession(recipient, recipientSession)
                    || (expected != null && !MatchesEntityControlSession(expected, expectedControllerSession)))
                { result = result with { Detail = "Server or session changed before invocation." }; return false; }
                if (SameControlConnection(controller, recipient))
                {
                    result = result with { Outcome = EntityControlOutcome.NoChange,
                        Detail = "The expected live recipient already controls this attachment; native assignment was not invoked." };
                    return true;
                }

                _changingEntityControl = true;
                entered = true;
                if (controller != null)
                {
                    invoked = true;
                    // AssignControl overwrites Controller without cleaning up its former remote controller.
                    attachment.RevokeControl(null);
                    if (CheckContext() != WorldReadStatus.Success
                        || !MatchesOwnedControlAttachment(entity, attachment, networkId)
                        || !MatchesEntityControlSession(expected, expectedControllerSession)
                        || !MatchesEntityControlSession(recipient, recipientSession)
                        || attachment.Controller != null)
                    {
                        result = new(networkId, EntityControlOutcome.Unconfirmed,
                            "Handoff stopped after revocation because the entity, controller or sessions changed; assignment was not attempted.", true, null);
                        return false;
                    }
                }

                invoked = true;
                attachment.AssignControl(recipient, null);
                if (CheckContext() != WorldReadStatus.Success
                    || !MatchesOwnedControlAttachment(entity, attachment, networkId)
                    || !MatchesEntityControlSession(recipient, recipientSession)
                    || !SameControlConnection(attachment.Controller, recipient))
                {
                    result = new(networkId, EntityControlOutcome.Unconfirmed,
                        "Assignment returned but the same owned attachment and requested live controller could not be confirmed.", true, null);
                    return false;
                }
                result = new(networkId, EntityControlOutcome.Assigned,
                    "Native assignment returned and the same server-owned attachment has the requested live controller.", true, null);
                return true;
            }
            catch (Exception error)
            {
                result = new(networkId, invoked ? EntityControlOutcome.Unconfirmed
                    : EntityControlOutcome.Rejected,
                    invoked ? "Control handoff or post-call observation faulted; native effects may be partial."
                        : "Pre-call validation faulted; native control was not changed.", invoked, error.GetType().FullName);
                return false;
            }
            finally
            {
                if (entered) _changingEntityControl = false;
                GC.KeepAlive(entity);
                GC.KeepAlive(attachment);
                GC.KeepAlive(expected);
                GC.KeepAlive(recipient);
            }
        }

        private static bool SameControlConnection(BoltConnection left, BoltConnection right) => left != null
            && right != null && !left.WasCollected && !right.WasCollected
            && left.Pointer == right.Pointer && left.ConnectionId == right.ConnectionId;

        private static bool MatchesEntityControlSession(BoltConnection connection, ServerSession session)
        {
            if (session == null || connection == null || connection.WasCollected
                || !ReferenceEquals(ServerSessions.ResolveObserved(connection), session)) return false;
            var transport = connection._udp;
            return transport != null && !transport.WasCollected && transport.ConnectionId == connection.ConnectionId
                && transport.State == UdpKit.UdpConnectionState.Connected;
        }

        private static bool MatchesOwnedControlAttachment(BoltEntity entity, Bolt.Entity attachment, ulong networkId)
            => MatchesEntityControlAttachment(entity, attachment, networkId)
                && entity.isOwner && attachment.IsOwner && attachment.Source == null;

        private static bool MatchesEntityControlAttachment(BoltEntity retained, Bolt.Entity attachment, ulong networkId)
        {
            if (retained is null || retained.WasCollected || !retained || !retained.isAttached
                || attachment is null || attachment.WasCollected || !attachment.IsAttached
                || retained.networkId.PackedValue != networkId || attachment.NetworkId.PackedValue != networkId)
                return false;
            var current = BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            if (current is null || current.WasCollected || !current || !current.isAttached
                || current.Pointer != retained.Pointer || current.networkId.PackedValue != networkId)
                return false;
            var active = current._entity;
            return active != null && !active.WasCollected && active.Pointer == attachment.Pointer;
        }
    }

    public enum EntityControlOutcome
    {
        /// <summary>Native control mutation was not invoked because validation failed.</summary>
        Rejected,
        /// <summary>The requested controller state already held; no native mutation was invoked.</summary>
        NoChange,
        /// <summary>Normal native return followed by observing no controller on the same owned attachment.</summary>
        Revoked,
        /// <summary>Native invocation was attempted, but its full outcome cannot be confirmed.</summary>
        Unconfirmed,
        /// <summary>Normal native return followed by observing the requested live remote controller.</summary>
        Assigned
    }

    /// <summary>
    /// Managed copies only. NativeInvoked includes an invocation that threw; it does not imply completion.
    /// Revoked and Assigned describe local controller state, not remote delivery, local control acquisition or every
    /// native side effect. Detail explains validation or observation failure; ExceptionType is null unless faulted.
    /// </summary>
    public sealed record EntityControlResult(ulong NetworkId, EntityControlOutcome Outcome,
        string Detail, bool NativeInvoked, string ExceptionType);
}
