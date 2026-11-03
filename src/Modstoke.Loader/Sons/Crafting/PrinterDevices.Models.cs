// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Numerics;

namespace Modstoke.Sons;

/// <summary>One exact world and pair of native attachments. The handle grants no permission.</summary>
public sealed class PrinterDevice
{
    internal PrinterDevice(WorldRecordContext world, Resin3dPrinterLaptop laptop,
        Resin3dPrinter printer, BoltEntity laptopEntity, BoltEntity printerEntity)
    {
        World = world; Laptop = laptop; Printer = printer; LaptopEntity = laptopEntity; PrinterEntity = printerEntity;
        LaptopAttachment = laptopEntity._entity; PrinterAttachment = printerEntity._entity;
        NetworkId = laptopEntity.networkId.PackedValue; PrinterNetworkId = printerEntity.networkId.PackedValue;
    }
    public WorldRecordContext World { get; }
    public ulong NetworkId { get; }
    internal ulong PrinterNetworkId { get; }
    internal Resin3dPrinterLaptop Laptop { get; }
    internal Resin3dPrinter Printer { get; }
    internal BoltEntity LaptopEntity { get; }
    internal BoltEntity PrinterEntity { get; }
    internal Bolt.Entity LaptopAttachment { get; }
    internal Bolt.Entity PrinterAttachment { get; }
}

/// <summary>Copied state from an actual connected Resin3dPrinterLaptop and Resin3dPrinter.</summary>
public sealed class PrinterDeviceSnapshot
{
    internal PrinterDeviceSnapshot(WorldRecordContext world, ulong networkId,
        Vector3 position, int selectedRecipe, IReadOnlyList<PrinterRecipeSnapshot> recipes,
        float resin, float capacity, bool printing, bool paused, bool plate, bool busy, bool idle,
        float? progress, bool owned)
    {
        World = world; NetworkId = networkId; Position = position; SelectedRecipeId = selectedRecipe; Recipes = recipes;
        Resin = resin; ResinCapacity = capacity; IsPrinting = printing; IsPaused = paused; HasCompletedPrintsOnBed = plate;
        IsBusy = busy; IsIdle = idle; PrintProgress = progress; IsServerOwned = owned; ObservedAt = DateTimeOffset.UtcNow;
    }
    public string DeviceType => "Resin3dPrinter";
    public WorldRecordContext World { get; }
    public ulong NetworkId { get; }
    public Vector3 Position { get; }
    public int SelectedRecipeId { get; }
    public IReadOnlyList<PrinterRecipeSnapshot> Recipes { get; }
    public float Resin { get; }
    public float ResinCapacity { get; }
    public bool IsPrinting { get; }
    public bool IsPaused { get; }
    public bool HasCompletedPrintsOnBed { get; }
    public bool IsBusy { get; }
    public bool IsIdle { get; }
    /// <summary>Native progress observation, not proof of production or pickup.</summary>
    public float? PrintProgress { get; }
    public bool IsServerOwned { get; }
    public DateTimeOffset ObservedAt { get; }
}

public sealed class PrinterRecipeSnapshot
{
    internal PrinterRecipeSnapshot(int id, string name, int resin, float seconds)
    { Id = id; Name = name; RequiredResin = resin; PrintSeconds = seconds; }
    public int Id { get; }
    public string Name { get; }
    public int RequiredResin { get; }
    public float PrintSeconds { get; }
}

public enum PrinterDeviceReceiptStatus { Rejected, Queued, NativeObserved }

/// <summary>Invocation/state evidence only. None of these statuses asserts completed production.</summary>
public sealed class PrinterDeviceReceipt
{
    internal PrinterDeviceReceipt(PrinterDeviceReceiptStatus status, string detail,
        PrinterDeviceSnapshot observed)
    { Status = status; Detail = detail; Observed = observed; }
    public PrinterDeviceReceiptStatus Status { get; }
    public string Detail { get; }
    public PrinterDeviceSnapshot Observed { get; }
}
