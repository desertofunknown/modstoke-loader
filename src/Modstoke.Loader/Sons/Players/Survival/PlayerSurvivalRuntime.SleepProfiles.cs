// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using global::Sons.StatSystem;
using UnityEngine;

namespace Modstoke.Sons
{
    public readonly record struct PlayerSurvivalOverride(int StatIndex,
        bool OverridesFade, bool OverridesRegeneration, float Fade, float Regeneration,
        float RegenerationMultiplier);

    /// <summary>Ordered native override entries, including disabled fields and mutable multipliers.</summary>
    public sealed class PlayerSurvivalOverrideProfile
    {
        internal const int EncodedLength = 4 + 9 * 20;
        private readonly PlayerSurvivalOverride[] _entries;
        public bool IsPresent { get; }
        public int Count => _entries.Length;
        public PlayerSurvivalOverride this[int index] => _entries[index];

        private PlayerSurvivalOverrideProfile(bool present, PlayerSurvivalOverride[] entries)
        { IsPresent = present; _entries = entries; }

        internal bool IsValid
        {
            get
            {
                if (Count > 9 || (!IsPresent && Count != 0)) return false;
                foreach (var entry in _entries)
                    if ((uint)entry.StatIndex >= 9 || !float.IsFinite(entry.Fade)
                        || !float.IsFinite(entry.Regeneration) || !float.IsFinite(entry.RegenerationMultiplier)
                        || !float.IsFinite(entry.Regeneration * entry.RegenerationMultiplier)) return false;
                return true;
            }
        }

        internal void Encode(Span<byte> bytes)
        {
            bytes.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(bytes, IsPresent ? Count : -1);
            for (int i = 0; i < Count; i++)
            {
                var entry = _entries[i];
                var slot = bytes.Slice(4 + i * 20, 20);
                BinaryPrimitives.WriteInt32LittleEndian(slot, entry.StatIndex);
                BinaryPrimitives.WriteUInt32LittleEndian(slot.Slice(4),
                    (entry.OverridesFade ? 1u : 0u) | (entry.OverridesRegeneration ? 2u : 0u));
                BinaryPrimitives.WriteSingleLittleEndian(slot.Slice(8), entry.Fade);
                BinaryPrimitives.WriteSingleLittleEndian(slot.Slice(12), entry.Regeneration);
                BinaryPrimitives.WriteSingleLittleEndian(slot.Slice(16), entry.RegenerationMultiplier);
            }
        }

        internal static bool TryDecode(ReadOnlySpan<byte> bytes, out PlayerSurvivalOverrideProfile profile)
        {
            profile = null;
            if (bytes.Length != EncodedLength) return false;
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (count < -1 || count > 9) return false;
            var entries = new PlayerSurvivalOverride[Math.Max(0, count)];
            for (int i = 0; i < entries.Length; i++)
            {
                var slot = bytes.Slice(4 + i * 20, 20);
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(slot.Slice(4));
                if ((flags & ~3u) != 0) return false;
                entries[i] = new(BinaryPrimitives.ReadInt32LittleEndian(slot), (flags & 1) != 0, (flags & 2) != 0,
                    BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(8)),
                    BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(12)),
                    BinaryPrimitives.ReadSingleLittleEndian(slot.Slice(16)));
            }
            foreach (byte unused in bytes.Slice(4 + entries.Length * 20))
                if (unused != 0) return false;
            var candidate = new PlayerSurvivalOverrideProfile(count >= 0, entries);
            if (!candidate.IsValid) return false;
            profile = candidate;
            return true;
        }

        internal static PlayerSurvivalOverrideProfile Capture(StatOverrideSet source, Stat[] stats)
        {
            if (source is null) return new(false, Array.Empty<PlayerSurvivalOverride>());
            if (source.WasCollected || !source || source._stats == null || source._stats.WasCollected
                || source._stats.Count > 9)
                throw new InvalidOperationException("The native override entries are unavailable or exceed the nine-stat contract.");
            var entries = new PlayerSurvivalOverride[source._stats.Count];
            for (int i = 0; i < entries.Length; i++) entries[i] = ReadEntry(source._stats[i], stats);
            var result = new PlayerSurvivalOverrideProfile(true, entries);
            if (!result.IsValid) throw new InvalidOperationException("A native override contains invalid values.");
            return result;
        }

        internal bool Matches(StatOverrideSet source, Stat[] stats)
        {
            if (!IsPresent) return source is null;
            if (source is null || source.WasCollected || !source || source._stats == null
                || source._stats.WasCollected || source._stats.Count != Count) return false;
            for (int i = 0; i < Count; i++)
                if (ReadEntry(source._stats[i], stats) != _entries[i]) return false;
            return true;
        }

        internal StatOverrideSet CreateActorCopy(Stat[] stats)
        {
            if (!IsPresent) return null;
            var copy = ScriptableObject.CreateInstance<StatOverrideSet>();
            if (copy is null || copy.WasCollected || !copy)
                throw new InvalidOperationException("An actor-local stat profile could not be created.");
            try
            {
                copy._stats = new Il2CppSystem.Collections.Generic.List<StatOverride>();
                foreach (var entry in _entries)
                    copy._stats.Add(new StatOverride
                    {
                        _assignedType = stats[entry.StatIndex].GetName(),
                        _overrideFadeRate = entry.OverridesFade, _fadeRate = entry.Fade,
                        _overrideRegenRate = entry.OverridesRegeneration, _regenRate = entry.Regeneration,
                        _regenRateMultiplier = entry.RegenerationMultiplier
                    });
                if (!Matches(copy, stats)) throw new InvalidOperationException("Actor-local override readback differs.");
                return copy;
            }
            catch { UnityEngine.Object.Destroy(copy); throw; }
        }

        private static PlayerSurvivalOverride ReadEntry(StatOverride entry, Stat[] stats)
        {
            if (entry == null || entry.WasCollected || string.IsNullOrEmpty(entry._assignedType))
                throw new InvalidOperationException("A native stat override entry is unavailable.");
            int index = -1;
            for (int i = 0; i < stats.Length; i++)
            {
                if (stats[i] == null || stats[i].WasCollected)
                    throw new InvalidOperationException("A registered stat name is unavailable.");
                string name = stats[i].GetName();
                if (string.IsNullOrEmpty(name)) throw new InvalidOperationException("A registered stat name is unavailable.");
                if (!string.Equals(entry._assignedType, name, StringComparison.Ordinal)) continue;
                if (index != -1) throw new InvalidOperationException("A native stat override name is ambiguous.");
                index = i;
            }
            if (index == -1) throw new InvalidOperationException("A native override targets an unregistered stat.");
            return new(index, entry._overrideFadeRate, entry._overrideRegenRate,
                entry._fadeRate, entry._regenRate, entry._regenRateMultiplier);
        }
    }
}
