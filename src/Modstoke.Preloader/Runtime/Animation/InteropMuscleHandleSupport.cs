// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Startup;

namespace Modstoke.Preloader
{
    // Public because the restored AnimationModule calls this helper.
    public static class InteropMuscleHandleSupport
    {
        private const int MuscleCount = 95;
        private const string AnimationModule = "UnityEngine.AnimationModule";
        private static readonly Version SupportedVersion = new(2022, 2, 16);

        public static void GetMuscleHandles<T>(T[] muscleHandles) where T : struct
        {
            ArgumentNullException.ThrowIfNull(muscleHandles);
            if (muscleHandles.Length < MuscleCount)
                throw new ArgumentException("At least 95 muscle handles are required.", nameof(muscleHandles));
            if (!HandleLayout<T>.IsSupported)
                throw new NotSupportedException("Muscle enumeration requires the exact restored MuscleHandle layout.");
            if (Il2CppInteropRuntime.Instance.UnityVersion != SupportedVersion)
                throw new NotSupportedException("Muscle enumeration requires the matched Unity 2022.2.16 runtime.");

            // The native enumerator writes these 95 pairs in order and preserves any trailing entries.
            var index = 0;
            for (var part = 0; part < 16; part++)
            {
                var count = part switch { 0 or 4 or 5 => 9, 1 => 12, 2 or 3 => 8, _ => 4 };
                for (var dof = 0; dof < count; dof++)
                    Unsafe.As<T, MuscleValue>(ref muscleHandles[index++]) = new MuscleValue(part, dof);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct MuscleValue
        {
            internal readonly int Part;
            internal readonly int Dof;

            internal MuscleValue(int part, int dof) { Part = part; Dof = dof; }
        }

        private static class HandleLayout<T> where T : struct
        {
            internal static readonly bool IsSupported = Validate();

            private static bool Validate()
            {
                var type = typeof(T);
                if (type.FullName != "UnityEngine.Animations.MuscleHandle" ||
                    type.Assembly.GetName().Name != AnimationModule || type.IsGenericType ||
                    !type.IsLayoutSequential || RuntimeHelpers.IsReferenceOrContainsReferences<T>() ||
                    Unsafe.SizeOf<T>() != 8)
                    return false;

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var fields = type.GetFields(flags);
                var part = type.GetField("<humanPartDof>k__BackingField", flags);
                var dof = type.GetField("<dof>k__BackingField", flags);
                if (fields.Length != 2 || part == null || dof == null || dof.FieldType != typeof(int) ||
                    Marshal.OffsetOf<T>(part.Name) != IntPtr.Zero || Marshal.OffsetOf<T>(dof.Name) != new IntPtr(4))
                    return false;

                var partType = part.FieldType;
                if (partType.FullName != "UnityEngine.HumanPartDof" || partType.Assembly != type.Assembly ||
                    !partType.IsEnum || Enum.GetUnderlyingType(partType) != typeof(int))
                    return false;

                string[] names = ["Body", "Head", "LeftLeg", "RightLeg", "LeftArm", "RightArm",
                    "LeftThumb", "LeftIndex", "LeftMiddle", "LeftRing", "LeftLittle",
                    "RightThumb", "RightIndex", "RightMiddle", "RightRing", "RightLittle", "LastHumanPartDof"];
                var actualNames = Enum.GetNames(partType);
                if (actualNames.Length != names.Length)
                    return false;
                for (var index = 0; index < names.Length; index++)
                {
                    if (actualNames[index] != names[index] ||
                        partType.GetField(names[index])?.GetRawConstantValue() is not int value || value != index)
                        return false;
                }
                return true;
            }
        }
    }
}
