// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.StatSystem;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatLifeHooks
    {
        private static readonly List<VitalsStartupObservation> VitalsStartup = new();
        private static int _vitalsStartupDepth;
        private static volatile bool _vitalsStartupIncomplete;

        private sealed class VitalsStartupObservation
        {
            internal VitalsStartupObservation(Vitals vitals, int instanceId)
            {
                Vitals = vitals;
                Pointer = vitals.Pointer;
                InstanceId = instanceId;
            }

            internal readonly Vitals Vitals;
            internal readonly nint Pointer;
            internal readonly int InstanceId;
            internal HealthStat Health;
            internal nint HealthPointer;
            internal bool Initialized, Failed;
        }

        private readonly struct VitalsStartupCall
        {
            internal VitalsStartupCall(VitalsStartupObservation observation, int depth)
            { Observation = observation; Depth = depth; }

            internal VitalsStartupObservation Observation { get; }
            internal int Depth { get; }
        }

        private static VitalsStartupCall BeginVitalsStartup(Vitals vitals)
        {
            if (!IsObserving) return default;
            if (!OnOwnerThread || _vitalsStartupDepth >= MaximumOwners)
            { _vitalsStartupIncomplete = true; return default; }
            try
            {
                if (vitals == null || vitals.WasCollected || !vitals)
                { _vitalsStartupIncomplete = true; return default; }
                var observation = FindVitalsStartup(vitals.Pointer);
                if (observation == null)
                {
                    if (VitalsStartup.Count == MaximumOwners)
                    { _vitalsStartupIncomplete = true; return default; }
                    observation = new VitalsStartupObservation(vitals, vitals.GetInstanceID());
                    VitalsStartup.Add(observation);
                }
                if (!SameStartupVitals(observation, vitals))
                { observation.Failed = true; return default; }
                return new VitalsStartupCall(observation, ++_vitalsStartupDepth);
            }
            catch { _vitalsStartupIncomplete = true; return default; }
        }

        private static Exception AfterVitalsInitialization(Exception __exception,
            bool __runOriginal, VitalsStartupCall __state)
            => FinishVitalsStartup(__state, __runOriginal, true, __exception);

        private static Exception AfterVitalsLoad(Exception __exception,
            bool __runOriginal, VitalsStartupCall __state)
            => FinishVitalsStartup(__state, __runOriginal, false, __exception);

        private static Exception FinishVitalsStartup(VitalsStartupCall call,
            bool ranOriginal, bool initialization, Exception error)
        {
            if (call.Depth == 0) return error;
            if (!OnOwnerThread || _vitalsStartupDepth != call.Depth)
            { _vitalsStartupIncomplete = true; return error; }
            try
            {
                var observation = call.Observation;
                if (error != null || !SameStartupVitals(observation, observation.Vitals))
                { observation.Failed = true; return error; }
                // A later prefix may skip the original. That is not a completed
                // initialization, even when the patched call returns normally.
                if (!ranOriginal) return error;
                var health = observation.Vitals._health;
                if (health == null || health.WasCollected || health.Pointer == 0)
                { observation.Failed = true; return error; }
                if (initialization)
                {
                    observation.Health = health;
                    observation.HealthPointer = health.Pointer;
                    observation.Initialized = true;
                }
                else if (observation.Initialized && health.Pointer != observation.HealthPointer)
                    observation.Failed = true;
            }
            catch { _vitalsStartupIncomplete = true; }
            finally { _vitalsStartupDepth--; }
            return error;
        }

        private static bool VitalsStartupReady(Vitals vitals, HealthStat health,
            out StrictCombatLifeStatus status)
        {
            status = StrictCombatLifeStatus.NativeStateUnavailable;
            if (_vitalsStartupIncomplete) return false;
            if (_vitalsStartupDepth != 0)
            { status = StrictCombatLifeStatus.Busy; return false; }
            var observation = FindVitalsStartup(vitals.Pointer);
            if (observation?.Failed == true) return false;
            if (observation == null || !observation.Initialized)
            { status = StrictCombatLifeStatus.NativeInitializationPending; return false; }
            if (!SameStartupVitals(observation, vitals) || observation.Health == null
                || observation.Health.WasCollected || health == null || health.WasCollected
                || observation.HealthPointer != health.Pointer) return false;
            status = StrictCombatLifeStatus.Success;
            return true;
        }

        private static VitalsStartupObservation FindVitalsStartup(nint pointer)
        {
            foreach (var observation in VitalsStartup)
                if (observation.Pointer == pointer) return observation;
            return null;
        }

        private static bool SameStartupVitals(VitalsStartupObservation observation, Vitals vitals)
            => observation != null && vitals != null && !vitals.WasCollected && vitals
                && observation.Vitals != null && !observation.Vitals.WasCollected && observation.Vitals
                && observation.Pointer == vitals.Pointer
                && observation.InstanceId == vitals.GetInstanceID()
                && observation.InstanceId == observation.Vitals.GetInstanceID();
    }
}
