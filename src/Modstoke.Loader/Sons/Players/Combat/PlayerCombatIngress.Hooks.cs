// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;
using NativeEventQueue = Il2CppSystem.Collections.Generic.Queue<Bolt.Event>;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerCombatIngress
    {
        private static bool _installAttempted, _installed;
        private static int _thread;
        [ThreadStatic] private static ProducerScope _producer;

        private sealed class ProducerScope
        {
            internal ProducerScope Parent;
            internal PlayerCombatIngress Owner;
            internal NativeEvent Record;
        }

        private sealed class SendScope
        {
            internal NativeEvent Record;
            internal NativeEventQueue Queue;
            internal int Count, Version;
        }

        private static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                    || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
                var producer = Qualify(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.SendPlayerHit), 0x32C2B90,
                    false, typeof(void), typeof(IImpactSender), typeof(IImpactData));
                var create = Qualify(typeof(HitPlayer), nameof(HitPlayer.Create), 0x8CC1E0,
                    true, typeof(HitPlayer), typeof(BoltEntity), typeof(EntityTargets));
                var send = Qualify(typeof(Bolt.Event), nameof(Bolt.Event.Send), 0x94ADA0, false, typeof(void));
                // The parent inserts TryDispatch into this already existing policy hook.
                Qualify(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch), 0x94D560,
                    true, typeof(void), typeof(Bolt.Event));
                Qualify(typeof(Bolt.Event), nameof(Bolt.Event.FreeStorage), 0x94ABE0, false, typeof(void));
                BoltNativeLayout.Qualify();
                _thread = Environment.CurrentManagedThreadId;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-player-combat-ingress");
                harmony.Patch(producer, prefix: Hook(nameof(BeforeProducer)), finalizer: Hook(nameof(FinishProducer)));
                harmony.Patch(create, finalizer: Hook(nameof(FinishCreate)));
                harmony.Patch(send, prefix: Hook(nameof(BeforeSend)), finalizer: Hook(nameof(FinishSend)));
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Native player combat ingress producer unavailable: " + error.Message);
            }
        }

        private static MethodInfo Qualify(Type type, string name, int rva, bool isStatic, Type returns, params Type[] args)
            => PlayerLifecycle.QualifyNative(type, name, rva, isStatic, returns, args);

        private static HarmonyMethod Hook(string name) => new(typeof(PlayerCombatIngress), name)
        { priority = HarmonyLib.Priority.First + 100 };

        private static void BeforeProducer(BoltPlayerSetup __instance, IImpactSender __0, IImpactData __1,
            bool __runOriginal, out ProducerScope __state)
        {
            __state = null;
            if (!_installed || !OnThread || (Owners.Count == 0 && _producer == null)) return;
            if (!BoltNetwork.isServer) return;
            PlayerCombatIngress owner = null;
            bool owned = Native(__instance) && Owners.TryGetValue(__instance.Pointer, out owner);
            if (_producer == null && (!__runOriginal || !owned)) return;
            // Even an unowned nested call must hide its parent's factory/send scope.
            __state = new ProducerScope { Parent = _producer };
            _producer = __state;
            if (!__runOriginal || !owned) return;
            __state.Owner = owner;
            owner._producerDepth++;
            try
            {
                if (!owner.CurrentTarget() || !owner.HasNativeReceiver())
                { owner.Fail("The retained native hit producer changed its target or callback binding."); return; }
                if (owner._pending >= MaximumPendingPerOwner)
                { owner.Fail("Native hit reservations are full; no accepted native action was discarded."); return; }
                if (Events.Count + _uncreatedReservations >= MaximumEventLifetimes) PruneCollectedEvents();
                if (Events.Count + _uncreatedReservations >= MaximumEventLifetimes)
                { owner.Fail("Native event lifetime tracking is full; owner activation is no longer qualified."); return; }
                // Reserve before interface getters: their native implementations may reenter.
                var record = new NativeEvent { Owner = owner, FactorySlotReserved = true };
                __state.Record = record;
                _uncreatedReservations++;
                if (owner._pending == 0) owner._lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
                owner._pending++;
                var origin = Origin.Capture(__0, __1);
                if (origin == null)
                {
                    // Other native causes are outside this Vail producer. No effect was taken.
                    AbandonObservation(record);
                    return;
                }
                record.Origin = origin;
            }
            catch (Exception error)
            {
                owner.Fail("Native hit origin could not be retained: " + error.Message);
                if (__state.Record != null) AbandonObservation(__state.Record);
            }
        }

        private static Exception FinishCreate(BoltEntity __0, EntityTargets __1, HitPlayer __result,
            bool __runOriginal, Exception __exception)
        {
            var record = _producer?.Record;
            if (record == null || record.Abandoned || record.Origin == null
                || !OnThread || !__runOriginal || __exception != null) return __exception;
            try
            {
                if (record.EventPointer != IntPtr.Zero || __1 != EntityTargets.Everyone
                    || !Same(__0, record.Owner._target) || !Native(__result)
                    || IL2CPP.il2cpp_object_get_class(__result.Pointer) != Il2CppClassPointerStore<HitPlayer>.NativeClassPtr
                    || !Native(__result.storage) || !Native(__result.Meta)
                    || !Same(__result.TargetEntity, record.Owner._attachment)
                    || __result.Targets != (int)EntityTargets.Everyone)
                    throw new InvalidOperationException("SendPlayerHit did not create exactly one event for its retained target.");
                var pointer = __result.Pointer;
                if (Events.TryGetValue(pointer, out var previous))
                {
                    if (previous.IsSameObject(pointer))
                        throw new InvalidOperationException("Native HitPlayer factory reused a live event.");
                    previous.ReleaseRoots();
                    Events.Remove(pointer);
                }
                record.EventPointer = pointer;
                record.FactoryStorage = __result.storage.Pointer;
                record.FactoryMeta = __result.Meta.Pointer;
                record.StrongRoot = IL2CPP.il2cpp_gchandle_new(pointer, false);
                record.WeakRoot = IL2CPP.il2cpp_gchandle_new_weakref(pointer, false);
                if (record.StrongRoot == 0 || record.WeakRoot == 0)
                    throw new InvalidOperationException("Native HitPlayer lifetime could not be retained.");
                Events.Add(pointer, record);
                _uncreatedReservations--;
                record.FactorySlotReserved = false;
            }
            catch (Exception error)
            {
                record.Owner.Fail("Native hit creation provenance failed: " + error.Message);
                AbandonObservation(record);
            }
            return __exception;
        }

        private static bool BeforeSend(Bolt.Event __instance, bool __runOriginal, out SendScope __state)
        {
            __state = null;
            if (!_installed || !OnThread || !Native(__instance)) return true;
            // Suppress only a second send of the same retained native event, not a second
            // impact/data ID. Native pooled impact data may legitimately produce many hits.
            if (Events.TryGetValue(__instance.Pointer, out var known) && known.IsSameObject(__instance.Pointer)
                && (known.Queued || known.Consumed)) return false;
            var record = _producer?.Record;
            if (!__runOriginal || record == null || record.Abandoned || record.Origin == null
                || record.EventPointer != __instance.Pointer) return true;
            try
            {
                if (!record.IsSameObject(__instance.Pointer) || !record.Owner.CurrentTarget()
                    || __instance.storage?.Pointer != record.FactoryStorage || __instance.Meta?.Pointer != record.FactoryMeta
                    || !record.Origin.StillMatchesProducer())
                    throw new InvalidOperationException("Native sender, impact data or target changed before Send.");
                var hit = __instance.TryCast<HitPlayer>();
                if (!Native(hit)) throw new InvalidOperationException("Native factory result is not HitPlayer.");
                record.Payload = new EventPayload(hit, record.Owner._attachment);
                var queue = EventDispatcher._dispatchQueue;
                if (!Native(queue) || queue._size < 0
                    || IL2CPP.il2cpp_object_get_class(queue.Pointer) != Il2CppClassPointerStore<NativeEventQueue>.NativeClassPtr)
                    throw new InvalidOperationException("Native event queue is unavailable.");
                __state = new SendScope { Record = record, Queue = queue, Count = queue._size, Version = queue._version };
            }
            catch (Exception error) { record.Owner.Fail("Native hit send provenance failed: " + error.Message); }
            return true;
        }

        private static Exception FinishSend(bool __runOriginal, Exception __exception, SendScope __state)
        {
            if (__state == null) return __exception;
            var record = __state.Record;
            try
            {
                var queue = __state.Queue;
                var array = queue._array;
                int tail = queue._tail;
                if (!__runOriginal || __exception != null || !Same(EventDispatcher._dispatchQueue, queue)
                    || queue._size != __state.Count + 1 || queue._version != unchecked(__state.Version + 1)
                    || !Native(array) || array.Length == 0 || tail < 0 || tail >= array.Length
                    || array[tail == 0 ? array.Length - 1 : tail - 1]?.Pointer != record.EventPointer)
                    throw new InvalidOperationException("The original Send did not append the exact native event once.");
                record.Queued = true;
                record.AcceptedAtTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            catch (Exception error) { record.Owner.Fail("Native hit enqueue is unconfirmed: " + error.Message); }
            return __exception;
        }

        private static Exception FinishProducer(bool __runOriginal, Exception __exception, ProducerScope __state)
        {
            if (__state == null) return __exception;
            try
            {
                var record = __state.Record;
                if (record != null && !record.Abandoned)
                {
                    // Scope exit is not a damage-completion result. A confirmed native
                    // enqueue still drains even when a later producer postfix throws.
                    record.ProducerExited = true;
                    if (!__runOriginal || __exception != null)
                        record.Owner.Fail("Native hit producer exited with suppression or an exception; accepted events still drain.");
                    if (record.EventPointer == IntPtr.Zero) AbandonObservation(record);
                    else if (!record.Queued)
                    {
                        record.Owner.Fail("Native hit factory ran without a confirmed enqueue; native routing still owns any event.");
                        AbandonObservation(record);
                    }
                }
            }
            finally
            {
                if (__state.Owner != null) __state.Owner._producerDepth--;
                if (!ReferenceEquals(_producer, __state)) __state.Owner?.Fail("Native hit producer scopes unwound out of order.");
                _producer = __state.Parent;
            }
            return __exception;
        }
    }
}
