// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    internal static class RuntimeStateStatus
    {
        internal static void Initialize()
        {
            if (!MenuRegistry.RegisterOrUpdateBuiltInProvider(new Provider()))
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Modstoke SDK runtime state provider could not be registered.");
            }
        }

        private static MenuContentSnapshot CreateContent()
        {
            var state = GameRuntime.State ?? GameState.CreateUnknown();
            return new MenuContentSnapshot(
                "Sons Runtime State",
                "Modstoke view of Sons scene, world, pause, and lifecycle event state.",
                new[]
                {
                    CreateStateSection(state),
                    CreateEventSection(state),
                    CreateSdkSection(state)
                });
        }

        private static MenuSectionSnapshot CreateStateSection(GameState state)
        {
            return new MenuSectionSnapshot(
                "Current State",
                new[]
                {
                    Row("Service", GameRuntime.StateServiceKey, "READY", "Public Modstoke SDK runtime state service."),
                    Row("Scene", Display(state.SceneName), SceneStatus(state), "Kind " + Display(state.SceneKind) + "; phase " + Display(state.ScenePhase) + "."),
                    Row("Build Index", state.BuildIndex >= 0 ? state.BuildIndex.ToString() : "Unknown", state.BuildIndex >= 0 ? "INFO" : "PENDING", "Unity scene build index observed by Modstoke."),
                    Row("World", state.IsInWorld ? "In world" : "Not in world", state.IsInWorld ? "READY" : "INFO", "Last world phase " + Display(state.WorldPhase) + "."),
                    Row("Pause Menu", state.PauseMenuKnown ? state.PauseMenuOpen ? "Open" : "Closed" : "Unknown", state.PauseMenuKnown ? "INFO" : "PENDING", "Last pause phase " + Display(state.PauseMenuPhase) + "."),
                    Row("Last Update", FormatTimestamp(state.TimestampUtc), "INFO", "Last Modstoke SDK state update timestamp.")
                });
        }

        private static MenuSectionSnapshot CreateEventSection(GameState state)
        {
            return new MenuSectionSnapshot(
                "Observed Events",
                new[]
                {
                    Row("Lifecycle Events", state.LifecycleEventCount.ToString(), state.LifecycleEventCount > 0 ? "READY" : "PENDING", "Last lifecycle event " + Display(state.LastLifecycleName) + "."),
                    Row("Scene Events", state.SceneEventCount.ToString(), state.SceneEventCount > 0 ? "READY" : "PENDING", "Scene loaded/initialized/unloaded/observed events published by Modstoke."),
                    Row("World Events", state.WorldEventCount.ToString(), state.WorldEventCount > 0 ? "INFO" : "PENDING", "World enter/exit changes observed from Sons runtime state."),
                    Row("Pause Events", state.PauseMenuEventCount.ToString(), state.PauseMenuEventCount > 0 ? "INFO" : "PENDING", "Pause menu open/close changes observed by Modstoke."),
                    Row("Title Scene", GameRuntime.IsTitleScene ? "Yes" : "No", GameRuntime.IsTitleScene ? "READY" : "INFO", "Derived from the Modstoke SDK scene classifier."),
                    Row("Loading Scene", GameRuntime.IsLoadingScene ? "Yes" : "No", GameRuntime.IsLoadingScene ? "INFO" : "READY", "Derived from the Modstoke SDK scene classifier."),
                    Row("Game Scene", GameRuntime.IsGameScene ? "Yes" : "No", GameRuntime.IsGameScene ? "READY" : "INFO", "Derived from the Modstoke SDK scene classifier.")
                });
        }

        private static MenuSectionSnapshot CreateSdkSection(GameState state)
        {
            return new MenuSectionSnapshot(
                "SDK Surface",
                new[]
                {
                    Row("Lifecycle Event", "GameRuntime.Lifecycle", "READY", "Subscribe to GameRuntime.Lifecycle for loader/Sons lifecycle events."),
                    Row("Scene Event", "GameRuntime.SceneChanged", "READY", "Subscribe to GameRuntime.SceneChanged for scene events."),
                    Row("World Event", "GameRuntime.WorldChanged", "READY", "Subscribe to GameRuntime.WorldChanged for world enter/exit events."),
                    Row("Pause Event", "GameRuntime.PauseMenuChanged", "READY", "Subscribe to GameRuntime.PauseMenuChanged for pause menu changes."),
                    Row("Input Context", Display(state.SceneKind), "INFO", "InputBindings uses this state for title/loading/game/in-world gates.")
                });
        }

        private static string SceneStatus(GameState state)
        {
            if (state == null || string.Equals(state.SceneKind, SceneKind.Unknown, StringComparison.Ordinal))
            {
                return "PENDING";
            }

            return string.Equals(state.SceneKind, SceneKind.Title, StringComparison.Ordinal)
                || string.Equals(state.SceneKind, SceneKind.Game, StringComparison.Ordinal)
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

        private static MenuRowSnapshot Row(string label, string value, string status, string detail)
        {
            return new MenuRowSnapshot(label, value, status, detail);
        }

        private sealed class Provider : IMenuContentProvider
        {
            public string Id => MenuProviderIds.SonsRuntimeState;
            public string DisplayName => "Sons Runtime State";
            public string Kind => MenuProviderKinds.Status;
            public int Priority => 717;
            public string Source => "Modstoke Loader";
            public string Description => "Show the Sons scene, world, pause, and lifecycle state Modstoke tracks.";
            public bool RequiresAuthorization => false;
            public bool Enabled => true;

            public MenuContentSnapshot CreateContent()
            {
                return RuntimeStateStatus.CreateContent();
            }
        }
    }
}
