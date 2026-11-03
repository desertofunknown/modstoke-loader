// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        // Consult from the existing qualified replica frame/transition and continuation
        // owner/scheduler gates. Do not install another remote hook owner here: admission
        // occurs after the parent has already patched and observed those native entries.
        // A held IEnumerator yields unchanged; its factory is never replayed or stopped.
        internal static bool IsOccupiedPlayerWriterHeld(Component component)
            => CartOccupantWriterHooks.IsPlayerWriterHeld(component);

        // Committed effects are not sampling ticks. The upstream ingress owner must retain
        // them before this boundary; unexpected delivery invalidates the cart transaction.
        internal static bool TryBlockOccupiedPlayerEffect(Component component, string method)
            => CartOccupantWriterHooks.TryBlockPlayerEffect(component, method);

        private static partial class CartOccupantWriterHooks
        {
            internal static bool PlayerRigWritersQualified { get; private set; }

            internal static bool IsPlayerWriterHeld(Component component)
                => component is not null && Find(component.Pointer)?.OwnsRetainedPlayerWriter(component) == true;

            internal static bool TryBlockPlayerEffect(Component component, string method)
            {
                var hold = component is null ? null : Find(component.Pointer);
                if (hold?.OwnsRetainedPlayerWriter(component) != true) return false;
                Interlocked.CompareExchange(ref hold.Conflict,
                    "Committed seated player effect reached its held writer: " + method, null);
                return true;
            }

            // Add before installing the existing occupant hook set. Only the exact retained
            // rig pointers are gated; camera look and every unrelated player remain native.
            private static void QualifyPlayerRigWriters(List<MethodInfo> receivers)
            {
                receivers.AddRange(new[]
                {
                    Q(typeof(FirstPersonCharacter), "Update", 0x2F7E640, 3344, "29B9D5C15EBF9452AAE1A2C0AC92B5EB547C5189104EEA4B15FCA3ECDCBCA034"),
                    Q(typeof(FirstPersonCharacter), "FixedUpdate", 0x2F7FF20, 4048, "408637FA1C258F9BA8B3B0B2595835C4F5A5775FD8E30C42FD6FCA18DF942624"),
                    Q(typeof(PlayerAnimatorControl), "Update", 0x35A2D90, 5344, "2DD66D243E7313234F8BDC5E85823BCD7AD205F0234BA0187F25C5A471BF1045"),
                    Q(typeof(PlayerAnimatorControl), "FixedUpdate", 0x35A44D0, 1968, "D1A5F4F7116AB5AD2714FE5D681FCED7592BAD54FE7613D97CA69EF998F3999D"),
                    Q(typeof(PlayerAnimatorControl), "LateUpdate", 0x35A4CF0, 928, "3230AC7DBCFB2C3BAEEC0499D13D3812286947DFD0CB1E9AE192DC1D62693C82"),
                    Q(typeof(PlayerAnimatorControl), "OnAnimatorMove", 0x35A5B50, 208, "2DAC961613E715D3306A9FE9B9F35351DAC76AD94CD54CF2A0DA4C2D6ED7D672")
                });
                PlayerRigWritersQualified = true;
            }
        }
    }
}
