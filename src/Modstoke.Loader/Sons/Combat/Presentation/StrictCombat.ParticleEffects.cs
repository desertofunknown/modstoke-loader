// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // The game strips these managed getters, but retains their Unity internal calls.
    // Each injected module is one ParticleSystem object reference passed by reference.
    internal static class StrictCombatParticleEffects
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte EnabledGetter(ref nint particleSystem);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int StopActionGetter(ref nint particleSystem);

        private sealed class Bindings
        {
            internal EnabledGetter Collision, Trigger, SubEmitters;
            internal StopActionGetter StopAction;
        }

        private static readonly Lazy<Bindings> Accessors = new(Resolve);

        private sealed class ImpactBindings
        {
            internal EnabledGetter Lights, ExternalForces;
        }

        private static readonly Lazy<ImpactBindings> ImpactAccessors = new(ResolveImpact);

        internal static bool TryReadImpactSafety(ParticleSystem particles, out bool safe)
        {
            safe = false;
            try
            {
                if (!TryReadSafety(particles, out bool basicSafe) || !basicSafe) return false;
                var accessors = ImpactAccessors.Value;
                if (accessors == null || particles.WasCollected || !particles) return false;
                nint pointer = IL2CPP.Il2CppObjectBaseToPtrNotNull(particles);
                bool disabled = accessors.Lights(ref pointer) == 0 && accessors.ExternalForces(ref pointer) == 0;
                if (particles.WasCollected || !particles || pointer != particles.Pointer) return false;
                safe = disabled;
                return true;
            }
            catch (Exception) { return false; }
            finally { GC.KeepAlive(particles); }
        }

        private static ImpactBindings ResolveImpact()
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return null;
            try
            {
                var result = new ImpactBindings
                {
                    Lights = IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/LightsModule::get_enabled_Injected"),
                    ExternalForces = IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/ExternalForcesModule::get_enabled_Injected")
                };
                return result.Lights != null && result.ExternalForces != null ? result : null;
            }
            catch (Exception) { return null; }
        }

        // Called only within the captured weapon's game-thread presentation operation.
        internal static bool TryReadSafety(ParticleSystem particles, out bool safe)
        {
            safe = false;
            try
            {
                if (particles == null || particles.WasCollected || !particles) return false;
                var accessors = Accessors.Value;
                if (accessors == null) return false;
                nint pointer = IL2CPP.Il2CppObjectBaseToPtrNotNull(particles);
                bool disabled = accessors.Collision(ref pointer) == 0
                    && accessors.Trigger(ref pointer) == 0
                    && accessors.SubEmitters(ref pointer) == 0
                    && accessors.StopAction(ref pointer) == 0;
                // Null native receivers return zero too; never treat a lost object as safe.
                if (particles.WasCollected || !particles || pointer != particles.Pointer) return false;
                safe = disabled;
                return true;
            }
            catch (Exception) { return false; }
            finally { GC.KeepAlive(particles); }
        }

        private static Bindings Resolve()
        {
            // This ABI is qualified against the Windows x64 player and its original bindings.
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                return null;
            try
            {
                var result = new Bindings
                {
                    Collision = IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/CollisionModule::get_enabled_Injected"),
                    Trigger = IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/TriggerModule::get_enabled_Injected"),
                    SubEmitters = IL2CPP.ResolveICall<EnabledGetter>("UnityEngine.ParticleSystem/SubEmittersModule::get_enabled_Injected"),
                    StopAction = IL2CPP.ResolveICall<StopActionGetter>("UnityEngine.ParticleSystem/MainModule::get_stopAction_Injected")
                };
                return result.Collision != null && result.Trigger != null
                    && result.SubEmitters != null && result.StopAction != null ? result : null;
            }
            catch (Exception) { return null; }
        }
    }
}
