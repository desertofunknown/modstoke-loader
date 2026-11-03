// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace Modstoke.Preloader
{
    /// <remarks>
    /// Addresses are borrowed for synchronous use before disposal. Callers must serialize
    /// scope operations, address use and disposal. Disposal is terminal; previously returned
    /// pointers must not be used afterward and cannot be revoked by this scope.
    /// </remarks>
    public sealed partial class InteropNativeReferenceScope : IDisposable
    {
        private readonly IntPtr[] handles;
        private int count;
        private bool disposed;

        public InteropNativeReferenceScope(int capacity) => handles = new IntPtr[capacity];

        public IntPtr ConstructorReceiver(InteropReferenceLocation location, IntPtr method) => ValueReceiver(location, method);

        public IntPtr ConstructorArgument(InteropReferenceLocation location, IntPtr method, int index) => ValueArgument(location, method, index);

        public IntPtr ValueReceiver(InteropReferenceLocation location, IntPtr method)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (method == IntPtr.Zero) throw new MissingMethodException("The native value method is unavailable.");
            return StorageAddress(location, IL2CPP.il2cpp_method_get_class(method), writable: true);
        }

        public IntPtr ValueArgument(InteropReferenceLocation location, IntPtr method, int index)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return StorageAddress(location, ReferenceParameterClass(method, index), writable: true);
        }

        public IntPtr MethodArgument(InteropReferenceLocation location, IntPtr method, int index)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var elementClass = ReferenceParameterClass(method, index);
            // Native code owns assignment barriers for reference slots as well as aggregate fields.
            return IL2CPP.il2cpp_class_is_valuetype(elementClass)
                ? StorageAddress(location, elementClass, writable: true)
                : ReferenceAddress(location, elementClass);
        }

        private static IntPtr ReferenceParameterClass(IntPtr method, int index)
        {
            if (method == IntPtr.Zero || index < 0 || index >= IL2CPP.il2cpp_method_get_param_count(method))
                throw new MissingMethodException("The native value method parameter is unavailable.");
            var parameter = IL2CPP.il2cpp_method_get_param(method, (uint)index);
            if (parameter == IntPtr.Zero || !IL2CPP.il2cpp_type_is_byref(parameter))
                throw new NotSupportedException("A native value location must correspond to a byref parameter.");
            var elementClass = IL2CPP.il2cpp_class_from_type(parameter);
            if (elementClass == IntPtr.Zero)
                throw new MissingMethodException("The native byref parameter has no resolved class.");
            return elementClass;
        }

        internal IntPtr Pin(Il2CppObjectBase owner)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (owner == null) throw new NullReferenceException();
            if (count == handles.Length) throw new InvalidOperationException("Native pin capacity was exceeded.");
            var handle = IL2CPP.il2cpp_gchandle_new(owner.Pointer, true);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("The native owner could not be pinned.");
            // No allocations or throwing work between acquisition and registering cleanup.
            handles[count++] = handle;
            var pointer = IL2CPP.il2cpp_gchandle_get_target(handle);
            GC.KeepAlive(owner);
            if (pointer == IntPtr.Zero) throw new InvalidOperationException("The pinned native owner is unavailable.");
            return pointer;
        }

        internal IntPtr Address(InteropReferenceLocation location, IntPtr elementClass)
            => WritableAddress(location, elementClass);

        internal IntPtr ReadAddress(InteropReferenceLocation location, IntPtr elementClass)
            => ResolveAddress(location, elementClass, writable: false);

        internal IntPtr WritableAddress(InteropReferenceLocation location, IntPtr elementClass)
            => ResolveAddress(location, elementClass, writable: true);

        internal IntPtr StorageAddress(InteropReferenceLocation location, IntPtr elementClass, bool writable)
            => ResolveAddress(location, elementClass, writable, allowReferences: true);

        internal IntPtr ReferenceAddress(InteropReferenceLocation location, IntPtr elementClass, bool writable = true)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(location);
            if (writable && location.IsReadOnly)
                throw new NotSupportedException("A readonly native reference cannot be written or passed to an atomic operation.");
            if (IntPtr.Size != 8 || elementClass == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(elementClass))
                throw new NotSupportedException("A native reference slot requires x64 reference identity.");
            if (IsStaticField(location))
                return AlignedReference(StaticAddress(location, elementClass, IntPtr.Size, writable));
            if (location.Parent == null && location.RectangularRank == 0 &&
                location.FieldOwner == null && location.FieldPointerName == null && location.Owner is Il2CppArrayBase array)
                return ReferenceArrayAddress(array, location.Index, elementClass, location.IsReadOnly);
            if (location.FieldOwner == null || location.FieldPointerName == null || location.RectangularRank != 0)
                throw new NotSupportedException("A native reference requires a rooted instance field or vector element.");
            var field = InteropNativeReferenceCall.ReadMetadataPointer(location.FieldOwner, location.FieldPointerName);
            if ((IL2CPP.il2cpp_field_get_flags(field) & (writable ? 0x70 : 0x50)) != 0 ||
                IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != elementClass)
                throw new NotSupportedException("The field must have the exact native reference type and requested access.");
            if (location.Parent != null)
                return AlignedReference(NestedFieldAddress(location, field, IntPtr.Size, writable));
            if (location.Owner is not Il2CppObjectBase owner)
                throw new NotSupportedException("The instance reference field has no pinnable native owner.");
            var pointer = Pin(owner);
            var ownerClass = IL2CPP.il2cpp_object_get_class(pointer);
            if (!IL2CPP.il2cpp_class_is_assignable_from(IL2CPP.il2cpp_field_get_parent(field), ownerClass))
                throw new InvalidCastException("The reference field does not belong to the pinned native owner.");
            var offset = IL2CPP.il2cpp_field_get_offset(field);
            if (offset < il2cpp_object_header_size() || offset > int.MaxValue ||
                (long)offset + IntPtr.Size > IL2CPP.il2cpp_class_instance_size(ownerClass))
                throw new NotSupportedException("The reference field lies outside its native owner.");
            return AlignedReference(new IntPtr(checked(pointer.ToInt64() + offset)));
        }

        private static IntPtr AlignedReference(IntPtr address) =>
            (address.ToInt64() & (IntPtr.Size - 1)) == 0 ? address :
                throw new NotSupportedException("The native reference field is not pointer-aligned.");

        internal IntPtr ReferenceArrayAddress(Il2CppArrayBase owner, int index, IntPtr elementClass, bool readOnly)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Scoped native array references require the verified x64 layout.");
            var pointer = Pin(owner);
            InteropNativeReferenceArray.ValidateLocation(pointer, index, elementClass, readOnly);
            var address = checked(pointer.ToInt64() + il2cpp_array_object_header_size() + (long)index * IntPtr.Size);
            if ((address & (IntPtr.Size - 1)) != 0)
                throw new NotSupportedException("The native array reference is not pointer-aligned.");
            return new IntPtr(address);
        }

        private IntPtr ResolveAddress(InteropReferenceLocation location, IntPtr elementClass, bool writable,
            bool allowReferences = false)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentNullException.ThrowIfNull(location);
            if (writable && location.IsReadOnly)
                throw new NotSupportedException("A readonly native location cannot be used for a write or native call.");
            if (IntPtr.Size != 8)
                throw new PlatformNotSupportedException("Scoped native references require the verified x64 runtime contract.");
            if (elementClass == IntPtr.Zero || !IL2CPP.il2cpp_class_is_valuetype(elementClass) ||
                !allowReferences && IL2CPP.il2cpp_class_has_references(elementClass))
                throw new NotSupportedException("Scoped calls currently require native value slots without embedded references.");
            uint alignment = 0;
            var size = IL2CPP.il2cpp_class_value_size(elementClass, ref alignment);
            if (size <= 0) throw new NotSupportedException("The native value slot has no verified size.");
            if (IsStaticField(location))
                return StaticAddress(location, elementClass, size, writable);
            if (location.Parent != null)
            {
                var nestedField = InteropNativeReferenceCall.ReadMetadataPointer(location.FieldOwner, location.FieldPointerName);
                if ((IL2CPP.il2cpp_field_get_flags(nestedField) & (writable ? 0x70 : 0x50)) != 0 ||
                    IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(nestedField)) != elementClass)
                    throw new NotSupportedException("A nested reference requires an exact instance value field with the requested access.");
                return NestedFieldAddress(location, nestedField, size, writable);
            }
            if (location.Owner is not Il2CppObjectBase owner)
                throw new NotSupportedException("The native location has no pinnable owner; static and external references are not supported.");
            var pointer = Pin(owner);
            var ownerClass = IL2CPP.il2cpp_object_get_class(pointer);
            if (location.FieldPointerName == null)
            {
                var rectangular = location.RectangularRank != 0;
                if ((!rectangular && owner is not Il2CppArrayBase) ||
                    (rectangular && (location.RectangularRank < 1 || location.RectangularRank > 32)) ||
                    IL2CPP.il2cpp_class_get_rank(ownerClass) != (rectangular ? location.RectangularRank : 1) ||
                    IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_class_get_type(ownerClass)) != (rectangular ? 0x14 : 0x1d) ||
                    IL2CPP.il2cpp_class_get_element_class(ownerClass) != elementClass ||
                    IL2CPP.il2cpp_array_element_size(ownerClass) != size)
                    throw new NotSupportedException("The native array element identity or layout differs from the reference.");
                var index = rectangular ? location.LinearIndex : location.Index;
                if (index < 0 || (ulong)index >= IL2CPP.il2cpp_array_length(pointer))
                    throw new IndexOutOfRangeException();
                var offset = checked((long)il2cpp_array_object_header_size() + index * size);
                return new IntPtr(checked(pointer.ToInt64() + offset));
            }
            var field = InteropNativeReferenceCall.ReadMetadataPointer(location.FieldOwner, location.FieldPointerName);
            // A native object (including an actual boxed value) owns these field bytes. Field
            // offsets include its object header, as in the existing generated native accessors.
            if ((IL2CPP.il2cpp_field_get_flags(field) & (writable ? 0x70 : 0x50)) != 0 ||
                IL2CPP.il2cpp_class_from_type(IL2CPP.il2cpp_field_get_type(field)) != elementClass ||
                !IL2CPP.il2cpp_class_is_assignable_from(IL2CPP.il2cpp_field_get_parent(field), ownerClass))
                throw new NotSupportedException("The field is not an exact instance value slot with the requested access.");
            var fieldOffset = IL2CPP.il2cpp_field_get_offset(field);
            if (fieldOffset < il2cpp_object_header_size() || fieldOffset > int.MaxValue ||
                (long)fieldOffset + size > IL2CPP.il2cpp_class_instance_size(ownerClass))
                throw new NotSupportedException("The field lies outside its pinned native owner.");
            return new IntPtr(checked(pointer.ToInt64() + fieldOffset));
        }

        private IntPtr NestedFieldAddress(InteropReferenceLocation location, IntPtr field, int size, bool writable)
        {
            var container = IL2CPP.il2cpp_field_get_parent(field);
            if (!IL2CPP.il2cpp_class_is_valuetype(container))
                throw new NotSupportedException("A nested field must belong to its containing native value type.");
            uint alignment = 0;
            var containerSize = IL2CPP.il2cpp_class_value_size(container, ref alignment);
            var offset = IL2CPP.il2cpp_field_get_offset(field);
            var headerSize = il2cpp_object_header_size();
            if (offset < headerSize || offset > int.MaxValue || containerSize <= 0 ||
                (long)offset - headerSize + size > containerSize)
                throw new NotSupportedException("The nested field lies outside its containing native value.");
            // Walking storage does not copy the aggregate. Only the final leaf may be read or
            // written, with a reference barrier or a separately verified reference-free layout.
            var parent = ResolveAddress(location.Parent, container, writable, allowReferences: true);
            return new IntPtr(checked(parent.ToInt64() + offset - headerSize));
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Exception failure = null;
            while (count != 0)
            {
                var index = --count;
                var handle = handles[index];
                handles[index] = IntPtr.Zero;
                try { IL2CPP.il2cpp_gchandle_free(handle); }
                catch (Exception error) { InteropExceptionSupport.CollectNativeCleanupFailure(error, ref failure); }
            }
            if (failure != null)
                InteropExceptionSupport.FinishNativeCleanup(null, failure, nameof(InteropNativeReferenceScope));
        }

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_array_object_header_size();

        [DllImport("GameAssembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern uint il2cpp_object_header_size();
    }
}
