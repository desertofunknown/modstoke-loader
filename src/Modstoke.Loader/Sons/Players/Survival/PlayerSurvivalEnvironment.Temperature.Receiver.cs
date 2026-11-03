// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Native ITemperatureModifierReceiver for a carrier's child capsule.</summary>
    public sealed class PlayerTemperatureReceiver : MonoBehaviour
    {
        private PlayerSurvivalEnvironmentTemperature _owner;
        private bool _retired;

        public PlayerTemperatureReceiver(IntPtr pointer) : base(pointer) { }

        [HideFromIl2Cpp]
        internal void Bind(PlayerSurvivalEnvironmentTemperature owner)
        {
            if (_retired || _owner != null || owner == null || owner.IsDisposed)
                throw new InvalidOperationException("A temperature receiver cannot be rebound.");
            _owner = owner;
        }

        [HideFromIl2Cpp]
        internal bool IsBoundTo(PlayerSurvivalEnvironmentTemperature owner)
            => !_retired && ReferenceEquals(_owner, owner);

        [HideFromIl2Cpp]
        internal void Unbind(PlayerSurvivalEnvironmentTemperature owner)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _retired = true;
            _owner = null;
        }

        public void OnEnteredTemperatureVolume(float temperatureCelsius) => _owner?.Receive(Pointer, true, temperatureCelsius);
        public void OnStayedTemperatureVolume(float temperatureCelsius) => _owner?.Receive(Pointer, true, temperatureCelsius);
        public void OnExitedTemperatureVolume(float temperatureCelsius) => _owner?.Receive(Pointer, false, temperatureCelsius);

        private void OnDestroy() => _owner?.ReceiverDestroyed();
    }
}
