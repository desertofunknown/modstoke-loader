// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.IO;
using System.Text;

namespace Modstoke.Sons
{
    internal enum PlayerExposureWriter : byte
    {
        Capture, Wetness, OverrideAdded, OverrideRemoved, OverrideDisabled,
        RainStarted, RainStopped, SnowSet, RainToSnowChecked
    }

    // Identity is scoped to the originating capture, never a remote native pointer.
    internal readonly record struct PlayerExposureOverride(
        ulong Identity, string ScenePath, string Hierarchy, int ComponentIndex, float Wetness, float Rain);

    /// <summary>Immutable source observation. Authentication belongs to the migration envelope.</summary>
    internal sealed class PlayerExposureSnapshot
    {
        internal const int MaximumOverrides = 64;
        internal const int MaximumBytes = 65536 + PlayerExposureGraph.MaximumBytes;
        private static readonly UTF8Encoding TextEncoding = new(false, true);
        private readonly PlayerExposureOverride[] _overrides;
        private readonly byte[] _graph;
        internal byte[] CopyGraph() => (byte[])_graph.Clone();
        internal Guid CaptureId { get; }
        internal ulong SourceNetworkId { get; }
        internal ulong Sequence { get; }
        internal int SourceFrame { get; }
        internal PlayerExposureWriter Writer { get; }
        internal float RawWetness { get; }
        internal float RainIntensity { get; }
        internal float ExternalWetness { get; }
        internal bool Raining { get; }
        internal bool Snowing { get; }
        internal int OverrideCount => _overrides.Length;
        internal PlayerExposureOverride OverrideAt(int index) => _overrides[index];

        internal bool SameObservation(PlayerExposureSnapshot other)
        {
            if (other == null || CaptureId != other.CaptureId || SourceNetworkId != other.SourceNetworkId
                || Sequence != other.Sequence || SourceFrame != other.SourceFrame || Writer != other.Writer
                || RawWetness != other.RawWetness || RainIntensity != other.RainIntensity
                || ExternalWetness != other.ExternalWetness || Raining != other.Raining || Snowing != other.Snowing
                || OverrideCount != other.OverrideCount || !_graph.AsSpan().SequenceEqual(other._graph)) return false;
            for (int i = 0; i < OverrideCount; i++) if (_overrides[i] != other._overrides[i]) return false;
            return true;
        }

        internal PlayerExposureSnapshot(Guid capture, ulong source, ulong sequence, int frame,
            PlayerExposureWriter writer, float rawWetness, float rainIntensity,
            float externalWetness, bool raining, bool snowing, PlayerExposureOverride[] overrides, byte[] graph)
        {
            CaptureId = capture; SourceNetworkId = source; Sequence = sequence; SourceFrame = frame;
            Writer = writer; RawWetness = rawWetness; RainIntensity = rainIntensity;
            ExternalWetness = externalWetness; Raining = raining; Snowing = snowing;
            _overrides = overrides == null ? null : (PlayerExposureOverride[])overrides.Clone();
            _graph = graph == null ? null : (byte[])graph.Clone();
            if (!Valid()) throw new ArgumentException("Invalid actor exposure observation.");
        }

        internal byte[] Export()
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(0x32585045u); // EPX2 includes the captured native scene graph and actor history.
            writer.Write(CaptureId.ToByteArray()); writer.Write(SourceNetworkId); writer.Write(Sequence);
            writer.Write(SourceFrame); writer.Write((byte)Writer);
            writer.Write(RawWetness); writer.Write(RainIntensity); writer.Write(ExternalWetness);
            writer.Write((byte)((Raining ? 1 : 0) | (Snowing ? 2 : 0)));
            writer.Write((byte)_overrides.Length);
            foreach (var value in _overrides)
            {
                writer.Write(value.Identity); WriteText(writer, value.ScenePath); WriteText(writer, value.Hierarchy);
                writer.Write(value.ComponentIndex); writer.Write(value.Wetness); writer.Write(value.Rain);
            }
            writer.Write(_graph.Length); writer.Write(_graph);
            writer.Flush();
            if (stream.Length > MaximumBytes) throw new InvalidOperationException("Exposure observation exceeds the transfer bound.");
            return stream.ToArray();
        }

        internal static bool TryImport(byte[] bytes, out PlayerExposureSnapshot value, out string detail)
        {
            value = null;
            detail = "Exposure observation is malformed or exceeds its bound.";
            if (bytes == null || bytes.Length < 67 || bytes.Length > MaximumBytes) return false;
            try
            {
                using var stream = new MemoryStream(bytes, false);
                using var reader = new BinaryReader(stream, TextEncoding);
                if (reader.ReadUInt32() != 0x32585045u) return false;
                var id = new Guid(reader.ReadBytes(16));
                ulong source = reader.ReadUInt64(), sequence = reader.ReadUInt64();
                int frame = reader.ReadInt32();
                var kind = (PlayerExposureWriter)reader.ReadByte();
                float raw = reader.ReadSingle(), rain = reader.ReadSingle(), external = reader.ReadSingle();
                byte flags = reader.ReadByte(), count = reader.ReadByte();
                if (flags > 3 || count > MaximumOverrides) return false;
                var overrides = new PlayerExposureOverride[count];
                for (int i = 0; i < count; i++)
                    overrides[i] = new(reader.ReadUInt64(), ReadText(reader), ReadText(reader),
                        reader.ReadInt32(), reader.ReadSingle(), reader.ReadSingle());
                int graphLength = reader.ReadInt32();
                if (graphLength < 8 || graphLength > PlayerExposureGraph.MaximumBytes
                    || graphLength != stream.Length - stream.Position) return false;
                byte[] graph = reader.ReadBytes(graphLength);
                if (stream.Position != stream.Length) return false;
                value = new(id, source, sequence, frame, kind, raw, rain, external,
                    (flags & 1) != 0, (flags & 2) != 0, overrides, graph);
                detail = "Exposure observation decoded; this does not authenticate its source or approve its history.";
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
            { detail = "Exposure import refused: " + error.Message; return false; }
        }

        private bool Valid()
        {
            if (CaptureId == Guid.Empty || SourceNetworkId == 0 || SourceFrame < 0
                || Writer > PlayerExposureWriter.RainToSnowChecked
                || (Sequence == 0) != (Writer == PlayerExposureWriter.Capture)
                || !Unit(RawWetness) || !float.IsFinite(RainIntensity) || !float.IsFinite(ExternalWetness)
                || _overrides == null || _overrides.Length > MaximumOverrides
                || _graph == null || _graph.Length < 8 || _graph.Length > PlayerExposureGraph.MaximumBytes) return false;
            int bytes = 59 + _graph.Length;
            for (int i = 0; i < _overrides.Length; i++)
            {
                var item = _overrides[i];
                if (item.Identity == 0 || !TextValid(item.ScenePath) || !TextValid(item.Hierarchy)
                    || item.ComponentIndex < 0 || item.ComponentIndex > 63
                    || !float.IsFinite(item.Wetness) || !float.IsFinite(item.Rain)) return false;
                bytes += 24 + Encoding.UTF8.GetByteCount(item.ScenePath) + Encoding.UTF8.GetByteCount(item.Hierarchy);
                if (bytes > MaximumBytes) return false;
                for (int j = 0; j < i; j++)
                    if (_overrides[j].Identity == item.Identity) return false;
            }
            return true;
        }

        private static bool Unit(float value) => float.IsFinite(value) && value >= 0 && value <= 1;
        private static bool TextValid(string text)
            => !string.IsNullOrEmpty(text) && text.Length <= 4096 && Encoding.UTF8.GetByteCount(text) <= 4096;
        private static void WriteText(BinaryWriter writer, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            writer.Write((ushort)bytes.Length); writer.Write(bytes);
        }
        private static string ReadText(BinaryReader reader)
        {
            int length = reader.ReadUInt16();
            if (length is < 1 or > 4096) throw new InvalidDataException("Invalid exposure identity length.");
            byte[] bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return TextEncoding.GetString(bytes);
        }
    }

    public sealed partial class PlayerSurvivalSnapshot
    {
        internal PlayerExposureSnapshot ExposureBaseline { get; private set; }

        // Call only after the aggregate authenticated the same source envelope. Binding
        // never accepts a host-generated replacement or silently fabricates an empty list.
        internal bool TryBindExposureBaseline(PlayerExposureSnapshot baseline, out string detail)
        {
            detail = "The exposure baseline must match the same captured survival actor and external wetness.";
            if (baseline == null || baseline.Sequence != 0 || baseline.SourceNetworkId != SourceNetworkId
                || baseline.ExternalWetness != ReadScalar(PlayerSurvivalScalar.ExternalWetness)
                || (ExposureBaseline != null && !ExposureBaseline.SameObservation(baseline))) return false;
            if (ExposureBaseline != null) return true;
            ExposureBaseline = baseline;
            detail = "Exact exposure baseline bound; native identities and source authenticity still require aggregate qualification.";
            return true;
        }
    }
}
