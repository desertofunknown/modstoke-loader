// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;

namespace Modstoke.Loader
{
    public static class UiState
    {
        public const string ServiceKey = "openacai.loader.ui";

        private static readonly object SyncRoot = new();
        private static readonly Dictionary<string, UiSurfaceSnapshot> Surfaces =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SurfaceLease> Leases = new(StringComparer.OrdinalIgnoreCase);

        private static string _lastTooltip;

        public static event Action<UiSurfaceSnapshot> SurfaceChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Surfaces.Clear();
                Leases.Clear();
                _lastTooltip = null;
                SetSurfaceNoLock(UiSurfaceSnapshot.Create(
                    UiSurfaceIds.ModsMenu,
                    "Mods Menu",
                    "native-menu",
                    UiSurfaceStatus.Hidden,
                    false,
                    true,
                    "list",
                    "Modstoke mods menu is not visible."));
                SetSurfaceNoLock(UiSurfaceSnapshot.Create(
                    UiSurfaceIds.ModConfig,
                    "Mod Config",
                    "native-menu",
                    UiSurfaceStatus.Hidden,
                    false,
                    true,
                    "closed",
                    "No mod config is open."));
                PublishServiceNoLock();
            }
        }

        public static IReadOnlyDictionary<string, UiSurfaceSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, UiSurfaceSnapshot>(Surfaces, StringComparer.OrdinalIgnoreCase);
            }
        }

#nullable enable annotations
        /// <summary>Marks a mod's own menu or screen visible in <see cref="Snapshot"/> until the lease is disposed.</summary>
        /// <remarks>
        /// Main-thread only. Returns false for an empty ID, the loader's own surface IDs, or an ID another lease
        /// already shows. Other mods read the surface to keep world input closed while it is open.
        /// </remarks>
        public static bool TryShowSurface(string id, string displayName, string kind, out IDisposable? lease)
        {
            lease = null;
            if (string.IsNullOrWhiteSpace(id)
                || string.Equals(id, UiSurfaceIds.ModsMenu, StringComparison.OrdinalIgnoreCase)
                || string.Equals(id, UiSurfaceIds.ModConfig, StringComparison.OrdinalIgnoreCase))
                return false;

            SurfaceLease created;
            lock (SyncRoot)
            {
                if (Leases.ContainsKey(id))
                    return false;
                created = new SurfaceLease(id, string.IsNullOrWhiteSpace(displayName) ? id : displayName, string.IsNullOrWhiteSpace(kind) ? "mod-menu" : kind);
                Leases[id] = created;
            }

            MarkVisible(created.Id, created.DisplayName, created.Kind, "open", created.DisplayName + " is open.");
            lease = created;
            return true;
        }
#nullable restore annotations

        private sealed class SurfaceLease : IDisposable
        {
            internal readonly string Id;
            internal readonly string DisplayName;
            internal readonly string Kind;

            internal SurfaceLease(string id, string displayName, string kind)
            {
                Id = id;
                DisplayName = displayName;
                Kind = kind;
            }

            public void Dispose()
            {
                lock (SyncRoot)
                {
                    // A stale lease cannot hide a surface a newer lease shows.
                    if (!Leases.TryGetValue(Id, out var current) || !ReferenceEquals(current, this))
                        return;
                    Leases.Remove(Id);
                }

                MarkHidden(Id, DisplayName, Kind, "closed", DisplayName + " is closed.");
            }
        }

        internal static UiSurfaceSnapshot MarkVisible(string id, string displayName, string kind, string view, string detail)
            => MarkSurface(id, displayName, kind, UiSurfaceStatus.Visible, true, view, detail);

        internal static UiSurfaceSnapshot MarkHidden(string id, string displayName, string kind, string view, string detail)
            => MarkSurface(id, displayName, kind, UiSurfaceStatus.Hidden, false, view, detail);

        internal static UiSurfaceSnapshot MarkFallback(string id, string displayName, string view, string detail)
            => MarkSurface(id, displayName, "imgui-fallback", UiSurfaceStatus.Fallback, true, view, detail);

        internal static void SetTooltip(string tooltip)
        {
            lock (SyncRoot)
            {
                _lastTooltip = tooltip;
                PublishServiceNoLock();
            }
        }

        internal static object CreateState()
        {
            lock (SyncRoot)
            {
                return CreateStateNoLock();
            }
        }

        private static UiSurfaceSnapshot MarkSurface(
            string id,
            string displayName,
            string kind,
            string status,
            bool visible,
            string view,
            string detail)
        {
            UiSurfaceSnapshot snapshot;
            Action<UiSurfaceSnapshot> handlers;
            lock (SyncRoot)
            {
                snapshot = UiSurfaceSnapshot.Create(
                    id,
                    displayName,
                    kind,
                    status,
                    visible,
                    true,
                    view,
                    detail);
                SetSurfaceNoLock(snapshot);
                PublishServiceNoLock();
                handlers = SurfaceChanged;
            }

            DispatchSurfaceChanged(snapshot, handlers);
            return snapshot;
        }

        private static void SetSurfaceNoLock(UiSurfaceSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Id))
            {
                return;
            }

            Surfaces[snapshot.Id] = snapshot;
        }

        private static void PublishServiceNoLock()
        {
            Modding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static object CreateStateNoLock()
        {
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                lastTooltip = _lastTooltip,
                surfaces = new Dictionary<string, UiSurfaceSnapshot>(Surfaces, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void DispatchSurfaceChanged(UiSurfaceSnapshot snapshot, Action<UiSurfaceSnapshot> handlers)
        {
            if (handlers == null)
                return;

            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try
                {
                    handler(snapshot);
                }
                catch (Exception ex)
                {
                    try { global::Modstoke.Loader.RuntimeEntryPoint.LogSource?.LogWarning("Modstoke UI surface event subscriber failed: " + ex.Message); }
                    catch { /* Continue delivering the published surface to other subscribers. */ }
                }
            }
        }
    }

    public static class UiSurfaceIds
    {
        public const string ModsMenu = "openacai.ui.mods";
        public const string ModConfig = "openacai.ui.modConfig";
    }

    public static class UiSurfaceStatus
    {
        public const string Hidden = "hidden";
        public const string Visible = "visible";
        public const string Fallback = "fallback";
        public const string Failed = "failed";
    }

    public sealed class UiSurfaceSnapshot
    {
        private UiSurfaceSnapshot(
            string id,
            string displayName,
            string kind,
            string status,
            bool visible,
            bool nativePreferred,
            string view,
            string detail,
            string updatedUtc)
        {
            Id = id;
            DisplayName = displayName;
            Kind = kind;
            Status = status;
            Visible = visible;
            NativePreferred = nativePreferred;
            View = view;
            Detail = detail;
            UpdatedUtc = updatedUtc;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Kind { get; }
        public string Status { get; }
        public bool Visible { get; }
        public bool NativePreferred { get; }
        public string View { get; }
        public string Detail { get; }
        public string UpdatedUtc { get; }

        internal static UiSurfaceSnapshot Create(
            string id,
            string displayName,
            string kind,
            string status,
            bool visible,
            bool nativePreferred,
            string view,
            string detail)
        {
            return new UiSurfaceSnapshot(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                string.IsNullOrWhiteSpace(kind) ? "unknown" : kind,
                string.IsNullOrWhiteSpace(status) ? UiSurfaceStatus.Hidden : status,
                visible,
                nativePreferred,
                string.IsNullOrWhiteSpace(view) ? "unknown" : view,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}

