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

        private static string _lastTooltip;

        public static event Action<UiSurfaceSnapshot> SurfaceChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Surfaces.Clear();
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

