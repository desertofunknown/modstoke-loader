// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using Armour = Modstoke.Sons.PlayerArmourAuthority;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerArmourRoute
    {
        internal enum Message : byte { Command = 1, Applied, Commit, Completed, Unconfirmed, Ready }

        // One bounded command is fragmented over the existing authenticated 256-byte channel.
        // A digest correlates data; only the current bridge peer supplies transport identity.
        internal sealed class Command
        {
            internal const int MaximumBytes = 1200;
            internal BoltMigrationPlan Plan { get; }
            internal ulong Actor { get; }
            internal string ArmourFingerprint { get; }
            internal string InventoryFingerprint { get; }
            internal Armour.Calculation Numbers { get; }
            internal string Fingerprint { get; }
            private readonly byte[] _bytes;
            internal ReadOnlySpan<byte> Bytes => _bytes;

            private Command(BoltMigrationPlan plan, ulong actor, string armour, string inventory,
                Armour.Calculation numbers)
            {
                Plan = plan; Actor = actor; ArmourFingerprint = armour; InventoryFingerprint = inventory; Numbers = numbers;
                using var stream = new MemoryStream();
                using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(0x32435241u); // ARC2
                    writer.Write(plan.Id.ToByteArray()); writer.Write(plan.Generation); writer.Write(plan.OldNetworkId);
                    writer.Write(plan.PrefabId); writer.Write(plan.StateTypeId); writer.Write(plan.ContractId.ToByteArray());
                    writer.Write(plan.ContractVersion); writer.Write(actor);
                    writer.Write(Convert.FromHexString(armour)); writer.Write(Convert.FromHexString(inventory));
                    writer.Write(numbers.Transaction); writer.Write(numbers.BeforeRevision); writer.Write(numbers.BeforeInventoryTransaction);
                    writer.Write(numbers.IncomingDamage); writer.Write(numbers.IgnoreArmour); writer.Write(numbers.Demonic);
                    WriteState(writer, numbers.Before); WriteState(writer, numbers.After);
                    writer.Write(numbers.RemainingDamage); writer.Write(numbers.GoldenApplied); writer.Write(numbers.ChangeCount);
                    for (int i = 0; i < numbers.ChangeCount; i++)
                    {
                        var change = numbers.GetChange(i);
                        writer.Write(change.OrderIndex); writer.Write(change.Before.Index);
                        writer.Write(change.Absorbed); writer.Write(change.Broken);
                    }
                }
                _bytes = stream.ToArray();
                if (_bytes.Length > MaximumBytes) throw new InvalidOperationException("Armour command exceeds the native slot bound.");
                Fingerprint = Convert.ToHexString(SHA256.HashData(_bytes));
            }

            internal static Command Capture(Armour.Calculation numbers)
                => new(numbers.Owner.Plan, numbers.Owner.ActorNetworkId, numbers.Owner.AcceptedBaselineFingerprint,
                    numbers.Owner.AcceptedInventoryFingerprint, numbers);

            internal static Command Read(byte[] bytes)
            {
                if (bytes == null || bytes.Length > MaximumBytes) throw new InvalidDataException("Armour command size is invalid.");
                using var reader = new BinaryReader(new MemoryStream(bytes, false));
                if (reader.ReadUInt32() != 0x32435241) throw new InvalidDataException("Unknown armour command.");
                var plan = new BoltMigrationPlan(new Guid(reader.ReadBytes(16)), reader.ReadUInt64(),
                    reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32(), new Guid(reader.ReadBytes(16)), reader.ReadUInt32());
                ulong actor = reader.ReadUInt64();
                string armour = Convert.ToHexString(reader.ReadBytes(32)), inventory = Convert.ToHexString(reader.ReadBytes(32));
                ulong transaction = reader.ReadUInt64(), revision = reader.ReadUInt64(), floor = reader.ReadUInt64();
                float damage = reader.ReadSingle(); bool ignore = reader.ReadBoolean(), demonic = reader.ReadBoolean();
                var before = ReadState(reader);
                if (!plan.IsValid || actor == 0 || transaction <= floor || revision == ulong.MaxValue)
                    throw new InvalidDataException("Invalid armour transaction identity.");
                var numbers = Armour.Calculate(null, transaction, revision, floor, before, damage, ignore, demonic);
                var command = new Command(plan, actor, armour, inventory, numbers);
                // Recompute every result and ordered change from the transmitted before-state.
                // Reject noncanonical booleans, trailing data and inconsistent numeric previews.
                if (!command.Bytes.SequenceEqual(bytes)) throw new InvalidDataException("Armour command numbers are inconsistent.");
                return command;
            }

            internal static bool SameState(Armour.State a, Armour.State b)
            {
                if (a == null || b == null || a.SlotCount != b.SlotCount || a.OrderCount != b.OrderCount
                    || a.NextAvailableOrderIndex != b.NextAvailableOrderIndex || a.WearingGoldenClothing != b.WearingGoldenClothing
                    || !SameFloat(a.GoldenGeneralMultiplier, b.GoldenGeneralMultiplier)
                    || !SameFloat(a.GoldenDemonicMultiplier, b.GoldenDemonicMultiplier)) return false;
                for (int i = 0; i < a.OrderCount; i++) if (a.GetOrderSlot(i) != b.GetOrderSlot(i)) return false;
                for (int i = 0; i < a.SlotCount; i++)
                {
                    var x = a.GetSlot(i); var y = b.GetSlot(i);
                    if (x.Index != y.Index || x.InventoryKey != y.InventoryKey || x.ItemId != y.ItemId
                        || !SameFloat(x.RemainingPoints, y.RemainingPoints)) return false;
                }
                return true;
            }

            internal static bool SameFloat(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);

            private static void WriteState(BinaryWriter writer, Armour.State state)
            {
                writer.Write(state.SlotCount); writer.Write(state.OrderCount); writer.Write(state.NextAvailableOrderIndex);
                writer.Write(state.WearingGoldenClothing); writer.Write(state.GoldenGeneralMultiplier); writer.Write(state.GoldenDemonicMultiplier);
                for (int i = 0; i < state.OrderCount; i++) writer.Write(state.GetOrderSlot(i));
                for (int i = 0; i < state.SlotCount; i++)
                {
                    var slot = state.GetSlot(i);
                    writer.Write(slot.Index); writer.Write(slot.InventoryKey); writer.Write(slot.ItemId); writer.Write(slot.RemainingPoints);
                }
            }

            private static Armour.State ReadState(BinaryReader reader)
            {
                int slots = reader.ReadInt32(), count = reader.ReadInt32(), cursor = reader.ReadInt32();
                bool golden = reader.ReadBoolean(); float general = reader.ReadSingle(), demonic = reader.ReadSingle();
                if (slots is < 0 or > 12 || count is < 0 or > 12 || !float.IsFinite(general) || !float.IsFinite(demonic))
                    throw new InvalidDataException("Invalid armour layout.");
                var order = new int[count]; var data = new Armour.Slot[slots];
                for (int i = 0; i < count; i++)
                {
                    order[i] = reader.ReadInt32();
                    if ((uint)order[i] >= (uint)slots || Array.IndexOf(order, order[i], 0, i) >= 0)
                        throw new InvalidDataException("Invalid configured armour order.");
                }
                for (int i = 0; i < slots; i++)
                {
                    data[i] = new(reader.ReadInt32(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadSingle());
                    if (data[i].Index != i || data[i].ItemId < 0 || (data[i].ItemId == 0) != (data[i].InventoryKey == 0)
                        || !float.IsFinite(data[i].RemainingPoints)) throw new InvalidDataException("Invalid armour slot.");
                    for (int j = 0; j < i; j++)
                        if (data[i].InventoryKey != 0 && data[i].InventoryKey == data[j].InventoryKey)
                            throw new InvalidDataException("Repeated armour inventory key.");
                }
                return new(data, order, cursor, golden, general, demonic);
            }
        }

        private readonly record struct Frame(Message Kind, Guid Migration, ulong Generation, ulong Actor,
            ulong Transaction, ulong Revision, string Fingerprint, ushort Offset = 0, ushort Total = 0)
        {
            internal const int HeaderBytes = 92, ChunkBytes = Datagrams.MaxPayloadBytes - HeaderBytes;
            internal static bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length >= HeaderBytes
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == 0x5241504D; // MPAR
            internal bool SameCommand(Frame other) => Migration == other.Migration && Generation == other.Generation
                && Actor == other.Actor && Transaction == other.Transaction && Revision == other.Revision && Fingerprint == other.Fingerprint;
            internal static Frame For(Message kind, Command command) => new(kind, command.Plan.Id, command.Plan.Generation,
                command.Actor, command.Numbers.Transaction, command.Numbers.BeforeRevision, command.Fingerprint);
            internal byte[] Write(ReadOnlySpan<byte> body = default)
            {
                var bytes = new byte[HeaderBytes + body.Length]; var span = bytes.AsSpan();
                BinaryPrimitives.WriteUInt32LittleEndian(span, 0x5241504D); span[4] = 1; span[5] = (byte)Kind;
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6), (ushort)body.Length);
                Migration.TryWriteBytes(span.Slice(8));
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(24), Generation);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(32), Actor);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(40), Transaction);
                BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(48), Revision);
                Convert.FromHexString(Fingerprint).CopyTo(span.Slice(56, 32));
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(88), Offset);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(90), Total);
                body.CopyTo(span.Slice(HeaderBytes)); return bytes;
            }
            internal static bool TryRead(ReadOnlySpan<byte> bytes, out Frame frame)
            {
                frame = default;
                if (!Matches(bytes) || bytes.Length > Datagrams.MaxPayloadBytes || bytes[4] != 1
                    || bytes[5] is < 1 or > 6 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6)) != bytes.Length - HeaderBytes) return false;
                frame = new((Message)bytes[5], new Guid(bytes.Slice(8, 16)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(24)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32)), BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(40)),
                    BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(48)), Convert.ToHexString(bytes.Slice(56, 32)),
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(88)), BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(90)));
                if (frame.Kind != Message.Command) return frame.Offset == 0 && frame.Total == 0 && bytes.Length == HeaderBytes;
                return frame.Total is > 0 and <= Command.MaximumBytes && frame.Offset < frame.Total
                    && frame.Offset % ChunkBytes == 0 && bytes.Length - HeaderBytes == Math.Min(ChunkBytes, frame.Total - frame.Offset);
            }
        }
    }
}
