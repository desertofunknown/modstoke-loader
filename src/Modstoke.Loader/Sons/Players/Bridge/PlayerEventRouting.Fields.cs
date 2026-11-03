// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Bolt;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Modstoke.Sons
{
    internal static partial class PlayerEventRouting
    {
        private const BindingFlags DeclaredPayload = BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly;
        private static readonly Dictionary<IntPtr, EventReferences> ReferenceSchemas = new();
        private static readonly HashSet<IntPtr> RewritingReferences = new();
        private static bool _referenceSchemasPrepared;

        private static void PrepareReferenceSchemas()
        {
            if (_referenceSchemasPrepared) return;
            ReferenceSchemas.Clear();
            foreach (var type in typeof(HitPlayer).Assembly.GetTypes())
            {
                if (type.IsAbstract || type.ContainsGenericParameters || !type.IsSubclassOf(typeof(Bolt.Event))) continue;
                RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                var pointerStore = typeof(Il2CppClassPointerStore<>).MakeGenericType(type);
                var pointer = pointerStore.GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static);
                if (pointer?.GetValue(null) is not IntPtr nativeClass || nativeClass == IntPtr.Zero)
                    throw new InvalidOperationException("Generated event native class is unavailable: " + type.FullName);
                var constructor = type.GetConstructor(new[] { typeof(IntPtr) })
                    ?? throw new MissingMethodException(type.FullName, ".ctor(IntPtr)");
                var fields = new List<EntityReference>();
                foreach (var property in type.GetProperties(DeclaredPayload))
                {
                    // A NetworkId-valued payload is not necessarily an entity reference (for
                    // example BlobSyncConnect.AccessToken). Only generated BoltEntity fields
                    // have the entity/raw-ID accessor contract used here.
                    if (property.PropertyType != typeof(BoltEntity) || property.GetIndexParameters().Length != 0) continue;
                    var read = type.GetMethod("Get" + property.Name + "NetworkIdValue", DeclaredPayload,
                        null, Type.EmptyTypes, null);
                    var write = property.GetSetMethod();
                    if (read == null || read.IsStatic || read.ContainsGenericParameters || read.ReturnType != typeof(NetworkId)
                        || write == null || write.IsStatic || write.ReturnType != typeof(void))
                        throw new InvalidOperationException("Generated event entity field is incomplete: " + type.FullName + "." + property.Name);
                    fields.Add(new EntityReference(type, read, write));
                }
                ReferenceSchemas.Add(nativeClass, new EventReferences(
                    fields.Count == 0 ? null : CompileWrap(constructor), fields.ToArray()));
            }
            _referenceSchemasPrepared = true;
        }

        private static bool TryReadReferenceSchema(Bolt.Event value, out EventReferences schema)
        {
            schema = null;
            return value != null && !value.WasCollected && value.Pointer != IntPtr.Zero
                && ReferenceSchemas.TryGetValue(IL2CPP.il2cpp_object_get_class(value.Pointer), out schema);
        }

        private static void RewriteEntityReferences(Bolt.Event value)
        {
            if (!TryReadReferenceSchema(value, out var schema) || schema.Fields.Length == 0) return;
            if (!RewritingReferences.Add(value.Pointer))
                throw new InvalidOperationException("Player event reference repair reentered the same native event.");
            try
            {
                // Counts come from the recognized generated assembly, not from the packet.
                // Stage every intended identity before making the first payload write.
                Span<ulong> original = stackalloc ulong[schema.Fields.Length];
                Span<ulong> replacement = stackalloc ulong[schema.Fields.Length];
                var payload = schema.Wrap(value.Pointer);
                for (int i = 0; i < schema.Fields.Length; i++)
                {
                    original[i] = schema.Fields[i].Read(payload).PackedValue;
                    replacement[i] = TryResolve(original[i], value, out var target) ? target.networkId.PackedValue : 0;
                }
                for (int i = 0; i < schema.Fields.Length; i++)
                {
                    if (replacement[i] == 0) continue;
                    var field = schema.Fields[i];
                    if (field.Read(payload).PackedValue != original[i]
                        || !TryResolve(original[i], value, out var target) || target.networkId.PackedValue != replacement[i])
                        throw new InvalidOperationException("Player event reference changed before repair.");
                    field.Write(payload, target);
                    if (field.Read(payload).PackedValue != replacement[i])
                        throw new InvalidOperationException("Player event reference repair did not retain the replacement identity.");
                }
                for (int i = 0; i < schema.Fields.Length; i++)
                    if (schema.Fields[i].Read(payload).PackedValue != (replacement[i] == 0 ? original[i] : replacement[i]))
                        throw new InvalidOperationException("A native event setter changed another payload identity.");
            }
            finally { RewritingReferences.Remove(value.Pointer); }
        }

        private static Func<IntPtr, Il2CppObjectBase> CompileWrap(ConstructorInfo constructor)
        {
            var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
            return Expression.Lambda<Func<IntPtr, Il2CppObjectBase>>(Expression.New(constructor, pointer), pointer).Compile();
        }

        // Wrap is null for events without entity fields. Every field accessor takes the
        // wrapper Wrap produced, so one repair or ledger pass shares a single instance.
        private sealed record EventReferences(Func<IntPtr, Il2CppObjectBase> Wrap, EntityReference[] Fields);

        private sealed class EntityReference
        {
            internal readonly Func<Il2CppObjectBase, NetworkId> Read;
            internal readonly Action<Il2CppObjectBase, BoltEntity> Write;

            internal EntityReference(Type type, MethodInfo read, MethodInfo write)
            {
                var payload = Expression.Parameter(typeof(Il2CppObjectBase), "payload");
                var entity = Expression.Parameter(typeof(BoltEntity), "entity");
                var typed = Expression.Convert(payload, type);
                Read = Expression.Lambda<Func<Il2CppObjectBase, NetworkId>>(
                    Expression.Call(typed, read), payload).Compile();
                Write = Expression.Lambda<Action<Il2CppObjectBase, BoltEntity>>(
                    Expression.Call(typed, write, entity), payload, entity).Compile();
            }
        }
    }
}
