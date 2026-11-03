// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Common;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerLifecycle
    {
        private static bool _installAttempted;
        private static bool _installed;
        private static int _ownerThread;
        private static RetainedPublisher[] _publishers = Array.Empty<RetainedPublisher>();
        private static bool HooksAvailable => _installed && Environment.CurrentManagedThreadId == _ownerThread;

        /// <summary>
        /// Parent calls at startup. Only inert retained-publisher gates are installed here.
        /// Action observation requires explicit SelectActionTrackingForNextStart activation.
        /// They prevent the retired source from becoming a remote renderer/state writer after
        /// its EntityBehaviour cache is rebound. They never redirect a host's LocalPlayer.
        /// </summary>
        internal static void Install(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (_installAttempted) return;
            _installAttempted = true;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support?.SafeForHarmonyPatches != true || !GameBuild.IsRecognized
                    || !OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return;
                _ownerThread = Environment.CurrentManagedThreadId;
                // Qualify ALL entries before patching. This list has no shared empty native stubs.
                var methods = new[]
                {
                    Qualify(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.Update), 0x32BB9A0),
                    Qualify(typeof(BoltPlayerSetup), nameof(BoltPlayerSetup.UpdateState), 0x32C3CF0),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.LateUpdate), 0x30FB190),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwner), 0x30FB4E0),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateOwnerEventSync), 0x30FB8F0),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateRemote), 0x30FD6B0, typeof(bool)),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.UpdateMecanimPropertiesFromState), 0x30FAD30),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.ApplyTransitionRemote), 0x30FC850,
                        typeof(int), typeof(int), typeof(bool), typeof(float), typeof(bool), typeof(float)),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.ApplyHashToRemote), 0x30FC6F0,
                        typeof(int), typeof(int), typeof(bool), typeof(float), typeof(bool)),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.DoCrossFade), 0x30FD090,
                        typeof(int), typeof(int), typeof(float), typeof(float), typeof(bool)),
                    Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.SendStateEventsForCurrentState), 0x30FAAE0, typeof(int))
                };
                var send = Qualify(typeof(CoopMecanimReplicator), nameof(CoopMecanimReplicator.SendTransitionEvent), 0x30FC110,
                    typeof(int), typeof(int), typeof(float), typeof(BoltEntity), typeof(bool));
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-retained-player-lifecycle");
                foreach (var method in methods)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(PlayerLifecycle), nameof(BeforePublisher))
                        { priority = Priority.First });
                harmony.Patch(send,
                    prefix: new HarmonyMethod(typeof(PlayerLifecycle), nameof(BeforeSendTransition)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(PlayerLifecycle), nameof(AfterSendTransition)) { priority = Priority.Last });
                _installed = true;
            }
            catch (Exception error)
            {
                try { harmony?.UnpatchSelf(); } catch (Exception) { }
                warning?.Invoke("Retained player publisher gates unavailable (" + error.GetType().Name + ").");
            }
        }

        private static MethodInfo Qualify(Type type, string name, int rva, params Type[] arguments)
            => QualifyNative(type, name, rva, false, typeof(void), arguments);

        internal static MethodInfo QualifyNative(Type type, string name, int rva, bool isStatic,
            Type returnType, params Type[] arguments)
        {
            var method = AccessTools.DeclaredMethod(type, name, arguments);
            if (method == null || method.IsStatic != isStatic || method.ReturnType != returnType)
                throw new MissingMethodException(type.FullName, name);
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
            if (field?.GetValue(null) is not nint info || info == 0)
                throw new InvalidOperationException("Native method information missing: " + name);
            var module = GetModuleHandle("GameAssembly.dll");
            if (module == 0 || Marshal.ReadIntPtr(info) != module + rva)
                throw new InvalidOperationException("Native retained publisher entry changed: " + name);
            return method;
        }

        private static void RegisterRetainedPublishers(PlayerLifecycle owner)
        {
            if (!HooksAvailable) throw new InvalidOperationException("Retained publisher thread changed.");
            var next = new List<RetainedPublisher>();
            foreach (var publisher in _publishers)
                if (publisher.IsAlive) next.Add(publisher);
            Add(owner._setup);
            foreach (var animation in owner._animations) Add(animation);
            Volatile.Write(ref _publishers, next.ToArray());

            void Add(Component component)
            {
                foreach (var publisher in next)
                    if (publisher.Pointer == component.Pointer)
                        throw new InvalidOperationException("Retained publisher already registered.");
                next.Add(new RetainedPublisher(component));
            }
        }

        private static bool BeforePublisher(Component __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (!_installed || __instance == null) return true;
            var fence = _actionFence;
            if (fence != null && fence._freezeStarted && fence.OwnsPublisher(__instance)) return false;
            // Exact native identity, independent of LocalPlayer globals or the new carrier's ID.
            // A failed aggregate migration must NOT let the old publisher begin running again.
            foreach (var publisher in Volatile.Read(ref _publishers))
            {
                if (publisher.Pointer != __instance.Pointer) continue;
                try { return !publisher.Matches(__instance); }
                catch (Exception) { return false; }
            }
            return true;
        }

        private static bool BeforeSendTransition(CoopMecanimReplicator __instance, int __0, int __1, float __2,
            BoltEntity __3, bool __4, bool __runOriginal)
        {
            if (!__runOriginal || !BeforePublisher(__instance, true)) return false;
            return !PlayerPreservation.TryRouteDelayedAnimationTransition(__instance, __0, __1, __2, __3, __4);
        }

        private static void AfterSendTransition(CoopMecanimReplicator __instance, int __0, int __1, float __2,
            BoltEntity __3, bool __4, bool __runOriginal)
            => PlayerPreservation.ObserveNativeAnimationTransition(__instance, __0, __1, __2, __3, __4, __runOriginal);

        private static void PruneRetainedPublishers()
        {
            var publishers = _publishers;
            List<RetainedPublisher> next = null;
            for (int i = 0; i < publishers.Length; i++)
            {
                var publisher = publishers[i];
                if (publisher.IsAlive) next?.Add(publisher);
                else if (next == null)
                {
                    // Keep the prefix that already passed its native lifetime checks.
                    next = new List<RetainedPublisher>(publishers.Length - 1);
                    for (int previous = 0; previous < i; previous++) next.Add(publishers[previous]);
                }
            }
            if (next != null) Volatile.Write(ref _publishers, next.ToArray());
        }

        private sealed class RetainedPublisher
        {
            private readonly Component _component;
            private readonly int _instance;
            internal readonly IntPtr Pointer;
            internal RetainedPublisher(Component component)
            { _component = component; Pointer = component.Pointer; _instance = component.GetInstanceID(); }
            internal bool IsAlive => Alive(_component) && _component.GetInstanceID() == _instance;
            internal bool Matches(Component component) => component != null && component.Pointer == Pointer
                && IsAlive && component.GetInstanceID() == _instance;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        private static extern nint GetModuleHandle(string name);
    }
}
