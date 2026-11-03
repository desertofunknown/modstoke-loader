// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bolt;
using Il2CppInterop.Runtime;
using NativeEventQueue = Il2CppSystem.Collections.Generic.Queue<Bolt.Event>;

namespace Modstoke.Sons
{
    internal static partial class PlayerEventRouting
    {
        internal static void InstallProtection(HarmonyLib.Harmony harmony,
            Func<Type, string, int, bool, Type, Type[], MethodInfo> qualify)
        {
            if (_protectionPrepared) throw new InvalidOperationException("Native event protection is already installed.");
            PrepareReferenceSchemas();
            // Send and Received inline the queue append: an Enqueue-only ledger is incomplete.
            qualify(typeof(EventDispatcher), nameof(EventDispatcher.Enqueue), 0x94D2D0,
                true, typeof(void), new[] { typeof(Bolt.Event) });
            qualify(typeof(EventDispatcher), nameof(EventDispatcher.Received), 0x94D350,
                true, typeof(void), new[] { typeof(Bolt.Event) });
            qualify(typeof(Bolt.Event), nameof(Bolt.Event.Send), 0x94ADA0,
                false, typeof(void), Type.EmptyTypes);
            qualify(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), 0x94D560,
                true, typeof(void), new[] { typeof(Bolt.Event) });
            var pump = qualify(typeof(EventDispatcher), nameof(EventDispatcher.DispatchAllEvents), 0x94D4A0,
                true, typeof(void), Type.EmptyTypes);
            // The ordinary Poll path inlines the dequeue loop and calls Dispatch directly.
            // There is no callback between its Dequeue and Dispatch; the central scope covers it.
            qualify(typeof(BoltCore), nameof(BoltCore.Poll), 0x7B77F0,
                true, typeof(void), Type.EmptyTypes);

            BoltNativeLayout.Qualify();
            harmony.Patch(pump, prefix: Hook(nameof(BeforeProtectionPump)),
                finalizer: Hook(nameof(FinishProtectionPump)));
            _protectionPrepared = true;
        }

        private static bool ProtectionQueueMatches(IntPtr pointer)
        {
            var queue = EventDispatcher._dispatchQueue;
            return queue != null && !queue.WasCollected && queue.Pointer == pointer;
        }

        private static void CaptureProtectionQueue(ProtectionLease lease)
        {
            var queue = EventDispatcher._dispatchQueue;
            if (queue == null || queue.WasCollected
                || IL2CPP.il2cpp_object_get_class(queue.Pointer) != Il2CppClassPointerStore<NativeEventQueue>.NativeClassPtr)
                throw new InvalidOperationException("The exact native dispatch queue is unavailable.");
            lease.QueuePointer = queue.Pointer;
            int version = queue._version, count = queue._size, head = queue._head, tail = queue._tail;
            var array = queue._array;
            if (array == null || array.WasCollected || count < 0 || count > MaximumProtectionEvents
                || count > array.Length || head < 0 || tail < 0
                || (array.Length == 0 ? count != 0 || head != 0 || tail != 0
                    : head >= array.Length || tail >= array.Length || ((long)head + count) % array.Length != tail))
                throw new InvalidOperationException("The native dispatch queue is invalid or exceeds the bounded capture size.");

            for (int i = 0; i < count; i++)
            {
                if (_protectionFault != null) throw new InvalidOperationException(_protectionFault);
                var value = array[(int)(((long)head + i) % array.Length)];
                if (value == null || value.WasCollected || lease.Pending.ContainsKey(value.Pointer))
                    throw new InvalidOperationException("Queued event identity is null or duplicated.");
                var record = new ProtectionEvent(value, i);
                try { lease.Pending.Add(record.Pointer, record); }
                catch { record.ReleaseRoot(); throw; }
            }
            // Neither ToArray nor an enumerator owns dequeue. Validate the same backing slots
            // after capture; no native callback or queue version change may straddle the cutoff.
            if (!ProtectionQueueMatches(lease.QueuePointer) || queue._version != version
                || queue._size != count || queue._head != head || queue._tail != tail
                || queue._array?.Pointer != array.Pointer)
                throw new InvalidOperationException("Native queue changed while capturing its cutoff.");
            for (int i = 0; i < count; i++)
            {
                if (_protectionFault != null) throw new InvalidOperationException(_protectionFault);
                var value = array[(int)(((long)head + i) % array.Length)];
                if (value == null || !lease.Pending.TryGetValue(value.Pointer, out var record)
                    || record.Ordinal != i || !record.Matches(value))
                    throw new InvalidOperationException("Native event identity changed while capturing its cutoff.");
            }
            if (_protectionFault != null || queue._version != version)
                throw new InvalidOperationException("Native dispatch reentered cutoff capture.");
        }

        internal sealed class ProtectionEvent
        {
            internal readonly IntPtr Pointer;
            internal readonly int Ordinal;
            private readonly IntPtr _class, _storage, _target, _targetConnection, _sourceConnection, _meta;
            private readonly int _targets;
            private readonly EventReferences _schema;
            private readonly ulong[] _references;
            private nint _root;
            internal bool Dispatching;

            internal ProtectionEvent(Bolt.Event value, int ordinal)
            {
                if (!TryReadReferenceSchema(value, out var schema) || schema.Fields.Length > 64)
                    throw new InvalidOperationException("Queued event has an unqualified or oversized entity payload schema.");
                Pointer = value.Pointer;
                Ordinal = ordinal;
                _class = IL2CPP.il2cpp_object_get_class(Pointer);
                _storage = Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventStorage);
                _targets = Marshal.ReadInt32(Pointer, BoltNativeLayout.EventTargets);
                _target = Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventTargetEntity);
                _targetConnection = Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventTargetConnection);
                _sourceConnection = Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventSourceConnection);
                _meta = Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventMeta);
                if (_storage == IntPtr.Zero || _meta == IntPtr.Zero)
                    throw new InvalidOperationException("Queued native event has no live storage or metadata.");
                _schema = schema;
                _references = new ulong[schema.Fields.Length];
                if (_references.Length != 0)
                {
                    var payload = schema.Wrap(Pointer);
                    for (int i = 0; i < _references.Length; i++)
                        _references[i] = schema.Fields[i].Read(payload).PackedValue;
                }
                _root = IL2CPP.il2cpp_gchandle_new(Pointer, false);
                if (_root == 0) throw new InvalidOperationException("Native queued event could not be rooted.");
            }

            internal bool Matches(Bolt.Event value)
            {
                if (value == null || value.WasCollected || value.Pointer != Pointer || _root == 0
                    || IL2CPP.il2cpp_gchandle_get_target(_root) != Pointer
                    || IL2CPP.il2cpp_object_get_class(Pointer) != _class
                    || Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventStorage) != _storage
                    || Marshal.ReadInt32(Pointer, BoltNativeLayout.EventTargets) != _targets
                    || Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventTargetEntity) != _target
                    || Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventTargetConnection) != _targetConnection
                    || Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventSourceConnection) != _sourceConnection
                    || Marshal.ReadIntPtr(Pointer, BoltNativeLayout.EventMeta) != _meta)
                    return false;
                if (_references.Length == 0) return true;
                var payload = _schema.Wrap(Pointer);
                for (int i = 0; i < _references.Length; i++)
                    if (_schema.Fields[i].Read(payload).PackedValue != _references[i]) return false;
                return true;
            }

            internal void ReleaseRoot()
            {
                if (_root == 0) return;
                IL2CPP.il2cpp_gchandle_free(_root);
                _root = 0;
            }
        }
    }

    /// <summary>Bolt event and dispatch queue field offsets qualified for the recognized game build.</summary>
    internal static class BoltNativeLayout
    {
        internal const int QueueArray = 0x10, QueueHead = 0x18, QueueTail = 0x1c, QueueSize = 0x20, QueueVersion = 0x24;
        internal const int EventStorage = 0x40, EventTargets = 0x50, EventTargetEntity = 0x58,
            EventTargetConnection = 0x60, EventSourceConnection = 0x68, EventMeta = 0x70;

        internal static void Qualify()
        {
            RuntimeHelpers.RunClassConstructor(typeof(NativeEventQueue).TypeHandle);
            var queueClass = Il2CppClassPointerStore<NativeEventQueue>.NativeClassPtr;
            var field = Require(Il2CppClassPointerStore<EventDispatcher>.NativeClassPtr, "_dispatchQueue", 0, true);
            if (IL2CPP.il2cpp_type_get_class_or_element_class(IL2CPP.il2cpp_field_get_type(field)) != queueClass)
                throw new InvalidOperationException("Native dispatcher queue type is not Queue<Event>.");
            Require(queueClass, "_array", QueueArray, false);
            Require(queueClass, "_head", QueueHead, false);
            Require(queueClass, "_tail", QueueTail, false);
            Require(queueClass, "_size", QueueSize, false);
            Require(queueClass, "_version", QueueVersion, false);
            var eventClass = Il2CppClassPointerStore<Bolt.Event>.NativeClassPtr;
            Require(eventClass, "storage", EventStorage, false);
            Require(eventClass, "Targets", EventTargets, false);
            Require(eventClass, "TargetEntity", EventTargetEntity, false);
            Require(eventClass, "TargetConnection", EventTargetConnection, false);
            Require(eventClass, "SourceConnection", EventSourceConnection, false);
            Require(eventClass, "Meta", EventMeta, false);
        }

        private static IntPtr Require(IntPtr type, string name, int offset, bool isStatic)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(type, name);
            if (field == IntPtr.Zero || IL2CPP.il2cpp_field_get_offset(field) != offset
                || ((IL2CPP.il2cpp_field_get_flags(field) & (int)FieldAttributes.Static) != 0) != isStatic)
                throw new InvalidOperationException("Unqualified native event field: " + name);
            return field;
        }
    }
}
