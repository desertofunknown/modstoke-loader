// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Ballistics;
using BepInEx.Unity.IL2CPP.Hook;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using global::Sons.Inventory;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    internal static partial class StrictCombatWeaponHooks
    {
        private static NativeProjectileHooks _projectileHooks;
        private static int _projectileFailureLogged;

        private static void ProjectileOwnershipFailure(Exception error)
        {
            _historyIncomplete = true;
            foreach (var owner in Volatile.Read(ref _owners)) owner.Retire();
            foreach (var prepared in Volatile.Read(ref _preparedEquipment)) prepared.Retire();
            if (Interlocked.Exchange(ref _projectileFailureLogged, 1) != 0) return;
            try { _warning?.Invoke("Strict combat projectile ownership is unavailable: " + error.Message); }
            catch { }
        }

        // The generated BulletData wrapper is a reference class around a native value type.
        // Harmony's by-reference conversion does not preserve its payload. This hook receives
        // the original writable record; cancellation never flushes its retained impacts.
        private sealed class NativeProjectileHooks
        {
            private const int HeaderSize = 0x10;
            private const int RecordSize = 0x68;
            private const int VelocityOffset = 0x14;
            private const int FlightTimeOffset = 0x28;
            private const int LifeTimeOffset = 0x2C;
            private const int WeaponOffset = 0x48;
            private const int SurfaceWeaponOffset = 0x40;
            private const int ImpactWeaponOffset = 0x38;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void CheckPath(nint handler, nint record, byte trajectoryTest, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void UpdateVelocity(nint handler, nint record, float distance, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void SurfaceImpact(nint handler, nint record, nint ballisticObject,
                nint rayHit, nint material, byte trajectoryTest, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void Impact(nint handler, nint record, nint impactValue, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void StopBullet(nint handler, nint record, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void PayloadImpact(nint receiver, nint payload, nint methodInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void ImpactReceivers(nint receiver, nint payload, nint itemInstance, nint methodInfo);

            private readonly Entry<CheckPath> _path;
            private readonly Entry<UpdateVelocity> _velocity;
            private readonly Entry<SurfaceImpact> _surface;
            private readonly Entry<Impact> _impact;
            private readonly Entry<StopBullet> _stop;
            private readonly PayloadEntry[] _payloads;
            private readonly Entry<ImpactReceivers> _receivers;

            internal NativeProjectileHooks()
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new PlatformNotSupportedException("Native projectile ownership requires the supported Windows x64 game.");
                RequireLayout();
                var record = typeof(BulletData).MakeByRefType();
                var handler = typeof(BulletHandler);
                _path = new(handler, "checkPath", new[] { record, typeof(bool) },
                    "A5D85F835E90BCEC0208B7E924B394002D805AC8DDB10FE6F9199C3B74A47C07");
                _velocity = new(handler, "updateVelocity", new[] { record, typeof(float) },
                    "73D9B0B3A736767F978E6ED80C4DDB427F13A409F20ECBA3F8A7EFAA6F7ECC8F");
                _surface = new(handler, "surfaceImpact", new[] { record, typeof(BallisticObject),
                    typeof(RaycastHit).MakeByRefType(), typeof(global::Ballistics.BallisticMaterial), typeof(bool) },
                    "2DD49A8F92A7228EB0D09B995B1B09845EAFE386C2072513D630EB48B8558E7A");
                _impact = new(handler, "impact", new[] { record, typeof(BulletImpact) },
                    "A3F968AAB47B5F02E8770FA94FB18BBC380E0A23956F4E02FDFE351D53AC26D3");
                _stop = new(handler, "stopBullet", new[] { record },
                    "1FF9A8CE8D483FFAC16FA1D4D0380897F36D7AEAE221EA3565ED010E369B1554");
                _payloads = new[]
                {
                    new PayloadEntry(typeof(Bullet), "Impact", typeof(ImpactInfo), ImpactWeaponOffset,
                        "F030C4E40DF191C193487F4371640EADB76EAC53F320E112D7AB6A0C79E0D732"),
                    new PayloadEntry(typeof(Bullet), "SurfaceImpact", typeof(SurfaceImpactInfo), SurfaceWeaponOffset,
                        "F10C38856F4F985F79A9356009D62B7E32729E9B4BF7EEA99763F87762028C55"),
                    new PayloadEntry(typeof(global::ProxyBulletOnImpact), "Impact", typeof(ImpactInfo), ImpactWeaponOffset,
                        "92A44CAE7CB07D4D3AB7184E6BA18717B6AA44ED8FC355CF20F65DB6CEEA1271"),
                    new PayloadEntry(typeof(global::ProxyBulletOnImpact), "SurfaceImpact", typeof(SurfaceImpactInfo), SurfaceWeaponOffset,
                        "9A6E173B2668D44AC4434D3115B2F63A16E40494707A5EBB392E9377AC0F04A8"),
                    new PayloadEntry(typeof(global::ProjectileImpactObject), "Impact", typeof(SurfaceImpactInfo), SurfaceWeaponOffset,
                        "CC7119602AD24C74FD1AC952D29F3D1A96719A68FC49893F34D0F4388C1C3E65")
                };
                _receivers = new(typeof(global::ProjectileImpactObject), "ProcessReceivers",
                    new[] { typeof(SurfaceImpactInfo), typeof(ItemInstance) },
                    "A5595ADD2BC048A2B2FC3A428F8C2B85FFC715CE57223538B87C1E1EA80FB9E1");
            }

            internal void Install()
            {
                RequireLayout();
                _path.Install(InvokePath);
                _velocity.Install(InvokeVelocity);
                _surface.Install(InvokeSurface);
                _impact.Install(InvokeImpact);
                _stop.Install(InvokeStop);
                foreach (var payload in _payloads) payload.Install();
                _receivers.Install(InvokeReceivers);
            }

            private static void RequireLayout()
            {
                var klass = Il2CppClassPointerStore<BulletData>.NativeClassPtr;
                uint alignment = 0;
                if (klass == 0 || !IL2CPP.il2cpp_class_is_valuetype(klass)
                    || IL2CPP.il2cpp_class_instance_size(klass) != HeaderSize + RecordSize
                    || IL2CPP.il2cpp_class_value_size(klass, ref alignment) != RecordSize)
                    throw new InvalidOperationException("Native projectile record layout is unsupported.");
                RequireField(klass, "velocity", VelocityOffset);
                RequireField(klass, "flyTime", FlightTimeOffset);
                RequireField(klass, "lifeTime", LifeTimeOffset);
                RequireField(klass, "weapon", WeaponOffset);
                RequirePayloadLayout(Il2CppClassPointerStore<SurfaceImpactInfo>.NativeClassPtr, 0x58, SurfaceWeaponOffset);
                RequirePayloadLayout(Il2CppClassPointerStore<ImpactInfo>.NativeClassPtr, 0x40, ImpactWeaponOffset);
            }

            private static void RequirePayloadLayout(nint klass, int size, uint weaponOffset)
            {
                uint alignment = 0;
                if (klass == 0 || !IL2CPP.il2cpp_class_is_valuetype(klass)
                    || IL2CPP.il2cpp_class_instance_size(klass) != HeaderSize + size
                    || IL2CPP.il2cpp_class_value_size(klass, ref alignment) != size)
                    throw new InvalidOperationException("Native projectile impact payload layout is unsupported.");
                RequireField(klass, "weapon", weaponOffset);
            }

            private static void RequireField(nint klass, string name, uint payloadOffset)
            {
                var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
                // IL2CPP reports boxed offsets; the callback receives the unboxed payload.
                if (field == 0 || IL2CPP.il2cpp_field_get_offset(field) != HeaderSize + payloadOffset)
                    throw new InvalidOperationException("Native projectile field layout is unsupported: " + name);
            }

            private void InvokePath(nint handler, nint record, byte trajectoryTest, nint methodInfo)
            {
                if (!CancelOwnedRecord(record))
                    _path.Original(handler, record, trajectoryTest, methodInfo);
            }

            private void InvokeVelocity(nint handler, nint record, float distance, nint methodInfo)
            {
                if (!CancelOwnedRecord(record))
                    _velocity.Original(handler, record, distance, methodInfo);
            }

            private void InvokeSurface(nint handler, nint record, nint ballisticObject,
                nint rayHit, nint material, byte trajectoryTest, nint methodInfo)
            {
                // A surface callback can dispatch damage even during trajectory preview.
                if (!CancelOwnedRecord(record))
                    _surface.Original(handler, record, ballisticObject, rayHit, material, trajectoryTest, methodInfo);
            }

            private void InvokeImpact(nint handler, nint record, nint impactValue, nint methodInfo)
            {
                // BulletImpact is passed by value through a native temporary. Forward its
                // pointer unchanged; only the separate BulletData record establishes ownership.
                if (!CancelOwnedRecord(record))
                    _impact.Original(handler, record, impactValue, methodInfo);
            }

            private void InvokeStop(nint handler, nint record, nint methodInfo)
            {
                if (!CancelOwnedRecord(record, clearVelocity: true))
                    _stop.Original(handler, record, methodInfo);
            }

            private void InvokeReceivers(nint receiver, nint payload, nint itemInstance, nint methodInfo)
            {
                if (!HasOwnedWeapon(payload, SurfaceWeaponOffset))
                    _receivers.Original(receiver, payload, itemInstance, methodInfo);
            }

            private static unsafe bool HasOwnedWeapon(nint payload, int weaponOffset)
            {
                if (!IsObserving || payload == 0) return false;
                // These are native value payloads, not boxed objects or pointers from a mod
                // message. Never construct a wrapper from their weapon field. A missing
                // weapon is also used by native network attachment initialization.
                var weapon = Unsafe.ReadUnaligned<nint>((void*)(payload + weaponOffset));
                try { return IsRetainedWeapon(weapon); }
                catch (Exception error) { ProjectileOwnershipFailure(error); }
                return false;
            }

            private static unsafe bool CancelOwnedRecord(nint record, bool clearVelocity = false)
            {
                if (!HasOwnedWeapon(record, WeaponOffset))
                {
                    // An unidentified record is not evidence that an unrelated weapon is owned.
                    return false;
                }

                // Both simulation callers loop on flyTime, then lifeTime. Their existing
                // visual cleanup remains in charge. A lower entry's caller can still continue,
                // which is why surfaceImpact and impact require separate guards too.
                Unsafe.WriteUnaligned<ulong>((void*)(record + FlightTimeOffset), 0);
                if (clearVelocity)
                {
                    // Preserve stopBullet's terminal velocity without invoking its impact loop.
                    Unsafe.WriteUnaligned<ulong>((void*)(record + VelocityOffset), 0);
                    Unsafe.WriteUnaligned<uint>((void*)(record + VelocityOffset + 8), 0);
                }
                return true;
            }

            private sealed class PayloadEntry
            {
                private readonly Entry<PayloadImpact> _entry;
                private readonly int _weaponOffset;

                internal PayloadEntry(Type type, string name, Type payloadType, int weaponOffset, string hash)
                {
                    _weaponOffset = weaponOffset;
                    _entry = new(type, name, new[] { payloadType }, hash);
                }

                internal void Install() => _entry.Install(Invoke);

                private void Invoke(nint receiver, nint payload, nint methodInfo)
                {
                    // These value copies have no flight lifecycle to cancel. Withhold only
                    // their dispatch; leave the payload and native receiver untouched.
                    if (!HasOwnedWeapon(payload, _weaponOffset))
                        _entry.Original(receiver, payload, methodInfo);
                }
            }

            private sealed class Entry<T> where T : Delegate
            {
                private const int CodeLength = 96;
                private readonly string _name, _hash;
                private readonly nint _entry;
                private INativeDetour _detour;
                private T _callback, _original;

                internal Entry(Type type, string name, Type[] arguments, string hash)
                {
                    _name = type.FullName + "." + name;
                    _hash = hash;
                    var method = AccessTools.Method(type, name, arguments);
                    if (method == null || method.IsStatic || method.ReturnType != typeof(void))
                        throw new MissingMethodException("Native projectile method is unavailable: " + _name);
                    var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                    if (field?.GetValue(null) is not nint methodInfo || methodInfo == 0)
                        throw new InvalidOperationException("Native projectile method has no method info: " + _name);
                    _entry = (nint)BitConverter.ToInt64(ReadNativeAmmoMemory(methodInfo, IntPtr.Size), 0);
                    if (_entry == 0) throw new InvalidOperationException("Native projectile method has no entry: " + _name);
                    RequireCode();
                }

                internal T Original => Volatile.Read(ref _original);

                internal void Install(T callback)
                {
                    RequireCode();
                    // Keep every callback and trampoline rooted after any installation attempt.
                    _callback = callback;
                    _detour = INativeDetour.Create(_entry, _callback);
                    Volatile.Write(ref _original, _detour.GenerateTrampoline<T>());
                    _detour.Apply();
                }

                private void RequireCode()
                {
                    var hash = Convert.ToHexString(SHA256.HashData(ReadNativeAmmoMemory(_entry, CodeLength)));
                    if (!string.Equals(hash, _hash, StringComparison.Ordinal))
                        throw new InvalidOperationException("Native projectile code is unsupported or already patched: " + _name);
                }
            }
        }
    }
}
