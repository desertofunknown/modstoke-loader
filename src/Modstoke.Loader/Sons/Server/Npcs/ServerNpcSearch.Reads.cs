// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Ai.Vail;
using UnityEngine;

namespace Modstoke.Sons;

public static partial class ServerNpcSearch
{
    private const int MaxActors = 256;
    private const int ReadsPerFrame = 8;
    private static int _readFrame = -1;
    private static int _reads;

    internal static bool TryCount(NpcSearchRequest request, float radius, out int count,
        out NpcSearchReadStatus status)
    {
        count = 0;
        status = NpcSearchReadStatus.Unavailable;
        try
        {
            if (!IsCurrent(request)) { status = NpcSearchReadStatus.Closed; return false; }
            if (!OnServerThread()) { status = NpcSearchReadStatus.NotServer; return false; }
            if (!float.IsFinite(radius) || radius < 2 || radius > 40) return false;
            var frame = Time.frameCount;
            if (_readFrame != frame) { _readFrame = frame; _reads = 0; }
            if (_reads >= ReadsPerFrame) { status = NpcSearchReadStatus.BudgetExceeded; return false; }
            _reads++;
            var manager = VailActorManager._instance;
            if (manager == null || manager.WasCollected || !manager) return false;
            var actors = manager._activeActors;
            if (actors == null || actors.WasCollected) return false;
            var length = actors.Count;
            if (length > MaxActors) { status = NpcSearchReadStatus.LimitExceeded; return false; }
            var candidate = _scope.Actor;
            if (!IsLiveActor(candidate)) return false;
            var candidateFound = false;
            var matched = 0;
            var radiusSquared = (double)radius * radius;
            for (var index = 0; index < length; index++)
            {
                var actor = actors[index];
                if (!IsLiveActor(actor)) continue;
                if (Same(actor, candidate)) { candidateFound = true; continue; }
                var action = actor._activeActionInstance;
                if (action == null || action.WasCollected || !action
                    || !IsSearch(actor.GetActiveGroup(), actor.GetActiveThought())) continue;
                var active = actor._activeStimuli;
                var memory = active == null || active.WasCollected ? null : active.Stimuli?.TryCast<LastSeenStimuli>();
                if (memory == null || memory.WasCollected || !active._active || !active._hasPosition
                    || active._transform != null) return false;
                // Native Init copies LastSeenStimuli into this position. The memory object can
                // subsequently change; reading it would count a different destination.
                var position = active._position;
                if (!Finite(position)) return false;
                var dx = (double)position.x - request.TargetPosition.x;
                var dy = (double)position.y - request.TargetPosition.y;
                var dz = (double)position.z - request.TargetPosition.z;
                if (dx * dx + dy * dy + dz * dz <= radiusSquared) matched++;
            }
            if (!candidateFound || actors.Count != length || !Same(manager, VailActorManager._instance)
                || !Same(actors, manager._activeActors) || !IsLiveActor(candidate)) return false;
            count = matched;
            status = NpcSearchReadStatus.Success;
            return true;
        }
        catch (Exception error)
        {
            status = NpcSearchReadStatus.Failed;
            ReportFailure(error);
            return false;
        }
    }
}
