// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Attributes;
using global::Sons.Gameplay;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Actor-local native ISleepingReceiver; never invokes PlayerStats' LocalPlayer callbacks.</summary>
    public sealed class PlayerSleepReceiver : MonoBehaviour
    {
        private PlayerSurvivalRuntime _owner;
        private bool _retired;

        public PlayerSleepReceiver(IntPtr pointer) : base(pointer) { }

        [HideFromIl2Cpp]
        internal void Bind(PlayerSurvivalRuntime owner)
        {
            if (_retired || _owner != null || owner == null || owner.IsDisposed)
                throw new InvalidOperationException("A native actor sleep receiver cannot be rebound.");
            _owner = owner;
        }

        [HideFromIl2Cpp]
        internal bool IsBoundTo(PlayerSurvivalRuntime owner) => !_retired && ReferenceEquals(_owner, owner);

        [HideFromIl2Cpp]
        internal void Unbind(PlayerSurvivalRuntime owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _retired = true;
            _owner = null;
        }

        [HideFromIl2Cpp]
        internal void ReceiveDedicatedSleepFade(SleepingManager manager) => _owner?.ReceiveDedicatedSleepFade(manager);

        public void OnWentToSleep() => _owner?.ReceiveNativeSleep(Pointer, true);
        public void OnWokeUp() => _owner?.ReceiveNativeSleep(Pointer, false);
        private void OnDestroy() => _owner?.SleepReceiverDestroyed();
    }
}
