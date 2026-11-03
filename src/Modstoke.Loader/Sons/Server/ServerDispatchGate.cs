// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using Bolt;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Modstoke.Loader;
using global::Sons.Inventory;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>
    /// The shared early prefix on Bolt's EventDispatcher.Dispatch. It classifies each event once, keeps
    /// the common policy's retained events and releases storage for typed rejections, then runs the
    /// registered routes in <see cref="DispatchRoute"/> order. Each feature still reports its own state.
    /// </summary>
    internal static class ServerDispatchGate
    {
        private const int MaxCachedClasses = 1024;
        private const ulong Unclassified = ulong.MaxValue;
        private static readonly Dictionary<IntPtr, ulong> Classes = new();
        private static ulong _routes;
        private static int _thread;
        private static bool _patched;
        [ThreadStatic] private static HashSet<IntPtr> _retained;

        /// <summary>Enables a route, patching Dispatch on first use. Throws when the patch fails.</summary>
        internal static void Register(DispatchRoute route)
        {
            if (!_patched)
            {
                var target = AccessTools.Method(typeof(EventDispatcher), nameof(EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) })
                    ?? throw new MissingMethodException(typeof(EventDispatcher).FullName, "Dispatch(Event)");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-dispatch");
                _thread = Environment.CurrentManagedThreadId;
                try
                {
                    // The common policy's former slot: ahead of every typed policy, after player event routing.
                    // The finalizer keeps the default priority the common policy's finalizer had.
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(ServerDispatchGate), nameof(BeforeDispatch))
                        { priority = HarmonyLib.Priority.First + 100, before = new[] { "sonssdk_harmony" } },
                        finalizer: new HarmonyMethod(typeof(ServerDispatchGate), nameof(AfterDispatch)));
                }
                catch (Exception)
                {
                    // Leave no partial gate. A later route retries, as each separate prefix once did.
                    try { harmony.UnpatchSelf(); } catch (Exception) { }
                    throw;
                }
                _patched = true;
            }
            // Route tests can depend on registration, such as the free-form message classes.
            Classes.Clear();
            Volatile.Write(ref _routes, Volatile.Read(ref _routes) | Bit(route));
        }

        internal static void Remove(DispatchRoute route) =>
            Volatile.Write(ref _routes, Volatile.Read(ref _routes) & ~Bit(route));

        /// <summary>Retains an event until the gate finalizer. Null when an outer dispatch already holds it.</summary>
        internal static DispatchScope Retain(Bolt.Event source)
        {
            var active = _retained ??= new HashSet<IntPtr>();
            return active.Add(source.Pointer) ? new DispatchScope(source, active) : null;
        }

        private static bool BeforeDispatch(Bolt.Event __0, bool __runOriginal, out DispatchScope __state)
        {
            __state = null;
            var routes = Volatile.Read(ref _routes);
            if (__0 == null || routes == 0) return __runOriginal;
            var run = __runOriginal;
            // The outer call keeps a retained event until its finalizer, even if a callback re-enters Dispatch.
            if (run && Has(routes, DispatchRoute.Events))
                run = _retained?.Contains(__0.Pointer) != true && ServerEvents.Route(__0, out __state);

            var matches = run ? Classify(__0) : 0;
            if (run && Selected(routes, matches, DispatchRoute.Chat))
                run = Accept(__0, ServerChat.RouteDispatch(__0), DispatchRoute.Chat);
            if (run && Selected(routes, matches, DispatchRoute.LootSpawns))
                run = Accept(__0, ServerLootSpawns.RouteDispatch(__0), DispatchRoute.LootSpawns);
            if (run && Selected(routes, matches, DispatchRoute.WorldLocators))
                run = Accept(__0, ServerWorldLocators.RouteDispatch(__0), DispatchRoute.WorldLocators);
            if (run && Selected(routes, matches, DispatchRoute.ConstructionIngredients))
                run = Accept(__0, ServerConstruction.RouteIngredientDispatch(__0), DispatchRoute.ConstructionIngredients);
            if (run && Selected(routes, matches, DispatchRoute.ConstructionCompletion))
                run = Accept(__0, ServerConstruction.RouteCompletionDispatch(__0), DispatchRoute.ConstructionCompletion);
            if (run && Selected(routes, matches, DispatchRoute.Vehicles))
                run = Accept(__0, ServerVehicles.RouteDispatch(__0), DispatchRoute.Vehicles);
            if (run && Selected(routes, matches, DispatchRoute.FreeFormAuthority))
                run = Accept(__0, ServerFreeForm.RouteAuthorityDispatch(__0), DispatchRoute.FreeFormAuthority);
            if (run && Selected(routes, matches, DispatchRoute.PlayerSpawns))
                run = Accept(__0, ServerPlayerSpawns.RouteDispatch(__0), DispatchRoute.PlayerSpawns);
            if (run && Selected(routes, matches, DispatchRoute.PlayerHits))
                run = Accept(__0, ServerPlayerHits.RouteDispatch(__0), DispatchRoute.PlayerHits);
            if (run && Selected(routes, matches, DispatchRoute.PlayerRevives))
                run = Accept(__0, ServerPlayerRevives.RouteDispatch(__0), DispatchRoute.PlayerRevives);
            if (run && Selected(routes, matches, DispatchRoute.Inventory))
                run = Accept(__0, ServerInventory.RouteDispatch(__0), DispatchRoute.Inventory);
            if (run && Selected(routes, matches, DispatchRoute.Sleep))
                run = Accept(__0, ServerSleep.RouteDispatch(__0), DispatchRoute.Sleep);
            // Notices also forgets its own outbound frame when anything earlier rejected the event.
            if (Has(routes, DispatchRoute.Notices) && (!run || Selected(routes, matches, DispatchRoute.Notices)))
                run = Accept(__0, ServerNotices.RouteDispatch(__0, run), DispatchRoute.Notices);
            return run;
        }

        private static void AfterDispatch(DispatchScope __state)
        {
            if (__state == null) return;
            try
            {
                // Only the common policy's own rejection is released here. A typed route may reject and
                // free an event the common policy accepted; native routing owns accepted event storage.
                if (__state.Rejected) __state.Source.FreeStorage();
            }
            catch (Exception error) { ServerEvents.ReportFailure(error); }
            finally { __state.Active.Remove(__state.Source.Pointer); }
        }

        private static bool Accept(Bolt.Event source, DispatchVerdict verdict, DispatchRoute route)
        {
            if (verdict == DispatchVerdict.Forward) return true;
            if (verdict == DispatchVerdict.Reject)
            {
                // A skipped dispatch never transfers storage to native listeners or outgoing channels.
                try { source.FreeStorage(); }
                catch (Exception error) { ReportReleaseFailure(route, error); }
            }
            return false;
        }

        private static ulong Classify(Bolt.Event source)
        {
            // Off the game thread, or when the class can't be read, every route runs its own type test.
            if (Environment.CurrentManagedThreadId != _thread) return Unclassified;
            try
            {
                var nativeClass = IL2CPP.il2cpp_object_get_class(source.Pointer);
                if (nativeClass == IntPtr.Zero) return Unclassified;
                if (Classes.TryGetValue(nativeClass, out var matches)) return matches;
                matches = Match(nativeClass);
                if (Classes.Count < MaxCachedClasses) Classes.Add(nativeClass, matches);
                return matches;
            }
            catch (Exception) { return Unclassified; }
        }

        // Each test is the one its route applies itself: TryCast assignability or an exact class.
        private static ulong Match(IntPtr nativeClass)
        {
            ulong matches = Bit(DispatchRoute.Events);
            if (IsA<ChatEvent>(nativeClass))
                matches |= Bit(DispatchRoute.Chat) | Bit(DispatchRoute.Notices);
            if (nativeClass == Il2CppClassPointerStore<SpawnLootItemEvent>.NativeClassPtr)
                matches |= Bit(DispatchRoute.LootSpawns);
            if (IsA<WorldObjectLocatorState>(nativeClass))
                matches |= Bit(DispatchRoute.WorldLocators);
            if (IsA<StructureCraftingNodeAddIngredient>(nativeClass))
                matches |= Bit(DispatchRoute.ConstructionIngredients);
            if (IsA<StructureCraftingNodeComplete>(nativeClass))
                matches |= Bit(DispatchRoute.ConstructionCompletion);
            if (IsA<SledGrab>(nativeClass) || IsA<SledRelease>(nativeClass)
                || IsA<GolfCartShoveEvent>(nativeClass) || IsA<GolfCartConnectSlotEvent>(nativeClass))
                matches |= Bit(DispatchRoute.Vehicles);
            if (ServerFreeForm.IsServerMessageClass(nativeClass))
                matches |= Bit(DispatchRoute.FreeFormAuthority);
            if (IsA<SetPlayerSpawnPoint>(nativeClass))
                matches |= Bit(DispatchRoute.PlayerSpawns);
            // Retained combat ingress events are exact HitPlayer instances.
            if (IsA<HitPlayer>(nativeClass) || IsA<VailHitEvent>(nativeClass) || IsA<DismemberHitEvent>(nativeClass))
                matches |= Bit(DispatchRoute.PlayerHits);
            if (IsA<PlayerHealed>(nativeClass))
                matches |= Bit(DispatchRoute.PlayerRevives);
            if (IsA<PlayerAddItem>(nativeClass) || IsA<ItemRemoveFromPlayer>(nativeClass))
                matches |= Bit(DispatchRoute.Inventory);
            if (IsA<Sleep>(nativeClass))
                matches |= Bit(DispatchRoute.Sleep);
            return matches;
        }

        private static bool IsA<T>(IntPtr nativeClass) where T : Il2CppObjectBase
        {
            var target = Il2CppClassPointerStore<T>.NativeClassPtr;
            // TryCast throws for an unresolved class; that route still runs and fails as before.
            return target == IntPtr.Zero || IL2CPP.il2cpp_class_is_assignable_from(target, nativeClass);
        }

        private static void ReportReleaseFailure(DispatchRoute route, Exception error)
        {
            switch (route)
            {
                case DispatchRoute.Chat: ServerChat.ReportReleaseFailure(error); break;
                case DispatchRoute.LootSpawns: ServerLootSpawns.ReportReleaseFailure(error); break;
                case DispatchRoute.WorldLocators: ServerWorldLocators.ReportReleaseFailure(error); break;
                case DispatchRoute.ConstructionIngredients:
                case DispatchRoute.ConstructionCompletion: ServerConstruction.ReportReleaseFailure(error); break;
                case DispatchRoute.Vehicles: ServerVehicles.ReportReleaseFailure(error); break;
                case DispatchRoute.FreeFormAuthority: ServerFreeForm.ReportReleaseFailure(error); break;
                case DispatchRoute.PlayerSpawns: ServerPlayerSpawns.ReportReleaseFailure(error); break;
                case DispatchRoute.PlayerHits: ServerPlayerHits.ReportReleaseFailure(error); break;
                case DispatchRoute.PlayerRevives: ServerPlayerRevives.ReportReleaseFailure(error); break;
                case DispatchRoute.Inventory: ServerInventory.ReportReleaseFailure(error); break;
                case DispatchRoute.Sleep: ServerSleep.ReportReleaseFailure(error); break;
                case DispatchRoute.Notices: ServerNotices.ReportReleaseFailure(error); break;
            }
        }

        private static ulong Bit(DispatchRoute route) => 1UL << (int)route;
        private static bool Has(ulong routes, DispatchRoute route) => (routes & Bit(route)) != 0;
        private static bool Selected(ulong routes, ulong matches, DispatchRoute route) =>
            (routes & matches & Bit(route)) != 0;

        internal sealed class DispatchScope(Bolt.Event source, HashSet<IntPtr> active)
        {
            internal readonly Bolt.Event Source = source;
            internal readonly HashSet<IntPtr> Active = active;
            internal bool Rejected = true;
        }
    }

    /// <summary>
    /// Gate order, which is the order the separate Dispatch prefixes ran in: the common policy (First+100),
    /// then the Priority.First policies in Plugin registration order. Printers, named states and companions
    /// stay separate Priority.Last prefixes so they still run after other mods' prefixes.
    /// </summary>
    internal enum DispatchRoute
    {
        Events,
        Chat,
        LootSpawns,
        WorldLocators,
        ConstructionIngredients,
        ConstructionCompletion,
        Vehicles,
        FreeFormAuthority,
        PlayerSpawns,
        PlayerHits,
        PlayerRevives,
        Inventory,
        Sleep,
        Notices
    }

    internal enum DispatchVerdict
    {
        Forward,
        /// <summary>Rejected; the gate releases the event's storage before the next route.</summary>
        Reject,
        /// <summary>Rejected; storage stays with its owner, such as an outer dispatch or the combat ingress.</summary>
        Retain
    }
}
