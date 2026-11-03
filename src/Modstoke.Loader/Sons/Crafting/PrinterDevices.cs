// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Modstoke.Sons;

/// <summary>Current-world printer reads and explicit, attributed native commands on the game thread.</summary>
public static class PrinterDevices
{
    public const float InteractionRange = 12f;
    public const int MaximumDevices = 64;
    private const int MaximumRecipes = 32;
    [ThreadStatic] private static Invocation _invocation;
    [ThreadStatic] private static bool _authorizing;
    private static readonly Dictionary<IntPtr, PendingDispatch> Pending = new();
    internal static bool HasActiveInvocation => _invocation != null;
    internal static bool HasPendingDispatch(Bolt.Event value) => value != null && Pending.ContainsKey(value.Pointer);

    public static bool TryDiscover(WorldRecordContext world, Vector3 position,
        out IReadOnlyList<PrinterDeviceSnapshot> devices, out string problem)
    {
        devices = Array.Empty<PrinterDeviceSnapshot>();
        if (!CheckWorld(world, out problem)) return false;
        if (!Finite(position)) { problem = "Current player position is invalid."; return false; }
        try
        {
            // Native enumeration is limited to this component type. Assets and unloaded/inactive
            // scene objects are excluded by Capture; no prefab or arbitrary entity is a device.
            var found = UnityEngine.Resources.FindObjectsOfTypeAll<Resin3dPrinterLaptop>();
            if (found == null || found.Length > 256) { problem = "Printer discovery exceeds its bounded scene limit."; return false; }
            var copies = new List<PrinterDeviceSnapshot>();
            var ids = new HashSet<ulong>();
            foreach (var laptop in found)
            {
                if (!TryCaptureNative(world, laptop, out var handle) || !TryRead(handle, out var snapshot, out _)
                    || !WithinRange(position, snapshot.Position) || !ids.Add(snapshot.NetworkId)) continue;
                copies.Add(snapshot);
            }
            copies.Sort((a, b) =>
            {
                int distance = Vector3.DistanceSquared(position, a.Position).CompareTo(Vector3.DistanceSquared(position, b.Position));
                return distance != 0 ? distance : a.NetworkId.CompareTo(b.NetworkId);
            });
            if (copies.Count > MaximumDevices) copies.RemoveRange(MaximumDevices, copies.Count - MaximumDevices);
            if (!CheckWorld(world, out problem)) return false;
            devices = copies.AsReadOnly();
            return true;
        }
        catch (Exception) { problem = "Native printer discovery is unavailable."; return false; }
    }

    public static bool TryCapture(WorldRecordContext world, ulong networkId,
        out PrinterDevice handle, out PrinterDeviceSnapshot snapshot, out string problem)
    {
        handle = null;
        snapshot = null;
        if (!CheckWorld(world, out problem)) return false;
        try
        {
            var entity = networkId == 0 ? null : BoltNetwork.FindEntity(new Bolt.NetworkId(networkId));
            var laptop = Live(entity) ? entity.GetComponent<Resin3dPrinterLaptop>() : null;
            if (!TryCaptureNative(world, laptop, out var candidate) || candidate.NetworkId != networkId)
            { problem = "That current-world network ID is not an attached printer laptop."; return false; }
            if (!TryRead(candidate, out snapshot, out problem)) return false;
            handle = candidate;
            return true;
        }
        catch (Exception) { problem = "The printer is unavailable."; return false; }
    }

    public static bool TryRead(PrinterDevice handle,
        out PrinterDeviceSnapshot snapshot, out string problem)
    {
        snapshot = null;
        problem = "The paired printer attachment changed.";
        if (handle == null || !CheckWorld(handle.World, out problem)) return false;
        try
        {
            var laptop = handle.Laptop;
            var printer = handle.Printer;
            if (!Matches(handle)) { problem = "The paired printer attachment changed."; return false; }
            var nativePosition = laptop.transform.position;
            var position = new Vector3(nativePosition.x, nativePosition.y, nativePosition.z);
            var available = laptop._availableBlueprints;
            int count = available?.Count ?? 0;
            int selectedIndex = laptop._currentBlueprintIndex;
            if (!Finite(position) || count < 1 || count > MaximumRecipes || selectedIndex < 0 || selectedIndex >= count)
            { problem = "The native printer recipe list is unavailable."; return false; }
            var recipes = new List<PrinterRecipeSnapshot>(count);
            var ids = new HashSet<int>(count);
            int? selected = null;
            for (int i = 0; i < count; i++)
            {
                var blueprint = available[i];
                if (!Live(blueprint) || blueprint.Id < 0 || !ids.Add(blueprint.Id)
                    || blueprint.RequiredResin < 0 || !Single.IsFinite(blueprint.TotalPrintTime) || blueprint.TotalPrintTime < 0)
                { problem = "The native printer recipe data is invalid."; return false; }
                if (i == selectedIndex) selected = blueprint.Id;
                if (laptop.IsValidBlueprintForGameMode(i))
                    recipes.Add(new PrinterRecipeSnapshot(blueprint.Id, CopyText(blueprint.Name),
                        blueprint.RequiredResin, blueprint.TotalPrintTime));
            }
            float resin = printer.CurrentResinVolume, capacity = printer.MaxResinVolume;
            if (!Single.IsFinite(resin) || resin < 0 || !Single.IsFinite(capacity) || capacity < resin)
            { problem = "The native printer resin state is unavailable."; return false; }
            bool printing = printer.IsPrinting, paused = printer.IsPaused;
            float progress = printing || paused ? printer.PrintJobPercentComplete : Single.NaN;
            bool plate = printer.HasCompletedPrintsOnTheBed, busy = laptop._isBusy;
            bool owned = handle.LaptopEntity.isOwner && handle.PrinterEntity.isOwner;
            snapshot = new PrinterDeviceSnapshot(handle.World, handle.NetworkId, position,
                selected.Value, recipes.AsReadOnly(), resin, capacity, printing, paused, plate, busy,
                laptop._printerState == PrinterState.Idle && !printing && !paused && !plate && !busy,
                Single.IsFinite(progress) ? progress : null, owned);
            if (!CheckWorld(handle.World, out problem)) { snapshot = null; return false; }
            if (!Matches(handle)) { snapshot = null; problem = "The paired printer attachment changed."; return false; }
            return true;
        }
        catch (Exception) { snapshot = null; problem = "Native printer state is unavailable."; return false; }
    }

    /// <summary>Uses the native recipe setter or StartPrint(true); creates no resin or inventory items.
    /// The callback is rechecked after veto subscribers and again before queued print propagation.</summary>
    public static PrinterDeviceReceipt Execute(PrinterDevice device,
        PlayerHandle player, ServerSession session, HostPlayer host,
        PrinterOperation operation, int? recipeId, Func<bool> stillAuthorized)
    {
        if (_invocation != null || _authorizing || stillAuthorized == null
            || (operation != PrinterOperation.RecipeSelection && operation != PrinterOperation.PrintStart))
            return Rejected("Printer command context is unavailable.");
        var invocation = new Invocation(device, player, session, host, operation, recipeId, stillAuthorized);
        bool called = false;
        _invocation = invocation;
        try
        {
            string problem = null;
            PruneDispatches();
            if (operation == PrinterOperation.PrintStart && Pending.Count >= MaximumDevices)
                return Rejected("Printer propagation is still pending; no further print was started.");
            if (!invocation.Current(out var before) || !Ready(before, operation, recipeId, out problem))
                return Rejected(problem ?? "Player, printer, range or permission changed.");
            if (!ServerPrinters.AuthorizeDeviceCommand(invocation.Request(null)))
                return Rejected("An installed printer policy rejected the command.");
            if (!invocation.Current(out before) || !Ready(before, operation, recipeId, out problem))
                return Rejected(problem ?? "Player, printer, range or permission changed.");
            called = true;
            if (operation == PrinterOperation.RecipeSelection)
                device.Laptop.SetCurrentBlueprintIdRemotely(recipeId.Value);
            else
                device.Laptop.StartPrint(true);
            if (TryRead(device, out var observed, out _)
                && (operation == PrinterOperation.RecipeSelection
                    ? observed.SelectedRecipeId == recipeId : observed.IsPrinting || observed.IsPaused))
                return new(PrinterDeviceReceiptStatus.NativeObserved,
                    operation == PrinterOperation.RecipeSelection
                        ? "Native recipe selection observed; peer delivery is unconfirmed."
                        : "Native print activity observed; resin debit, peer delivery and production completion are unconfirmed.", observed);
            return invocation.Rejected
                ? Rejected("A native printer boundary rejected the command; no completed job is asserted.")
                : new(PrinterDeviceReceiptStatus.Queued,
                    "Native invocation returned; the requested state has not been observed. Read status before retrying.", observed);
        }
        catch (Exception)
        {
            return called ? new(PrinterDeviceReceiptStatus.Queued,
                "Native invocation did not return cleanly; its effects are unknown. Read status before retrying.", null)
                : Rejected("Printer command validation is unavailable.");
        }
        finally { _invocation = null; }
    }

    private static bool Ready(PrinterDeviceSnapshot snapshot, PrinterOperation operation,
        int? recipeId, out string problem)
    {
        problem = null;
        if (snapshot == null || !snapshot.IsServerOwned) problem = "The server does not own both native printer components.";
        else if (!snapshot.IsIdle) problem = "Printer is busy, printing, paused, or has prints on its bed.";
        else
        {
            int id = operation == PrinterOperation.PrintStart ? snapshot.SelectedRecipeId : recipeId ?? -1;
            PrinterRecipeSnapshot recipe = null;
            foreach (var candidate in snapshot.Recipes) if (candidate.Id == id) { recipe = candidate; break; }
            if (recipe == null) problem = "Choose a recipe ID shown in this printer's status.";
            else if (operation == PrinterOperation.PrintStart && snapshot.Resin < recipe.RequiredResin)
                problem = "The printer does not contain enough resin for its selected recipe.";
        }
        return problem == null;
    }

    internal static bool TryReadInvocation(Resin3dPrinterLaptop laptop, PrinterOperation operation,
        out PrinterRequest request)
    {
        request = null;
        var active = _invocation;
        if (active == null || laptop.Pointer != active.Device.Laptop.Pointer || operation != active.Operation) return false;
        if (active.Current(out var snapshot) && Ready(snapshot, operation, active.RecipeId, out _))
            request = active.Request(null);
        else active.Rejected = true;
        return true;
    }

    internal static bool FinishInvocationAuthorization(bool approved)
    {
        var active = _invocation;
        bool accepted = approved && active != null && active.Current(out var snapshot)
            && Ready(snapshot, active.Operation, active.RecipeId, out _)
            && (active.Operation != PrinterOperation.PrintStart || Pending.Count < MaximumDevices);
        if (!accepted && active != null) active.Rejected = true;
        return accepted;
    }

    internal static bool ObserveSend(Bolt.Event value)
    {
        var active = _invocation;
        if (active == null || value == null || active.Operation != PrinterOperation.PrintStart) return true;
        try
        {
            var start = value.TryCast<Resin3dPrinterStartPrintEvent>();
            if (start == null || start.GetTargetNetworkIdValue().PackedValue != active.Device.NetworkId
                || start.Targets != 4 || start.RaisedBy != null) return true;
            // Execute rejects nested commands. At most one native send belongs to this invocation.
            PruneDispatches();
            if (active.Sent || Pending.Count >= MaximumDevices) { active.Rejected = true; return false; }
            active.Sent = true;
            Pending.Add(value.Pointer, new PendingDispatch(value, active));
            return true;
        }
        catch (Exception) { active.Rejected = true; return false; }
    }

    internal static bool TryTakeDispatch(Bolt.Event value, out PrinterRequest request, out Func<bool> stillAuthorized)
    {
        request = null;
        stillAuthorized = null;
        if (!Pending.Remove(value.Pointer, out var pending)) return false;
        try
        {
            var start = value.TryCast<Resin3dPrinterStartPrintEvent>();
            if (start != null && value.RaisedBy == null && value.Targets == 4
                && start.GetTargetNetworkIdValue().PackedValue == pending.Invocation.Device.NetworkId
                && pending.StillAuthorized())
            {
                request = pending.Invocation.Request(value.Targets);
                stillAuthorized = pending.StillAuthorized;
            }
        }
        catch (Exception) { }
        return true;
    }

    internal static void ForgetDispatch(Bolt.Event value)
    {
        if (value != null) Pending.Remove(value.Pointer);
    }

    // Bolt drops queued events with its session, so attribution from a stopped session or an
    // earlier world can never be dispatched. Current-world entries stay until Dispatch takes them.
    private static void PruneDispatches()
    {
        if (Pending.Count == 0) return;
        bool running = BoltNetwork.isRunning;
        List<IntPtr> stale = null;
        foreach (var entry in Pending)
            if (!running || !entry.Value.Invocation.Device.World.IsCurrent) (stale ??= new()).Add(entry.Key);
        if (stale != null) foreach (var key in stale) Pending.Remove(key);
    }

    private static bool TryCaptureNative(WorldRecordContext world, Resin3dPrinterLaptop laptop,
        out PrinterDevice handle)
    {
        handle = null;
        if (!Live(laptop) || !laptop.isActiveAndEnabled || !laptop.gameObject.activeInHierarchy) return false;
        var printer = laptop._connectedPrinter;
        if (!Live(printer) || !printer.isActiveAndEnabled || !printer.gameObject.activeInHierarchy
            || !Live(printer._connectedLaptop) || printer._connectedLaptop.Pointer != laptop.Pointer) return false;
        var entity = laptop.entity;
        var printerEntity = printer.entity;
        if (!Live(entity) || !entity.isAttached || entity.networkId.PackedValue == 0
            || !Live(printerEntity) || !printerEntity.isAttached || entity._entity == null || printerEntity._entity == null) return false;
        handle = new PrinterDevice(world, laptop, printer, entity, printerEntity);
        return Matches(handle);
    }

    private static bool Matches(PrinterDevice handle)
    {
        var laptop = handle.Laptop;
        var printer = handle.Printer;
        return Live(laptop) && Live(printer) && laptop.isActiveAndEnabled && printer.isActiveAndEnabled
            && laptop.gameObject.activeInHierarchy && printer.gameObject.activeInHierarchy
            && Live(laptop._connectedPrinter) && laptop._connectedPrinter.Pointer == printer.Pointer
            && Live(printer._connectedLaptop) && printer._connectedLaptop.Pointer == laptop.Pointer
            && SameAttachment(laptop.entity, handle.LaptopEntity, handle.LaptopAttachment, handle.NetworkId)
            && SameAttachment(printer.entity, handle.PrinterEntity, handle.PrinterAttachment, handle.PrinterNetworkId);
    }

    private static bool SameAttachment(BoltEntity current, BoltEntity expected, Bolt.Entity attachment, ulong id) =>
        Live(current) && Live(expected) && current.Pointer == expected.Pointer && current.isAttached
        && attachment != null && !attachment.WasCollected && current._entity != null && !current._entity.WasCollected
        && current._entity.Pointer == attachment.Pointer && current.networkId.PackedValue == id;

    private static bool CheckWorld(WorldRecordContext expected, out string problem)
    {
        bool current = WorldRecords.TryGetCurrent(out var world, out var status)
            && expected != null && ReferenceEquals(world, expected) && expected.IsCurrent;
        problem = current ? null : "The server world is unavailable or changed (" + status + ").";
        return current;
    }

    public static bool WithinRange(Vector3 player, Vector3 device) =>
        Finite(player) && Finite(device) && Vector3.DistanceSquared(player, device) <= InteractionRange * InteractionRange;

    private static bool Finite(Vector3 value) => Single.IsFinite(value.X) && Single.IsFinite(value.Y) && Single.IsFinite(value.Z);
    private static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
    private static string CopyText(string text) => String.IsNullOrWhiteSpace(text) ? "Printer recipe"
        : text.Length > 64 ? text.Substring(0, 64) : text;
    private static PrinterDeviceReceipt Rejected(string detail) => new(PrinterDeviceReceiptStatus.Rejected, detail, null);

    private sealed class Invocation
    {
        internal readonly PrinterDevice Device;
        private readonly PlayerHandle _player;
        private readonly ServerSession _session;
        private readonly HostPlayer _host;
        internal readonly PrinterOperation Operation;
        internal readonly int? RecipeId;
        private readonly Func<bool> _authorized;
        internal bool Rejected;
        internal bool Sent;
        internal Invocation(PrinterDevice device, PlayerHandle player,
            ServerSession session, HostPlayer host, PrinterOperation operation,
            int? recipeId, Func<bool> authorized)
        { Device = device; _player = player; _session = session; _host = host; Operation = operation; RecipeId = recipeId; _authorized = authorized; }
        internal bool Current(out PrinterDeviceSnapshot snapshot)
        {
            snapshot = null;
            if (_authorizing) return false;
            _authorizing = true;
            try
            {
                return ActorCurrent() && _authorized() && ActorCurrent() && TryRead(Device, out snapshot, out _)
                    && ServerWorld.TryReadPlayerBinding(_player, out var actor, out _)
                    && WithinRange(actor.Position, snapshot.Position);
            }
            finally { _authorizing = false; }
        }
        private bool ActorCurrent() => _player != null && (_host != null
            ? _session == null && _host.IsCurrent && _player.IsLocalPlayer
            : _session != null && _session.IsCurrent && !_player.IsLocalPlayer && ReferenceEquals(_player.SourceSession, _session));
        internal PrinterRequest Request(int? routing) => new(Operation,
            PrinterOrigin.DeviceCommand, _session, Device.NetworkId, RecipeId, routing, DateTimeOffset.UtcNow, _host);
    }

    private sealed class PendingDispatch
    {
        // Retention prevents native event pointer reuse while its one-shot attribution is live.
        internal readonly Bolt.Event Event;
        internal readonly Invocation Invocation;
        private readonly long _expiresAt = Environment.TickCount64 + 5000;
        internal PendingDispatch(Bolt.Event value, Invocation invocation) { Event = value; Invocation = invocation; }
        internal bool StillAuthorized() => Environment.TickCount64 <= _expiresAt
            && Invocation.Current(out var snapshot) && snapshot.IsServerOwned
            && Environment.TickCount64 <= _expiresAt;
    }
}
