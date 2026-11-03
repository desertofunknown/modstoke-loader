// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace Modstoke.Sons
{
    /// <summary>Exact-actor Animator receiver; its presence prevents Unity's automatic root application.</summary>
    public sealed class PlayerClimbDriver : MonoBehaviour
    {
        private PlayerClimbMotor _motor;
        private bool _retired;

        public PlayerClimbDriver(IntPtr pointer) : base(pointer) { }

        [HideFromIl2Cpp]
        internal void Bind(PlayerClimbMotor motor)
        {
            if (_retired || _motor != null || motor == null || motor.IsDisposed)
                throw new InvalidOperationException("A climb receiver cannot be rebound or attached to a retired motor.");
            _motor = motor;
        }

        [HideFromIl2Cpp]
        internal bool IsBoundTo(PlayerClimbMotor motor) => !_retired && motor != null && ReferenceEquals(_motor, motor);

        [HideFromIl2Cpp]
        internal void Unbind(PlayerClimbMotor motor)
        { if (ReferenceEquals(_motor, motor)) { _retired = true; _motor = null; } }

        private void FixedUpdate() => _motor?.FixedTick();
        private void OnAnimatorMove() => _motor?.AnimatorMove();
        private void Update() => _motor?.UpdateTick();
        private void OnDisable() => _motor?.DriverDisabled();
        private void OnDestroy() => _motor?.Dispose();
    }
}
