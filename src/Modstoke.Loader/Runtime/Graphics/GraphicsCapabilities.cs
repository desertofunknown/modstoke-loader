// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace Modstoke.Loader
{
    public static class GraphicsCapabilities
    {
        private static readonly object SyncRoot = new object();
        private static int _ownerThreadId;
        private static GraphicsCapabilitiesSnapshot _snapshot = new GraphicsCapabilitiesSnapshot(
            GraphicsCapabilityStatus.Unknown, "Graphics capabilities have not been observed.");

        /// <summary>Returns the last completed observation without calling Unity. Safe on any thread.</summary>
        public static GraphicsCapabilitiesSnapshot Snapshot()
        {
            lock (SyncRoot)
            {
                return _snapshot;
            }
        }

        /// <summary>Reads graphics capabilities on the initialized Unity lifecycle thread.</summary>
        /// <remarks>
        /// Returns true only for Available. A completed Null-device observation is cached as
        /// Unavailable and returns false. Thread rejection or a getter exception returns Failed
        /// without replacing the cache. Support flags do not establish feature activation.
        /// </remarks>
        public static bool TryCapture(out GraphicsCapabilitiesSnapshot snapshot)
        {
            try
            {
                EnsureRuntimeThread();
                snapshot = Observe();
            }
            catch (Exception ex)
            {
                snapshot = new GraphicsCapabilitiesSnapshot(
                    GraphicsCapabilityStatus.Failed, "Graphics capability capture failed: " + ex);
                return false;
            }

            lock (SyncRoot)
            {
                _snapshot = snapshot;
            }

            return snapshot.Status == GraphicsCapabilityStatus.Available;
        }

        /// <summary>Binds the known Unity lifecycle thread without reading graphics state.</summary>
        /// <remarks>
        /// The runtime owner must call this from its established Unity thread. Binding lasts
        /// for the loader lifetime; repeating it on that thread is harmless, rebinding is rejected.
        /// </remarks>
        internal static void Initialize()
        {
            lock (SyncRoot)
            {
                var threadId = Environment.CurrentManagedThreadId;
                if (_ownerThreadId != 0 && _ownerThreadId != threadId)
                    throw new InvalidOperationException("Graphics capabilities already belong to another Unity lifecycle thread.");

                _ownerThreadId = threadId;
            }
        }

        private static void EnsureRuntimeThread()
        {
            lock (SyncRoot)
            {
                if (_ownerThreadId == 0 || Environment.CurrentManagedThreadId != _ownerThreadId)
                    throw new InvalidOperationException("Graphics capability capture requires the initialized Unity lifecycle thread.");
            }
        }

        private static GraphicsCapabilitiesSnapshot Observe()
        {
            var backend = SystemInfo.graphicsDeviceType;
            if (backend == GraphicsDeviceType.Null)
            {
                return new GraphicsCapabilitiesSnapshot(
                    GraphicsCapabilityStatus.Unavailable,
                    "Unity reports a Null graphics device; device details and support flags were not read.",
                    backend: backend.ToString(),
                    backendId: (int)backend,
                    observedUtc: DateTimeOffset.UtcNow.ToString("O"));
            }

            // Construct fully before publication; a failing getter must not expose partial values.
            return new GraphicsCapabilitiesSnapshot(
                GraphicsCapabilityStatus.Available,
                "Unity graphics capabilities observed; feature activation was not inspected.",
                backend: backend.ToString(),
                backendId: (int)backend,
                deviceName: SystemInfo.graphicsDeviceName,
                deviceVendor: SystemInfo.graphicsDeviceVendor,
                deviceId: SystemInfo.graphicsDeviceID,
                vendorId: SystemInfo.graphicsDeviceVendorID,
                deviceVersion: SystemInfo.graphicsDeviceVersion,
                shaderLevel: SystemInfo.graphicsShaderLevel,
                supportsComputeShaders: SystemInfo.supportsComputeShaders,
                supportsInstancing: SystemInfo.supportsInstancing,
                supportsGeometryShaders: SystemInfo.supportsGeometryShaders,
                supportsTessellationShaders: SystemInfo.supportsTessellationShaders,
                supportsAsyncCompute: SystemInfo.supportsAsyncCompute,
                supportsGraphicsFence: SystemInfo.supportsGraphicsFence,
                supportsRayTracing: SystemInfo.supportsRayTracing,
                supportsAsyncGpuReadback: SystemInfo.supportsAsyncGPUReadback,
                observedUtc: DateTimeOffset.UtcNow.ToString("O"));
        }
    }

    public enum GraphicsCapabilityStatus
    {
        Unknown,
        Available,
        Unavailable,
        Failed
    }

    /// <summary>A detached observation containing only managed strings, scalars and nullable values.</summary>
    /// <remarks>
    /// Values are raw Unity reports, not renderer settings or resource-lifetime guarantees.
    /// Unobserved numbers and support flags are null, not zero or false. Retained snapshots
    /// remain immutable but can become stale; ObservedUtc is absent for Unknown and Failed.
    /// </remarks>
    public sealed class GraphicsCapabilitiesSnapshot
    {
        internal GraphicsCapabilitiesSnapshot(
            GraphicsCapabilityStatus status,
            string detail,
            string backend = null,
            int? backendId = null,
            string deviceName = null,
            string deviceVendor = null,
            int? deviceId = null,
            int? vendorId = null,
            string deviceVersion = null,
            int? shaderLevel = null,
            bool? supportsComputeShaders = null,
            bool? supportsInstancing = null,
            bool? supportsGeometryShaders = null,
            bool? supportsTessellationShaders = null,
            bool? supportsAsyncCompute = null,
            bool? supportsGraphicsFence = null,
            bool? supportsRayTracing = null,
            bool? supportsAsyncGpuReadback = null,
            string observedUtc = null)
        {
            Status = status;
            Detail = detail;
            Backend = backend;
            BackendId = backendId;
            DeviceName = deviceName;
            DeviceVendor = deviceVendor;
            DeviceId = deviceId;
            VendorId = vendorId;
            DeviceVersion = deviceVersion;
            ShaderLevel = shaderLevel;
            SupportsComputeShaders = supportsComputeShaders;
            SupportsInstancing = supportsInstancing;
            SupportsGeometryShaders = supportsGeometryShaders;
            SupportsTessellationShaders = supportsTessellationShaders;
            SupportsAsyncCompute = supportsAsyncCompute;
            SupportsGraphicsFence = supportsGraphicsFence;
            SupportsRayTracing = supportsRayTracing;
            SupportsAsyncGpuReadback = supportsAsyncGpuReadback;
            ObservedUtc = observedUtc;
        }

        public GraphicsCapabilityStatus Status { get; }
        public string Detail { get; }
        public string Backend { get; }
        public int? BackendId { get; }
        public string DeviceName { get; }
        public string DeviceVendor { get; }
        public int? DeviceId { get; }
        public int? VendorId { get; }
        public string DeviceVersion { get; }
        public int? ShaderLevel { get; }
        public bool? SupportsComputeShaders { get; }
        public bool? SupportsInstancing { get; }
        public bool? SupportsGeometryShaders { get; }
        public bool? SupportsTessellationShaders { get; }
        public bool? SupportsAsyncCompute { get; }
        public bool? SupportsGraphicsFence { get; }
        public bool? SupportsRayTracing { get; }
        public bool? SupportsAsyncGpuReadback { get; }
        public string ObservedUtc { get; }
    }
}
