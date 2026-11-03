// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using global::Sons.Crafting.Structures;
using global::Sons.Gameplay.GameSetup;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        /// <summary>Reads creative mode independently of crafting-system availability on the initialized Unity thread.</summary>
        /// <remarks>False means the read is unavailable, not that creative mode is disabled. Does not change settings.</remarks>
        public static bool TryReadCreativeMode(out bool enabled)
        {
            enabled = false;
            if (!OnNativeThread) return false;
            try
            {
                enabled = GameSetupManager.GetCreativeModeSetting();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Reads instant-build mode from the active crafting object on the initialized Unity thread.</summary>
        /// <remarks>False means the read is unavailable, not that instant build is disabled. Does not change state.</remarks>
        public static bool TryReadInstantBuild(out bool enabled)
        {
            enabled = false;
            if (!OnNativeThread) return false;
            try
            {
                if (!TryGetActiveCraftingSystem(out var system)) return false;
                enabled = system.InstantBuild;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Reads the global structure-ghost activation flag on the initialized Unity thread.</summary>
        /// <remarks>Requires an active crafting object. False means the read is unavailable. The flag does not establish individual ghost visibility.</remarks>
        public static bool TryReadStructureGhosts(out bool active)
        {
            active = false;
            if (!OnNativeThread) return false;
            try
            {
                if (!TryGetActiveCraftingSystem(out var system)) return false;
                active = system._ghostsAreActive;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Calls the native creative-mode setter in a recognized single-player world or local multiplayer host.</summary>
        /// <remarks>Requires the initialized Unity thread with no building action in progress. Submitted confirms only native return;
        /// read the mode to observe the result. Failure after an attempted call may have partial effects; no retry or rollback is performed.</remarks>
        public static BuildingActionResult SetCreativeMode(bool enabled) => SetMode(Mode.Creative, enabled);

        /// <summary>Calls the active crafting object's instant-build setter in a recognized single-player world or local multiplayer host.</summary>
        /// <remarks>Requires the initialized Unity thread with no building action in progress. Submitted confirms only native return;
        /// read the mode to observe the result. Failure after an attempted call may have partial effects; no retry or rollback is performed.</remarks>
        public static BuildingActionResult SetInstantBuild(bool enabled) => SetMode(Mode.InstantBuild, enabled);

        /// <summary>Calls native ghost activation on the active crafting object in a recognized single-player world or local multiplayer host.</summary>
        /// <remarks>Requires the initialized Unity thread with no building action in progress. Submitted confirms only native return;
        /// the global flag does not establish individual ghost visibility, persistence or replication. An attempted call may have
        /// partial effects on failure; no retry or rollback is performed.</remarks>
        public static BuildingActionResult SetStructureGhostsActive(bool active) => SetMode(Mode.StructureGhosts, active);

        private enum Mode { Creative, InstantBuild, StructureGhosts }

        private static BuildingActionResult SetMode(Mode mode, bool enabled)
        {
            if (!TryEnterNative(out var rejected)) return rejected;
            var attempted = false;
            try
            {
                if (!GameBuild.IsRecognized || !GameRuntime.IsGameScene || !LocalPlayer.IsInWorld)
                    return BuildingResult(BuildingActionStatus.Unavailable, 0, false,
                        "Construction modes require a local player in a recognized game world.");
                if (BoltNetwork.isRunning ? !BoltNetwork.isServer : !GameSetup.IsSinglePlayer)
                    return BuildingResult(BuildingActionStatus.Rejected, 0, false,
                        "Construction modes require a single-player world or the multiplayer host.");

                if (mode == Mode.Creative)
                {
                    attempted = true;
                    GameSetupManager.SetCreativeModeSetting(enabled);
                }
                else
                {
                    if (!TryGetActiveCraftingSystem(out var system))
                        return BuildingResult(BuildingActionStatus.Unavailable, 0, false,
                            "Structure crafting system is not active");
                    attempted = true;
                    if (mode == Mode.InstantBuild) system.InstantBuild = enabled;
                    else system.ActivateAllGhosts(enabled);
                }
                return BuildingResult(BuildingActionStatus.Submitted, 0, true,
                    "The native construction mode call returned; the requested state is not confirmed.");
            }
            catch (Exception exception)
            {
                return BuildingResult(BuildingActionStatus.Failed, 0, attempted,
                    exception.Message + (attempted ? " Native side effects may have occurred; do not blindly retry." : ""));
            }
            finally { _nativeBusy = false; }
        }

        private static bool TryGetActiveCraftingSystem(out StructureCraftingSystem system)
        {
            system = StructureCraftingSystem._instance;
            if (!system) return false;
            var gameObject = system.gameObject;
            // These mode operations do not require an enabled component or ready recipe database.
            return gameObject && gameObject.activeInHierarchy;
        }
    }
}
