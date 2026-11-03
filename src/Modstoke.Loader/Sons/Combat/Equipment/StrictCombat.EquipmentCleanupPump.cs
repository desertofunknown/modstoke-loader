// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;

namespace Modstoke.Sons.Combat
{
    // Retain before any equipment mutation so retiring a mod cannot orphan its cleanup.
    // Unfinished operations have no expiry and are never evicted to admit another client.
    internal static class StrictCombatEquipmentCleanupPump
    {
        private static readonly StrictCombatClient[] Clients = new StrictCombatClient[32];
        private static int _ownerThreadId;
        private static bool _ticking;

        internal static bool TryRetain(StrictCombatClient client, int ownerThreadId)
        {
            if (Environment.CurrentManagedThreadId != ownerThreadId) return false;
            int registeredThread = Interlocked.CompareExchange(ref _ownerThreadId, ownerThreadId, 0);
            if ((registeredThread != 0 && registeredThread != ownerThreadId) || _ticking) return false;

            int available = -1;
            for (int i = 0; i < Clients.Length; i++)
            {
                var retained = Clients[i];
                if (ReferenceEquals(retained, client)) return true;
                if (retained is not null && !retained.HasUnfinishedEquipmentOperation)
                    Clients[i] = null;
                if (Clients[i] is null && available < 0) available = i;
            }
            if (available < 0) return false;
            Clients[available] = client;
            return true;
        }

        internal static void Tick()
        {
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId) || _ticking) return;
            _ticking = true;
            try
            {
                for (int i = 0; i < Clients.Length; i++)
                {
                    var client = Clients[i];
                    if (client is null) continue;
                    if (client.EquipmentCleanupPending) client.UpdateLocalEquipment();
                    // Finished uncertainty is still uncertainty; releasing this retention
                    // never makes the client's operation returned or permits another try.
                    if (!client.HasUnfinishedEquipmentOperation) Clients[i] = null;
                }
            }
            finally { _ticking = false; }
        }
    }
}
