// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// OpenACAI-owned UI state service. Current native/fallback menus still render
// in the bridge, but their state is exposed through this runtime surface.

using System;
using System.Collections.Generic;

namespace OpenACAI.Loader
{
    public static class OpenACAIUi
    {
        public const string ServiceKey = "openacai.loader.ui";

        private static readonly object SyncRoot = new object();
        private static readonly Dictionary<string, OpenACAIUiSurfaceSnapshot> Surfaces =
            new Dictionary<string, OpenACAIUiSurfaceSnapshot>(StringComparer.OrdinalIgnoreCase);

        private static string _lastTooltip;

        public static event Action<OpenACAIUiSurfaceSnapshot> SurfaceChanged;

        internal static void InitializeDefaults()
        {
            lock (SyncRoot)
            {
                Surfaces.Clear();
                _lastTooltip = null;
                SetSurfaceNoLock(OpenACAIUiSurfaceSnapshot.Create(
                    OpenACAIUiSurfaceIds.ModsMenu,
                    "Mods Menu",
                    "native-menu",
                    OpenACAIUiSurfaceStatus.Hidden,
                    false,
                    true,
                    "list",
                    "Modstoke mods menu is not visible."));
                SetSurfaceNoLock(OpenACAIUiSurfaceSnapshot.Create(
                    OpenACAIUiSurfaceIds.ModConfig,
                    "Mod Config",
                    "native-menu",
                    OpenACAIUiSurfaceStatus.Hidden,
                    false,
                    true,
                    "closed",
                    "No mod config is open."));
                PublishServiceNoLock();
            }
        }

        public static IReadOnlyDictionary<string, OpenACAIUiSurfaceSnapshot> Snapshot()
        {
            lock (SyncRoot)
            {
                return new Dictionary<string, OpenACAIUiSurfaceSnapshot>(Surfaces, StringComparer.OrdinalIgnoreCase);
            }
        }

        internal static OpenACAIUiSurfaceSnapshot MarkVisible(string id, string displayName, string kind, string view, string detail)
        {
            return MarkSurface(id, displayName, kind, OpenACAIUiSurfaceStatus.Visible, true, view, detail);
        }

        internal static OpenACAIUiSurfaceSnapshot MarkHidden(string id, string displayName, string kind, string view, string detail)
        {
            return MarkSurface(id, displayName, kind, OpenACAIUiSurfaceStatus.Hidden, false, view, detail);
        }

        internal static OpenACAIUiSurfaceSnapshot MarkFallback(string id, string displayName, string view, string detail)
        {
            return MarkSurface(id, displayName, "imgui-fallback", OpenACAIUiSurfaceStatus.Fallback, true, view, detail);
        }

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
                return new
                {
                    schema = 1,
                    serviceKey = ServiceKey,
                    lastTooltip = _lastTooltip,
                    surfaces = new Dictionary<string, OpenACAIUiSurfaceSnapshot>(Surfaces, StringComparer.OrdinalIgnoreCase)
                };
            }
        }

        private static OpenACAIUiSurfaceSnapshot MarkSurface(
            string id,
            string displayName,
            string kind,
            string status,
            bool visible,
            string view,
            string detail)
        {
            OpenACAIUiSurfaceSnapshot snapshot;
            Action<OpenACAIUiSurfaceSnapshot> handlers;
            lock (SyncRoot)
            {
                snapshot = OpenACAIUiSurfaceSnapshot.Create(
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

        private static void SetSurfaceNoLock(OpenACAIUiSurfaceSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Id))
            {
                return;
            }

            Surfaces[snapshot.Id] = snapshot;
        }

        private static void PublishServiceNoLock()
        {
            OpenACAIModding.SetService(ServiceKey, CreateStateNoLock());
        }

        private static object CreateStateNoLock()
        {
            return new
            {
                schema = 1,
                serviceKey = ServiceKey,
                lastTooltip = _lastTooltip,
                surfaces = new Dictionary<string, OpenACAIUiSurfaceSnapshot>(Surfaces, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static void DispatchSurfaceChanged(OpenACAIUiSurfaceSnapshot snapshot, Action<OpenACAIUiSurfaceSnapshot> handlers)
        {
            try
            {
                handlers?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                OpenACAI.Endnight.Loader.OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke UI surface event subscriber failed: " + ex.Message);
            }
        }
    }

    public static class OpenACAIUiSurfaceIds
    {
        public const string ModsMenu = "openacai.ui.mods";
        public const string ModConfig = "openacai.ui.modConfig";
    }

    public static class OpenACAIUiSurfaceStatus
    {
        public const string Hidden = "hidden";
        public const string Visible = "visible";
        public const string Fallback = "fallback";
        public const string Failed = "failed";
    }

    public sealed class OpenACAIUiSurfaceSnapshot
    {
        private OpenACAIUiSurfaceSnapshot(
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

        internal static OpenACAIUiSurfaceSnapshot Create(
            string id,
            string displayName,
            string kind,
            string status,
            bool visible,
            bool nativePreferred,
            string view,
            string detail)
        {
            return new OpenACAIUiSurfaceSnapshot(
                id,
                string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                string.IsNullOrWhiteSpace(kind) ? "unknown" : kind,
                string.IsNullOrWhiteSpace(status) ? OpenACAIUiSurfaceStatus.Hidden : status,
                visible,
                nativePreferred,
                string.IsNullOrWhiteSpace(view) ? "unknown" : view,
                detail,
                DateTimeOffset.UtcNow.ToString("O"));
        }
    }
}

