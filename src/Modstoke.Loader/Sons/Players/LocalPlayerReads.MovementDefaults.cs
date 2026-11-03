// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    public static partial class LocalPlayerReads
    {
        private static int _movementDefaultsAttempted, _movementDefaultsThread;
        private static PlayerMovementDefaults? _movementDefaults;

        /// <summary>
        /// Copies the movement settings the most recent local player rig woke with, before game slow
        /// effects or mods changed the live component. A process that never had a local player, such as a
        /// dedicated server, returns PlayerUnavailable.
        /// </summary>
        /// <remarks>The values come from the shipped player prefab unless another mod changed it before the
        /// rig woke. Live speeds can sit below them while the game applies slow effects.</remarks>
        public static bool TryReadMovementDefaults(out PlayerMovementDefaults defaults,
            out LocalPlayerReadStatus status)
        {
            defaults = default;
            var thread = Volatile.Read(ref _movementDefaultsThread);
            if (thread == 0 || Environment.CurrentManagedThreadId != thread)
            {
                status = thread == 0 ? LocalPlayerReadStatus.Unavailable : LocalPlayerReadStatus.WrongThread;
                return false;
            }
            if (_movementDefaults is not { } captured)
            {
                status = LocalPlayerReadStatus.PlayerUnavailable;
                return false;
            }
            defaults = captured;
            status = LocalPlayerReadStatus.Success;
            return true;
        }

        internal static void InstallMovementDefaults(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _movementDefaultsAttempted, 1) != 0) return;
            if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                var awake = PlayerLifecycle.QualifyNative(typeof(FirstPersonCharacter), "Awake", 0x2F7D990,
                    false, typeof(void));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-movement-defaults");
                // First, so no other prefix can change the settings before they are copied.
                harmony.Patch(awake, prefix: new HarmonyMethod(typeof(LocalPlayerReads), nameof(BeforeCharacterAwake))
                    { priority = Priority.First });
                Volatile.Write(ref _movementDefaultsThread, Environment.CurrentManagedThreadId);
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Player movement defaults hook unavailable (" + error.GetType().Name + ").");
            }
        }

        // Unity applies serialized values before Awake, so these are the prefab's settings.
        private static void BeforeCharacterAwake(FirstPersonCharacter __instance)
        {
            if (Environment.CurrentManagedThreadId != _movementDefaultsThread) return;
            var defaults = new PlayerMovementDefaults(__instance._walkSpeed, __instance._runSpeed,
                __instance._strafeSpeed, __instance._crouchSpeed, __instance._swimSpeed,
                __instance._maxSwimVelocity, __instance._swimSpeedMultiplier, __instance.backwardSpeedMult);
            if (Valid(defaults.WalkSpeed) && Valid(defaults.RunSpeed) && Valid(defaults.StrafeSpeed)
                && Valid(defaults.CrouchSpeed) && Valid(defaults.SwimSpeed) && Valid(defaults.MaximumSwimVelocity)
                && Valid(defaults.SwimSpeedMultiplier) && Valid(defaults.BackwardSpeedMultiplier))
                _movementDefaults = defaults;

            static bool Valid(float value) => float.IsFinite(value) && value >= 0f;
        }
    }

    /// <summary>
    /// FirstPersonCharacter movement settings as a local rig woke. The game has no separate sprint;
    /// running is its sprint.
    /// </summary>
    public readonly record struct PlayerMovementDefaults(
        float WalkSpeed, float RunSpeed, float StrafeSpeed, float CrouchSpeed, float SwimSpeed,
        float MaximumSwimVelocity, float SwimSpeedMultiplier, float BackwardSpeedMultiplier);
}
