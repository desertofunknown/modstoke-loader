// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private unsafe BoltMigrationAdmission ReadKnockbackAdmission(out string detail)
        {
            detail = "The retained knockback trigger storage layout is not qualified.";
            var state = _state.TryCast<global::PlayerState>();
            if (!Live(state)) return BoltMigrationAdmission.Rejected;
            BoltProperties.RequireLayout();
            RuntimeHelpers.RunClassConstructor(typeof(NetworkProperty_Trigger).TypeHandle);
            RuntimeHelpers.RunClassConstructor(typeof(NetworkValue).TypeHandle);
            var valueType = Il2CppClassPointerStore<NetworkValue>.NativeClassPtr;
            var localField = IL2CPP.il2cpp_class_get_field_from_name(valueType, "TriggerLocal");
            var sendField = IL2CPP.il2cpp_class_get_field_from_name(valueType, "TriggerSend");
            var objectField = IL2CPP.il2cpp_class_get_field_from_name(valueType, "Object");
            if (localField == IntPtr.Zero || sendField == IntPtr.Zero || objectField == IntPtr.Zero
                || IL2CPP.il2cpp_field_get_offset(localField) != 16
                || IL2CPP.il2cpp_field_get_offset(sendField) != 24
                || IL2CPP.il2cpp_field_get_offset(objectField) != 32)
                return BoltMigrationAdmission.Rejected;
            state.GetDynamicInternal("knockBackTrigger", out int index);
            var properties = state.Meta?.Properties;
            var objects = state.RootObjects;
            if (properties == null || properties.WasCollected || index < 0 || index >= properties.Length
                || properties.Length > 4096 || objects == null || objects.WasCollected)
                return BoltMigrationAdmission.Rejected;
            byte* descriptor = (byte*)properties.Pointer + 32 + 32L * index;
            int objectIndex = *(int*)(descriptor + 16);
            IntPtr pointer = *(IntPtr*)(descriptor + 24);
            if (pointer == IntPtr.Zero || objectIndex < 0 || objectIndex >= objects.Count
                || objects[objectIndex]?.Pointer != state.Pointer || state.RootState?.Pointer != state.Pointer
                || IL2CPP.il2cpp_object_get_class(pointer) != Il2CppClassPointerStore<NetworkProperty_Trigger>.NativeClassPtr)
                return BoltMigrationAdmission.Rejected;
            var property = Il2CppObjectPool.Get<NetworkProperty_Trigger>(pointer);
            // Matched PlayerState.knockBackTrigger (0x804590): root property 97/storage 114.
            if (property.PropertyName != "knockBackTrigger" || property.OffsetProperties != 97
                || property.OffsetStorage != 114 || state.OffsetProperties + property.OffsetProperties != index)
                return BoltMigrationAdmission.Rejected;
            var frames = state.Frames;
            var storage = state.Storage;
            var values = storage?.Values;
            long slot = (long)state.OffsetStorage + property.OffsetStorage;
            // This source-owned participant does not certify interpolated replica histories.
            if (frames == null || frames.WasCollected || frames.count != 1 || !Same(frames.first, storage)
                || storage?.Root?.Pointer != state.Pointer || values == null || values.WasCollected
                || state.OffsetStorage < 0 || slot < 0 || slot >= values.Length)
            { detail = "Knockback drain requires the original owner's single current native storage frame."; return BoltMigrationAdmission.Rejected; }
            byte* value = (byte*)values.Pointer + 32 + 24 * slot;
            if (*(IntPtr*)(value + 16) != IntPtr.Zero)
            { detail = "A native knockback callback is present; callback reconstruction remains unqualified."; return BoltMigrationAdmission.Rejected; }
            // Matched NetworkProperty_Trigger.MecanimPush (0x9645D0) clears these local
            // history bits BEFORE invoking the callback/Animator trigger. TriggerSend is sent
            // history, not a pending callback queue, and is deliberately not reset or replayed.
            if (*(int*)(value + 4) != 0)
            { detail = "Native knockBackTrigger delivery has not drained."; return BoltMigrationAdmission.Pending; }
            detail = "Native knockback delivery queue is empty and the original callback is null.";
            return BoltMigrationAdmission.Ready;
        }
    }
}
