// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Gameplay;
using UnityEngine;
using ExitRoutine = global::Sons.Gameplay.GolfCartController._DelayedFixPlayerProxyRotation_d__270;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Observe the already-qualified original factory/MoveNext hooks. A null coroutine
        // field is not evidence that a previously scheduled native dismount has completed.
        private static class CartPlayerCallbackSettlement
        {
            private sealed record Pending(GolfCartController Cart, Transform Target, ExitRoutine Routine)
            {
                internal string Failure;
                internal CartPlayerPassengerAuthority Passenger;
                internal bool IsPassenger;
            }
            private static readonly Dictionary<IntPtr, Pending> PendingExits = new();
            private static string _failure;

            internal static void Observe(GolfCartController cart, Transform target,
                Il2CppSystem.Collections.IEnumerator routine)
            {
                try
                {
                    Require(OnSelectedThread(), "Cart continuation factory left the selected thread.");
                    var exit = routine?.TryCast<ExitRoutine>();
                    Require(Live(cart) && (target is null || Live(target)) && exit != null
                        && exit.__4__this?.Pointer == cart.Pointer
                        && (exit.targetTransform?.Pointer ?? IntPtr.Zero) == (target?.Pointer ?? IntPtr.Zero),
                        "The original cart continuation did not retain its exact native owner and target.");
                    Require(PendingExits.Count < 64 && !PendingExits.ContainsKey(exit.Pointer),
                        "Native cart continuation tracking is full or a factory repeated an existing routine.");
                    var passenger = CartPlayerPassengerAuthority.ObserveExit(cart, target, exit);
                    PendingExits.Add(exit.Pointer, new(cart, target, exit)
                    {
                        Passenger = passenger,
                        IsPassenger = passenger != null || target is null
                            || CartPlayerProxyAuthority.Find(cart)?.OwnsPassengerProjection(target) == true
                    });
                }
                catch (Exception error) { _failure ??= error.Message; }
            }

            // The same native factory services all seats. The driver's retained continuation
            // must not claim a passenger (or the native null-target cleanup path).
            internal static bool IsPassengerExit(ExitRoutine routine)
                => OnSelectedThread() && routine != null
                    && PendingExits.TryGetValue(routine.Pointer, out var pending) && pending.IsPassenger;

            internal static void RequireSettled(GolfCartController cart)
            {
                Require(OnSelectedThread() && _failure == null, _failure ?? "Cart callback settlement requires its selected thread.");
                foreach (var pending in PendingExits.Values)
                    Require(pending.Cart.Pointer != cart.Pointer,
                        pending.Failure ?? "Wait for the original native driver-disconnect continuation to finish.");
            }

            internal static bool Before(ExitRoutine routine)
            {
                if (!OnSelectedThread())
                { _failure ??= "Cart continuation moved off its selected thread."; return false; }
                if (!PendingExits.TryGetValue(routine.Pointer, out var pending)) return true;
                if (pending.Failure != null) return false;
                if (!Live(pending.Cart) || (pending.Target is not null && !Live(pending.Target))
                    || routine.__4__this?.Pointer != pending.Cart.Pointer
                    || (routine.targetTransform?.Pointer ?? IntPtr.Zero) != (pending.Target?.Pointer ?? IntPtr.Zero))
                { pending.Failure = "Native cart continuation owner/target changed."; pending.Passenger?.Quarantine(); return false; }
                try { pending.Passenger?.RequireContinuation(routine); }
                catch (Exception error)
                { pending.Failure = error.Message; pending.Passenger?.Quarantine(); return false; }
                return !CartWriterHooks.IsHeld(pending.Cart) && OccupantsFor(pending.Cart) == null;
            }

            internal static void After(ExitRoutine routine, bool ran, bool more, Exception error)
            {
                if (!OnSelectedThread()) { _failure ??= "Cart continuation completion left its thread."; return; }
                if (!PendingExits.TryGetValue(routine.Pointer, out var pending)) return;
                pending.Passenger?.CompleteContinuation(ran, more, error);
                if (error != null) pending.Failure ??= "Original native cart continuation failed: " + error.Message;
                else if (ran && !more) PendingExits.Remove(routine.Pointer);
            }
        }
    }
}
