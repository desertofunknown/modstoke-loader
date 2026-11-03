// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Areas;
using global::Sons.Physics;
using static Modstoke.Sons.PlayerSurvivalEnvironmentExposure;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerExposureGraph
    {
        private void AddProxy(TriggerProxy value)
        {
            if (_proxies.ContainsKey(value.Pointer)) return;
            Pin(value);
            var node = new Proxy { Native = value, Block = value._block, Tag = value._tagFilter };
            _definition.Write(node.Block); Text(node.Tag ?? "");
            node.Calls = new[]
            {
                ProxyCalls(value, value.TriggerEnterEvent, CaveSystem.EnterCallbackIndex, false),
                ProxyCalls(value, value.TriggerExitEvent, CaveSystem.ExitCallbackIndex, true),
                ProxyCalls(value, value.TriggerStayEvent, CaveSystem.StayCallbackIndex, false)
            };
            var enter = value.TriggerEnterEvent; var exit = value.TriggerExitEvent; var stay = value.TriggerStayEvent;
            AddCheck(value, () =>
            {
                if (!Alive(value) || value._block != node.Block || value._tagFilter != node.Tag
                    || !SameDelegate(value.TriggerEnterEvent, enter) || !SameDelegate(value.TriggerExitEvent, exit)
                    || !SameDelegate(value.TriggerStayEvent, stay))
                    throw new InvalidOperationException("Cave trigger proxy filtering changed.");
            });
            _proxies.Add(value.Pointer, node);
        }

        private Action<int> ProxyCalls(TriggerProxy proxy, Il2CppSystem.Delegate callbacks, int slot, bool exit)
        {
            var list = callbacks?.GetInvocationList();
            int count = list?.Length ?? 0;
            if (count > 256) throw new InvalidOperationException("Cave proxy callbacks exceed their mapping bound.");
            _definition.Write(slot); _definition.Write(count);
            var calls = new Action<int>[count];
            for (int i = 0; i < count; i++)
            {
                var callback = list[i];
                CaveSystem owner = null;
                foreach (var cave in _caves.Values)
                {
                    var bindings = cave.Native._extentsCallbacks;
                    if (bindings == null || !bindings.TryGetValue(proxy, out var entries) || entries == null
                        || slot < 0 || slot >= entries.Length || entries[slot] == null) continue;
                    // Match the actual registered closure, not a compiler-generated name
                    // or a guessed relationship between two overlapping scene extents.
                    if (callback == null || callback.Pointer != entries[slot].Pointer) continue;
                    if (owner != null) throw new InvalidOperationException("Ambiguous native cave proxy callback owner.");
                    owner = cave.Native;
                }
                if (owner == null) throw new InvalidOperationException("Cave proxy has a callback without an exact registered CaveSystem owner.");
                Pin(owner);
                CaveSystem captured = owner;
                calls[i] = _ => ExtentContact(captured, proxy, exit);
            }
            return Join(calls);
        }
    }
}
