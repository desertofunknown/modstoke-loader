// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime;

namespace Modstoke.Sons
{
    public enum BoltFieldReadStatus
    {
        Success,
        UnknownField,
        UnsupportedType,
        TooLarge,
        Failed,
        CallbackClosed,
        WrongThread,
        UnknownEventType,
        ContextChanged
    }

    public sealed class BoltField
    {
        private readonly Lazy<Func<IntPtr, object>> _reader;

        internal BoltField(string name, string typeName, string valueKind,
            Lazy<Func<IntPtr, object>> reader)
        {
            Name = name;
            TypeName = typeName;
            ValueKind = valueKind;
            _reader = reader;
        }

        public string Name { get; }
        /// <summary>The generated payload property type, before conversion to a managed copy.</summary>
        public string TypeName { get; }

        /// <summary>The managed copy category; NetworkId is an unsigned 64-bit raw identity.</summary>
        public string ValueKind { get; }

        /// <summary>
        /// Whether an eligible accessor exists and has no cached compilation failure, not whether
        /// a native read will succeed. Inspection does not compile or invoke the accessor.
        /// </summary>
        public bool CanRead => _reader != null && (!_reader.IsValueCreated || _reader.Value != null);

        internal Func<IntPtr, object> Reader => _reader?.Value;
    }

    public sealed class BoltEventSchema
    {
        private const BindingFlags DeclaredPublicInstance =
            BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
        private const int MaximumStringLength = 1024;
        private readonly Dictionary<string, BoltField> _fields;
        private readonly Lazy<IntPtr> _nativeClass;

        private BoltEventSchema(Type generatedEventType, BoltField[] fields)
        {
            Name = generatedEventType.FullName;
            Fields = Array.AsReadOnly(fields);
            _fields = fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
            _nativeClass = new Lazy<IntPtr>(() =>
            {
                RuntimeHelpers.RunClassConstructor(generatedEventType.TypeHandle);
                var store = typeof(Il2CppClassPointerStore<>).MakeGenericType(generatedEventType);
                var field = store.GetField("NativeClassPtr", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new MissingFieldException(store.FullName, "NativeClassPtr");
                return (IntPtr)field.GetValue(null);
            }, true);
        }

        public string Name { get; }
        public IReadOnlyList<BoltField> Fields { get; }

        internal bool MatchesNativeClass(IntPtr actualClass)
        {
            if (actualClass == IntPtr.Zero) return false;
            try
            {
                // Lazy caches both the pointer and resolution failures; names are not native identity.
                var expectedClass = _nativeClass.Value;
                return expectedClass != IntPtr.Zero && expectedClass == actualClass;
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static BoltEventSchema Create(Type generatedEventType)
        {
            ArgumentNullException.ThrowIfNull(generatedEventType);
            // Anchor discovery to the referenced generated game assembly, never mod assemblies.
            if (generatedEventType.Assembly != typeof(global::PickupRequest).Assembly
                || !generatedEventType.IsClass || generatedEventType.IsAbstract
                || generatedEventType.ContainsGenericParameters
                || !generatedEventType.IsSubclassOf(typeof(Bolt.Event)))
            {
                throw new ArgumentException("Expected a concrete generated Bolt event wrapper.", nameof(generatedEventType));
            }

            var constructor = generatedEventType.GetConstructor(new[] { typeof(IntPtr) })
                ?? throw new MissingMethodException(generatedEventType.FullName, ".ctor(IntPtr)");
            var fields = new List<BoltField>();
            foreach (var property in generatedEventType.GetProperties(DeclaredPublicInstance)
                .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var getter = property.GetGetMethod();
                if (getter == null || getter.IsStatic || property.GetIndexParameters().Length != 0)
                    continue;

                var propertyType = property.PropertyType;
                var kind = GetValueKind(propertyType);
                var readerMethod = getter;
                if (kind == "NetworkId")
                {
                    // The entity property getter resolves an entity; only the raw ID accessor is allowed.
                    readerMethod = generatedEventType.GetMethod("Get" + property.Name + "NetworkIdValue",
                        DeclaredPublicInstance, null, Type.EmptyTypes, null);
                    if (readerMethod == null || readerMethod.ContainsGenericParameters
                        || readerMethod.ReturnType != typeof(Bolt.NetworkId))
                        kind = "Unsupported";
                }

                var reader = kind == "Unsupported" ? null : new Lazy<Func<IntPtr, object>>(
                    () => CompileReader(constructor, readerMethod), true);
                fields.Add(new BoltField(property.Name,
                    propertyType.FullName ?? propertyType.Name, kind, reader));
            }

            return new BoltEventSchema(generatedEventType, fields.ToArray());
        }

        internal bool TryRead(IntPtr eventPointer, string fieldName, out object value,
            out BoltFieldReadStatus status)
        {
            value = null;
            if (fieldName == null || !_fields.TryGetValue(fieldName, out var field))
            {
                status = BoltFieldReadStatus.UnknownField;
                return false;
            }

            var reader = field.Reader;
            if (reader == null)
            {
                status = BoltFieldReadStatus.UnsupportedType;
                return false;
            }

            if (eventPointer == IntPtr.Zero)
            {
                status = BoltFieldReadStatus.Failed;
                return false;
            }

            try
            {
                var result = reader(eventPointer);
                if (result is string text && text.Length > MaximumStringLength)
                {
                    status = BoltFieldReadStatus.TooLarge;
                    return false;
                }

                value = result;
                status = BoltFieldReadStatus.Success;
                return true;
            }
            catch (Exception)
            {
                status = BoltFieldReadStatus.Failed;
                return false;
            }
        }

        private static string GetValueKind(Type type)
        {
            if (type == typeof(bool)) return "Boolean";
            if (type == typeof(string)) return "String";
            if (type == typeof(byte) || type == typeof(sbyte)
                || type == typeof(short) || type == typeof(ushort)
                || type == typeof(int) || type == typeof(uint)
                || type == typeof(long) || type == typeof(ulong)
                || type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "Number";
            if (type == typeof(UnityEngine.Vector2)) return "Vector2";
            if (type == typeof(UnityEngine.Vector3)) return "Vector3";
            if (type == typeof(UnityEngine.Quaternion)) return "Quaternion";
            if (type == typeof(global::BoltEntity)) return "NetworkId";
            return "Unsupported";
        }

        private static Func<IntPtr, object> CompileReader(ConstructorInfo constructor, MethodInfo getter)
        {
            try
            {
                var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
                Expression result = Expression.Call(Expression.New(constructor, pointer), getter);
                var returnType = getter.ReturnType;
                string conversion = null;
                if (returnType == typeof(UnityEngine.Vector2)) conversion = nameof(CopyVector2);
                else if (returnType == typeof(UnityEngine.Vector3)) conversion = nameof(CopyVector);
                else if (returnType == typeof(UnityEngine.Quaternion)) conversion = nameof(CopyQuaternion);
                else if (returnType == typeof(Bolt.NetworkId)) conversion = nameof(CopyNetworkId);
                if (conversion != null)
                {
                    var method = typeof(BoltEventSchema).GetMethod(conversion,
                        BindingFlags.Static | BindingFlags.NonPublic);
                    result = Expression.Call(method, result);
                }

                // The compiled delegate retains metadata only; each read creates a temporary wrapper.
                return Expression.Lambda<Func<IntPtr, object>>(
                    Expression.Convert(result, typeof(object)), pointer).Compile();
            }
            catch (Exception)
            {
                // Lazy caches this failure permanently; never retry or reflectively invoke per message.
                return null;
            }
        }

        private static System.Numerics.Vector2 CopyVector2(UnityEngine.Vector2 value)
            => new System.Numerics.Vector2(value.x, value.y);

        private static System.Numerics.Vector3 CopyVector(UnityEngine.Vector3 value)
            => new System.Numerics.Vector3(value.x, value.y, value.z);

        private static System.Numerics.Quaternion CopyQuaternion(UnityEngine.Quaternion value)
            => new System.Numerics.Quaternion(value.x, value.y, value.z, value.w);

        private static ulong CopyNetworkId(Bolt.NetworkId value) => value.PackedValue;
    }
}
