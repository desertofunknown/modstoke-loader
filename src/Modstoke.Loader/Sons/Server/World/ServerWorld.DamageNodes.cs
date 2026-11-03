// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        /// <summary>
        /// Copies the direct damage node reached by one current collision's supported player
        /// receiver route. A controller alone does not identify a node. No damage or callbacks
        /// are invoked, and copied resource state does not become server-owned authority.
        /// </summary>
        public static bool TryReadHitPlayerDamageNode(CollisionHit hit,
            PlayerHandle binding, out PlayerDamageNodeSnapshot snapshot,
            out CollisionReadStatus status)
        {
            snapshot = null;
            if (!TryMatchHitPlayerCore(hit, binding, true, true, out _, out var observation, out var route, out status))
                return false;
            snapshot = new PlayerDamageNodeSnapshot(observation, route);
            return true;
        }

        /// <summary>
        /// Checks identity continuity between a retained original node and a current snapshot.
        /// The current snapshot must be fresh; the original may be from an older frame, and its
        /// observed values remain stale. This consumes one shared collision-read budget entry.
        /// Identity continuity does not establish immutable configuration or damage authority.
        /// </summary>
        public static bool IsSamePlayerDamageNode(PlayerDamageNodeSnapshot original,
            PlayerDamageNodeSnapshot current, out CollisionReadStatus status)
        {
            status = CollisionReadStatus.InvalidQuery;
            if (original == null || current == null) return false;
            try
            {
                if (!IsCurrentPlayerDamageNodeSnapshot(current, out status)) return false;
                status = CollisionReadStatus.ReceiverChanged;
                if (!original.Observation.HasSameNodeIdentity(current.Observation)
                    || !original.Route.Matches(current.Route)) return false;
                status = CollisionReadStatus.StaleHit;
                if (!CollisionContextCurrent(current.Observation.World, current.Frame)) return false;
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }

        /// <summary>
        /// Rechecks the exact route, node, copied values and retained references in the captured
        /// world and frame. Reads and checks each consume one shared collision-read budget entry.
        /// This is a freshness check, not native impact admission or permission to deal damage.
        /// </summary>
        public static bool IsCurrentPlayerDamageNodeSnapshot(PlayerDamageNodeSnapshot snapshot,
            out CollisionReadStatus status)
        {
            status = CollisionReadStatus.InvalidQuery;
            if (snapshot == null) return false;
            try
            {
                var retained = snapshot.Observation;
                if (retained.MeleeRequest != null)
                    return IsCurrentMeleePlayerDamageNodeSnapshot(snapshot, out status);
                if (!TryMatchHitPlayerCore(retained.Hit, retained.Binding, true, true,
                    out _, out var current, out var route, out status)) return false;
                status = CollisionReadStatus.ReceiverChanged;
                if (!snapshot.Route.Matches(route) || !retained.Matches(current)) return false;
                status = CollisionReadStatus.StaleHit;
                if (!CollisionContextCurrent(retained.Hit.World, retained.Frame)) return false;
                status = CollisionReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                status = CollisionReadStatus.Failed;
                ReportFailure(error);
                return false;
            }
        }
    }
}
