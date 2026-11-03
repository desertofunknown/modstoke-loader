// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;
using UnityEngine;
using UnityEngine.ParticleSystemJobs;

namespace Modstoke.Jobs;

public readonly unsafe ref partial struct ParticleJobData
{
    private static readonly object LayoutGate = new();
    private static IntPtr _particleClass;

    // The runtime attaches this producer to IL2CPP before entering the managed callback.
    internal static ParticleJobData Read(IntPtr additionalData)
    {
        RequireLayout();
        if (additionalData == IntPtr.Zero)
            throw new InvalidOperationException("Unity supplied no particle job descriptor.");
        var system = ((NativeListData*)additionalData)->system;
        if (system == IntPtr.Zero)
            throw new InvalidOperationException("Unity supplied no native particle system.");
        ParticleSystem.CopyManagedJobData(system.ToPointer(), out var nativeData);
        if (nativeData.count < 0)
            throw new InvalidOperationException("Unity supplied a negative particle count.");
        return new ParticleJobData(in nativeData);
    }

    private static void RequireLayout()
    {
        var validated = Volatile.Read(ref _particleClass);
        if (validated == IntPtr.Zero)
        {
            lock (LayoutGate)
            {
                validated = _particleClass;
                if (validated == IntPtr.Zero)
                {
                    if (IntPtr.Size != 8 || sizeof(NativeParticleData) != 256 ||
                        sizeof(NativeParticleData.Array3) != 24 || sizeof(NativeParticleData.Array4) != 32 ||
                        sizeof(NativeListData) != 16 || sizeof(Color32) != 4 ||
                        !typeof(NativeParticleData).IsExplicitLayout ||
                        !typeof(NativeParticleData.Array3).IsExplicitLayout ||
                        !typeof(NativeParticleData.Array4).IsExplicitLayout)
                        throw new NotSupportedException("The CLR particle data layout differs from Unity 2022.2.16 x64.");

                    RequireManagedField<NativeListData>("system", 0, typeof(IntPtr));
                    RequireManagedField<NativeListData>("length", 8, typeof(int));
                    RequireManagedField<NativeListData>("capacity", 12, typeof(int));
                    validated = Il2CppClassPointerStore<NativeParticleData>.NativeClassPtr;
                    var array3 = Il2CppClassPointerStore<NativeParticleData.Array3>.NativeClassPtr;
                    var array4 = Il2CppClassPointerStore<NativeParticleData.Array4>.NativeClassPtr;
                    var color = Il2CppClassPointerStore<Color32>.NativeClassPtr;
                    if (validated == IntPtr.Zero || validated != IL2CPP.GetIl2CppClass(
                            "UnityEngine.ParticleSystemModule.dll", "UnityEngine.ParticleSystemJobs", "NativeParticleData") ||
                        array3 == IntPtr.Zero || array3 != IL2CPP.GetIl2CppNestedType(validated, "Array3") ||
                        array4 == IntPtr.Zero || array4 != IL2CPP.GetIl2CppNestedType(validated, "Array4") ||
                        color == IntPtr.Zero || color != IL2CPP.GetIl2CppClass(
                            "UnityEngine.CoreModule.dll", "UnityEngine", "Color32"))
                        throw new NotSupportedException("Particle data native class identities do not match the SDK.");
                    RequireNativeSize(validated, 256, 8);
                    RequireNativeSize(array3, 24, 8);
                    RequireNativeSize(array4, 32, 8);
                    RequireNativeSize(color, 4, 4);

                    RequireField<NativeParticleData>(validated, "count", 0, typeof(int),
                        Il2CppClassPointerStore<int>.NativeClassPtr);
                    var vector3Fields = new[] { "positions", "velocities", "axisOfRotations", "rotations", "rotationalSpeeds", "sizes" };
                    for (var index = 0; index < vector3Fields.Length; index++)
                        RequireField<NativeParticleData>(validated, vector3Fields[index], 8 + index * 24,
                            typeof(NativeParticleData.Array3), array3);
                    var scalarFields = new[] { "startColors", "aliveTimePercent", "inverseStartLifetimes", "randomSeeds" };
                    for (var index = 0; index < scalarFields.Length; index++)
                        RequireField<NativeParticleData>(validated, scalarFields[index], 152 + index * 8,
                            typeof(IntPtr), IntPtr.Zero, Il2CppTypeEnum.IL2CPP_TYPE_VOID);
                    RequireField<NativeParticleData>(validated, "customData1", 184, typeof(NativeParticleData.Array4), array4);
                    RequireField<NativeParticleData>(validated, "customData2", 216, typeof(NativeParticleData.Array4), array4);
                    RequireField<NativeParticleData>(validated, "meshIndices", 248, typeof(IntPtr),
                        IntPtr.Zero, Il2CppTypeEnum.IL2CPP_TYPE_VOID);

                    var components = new[] { "x", "y", "z", "w" };
                    for (var index = 0; index < components.Length; index++)
                    {
                        if (index < 3)
                            RequireField<NativeParticleData.Array3>(array3, components[index], index * 8,
                                typeof(IntPtr), IntPtr.Zero, Il2CppTypeEnum.IL2CPP_TYPE_R4);
                        RequireField<NativeParticleData.Array4>(array4, components[index], index * 8,
                            typeof(IntPtr), IntPtr.Zero, Il2CppTypeEnum.IL2CPP_TYPE_R4);
                    }
                    var colors = new[] { "r", "g", "b", "a" };
                    for (var index = 0; index < colors.Length; index++)
                        RequireField<Color32>(color, colors[index], index, typeof(byte),
                            Il2CppClassPointerStore<byte>.NativeClassPtr);
                    Volatile.Write(ref _particleClass, validated);
                }
            }
        }
        if (validated != Il2CppClassPointerStore<NativeParticleData>.NativeClassPtr)
            throw new NotSupportedException("The particle data native class identity has changed.");
    }

    private static void RequireNativeSize(IntPtr type, int size, uint expectedAlignment)
    {
        uint alignment = 0;
        if (!IL2CPP.il2cpp_class_is_valuetype(type) || IL2CPP.il2cpp_class_has_references(type) ||
            IL2CPP.il2cpp_class_value_size(type, ref alignment) != size || alignment != expectedAlignment ||
            IL2CPP.il2cpp_class_array_element_size(type) != size)
            throw new NotSupportedException("The native particle value layout differs from the SDK.");
    }

    private static void RequireManagedField<T>(string name, int offset, Type fieldType) where T : struct
    {
        var field = typeof(T).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null || field.FieldType != fieldType || Marshal.OffsetOf<T>(name) != new IntPtr(offset))
            throw new NotSupportedException("The CLR particle field layout differs from the SDK: " +
                typeof(T).Name + "." + name);
    }

    private static void RequireField<T>(IntPtr type, string name, int offset, Type managedFieldType,
        IntPtr nativeFieldClass, Il2CppTypeEnum pointerElement = Il2CppTypeEnum.IL2CPP_TYPE_END) where T : struct
    {
        RequireManagedField<T>(name, offset, managedFieldType);
        var field = IL2CPP.il2cpp_class_get_field_from_name(type, name);
        if (field == IntPtr.Zero || ((FieldAttributes)IL2CPP.il2cpp_field_get_flags(field) & FieldAttributes.Static) != 0 ||
            IL2CPP.il2cpp_field_get_offset(field) != 16 + offset)
            throw new NotSupportedException("The native particle field offset differs from the SDK: " +
                typeof(T).Name + "." + name);
        var fieldType = IL2CPP.il2cpp_field_get_type(field);
        if (fieldType == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(fieldType))
            throw new NotSupportedException("The native particle field type differs from the SDK: " +
                typeof(T).Name + "." + name);
        if (pointerElement == Il2CppTypeEnum.IL2CPP_TYPE_END)
        {
            if (nativeFieldClass == IntPtr.Zero || IL2CPP.il2cpp_class_from_type(fieldType) != nativeFieldClass)
                throw new NotSupportedException("The native particle field class differs from the SDK: " +
                    typeof(T).Name + "." + name);
        }
        else
        {
            var pointer = UnityVersionHandler.Wrap((Il2CppTypeStruct*)fieldType);
            if (pointer.ValueType || pointer.Type != Il2CppTypeEnum.IL2CPP_TYPE_PTR || pointer.Data == IntPtr.Zero)
                throw new NotSupportedException("The native particle field is not the expected pointer: " +
                    typeof(T).Name + "." + name);
            var element = UnityVersionHandler.Wrap((Il2CppTypeStruct*)pointer.Data);
            if (element.ByRef || element.Type != pointerElement)
                throw new NotSupportedException("The native particle pointer element differs from the SDK: " +
                    typeof(T).Name + "." + name);
        }
    }
}
