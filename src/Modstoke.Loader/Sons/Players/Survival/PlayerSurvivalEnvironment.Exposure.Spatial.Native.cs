// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Concurrent;
using System.Reflection;
using Endnight.Utilities;
using HarmonyLib;
using global::Sons.Areas;
using global::Sons.Gameplay;
using global::Sons.Physics;
using UnityEngine;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentExposure
    {
        private static void InstallSpatialHooks()
        {
            foreach (var item in new[]
            {
                (nameof(TriggerProxy.OnTriggerEnter), 0x2E78DD0),
                (nameof(TriggerProxy.OnTriggerExit), 0x2E78E40),
                (nameof(TriggerProxy.OnTriggerStay), 0x2E78EB0)
            })
                _exposureHarmony.Patch(Entry(typeof(TriggerProxy), item.Item1, item.Item2,
                    typeof(void), typeof(Collider)), prefix: Hook(nameof(BeforeExposureProxy), Priority.First));
            foreach (var item in new[]
            {
                (nameof(FilterTriggerCollider.OnTriggerEnter), 0xB33600),
                (nameof(FilterTriggerCollider.OnTriggerExit), 0xB33890),
                (nameof(FilterTriggerCollider.OnTriggerStay), 0xB33A60)
            })
                _exposureHarmony.Patch(Entry(typeof(FilterTriggerCollider), item.Item1, item.Item2,
                    typeof(void), typeof(Collider)), prefix: Hook(nameof(BeforeExposureFilter), Priority.First));
            foreach (var item in new[]
            {
                (nameof(LocalPlayerAreaMaskTrigger.OnTriggerEnter), 0x3342F30),
                (nameof(LocalPlayerAreaMaskTrigger.OnTriggerExit), 0x33430F0)
            })
                _exposureHarmony.Patch(Entry(typeof(LocalPlayerAreaMaskTrigger), item.Item1, item.Item2,
                    typeof(void), typeof(Collider)), prefix: Hook(nameof(BeforeExposureArea), Priority.First));
            foreach (var item in new[]
            {
                (nameof(CaveSystem.EnterExtent), 0x29FB250),
                (nameof(CaveSystem.StayExtent), 0x29FB360),
                (nameof(CaveSystem.ExitedExtent), 0x29FB470)
            })
                _exposureHarmony.Patch(Entry(typeof(CaveSystem), item.Item1, item.Item2,
                    typeof(void), typeof(Collider), typeof(TriggerProxy)), prefix: Hook(nameof(BeforeExposureExtent), Priority.First));
            _exposureHarmony.Patch(Entry(typeof(DirectionalTrigger), nameof(DirectionalTrigger.LateUpdateManaged),
                0xB2E920, typeof(void)), postfix: Hook(nameof(AfterExposureDirectionLate), Priority.Last));
            _exposureHarmony.Patch(Entry(typeof(CaveSystem), nameof(CaveSystem.LateUpdateManaged),
                0x29F9EF0, typeof(void)), postfix: Hook(nameof(AfterExposureCaveLate), Priority.Last));
        }

        private static bool BeforeExposureFilter(FilterTriggerCollider __instance, Collider __0, MethodBase __originalMethod)
        {
            if (__0 == null || !PlayerSurvivalEnvironmentExposureServer.SpatialOwners.TryGetValue(__0.Pointer, out var owner)) return true;
            return owner.FilterContact(__instance, __0, __originalMethod.Name);
        }
        private static bool BeforeExposureArea(LocalPlayerAreaMaskTrigger __instance, Collider __0, MethodBase __originalMethod)
        {
            if (__0 == null || !PlayerSurvivalEnvironmentExposureServer.SpatialOwners.TryGetValue(__0.Pointer, out var owner)) return true;
            owner.AreaContact(__instance, __0, __originalMethod.Name == nameof(LocalPlayerAreaMaskTrigger.OnTriggerEnter));
            return false;
        }
        private static bool BeforeExposureProxy(TriggerProxy __instance, Collider __0, MethodBase __originalMethod)
        {
            if (__0 == null || !PlayerSurvivalEnvironmentExposureServer.SpatialOwners.TryGetValue(__0.Pointer, out var owner)) return true;
            return owner.ProxyContact(__instance, __0, __originalMethod.Name);
        }
        private static bool BeforeExposureExtent(CaveSystem __instance, Collider __0, TriggerProxy __1, MethodBase __originalMethod)
        {
            if (__0 == null || !PlayerSurvivalEnvironmentExposureServer.SpatialOwners.TryGetValue(__0.Pointer, out var owner)) return true;
            owner.ExtentContact(__instance, __0, __1, __originalMethod.Name == nameof(CaveSystem.ExitedExtent));
            return false;
        }
        // Enumerate the map directly: .Values takes every lock and copies, and SpatialLate can retire owners mid-loop.
        private static void AfterExposureDirectionLate(DirectionalTrigger __instance)
        {
            var owners = PlayerSurvivalEnvironmentExposureServer.Owners;
            if (owners.IsEmpty) return;
            foreach (var owner in owners)
                owner.Value.SpatialLate(__instance, null);
        }
        private static void AfterExposureCaveLate(CaveSystem __instance)
        {
            var owners = PlayerSurvivalEnvironmentExposureServer.Owners;
            if (owners.IsEmpty) return;
            foreach (var owner in owners)
                owner.Value.SpatialLate(null, __instance);
        }
    }

    internal sealed partial class PlayerSurvivalEnvironmentExposureServer
    {
        internal static readonly ConcurrentDictionary<nint, PlayerSurvivalEnvironmentExposureServer> SpatialOwners = new();
        private nint _colliderPointer;
        private int _colliderId;
        private bool _spatialRegistered;

        private void RegisterSpatial()
        {
            _colliderPointer = _carrier.PrimaryCollider.Pointer;
            _colliderId = _carrier.PrimaryCollider.GetInstanceID();
            if (!SpatialOwners.TryAdd(_colliderPointer, this))
                throw new InvalidOperationException("The exact actor collider already owns an exposure continuation.");
            _spatialRegistered = true;
        }
        private void RequireCollider(Collider collider)
        {
            RequireCurrent();
            if (!PlayerSurvivalEnvironmentExposure.Same(collider, _carrier.PrimaryCollider)
                || collider.Pointer != _colliderPointer || collider.GetInstanceID() != _colliderId
                || !PlayerSurvivalEnvironmentExposure.Same(collider.attachedRigidbody, _carrier.Body))
                throw new InvalidOperationException("Exposure callback is not from the exact registered actor body/collider.");
        }
        internal bool FilterContact(FilterTriggerCollider filter, Collider collider, string method)
        {
            // Unrelated native filters remain with their existing owners.
            if (filter == null || !_graph.HasFilter(filter.Pointer)) return true;
            try
            {
                if (IsDisposed || FailureReason != null) return false;
                RequireCollider(collider);
                _graph.FilterContact(filter, method == nameof(FilterTriggerCollider.OnTriggerExit) ? 1
                    : method == nameof(FilterTriggerCollider.OnTriggerStay) ? 2 : 0);
            }
            catch (Exception error) { Fail(error.Message); }
            return false;
        }
        internal void AreaContact(LocalPlayerAreaMaskTrigger trigger, Collider collider, bool enter)
        {
            if (IsDisposed || FailureReason != null) return;
            try { RequireCollider(collider); _graph.AreaContact(trigger, enter); }
            catch (Exception error) { Fail(error.Message); }
        }
        internal bool ProxyContact(TriggerProxy proxy, Collider collider, string method)
        {
            if (proxy == null || !_graph.HasProxy(proxy.Pointer)) return true;
            try
            {
                if (IsDisposed || FailureReason != null) return false;
                RequireCollider(collider);
                _graph.ProxyContact(proxy, method == nameof(TriggerProxy.OnTriggerExit) ? 1
                    : method == nameof(TriggerProxy.OnTriggerStay) ? 2 : 0);
            }
            catch (Exception error) { Fail(error.Message); }
            return false;
        }
        internal void ExtentContact(CaveSystem cave, Collider collider, TriggerProxy extent, bool exit)
        {
            if (IsDisposed || FailureReason != null) return;
            try { RequireCollider(collider); _graph.ExtentContact(cave, extent, exit); }
            catch (Exception error) { Fail(error.Message); }
        }
        internal void SpatialLate(DirectionalTrigger direction, CaveSystem cave)
        {
            if (IsDisposed || FailureReason != null) return;
            try
            {
                RequireCurrent();
                if (direction != null) _graph.DirectionLate(direction);
                else if (cave != null) _graph.CaveLate(cave);
            }
            catch (Exception error) { Fail(error.Message); }
        }
        private void RetireSpatial()
        {
            if (_spatialRegistered) SpatialOwners.TryRemove(_colliderPointer, out _);
            _spatialRegistered = false;
        }
    }
}
