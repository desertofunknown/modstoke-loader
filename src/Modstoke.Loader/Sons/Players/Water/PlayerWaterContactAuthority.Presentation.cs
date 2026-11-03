// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using global::Sons.Physics;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modstoke.Sons
{
    public sealed partial class PlayerWaterContactAuthority
    {
        private PlayerWaterPresentation _presentation;
        private GameObject _presentationRoot;
        private FirstPersonCharacter _presentationCharacter;
        private PlayerAnimatorControl _presentationAnimation;
        private camFollowHead _presentationCamera;
        private Rigidbody _presentationBody;
        private int _presentationScene, _presentationSensorId;
        private Guid _retiredMigrationId;
        private ulong _retiredGeneration;

        private void CapturePresentationRig()
        {
            _presentationRoot = LocalPlayer.GameObject;
            _presentationCharacter = LocalPlayer.FpCharacter;
            _presentationAnimation = LocalPlayer.AnimControl;
            _presentationCamera = LocalPlayer.CamFollowHead;
            _presentationBody = LocalPlayer.Rigidbody;
            _presentationScene = _presentationRoot.scene.handle;
            _presentationSensorId = _sensor.GetInstanceID();
        }

        internal bool TryBindPresentation(PlayerWaterPresentation presentation)
        {
            if (_presentation != null || presentation == null || !PresentationCurrent()) return false;
            _presentation = presentation;
            return true;
        }

        // Deliberately does not use SourceBound, LocalPlayer.Entity or the old attachment:
        // the original rig outlives source entity retirement and LocalPlayer rebinding.
        internal bool PresentationCurrent()
        {
            if (_released || !_retiredSource || !_runnerRemoved || _retiringSource || _offThreadWriter != 0
                || Environment.CurrentManagedThreadId != _hookThread || !_hooksReady
                || !Alive(_sensor) || _sensor.enabled || _sensor.GetInstanceID() != _presentationSensorId
                || !Owners.TryGetValue(_sensorPointer, out var held) || !ReferenceEquals(held, this)
                || (NativeDepth.TryGetValue(_sensorPointer, out int depth) && depth != 0)
                || !Same(_sensor, LocalPlayer._WaterLevelSensor_k__BackingField)
                || !Alive(LocalPlayer._instance) || !Same(_sensor, LocalPlayer._instance._waterLevelSensor)
                || !Same(_presentationRoot, LocalPlayer.GameObject) || _presentationRoot.scene.handle != _presentationScene
                || !_presentationRoot.scene.isLoaded || !Same(_presentationCharacter, LocalPlayer.FpCharacter)
                || !Same(_presentationAnimation, LocalPlayer.AnimControl) || !Same(_presentationCamera, LocalPlayer.CamFollowHead)
                || !Same(_presentationBody, LocalPlayer.Rigidbody) || !Same(_sensor._rigidBody, _presentationBody)
                || !Same(_presentationCharacter._rigidbody, _presentationBody)) return false;
            return true;
        }

        internal bool MatchesPresentationSource(PlayerBridge bridge)
            => bridge != null && !bridge.IsServer && _baseline != null
                && bridge.Plan.Id == _retiredMigrationId && bridge.Plan.Generation == _retiredGeneration
                && bridge.Plan.OldNetworkId == _baseline.Snapshot.SourceNetworkId
                && bridge.Plan.PrefabId == _baseline.Snapshot.PrefabId
                && bridge.Plan.StateTypeId == _baseline.Snapshot.StateTypeId && PresentationCurrent();

        internal void ApplyPresentation(PlayerWaterPresentation presentation,
            in PlayerWaterContactSample sample, Collider hit)
        {
            if (!ReferenceEquals(_presentation, presentation) || !PresentationCurrent() || sample.InWater != Alive(hit))
                throw new InvalidOperationException("The exact retired water presentation binding changed.");
            // PAC's original control/camera path reads these fields directly. Do not invoke
            // UpdateInWaterControl, contact admission, cave checks or native provider queries.
            _sensor._isInWater = sample.HasWaterSurface;
            _sensor._isInOcean = sample.InOcean;
            _sensor._inCave = sample.InCave;
            _sensor._cachedWaterHeight = sample.Height;
            _sensor._cachedWaterDepth = sample.Depth;
            _sensor._waterVolumeLevelCache = sample.VolumeLevel;
            _sensor._cachedWaterNormal = new(sample.Normal.X, sample.Normal.Y, sample.Normal.Z);
            _sensor._cachedWaterSurfaceVelocity = new(sample.SurfaceVelocity.X, sample.SurfaceVelocity.Y, sample.SurfaceVelocity.Z);
            _sensor._lastWaterCollider = hit;
            _sensor._cacheFrame = Time.frameCount;
            if (!PresentationValuesMatch(sample, hit))
                throw new InvalidOperationException("Retained water presentation readback differs from the server sample.");
        }

        internal bool PresentationValuesMatch(in PlayerWaterContactSample sample, Collider hit)
            => PresentationCurrent() && _sensor._isInWater == sample.HasWaterSurface
                && _sensor._isInOcean == sample.InOcean && _sensor._inCave == sample.InCave
                && _sensor._cachedWaterHeight == sample.Height && _sensor._cachedWaterDepth == sample.Depth
                && _sensor._waterVolumeLevelCache == sample.VolumeLevel && V(_sensor._cachedWaterNormal) == sample.Normal
                && V(_sensor._cachedWaterSurfaceVelocity) == sample.SurfaceVelocity
                && (_sensor._lastWaterCollider?.Pointer ?? 0) == (hit?.Pointer ?? 0);

        private static bool BeforePresentationInWater(WaterLevelSensor __instance, ref bool __result)
            => BeforePresentationBoolean(__instance, false, ref __result);
        private static bool BeforePresentationInOcean(WaterLevelSensor __instance, ref bool __result)
            => BeforePresentationBoolean(__instance, true, ref __result);

        private static bool BeforePresentationBoolean(WaterLevelSensor sensor, bool ocean, ref bool result)
        {
            if (Environment.CurrentManagedThreadId != _hookThread || sensor is null
                || !Owners.TryGetValue(sensor.Pointer, out var owner) || !owner._retiredSource
                || owner._presentation?.HasAppliedSample != true) return true;
            // Keep the last authoritative observation if the route faults/retires. Its
            // parent must stop controls before disposal; falling back would fabricate dry.
            var sample = owner._presentation.AppliedSample;
            result = ocean ? sample.InOcean : sample.InWater;
            return false;
        }

        internal static byte[] DescribePresentationContact(Collider collider)
        {
            var bytes = new byte[PlayerWaterPresentationFrame.ContactLength];
            if (collider is null) return bytes;
            if (!Alive(collider)) throw new InvalidOperationException("Native last-contact destruction has not drained.");
            var identity = Describe(collider);
            if (identity.Kind is not (WaterColliderIdentityKind.SceneCollider
                or WaterColliderIdentityKind.BoltRootCollider or WaterColliderIdentityKind.BoltDescendantCollider)
                || identity.GeometryDigest == null || identity.Route == null)
                throw new InvalidOperationException("Native water presentation contact has no exact transferable identity.");
            bytes[0] = (byte)identity.Kind;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), identity.ComponentIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), identity.NetworkId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), identity.PrefabId);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), identity.StateTypeId);
            SceneDigest(identity.ScenePath).CopyTo(bytes, 24);
            PresentationIdentityDigest(identity).CopyTo(bytes, 56);
            string[] parts = identity.Route.Length == 0 ? Array.Empty<string>() : identity.Route.Split('/');
            if (parts.Length > 64) throw new InvalidOperationException("Water presentation route is too deep.");
            bytes[1] = (byte)parts.Length;
            int position = 88;
            foreach (string part in parts)
            {
                if (!Index(part, out int index)) throw new InvalidOperationException("Invalid native water collider route.");
                uint value = (uint)index;
                do
                {
                    if (position == bytes.Length)
                        throw new InvalidOperationException("Exact water contact route exceeds the 256-byte presentation frame; no route was truncated.");
                    bytes[position++] = (byte)((value & 127u) | (value > 127u ? 128u : 0u));
                    value >>= 7;
                } while (value != 0);
            }
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)(position - 88));
            if (!ValidPresentationContact(bytes)) throw new InvalidOperationException("Invalid native water contact address.");
            return bytes;
        }

        internal static bool ValidPresentationContact(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length != PlayerWaterPresentationFrame.ContactLength) return false;
            if (bytes[0] == 0) return Zero(bytes);
            if (bytes[0] is not (1 or 3 or 4) || bytes[1] > 64
                || BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) < 0) return false;
            bool scene = bytes[0] == (byte)WaterColliderIdentityKind.SceneCollider;
            ulong network = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
            if (scene ? network != 0 || BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != 0
                || BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]) != 0 || bytes[1] == 0 : network == 0) return false;
            if ((bytes[0] == 1 && bytes[1] != 0) || (bytes[0] == 4 && bytes[1] == 0)) return false;
            int length = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
            if (length > 72 || !Zero(bytes[(88 + length)..])) return false;
            int position = 88;
            for (int i = 0; i < bytes[1]; i++)
                if (!ReadRouteIndex(bytes[..(88 + length)], ref position, out _)) return false;
            return position == 88 + length;
        }

        internal static Collider ResolvePresentationContact(ReadOnlySpan<byte> bytes)
        {
            if (!ValidPresentationContact(bytes)) throw new InvalidDataException("Invalid water contact definition.");
            if (bytes[0] == 0) return null;
            var kind = (WaterColliderIdentityKind)bytes[0];
            string scenePath = null;
            if (kind == WaterColliderIdentityKind.SceneCollider)
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    if (!scene.IsValid() || !scene.isLoaded || !SceneDigest(scene.path).AsSpan().SequenceEqual(bytes[24..56])) continue;
                    if (scenePath != null) throw new InvalidOperationException("Ambiguous native water presentation scene.");
                    scenePath = scene.path;
                }
                if (scenePath == null) throw new InvalidOperationException("The server water contact scene is not loaded on the retained source.");
            }
            var route = new StringBuilder();
            int position = 88;
            for (int i = 0; i < bytes[1]; i++)
            {
                if (!ReadRouteIndex(bytes, ref position, out int index)) throw new InvalidDataException("Invalid water route.");
                if (i != 0) route.Append('/');
                route.Append(index.ToString(CultureInfo.InvariantCulture));
            }
            var address = new WaterColliderIdentity(kind, BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]),
                scenePath, null, 0, 0, null, false, false, false, false, default, default, default, default, default, 0, 0, 0)
                { Route = route.ToString(), ComponentIndex = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) };
            if (!TryResolveTransform(address, out var transform, out var status))
                throw new InvalidOperationException("The server water presentation contact cannot be resolved: " + status);
            var colliders = transform.GetComponents<Collider>();
            var hit = address.ComponentIndex >= colliders.Length ? null : colliders[address.ComponentIndex];
            if (!Alive(hit))
                throw new InvalidOperationException("The server water presentation collider was replaced.");
            var actual = Describe(hit);
            if (!SceneDigest(actual.ScenePath).AsSpan().SequenceEqual(bytes[24..56])
                || !PresentationIdentityDigest(actual).AsSpan().SequenceEqual(bytes[56..88]))
                throw new InvalidOperationException("Source water contact shape, pose, filters or native identity differs from the server.");
            return hit;
        }

        private static bool ReadRouteIndex(ReadOnlySpan<byte> bytes, ref int position, out int index)
        {
            index = 0;
            for (int shift = 0; shift <= 28 && position < bytes.Length; shift += 7)
            {
                byte value = bytes[position++];
                if (shift == 28 && value > 7) return false;
                index |= (value & 127) << shift;
                if ((value & 128) == 0) return shift == 0 || value != 0;
            }
            return false;
        }

        private static bool Zero(ReadOnlySpan<byte> bytes)
        { foreach (byte value in bytes) if (value != 0) return false; return true; }
        private static byte[] SceneDigest(string path) => SHA256.HashData(Encoding.UTF8.GetBytes(path ?? string.Empty));

        private static byte[] PresentationIdentityDigest(WaterColliderIdentity identity)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                void Text(string value) { writer.Write(value != null); if (value != null) writer.Write(value); }
                void Vector(System.Numerics.Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
                writer.Write((byte)identity.Kind); writer.Write(identity.NetworkId); writer.Write(identity.PrefabId);
                writer.Write(identity.StateTypeId); Text(identity.ScenePath); Text(identity.HierarchyPath);
                writer.Write(identity.Shape); writer.Write(identity.Layer); Text(identity.Tag);
                writer.Write(identity.Enabled); writer.Write(identity.Active); writer.Write(identity.IsTrigger); writer.Write(identity.IsOcean);
                Vector(identity.Position); writer.Write(identity.Rotation.X); writer.Write(identity.Rotation.Y);
                writer.Write(identity.Rotation.Z); writer.Write(identity.Rotation.W); Vector(identity.Scale);
                Vector(identity.Center); Vector(identity.Size); writer.Write(identity.Radius); writer.Write(identity.Height);
                writer.Write(identity.Direction); Text(identity.Route); writer.Write(identity.ComponentIndex); Text(identity.GeometryDigest);
            }
            return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
    }
}
