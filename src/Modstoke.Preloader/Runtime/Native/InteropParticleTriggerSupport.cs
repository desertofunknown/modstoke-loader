// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    // Public because the restored ParticleSystemModule calls this helper.
    public static class InteropParticleTriggerSupport
    {
        public static int Get<TParticle, TComponent>(Il2CppObjectBase system, int type, Il2CppObjectBase particles,
            ref Il2CppReferenceArray<TComponent> colliders, ref Il2CppStructArray<int> colliderIndices,
            ref Il2CppStructArray<int> particleStartIndices)
            where TParticle : unmanaged
            where TComponent : Il2CppObjectBase =>
            NativeParticleTriggerCalls.Get<TParticle, TComponent>(system, type, particles,
                ref colliders, ref colliderIndices, ref particleStartIndices, InteropExceptionSupport.WrapNativeException);
    }
}
