// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Threading;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Preloader
{
    public static class InteropManagedReferenceAtomic
    {
        public static T CompareExchange<T>(ref T location, T value, T comparand)
        {
            if (comparand is not Il2CppObjectBase nativeComparand)
                return Interlocked.CompareExchange(ref location, value, comparand);

            // Separate CLR wrappers may identify the same native object. Compare native
            // identity, but CAS the observed CLR reference at the caller's actual location.
            var observed = Interlocked.CompareExchange(ref location, default, default);
            while (observed is Il2CppObjectBase nativeObserved &&
                InteropNativeReferenceIdentity.AreEqual(nativeObserved, nativeComparand))
            {
                var previous = Interlocked.CompareExchange(ref location, value, observed);
                if (ReferenceEquals(previous, observed)) return previous;
                observed = previous;
            }
            return observed;
        }

        public static T Exchange<T>(ref T location, T value) => Interlocked.Exchange(ref location, value);
    }
}
