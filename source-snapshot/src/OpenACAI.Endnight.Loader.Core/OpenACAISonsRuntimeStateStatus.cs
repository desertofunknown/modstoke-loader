// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper
//
// Built-in public status provider for OpenACAI-owned Sons runtime lifecycle
// state. RedLoader/SonsSdk lifecycle events are compatibility relays; this is
// the OpenACAI source of truth for new SDK consumers.

using System;
using OpenACAI.Endnight.Loader;
using OpenACAI.Loader;

namespace OpenACAI.Sons
{
    internal static class OpenACAISonsRuntimeStateStatus
    {
        internal static void Initialize()
        {
            if (!OpenACAIMenu.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                OpenACAIEndnightRuntime.LogSource?.LogWarning("Modstoke SDK runtime state provider could not be registered.");
            }
        }

        private static OpenACAIMenuContentSnapshot CreateContent()
        {
            var state = OpenACAISons.State ?? OpenACAISonsState.CreateUnknown();
            return new OpenACAIMenuContentSnapshot(
                "Sons Runtime State",
                "OpenACAI-owned Sons scene, world, pause, and lifecycle event state.",
                new[]
                {
                    CreateStateSection(state),
                    CreateEventSection(state),
                    CreateSdkSection(state)
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateStateSection(OpenACAISonsState state)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Current State",
                new[]
                {
                    Row("Service", OpenACAISons.StateServiceKey, "READY", "Public Modstoke SDK runtime state service."),
                    Row("Scene", Display(state.SceneName), SceneStatus(state), "Kind " + Display(state.SceneKind) + "; phase " + Display(state.ScenePhase) + "."),
                    Row("Build Index", state.BuildIndex >= 0 ? state.BuildIndex.ToString() : "Unknown", state.BuildIndex >= 0 ? "INFO" : "PENDING", "Unity scene build index observed by OpenACAI."),
                    Row("World", state.IsInWorld ? "In world" : "Not in world", state.IsInWorld ? "READY" : "INFO", "Last world phase " + Display(state.WorldPhase) + "."),
                    Row("Pause Menu", state.PauseMenuKnown ? state.PauseMenuOpen ? "Open" : "Closed" : "Unknown", state.PauseMenuKnown ? "INFO" : "PENDING", "Last pause phase " + Display(state.PauseMenuPhase) + "."),
                    Row("Last Update", FormatTimestamp(state.TimestampUtc), "INFO", "Last Modstoke SDK state update timestamp.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateEventSection(OpenACAISonsState state)
        {
            return new OpenACAIMenuSectionSnapshot(
                "Observed Events",
                new[]
                {
                    Row("Lifecycle Events", state.LifecycleEventCount.ToString(), state.LifecycleEventCount > 0 ? "READY" : "PENDING", "Last lifecycle event " + Display(state.LastLifecycleName) + "."),
                    Row("Scene Events", state.SceneEventCount.ToString(), state.SceneEventCount > 0 ? "READY" : "PENDING", "Scene loaded/initialized/unloaded/observed events published by OpenACAI."),
                    Row("World Events", state.WorldEventCount.ToString(), state.WorldEventCount > 0 ? "INFO" : "PENDING", "World enter/exit changes observed from Sons runtime state."),
                    Row("Pause Events", state.PauseMenuEventCount.ToString(), state.PauseMenuEventCount > 0 ? "INFO" : "PENDING", "Pause menu open/close changes observed by OpenACAI."),
                    Row("Title Scene", OpenACAISons.IsTitleScene ? "Yes" : "No", OpenACAISons.IsTitleScene ? "READY" : "INFO", "Derived from the Modstoke SDK scene classifier."),
                    Row("Loading Scene", OpenACAISons.IsLoadingScene ? "Yes" : "No", OpenACAISons.IsLoadingScene ? "INFO" : "READY", "Derived from the Modstoke SDK scene classifier."),
                    Row("Game Scene", OpenACAISons.IsGameScene ? "Yes" : "No", OpenACAISons.IsGameScene ? "READY" : "INFO", "Derived from the Modstoke SDK scene classifier.")
                });
        }

        private static OpenACAIMenuSectionSnapshot CreateSdkSection(OpenACAISonsState state)
        {
            return new OpenACAIMenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Lifecycle Event", "OpenACAISons.Lifecycle", "READY", "Subscribe to OpenACAISons.Lifecycle for loader/Sons lifecycle events."),
                    Row("Scene Event", "OpenACAISons.SceneChanged", "READY", "Subscribe to OpenACAISons.SceneChanged for OpenACAI scene events."),
                    Row("World Event", "OpenACAISons.WorldChanged", "READY", "Subscribe to OpenACAISons.WorldChanged for world enter/exit events."),
                    Row("Pause Event", "OpenACAISons.PauseMenuChanged", "READY", "Subscribe to OpenACAISons.PauseMenuChanged for pause menu changes."),
                    Row("Input Context", Display(state.SceneKind), "INFO", "OpenACAIInput uses this state for title/loading/game/in-world gates.")
                });
        }

        private static string SceneStatus(OpenACAISonsState state)
        {
            if (state == null || string.Equals(state.SceneKind, OpenACAISonsSceneKind.Unknown, StringComparison.Ordinal))
            {
                return "PENDING";
            }

            return string.Equals(state.SceneKind, OpenACAISonsSceneKind.Title, StringComparison.Ordinal)
                || string.Equals(state.SceneKind, OpenACAISonsSceneKind.Game, StringComparison.Ordinal)
                ? "READY"
                : "INFO";
        }

        private static string Display(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
        }

        private static string FormatTimestamp(DateTimeOffset value)
        {
            return value == default(DateTimeOffset) ? "Unknown" : value.ToString("u");
        }

        private static OpenACAIMenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new OpenACAIMenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IOpenACAIMenuContentProvider
        {
            public string Id => OpenACAIMenuProviderIds.SonsRuntimeState;
            public string DisplayName => "Sons Runtime State";
            public string Kind => OpenACAIMenuProviderKinds.Status;
            public int Priority => 717;
            public string Source => "Modstoke Loader";
            public string Description => "Show OpenACAI-owned Sons scene, world, pause, and lifecycle state.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public OpenACAIMenuContentSnapshot CreateContent()
            {
                return OpenACAISonsRuntimeStateStatus.CreateContent();
            }
        }
    }
}
