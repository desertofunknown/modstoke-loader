// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalSnapshot
    {
        public PlayerWaterContactSnapshot WaterContacts { get; private set; }

        /// <summary>The adapter must transport this independent payload under the survival snapshot's authenticated source reservation.</summary>
        public bool TryCaptureWaterContacts(PlayerWaterContactAuthority owner,
            out PlayerWaterContactSnapshot contacts, out string detail)
        {
            contacts = null;
            detail = "The survival snapshot and water hold must identify the same original source.";
            if (owner == null || !owner.TryCapture(out var candidate, out detail)) return false;
            if (!TryBindWaterContacts(candidate, out detail)) return false;
            contacts = WaterContacts;
            return true;
        }

        internal bool TryBindWaterContacts(PlayerWaterContactSnapshot candidate, out string detail)
        {
            detail = "Water contacts must identify the same captured survival actor and configuration.";
            if (candidate == null || candidate.SourceNetworkId != SourceNetworkId || candidate.PrefabId != PrefabId
                || candidate.StateTypeId != StateTypeId || Water == null)
            { detail = "Survival and water contact snapshots do not identify the same source."; return false; }
            var s = candidate.State;
            if (s.WaterMask != Water.WaterLayerMask || s.BlockerMask != Water.BlockerLayerMask
                || s.CombinedMask != Water.WaterAndBlockerLayerMask || s.OceanMode != (int)Water.NativeOceanMode
                || BitConverter.SingleToInt32Bits(s.ObjectWidth) != BitConverter.SingleToInt32Bits(Water.ObjectWidth)
                || (candidate.Tags?.Count ?? 0) > 1
                || !string.Equals(candidate.Tags == null || candidate.Tags.Count == 0 ? null : candidate.Tags[0],
                    Water.RequiredWaterTag, StringComparison.Ordinal))
            { detail = "Survival water configuration differs from the held contact history; recapture under the same hold."; return false; }
            if (WaterContacts != null)
            {
                if (!WaterContacts.Export().AsSpan().SequenceEqual(candidate.Export()))
                { detail = "The retained water contact baseline cannot be replaced."; return false; }
                return true;
            }
            WaterContacts = candidate;
            detail = "Exact water contacts bound to the survival observation; server restoration remains separate.";
            return true;
        }
    }

    public enum WaterColliderIdentityKind : byte
    {
        UnresolvedSceneCollider,
        BoltRootCollider,
        DestroyedCollider,
        SceneCollider,
        BoltDescendantCollider
    }

    /// <summary>
    /// Native-free identity export. A scene/Bolt route selects a candidate, never admission:
    /// native filters, full pose, shape and mesh content must also match. Shape 1/2/3/4
    /// means box/sphere/capsule/mesh. Unknown or unreadable geometry remains unresolved.
    /// </summary>
    public sealed record WaterColliderIdentity(
        WaterColliderIdentityKind Kind, ulong NetworkId, int PrefabId, int StateTypeId,
        string ScenePath, string HierarchyPath, int Shape, int Layer, string Tag,
        bool Enabled, bool Active, bool IsTrigger, bool IsOcean,
        Vector3 Position, Quaternion Rotation, Vector3 Scale, Vector3 Center, Vector3 Size,
        float Radius, float Height, int Direction)
    {
        public string Route { get; init; }
        public int ComponentIndex { get; init; }
        public string GeometryDigest { get; init; }
    }

    /// <summary>
    /// Exact fields, not recomputed water classification. Frame values belong to SourceFrame.
    /// OceanSeaLevel observes the shared static cache; it is not actor-owned restoration storage.
    /// InWater retains _isInWater (surface cache), not the native InWater contact getter.
    /// </summary>
    public sealed record WaterSensorContinuation(
        int SourceFrame, int CacheFrame, int CaveFrame, int CaveInterval, int StayInterval, float StayOffset,
        bool InWater, bool InOcean, bool InCave, bool AutoUpdate, bool RefreshOnTrigger,
        int OceanMode, int WaterMask, int BlockerMask, int CombinedMask, float ObjectWidth,
        float WaterHeight, float WaterDepth, float VolumeLevel, float OceanSeaLevel,
        Vector3 Normal, Vector3 SurfaceVelocity, Vector3 LastManualPosition,
        Vector3 SensorPosition, Quaternion SensorRotation, Vector3 SensorScale,
        bool HasBody, Vector3 BodyPosition, Quaternion BodyRotation,
        bool HasCaveOverride, bool HasHeightHelper, bool HasFlowHelper, bool RunnerRegistered);

    /// <summary>
    /// Ordered contact/cached-state export from an exact held sensor. Indices address Identities;
    /// -1 is a native null reference. Null lists remain null, distinct from initialized empty lists.
    /// No pointer, Unity instance ID, provider handle or delegate crosses this boundary.
    /// </summary>
    public sealed partial class PlayerWaterContactSnapshot
    {
        internal const int MaximumContacts = 256, MaximumScratch = 4096, MaximumStringBytes = 4096;
        public const int MaximumExportBytes = 2 * 1024 * 1024;
        private const uint Magic = 0x32435750; // PWC2, independent of the survival codec.
        private static readonly UTF8Encoding TextEncoding = new(false, true);
        public ulong SourceNetworkId { get; }
        public int PrefabId { get; }
        public int StateTypeId { get; }
        public WaterSensorContinuation State { get; }
        public WaterColliderIdentity StayCollider { get; }
        public IReadOnlyList<WaterColliderIdentity> Identities { get; }
        public IReadOnlyList<int> Tracked { get; }
        public IReadOnlyList<int> TrackedOcean { get; }
        public IReadOnlyList<int> Scratch { get; }
        public IReadOnlyList<string> Tags { get; }
        public int LastWaterCollider { get; }
        internal WaterContactRuntimeState Runtime { get; }

        internal PlayerWaterContactSnapshot(ulong source, int prefab, int stateType,
            WaterSensorContinuation state, WaterColliderIdentity stay,
            WaterColliderIdentity[] identities, int[] tracked, int[] ocean, int[] scratch,
            string[] tags, int last, WaterContactRuntimeState runtime)
        {
            SourceNetworkId = source; PrefabId = prefab; StateTypeId = stateType; State = state;
            StayCollider = stay; Identities = Array.AsReadOnly((WaterColliderIdentity[])identities.Clone());
            Tracked = Copy(tracked); TrackedOcean = Copy(ocean); Scratch = Copy(scratch);
            Tags = tags == null ? null : Array.AsReadOnly((string[])tags.Clone()); LastWaterCollider = last;
            Runtime = runtime;
        }

        private static IReadOnlyList<int> Copy(int[] values)
            => values == null ? null : Array.AsReadOnly((int[])values.Clone());

        /// <summary>Use the remaining space AFTER every other field in the 1 MiB outer envelope.</summary>
        public bool TryExportForEnvelope(int remainingBytes, out byte[] bytes)
        {
            bytes = null;
            if (remainingBytes < 0 || remainingBytes > 1024 * 1024) return false;
            try
            {
                byte[] candidate = Export();
                if (candidate.Length > remainingBytes) return false;
                bytes = candidate; return true;
            }
            catch (InvalidDataException) { return false; }
        }

        /// <summary>Export only, not evidence that a client observation was approved by the server.</summary>
        public byte[] Export()
        {
            using var stream = new MemoryStream();
            using var w = new BinaryWriter(stream, TextEncoding, true);
            w.Write(Magic); w.Write(SourceNetworkId); w.Write(PrefabId); w.Write(StateTypeId);
            var s = State;
            w.Write(s.SourceFrame); w.Write(s.CacheFrame); w.Write(s.CaveFrame); w.Write(s.CaveInterval);
            w.Write(s.StayInterval); w.Write(s.StayOffset); w.Write(s.InWater); w.Write(s.InOcean); w.Write(s.InCave);
            w.Write(s.AutoUpdate); w.Write(s.RefreshOnTrigger); w.Write(s.OceanMode);
            w.Write(s.WaterMask); w.Write(s.BlockerMask); w.Write(s.CombinedMask); w.Write(s.ObjectWidth);
            w.Write(s.WaterHeight); w.Write(s.WaterDepth); w.Write(s.VolumeLevel); w.Write(s.OceanSeaLevel);
            V(w, s.Normal); V(w, s.SurfaceVelocity); V(w, s.LastManualPosition);
            V(w, s.SensorPosition); Q(w, s.SensorRotation); V(w, s.SensorScale);
            w.Write(s.HasBody); V(w, s.BodyPosition); Q(w, s.BodyRotation);
            w.Write(s.HasCaveOverride); w.Write(s.HasHeightHelper); w.Write(s.HasFlowHelper); w.Write(s.RunnerRegistered);
            WriteIdentity(w, StayCollider); w.Write(Identities.Count);
            foreach (var identity in Identities) WriteIdentity(w, identity);
            WriteIndices(w, Tracked); WriteIndices(w, TrackedOcean); WriteIndices(w, Scratch);
            w.Write(Tags?.Count ?? -1);
            if (Tags != null) foreach (var tag in Tags) WriteText(w, tag);
            w.Write(LastWaterCollider);
            WriteRuntime(w, Runtime);
            if (stream.Length > MaximumExportBytes) throw new InvalidDataException("Water contact export exceeds its bound.");
            return stream.ToArray();
        }

        public static bool TryImport(ReadOnlySpan<byte> bytes, out PlayerWaterContactSnapshot snapshot)
        {
            snapshot = null;
            if (bytes.Length > MaximumExportBytes || bytes.Length < 24) return false;
            try
            {
                using var stream = new MemoryStream(bytes.ToArray(), false);
                using var r = new BinaryReader(stream, TextEncoding);
                if (r.ReadUInt32() != Magic) return false;
                ulong source = r.ReadUInt64(); int prefab = r.ReadInt32(), stateType = r.ReadInt32();
                var state = new WaterSensorContinuation(
                    r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), F(r),
                    B(r), B(r), B(r), B(r), B(r), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
                    F(r), F(r), F(r), F(r), F(r), V(r), V(r), V(r), V(r), Q(r), V(r), B(r), V(r), Q(r),
                    B(r), B(r), B(r), B(r));
                if (source == 0 || state.SourceFrame < 0 || state.OceanMode is < 0 or > 2) return false;
                var stay = ReadIdentity(r);
                int count = Count(r, MaximumContacts, false);
                var identities = new WaterColliderIdentity[count];
                for (int i = 0; i < count; i++) identities[i] = ReadIdentity(r) ?? throw new InvalidDataException();
                var tracked = ReadIndices(r, MaximumContacts, count);
                var ocean = ReadIndices(r, MaximumContacts, count);
                var scratch = ReadIndices(r, MaximumScratch, count);
                int tagCount = Count(r, MaximumContacts, true);
                string[] tags = tagCount < 0 ? null : new string[tagCount];
                if (tags != null) for (int i = 0; i < tags.Length; i++) tags[i] = ReadText(r);
                int last = r.ReadInt32();
                var runtime = ReadRuntime(r);
                if (last < -1 || last >= count || stream.Position != stream.Length) return false;
                snapshot = new(source, prefab, stateType, state, stay, identities, tracked, ocean, scratch, tags, last, runtime);
                return true;
            }
            catch (Exception error) when (error is IOException or ArgumentException or OverflowException)
            { return false; }
        }

        private static void WriteIdentity(BinaryWriter w, WaterColliderIdentity i)
        {
            w.Write(i != null); if (i == null) return;
            w.Write((byte)i.Kind); w.Write(i.NetworkId); w.Write(i.PrefabId); w.Write(i.StateTypeId);
            WriteText(w, i.ScenePath); WriteText(w, i.HierarchyPath); w.Write(i.Shape); w.Write(i.Layer); WriteText(w, i.Tag);
            w.Write(i.Enabled); w.Write(i.Active); w.Write(i.IsTrigger); w.Write(i.IsOcean);
            V(w, i.Position); Q(w, i.Rotation); V(w, i.Scale); V(w, i.Center); V(w, i.Size);
            w.Write(i.Radius); w.Write(i.Height); w.Write(i.Direction);
            WriteText(w, i.Route); w.Write(i.ComponentIndex); WriteText(w, i.GeometryDigest);
        }

        private static WaterColliderIdentity ReadIdentity(BinaryReader r)
        {
            if (!B(r)) return null;
            var kind = (WaterColliderIdentityKind)r.ReadByte();
            ulong id = r.ReadUInt64(); int prefab = r.ReadInt32(), state = r.ReadInt32();
            string scene = ReadText(r), path = ReadText(r); int shape = r.ReadInt32(), layer = r.ReadInt32();
            string tag = ReadText(r);
            var result = new WaterColliderIdentity(kind, id, prefab, state, scene, path, shape, layer,
                tag, B(r), B(r), B(r), B(r), V(r), Q(r), V(r), V(r), V(r), F(r), F(r), r.ReadInt32())
            { Route = ReadText(r), ComponentIndex = r.ReadInt32(), GeometryDigest = ReadText(r) };
            if ((int)kind is < 0 or > 4
                || ((kind == WaterColliderIdentityKind.BoltRootCollider
                    || kind == WaterColliderIdentityKind.BoltDescendantCollider) && id == 0)
                || shape is < 0 or > 4 || layer is < 0 or > 31 || result.ComponentIndex < 0
                || result.ComponentIndex > MaximumContacts) throw new InvalidDataException();
            return result;
        }

        private static void WriteIndices(BinaryWriter w, IReadOnlyList<int> values)
        { w.Write(values?.Count ?? -1); if (values != null) foreach (int value in values) w.Write(value); }
        private static int[] ReadIndices(BinaryReader r, int maximum, int identities)
        {
            int count = Count(r, maximum, true);
            if (count < 0) return null;
            var result = new int[count];
            for (int i = 0; i < count; i++)
            { result[i] = r.ReadInt32(); if (result[i] < -1 || result[i] >= identities) throw new InvalidDataException(); }
            return result;
        }
        private static int Count(BinaryReader r, int maximum, bool nullable)
        { int n = r.ReadInt32(); if (n < (nullable ? -1 : 0) || n > maximum) throw new InvalidDataException(); return n; }
        private static void WriteText(BinaryWriter w, string text)
        {
            if (text == null) { w.Write(-1); return; }
            byte[] bytes = TextEncoding.GetBytes(text);
            if (bytes.Length > MaximumStringBytes) throw new InvalidDataException("Water contact text exceeds its bound.");
            w.Write(bytes.Length); w.Write(bytes);
        }
        private static string ReadText(BinaryReader r)
        {
            int length = Count(r, MaximumStringBytes, true); if (length < 0) return null;
            var bytes = r.ReadBytes(length); if (bytes.Length != length) throw new EndOfStreamException();
            return TextEncoding.GetString(bytes);
        }
        private static bool B(BinaryReader r)
        { byte b = r.ReadByte(); return b <= 1 ? b != 0 : throw new InvalidDataException(); }
        private static float F(BinaryReader r)
        { float f = r.ReadSingle(); return float.IsFinite(f) ? f : throw new InvalidDataException(); }
        private static void V(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
        private static void Q(BinaryWriter w, Quaternion q) { w.Write(q.X); w.Write(q.Y); w.Write(q.Z); w.Write(q.W); }
        private static Vector3 V(BinaryReader r) => new(F(r), F(r), F(r));
        private static Quaternion Q(BinaryReader r) => new(F(r), F(r), F(r), F(r));
    }
}
