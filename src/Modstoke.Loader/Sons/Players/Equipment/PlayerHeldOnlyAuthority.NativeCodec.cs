// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerHeldOnlyAuthority
    {
        // Parent owns authenticated framing/fragmentation. Oversize observations fail whole;
        // nothing is truncated and decoding is never evidence of sender authority.
        public const int MaximumNativeActionBytes = 16 * Snapshot.MaximumBytes;
        private static readonly JsonSerializerOptions JsonReadOptions = new()
        {
            MaxDepth = 16,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };

        public sealed partial class NativeCommand
        {
            public byte[] Export() => EncodeNative(new NativeCommandDocument
            { Transaction = Transaction, Kind = Kind, Drop = Drop, Before = Before.Export(), After = After.Export() });

            public static bool TryRead(BoltMigrationPlan plan, byte[] bytes,
                out NativeCommand command, out string detail)
            {
                command = null;
                try
                {
                    var document = DecodeNative<NativeCommandDocument>(bytes);
                    Require(document.Version == 1, "Unsupported held-only native command version.");
                    return TryRead(plan, document.Transaction, document.Kind, document.Drop,
                        document.Before, document.After, out command, out detail);
                }
                catch (Exception error) { detail = "Held-only command rejected: " + error.Message; return false; }
            }
        }

        public sealed partial class NativeReceipt
        {
            public byte[] Export()
            {
                var drops = new NativeDropDocument[_drops.Length];
                for (int i = 0; i < drops.Length; i++)
                {
                    var drop = _drops[i];
                    drops[i] = new NativeDropDocument { ItemId = drop.ItemId, PrefabId = drop.PrefabId,
                        Position = Pack(drop.Position), Rotation = new[] { drop.Rotation.x, drop.Rotation.y, drop.Rotation.z, drop.Rotation.w },
                        Velocity = Pack(drop.Velocity), Torque = Pack(drop.Torque), Modules = drop.NativeModuleSaveData, ModuleState = drop.ModuleState };
                }
                return EncodeNative(new NativeReceiptDocument { Transaction = Command.Transaction,
                    CommandFingerprint = Command.Fingerprint, After = After.Export(), Strength = StrengthGain,
                    RendererCount = BoltRendererCount, Drops = drops });
            }

            public static bool TryRead(NativeCommand command, byte[] bytes, out NativeReceipt receipt, out string detail)
            {
                receipt = null;
                try
                {
                    Require(command != null, "Original dispatched command is required.");
                    var document = DecodeNative<NativeReceiptDocument>(bytes);
                    Require(document.Version == 1 && document.Transaction == command.Transaction
                        && document.CommandFingerprint == command.Fingerprint && document.Drops != null
                        && document.Drops.Length == command.DropCount && float.IsFinite(document.Strength)
                        && document.Strength >= 0 && (command.Kind == MutationKind.Lift || document.Strength == 0)
                        && document.RendererCount >= 0 && document.RendererCount <= command.After.State.Count,
                        "Receipt does not match the dispatched native command.");
                    Require(Snapshot.TryReadObservation(command.Before.Plan, document.After, out var after, out detail)
                        && after.Fingerprint == command.After.Fingerprint, "Native receipt after-state differs.");
                    var drops = new NativeDropEffect[document.Drops.Length];
                    for (int i = 0; i < drops.Length; i++)
                    {
                        var drop = document.Drops[i];
                        Require(drop != null && drop.ItemId == command.Before.State.ItemId && drop.PrefabId >= 0
                            && drop.Modules != null && drop.Modules.Length <= Snapshot.MaximumBytes && drop.ModuleState != null,
                            "Outgoing drop identity/module observation is incomplete.");
                        // Reuse the baseline module validator without creating a new native item.
                        _ = new Snapshot(new Document { Plan = command.Before.Plan, Catalog = command.Before.GetCatalog(),
                            State = command.Before.State, DropProfile = command.Before.DropProfile,
                            Modules = drop.Modules, ModuleState = drop.ModuleState });
                        var position = ReadVector(drop.Position);
                        var velocity = ReadVector(drop.Velocity);
                        var torque = ReadVector(drop.Torque);
                        Require(Math.Abs(torque.x) <= 512f && Math.Abs(torque.y) <= 512f && Math.Abs(torque.z) <= 512f
                            && drop.Rotation != null && drop.Rotation.Length == 4, "Invalid native torque/rotation.");
                        foreach (float component in drop.Rotation) Require(float.IsFinite(component), "Non-finite native rotation.");
                        var rotation = new Quaternion(drop.Rotation[0], drop.Rotation[1], drop.Rotation[2], drop.Rotation[3]);
                        drops[i] = new NativeDropEffect(i, drop.ItemId, drop.PrefabId, position, rotation, velocity, torque, drop.Modules, drop.ModuleState);
                    }
                    receipt = new NativeReceipt(command, after, document.Strength, document.RendererCount, drops);
                    detail = "Exact native receipt decoded; authenticate source and settle concrete destination/stat owners separately.";
                    return true;
                }
                catch (Exception error) { detail = "Held-only receipt rejected: " + error.Message; return false; }
            }
        }

        private static byte[] EncodeNative<T>(T document)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
            Require(bytes.Length <= MaximumNativeActionBytes, "Held-only native payload exceeds its bound; no partial payload emitted.");
            return bytes;
        }

        private static T DecodeNative<T>(byte[] bytes) where T : class
        {
            Require(bytes != null && bytes.Length is > 0 and <= MaximumNativeActionBytes, "Invalid held-only native payload size.");
            using (var parsed = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 })) Snapshot.ValidateJson(parsed.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, JsonReadOptions)
                ?? throw new InvalidOperationException("Missing native payload document.");
        }

        private static float[] Pack(Vector3 value) => new[] { value.x, value.y, value.z };
        private static Vector3 ReadVector(float[] value)
        {
            Require(value != null && value.Length == 3, "Missing native vector.");
            foreach (float component in value) Require(float.IsFinite(component), "Non-finite native vector.");
            return new Vector3(value[0], value[1], value[2]);
        }

        internal sealed class NativeCommandDocument
        {
            public NativeCommandDocument() { }
            [JsonRequired] public int Version { get; set; } = 1;
            [JsonRequired] public ulong Transaction { get; set; }
            [JsonRequired] public MutationKind Kind { get; set; }
            [JsonRequired] public bool Drop { get; set; }
            [JsonRequired] public byte[] Before { get; set; }
            [JsonRequired] public byte[] After { get; set; }
        }
        internal sealed class NativeReceiptDocument
        {
            public NativeReceiptDocument() { }
            [JsonRequired] public int Version { get; set; } = 1;
            [JsonRequired] public ulong Transaction { get; set; }
            [JsonRequired] public string CommandFingerprint { get; set; }
            [JsonRequired] public byte[] After { get; set; }
            [JsonRequired] public float Strength { get; set; }
            [JsonRequired] public int RendererCount { get; set; }
            [JsonRequired] public NativeDropDocument[] Drops { get; set; }
        }
        internal sealed class NativeDropDocument
        {
            public NativeDropDocument() { }
            [JsonRequired] public int ItemId { get; set; }
            [JsonRequired] public int PrefabId { get; set; }
            [JsonRequired] public float[] Position { get; set; }
            [JsonRequired] public float[] Rotation { get; set; }
            [JsonRequired] public float[] Velocity { get; set; }
            [JsonRequired] public float[] Torque { get; set; }
            [JsonRequired] public string Modules { get; set; }
            [JsonRequired] public PlayerInventoryModuleState ModuleState { get; set; }
        }
    }
}
