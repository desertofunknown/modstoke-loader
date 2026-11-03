// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Sons;

public sealed partial class ModuleStackStore
{
    private PreparedChange _reservation;

    internal void ValidateOwnerSave()
    {
        lock (_gate)
        {
            Require(_localOwner && Crafting.OnCraftExecutionThread && _reservation == null,
                "The local store has an unsettled transfer or is on the wrong thread.");
            // A disconnect can retire Bolt before the game saves its still-live local inventory.
            // Preserve only the exact same native owner, never rebind historical data to a new player.
            var player = TheForest.Utils.LocalPlayer._instance;
            var inventory = TheForest.Utils.LocalPlayer.Inventory;
            Require(Crafting.CraftExecutionLive(player) && Crafting.CraftExecutionLive(inventory)
                && player.Pointer == _world.LocalPlayer && inventory.Pointer == _world.Inventory
                && SaveLifecycle.State.BeforeLoadCount == _world.BeforeLoadCount,
                "The saved local inventory no longer belongs to this module store.");
        }
    }

    internal static ModuleStackStore CreateLocalOwner(ModuleStackSnapshot restored,
        ModuleStackLimits limits)
    {
        var store = new ModuleStackStore(CaptureWorld(true), limits, true);
        if (restored != null) store._state = ParseSnapshot(restored.ExportJson(), limits);
        store.RequireCurrent();
        return store;
    }

    internal PreparedChange PrepareTransfer(bool deposit, ItemModuleSnapshot item, long revision)
    {
        lock (_gate)
        {
            RequireMutation(revision);
            var next = ChangedSnapshot(_state, deposit, item, Limits);
            var prepared = new PreparedChange(this, _state, next);
            _reservation = prepared;
            return prepared;
        }
    }

    internal static ModuleStackSnapshot ChangedSnapshot(ModuleStackSnapshot before,
        bool deposit, ItemModuleSnapshot item, ModuleStackLimits limits)
    {
        Require(before != null && item != null && before.Revision < long.MaxValue, "Invalid transfer revision or item.");
        var runs = new List<ModuleStackRun>(before.Runs);
        int count = before.TotalCount;
        if (deposit)
        {
            Require(count < limits.MaxCount, "Module storage is full.");
            if (runs.Count > 0 && runs[^1].Snapshot.SameState(item))
                runs[^1] = new(item, runs[^1].Count + 1);
            else
            {
                Require(runs.Count < limits.MaxRuns, "Module storage has reached its distinct-run limit.");
                runs.Add(new(item, 1));
            }
            count++;
        }
        else
        {
            Require(runs.Count > 0 && runs[^1].Snapshot.SameState(item), "The requested item is no longer at the top of storage.");
            var top = runs[^1];
            if (top.Count == 1) runs.RemoveAt(runs.Count - 1);
            else runs[^1] = new(top.Snapshot, top.Count - 1);
            count--;
        }
        return CreateSnapshot(before.Revision + 1, count, runs.ToArray(), limits);
    }

    internal sealed class PreparedChange
    {
        private readonly ModuleStackStore _owner;
        internal readonly ModuleStackSnapshot Before, After;
        internal PreparedChange(ModuleStackStore owner, ModuleStackSnapshot before,
            ModuleStackSnapshot after) { _owner = owner; Before = before; After = after; }

        internal void Validate()
        {
            lock (_owner._gate)
            {
                _owner.RequireCurrent();
                Require(ReferenceEquals(_owner._reservation, this) && ReferenceEquals(_owner._state, Before),
                    "The reserved module transfer is no longer current.");
            }
        }

        internal void Commit()
        {
            lock (_owner._gate)
            {
                Validate();
                _owner._state = After;
                _owner._reservation = null;
            }
        }

        internal void Cancel()
        {
            lock (_owner._gate)
                if (ReferenceEquals(_owner._reservation, this)) _owner._reservation = null;
        }
    }
}
