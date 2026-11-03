// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Modstoke.Loader;
using RedLoader;
using SonsSdk;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modstoke.Loader
{
    internal sealed class GlobalInput : ISdkGlobalInputDriver
    {
        internal static readonly global::Modstoke.Loader.GlobalInput Instance = new();
        private static readonly object SyncRoot = new();
        private static readonly Dictionary<KeyCode, long> RegistrationIds = new();
        private static long NextRegistrationId;
        private static long[] SnapshotRegistrationIds = Array.Empty<long>();

        internal static ISdkGlobalInputDriver Create() => Instance;

        private GlobalInput() { }

        public bool RegisterKey(KeyCode key, Action action)
        {
            if (action == null)
                return false;

            lock (SyncRoot)
            {
                foreach (var registration in global::SonsSdk.GlobalInput._keyRegistrations)
                    if (registration.Key == key)
                        return false;

                global::SonsSdk.GlobalInput._keyRegistrations.Add(new global::SonsSdk.GlobalInput.KeyRegistration
                {
                    Key = key,
                    Action = action
                });
                RegistrationIds[key] = ++NextRegistrationId;
                global::SonsSdk.GlobalInput._keySnapshot = null;
                return true;
            }
        }

        public void UnregisterKey(KeyCode key)
        {
            lock (SyncRoot)
            {
                for (var index = global::SonsSdk.GlobalInput._keyRegistrations.Count - 1; index >= 0; index--)
                {
                    if (global::SonsSdk.GlobalInput._keyRegistrations[index].Key != key)
                        continue;
                    global::SonsSdk.GlobalInput._keyRegistrations.RemoveAt(index);
                    RegistrationIds.Remove(key);
                    global::SonsSdk.GlobalInput._keySnapshot = null;
                }
            }
        }

        public void UnregisterKey(KeyCode key, Action action)
        {
            lock (SyncRoot)
            {
                for (var index = global::SonsSdk.GlobalInput._keyRegistrations.Count - 1; index >= 0; index--)
                {
                    var registration = global::SonsSdk.GlobalInput._keyRegistrations[index];
                    if (registration.Key != key || !ReferenceEquals(registration.Action, action))
                        continue;
                    global::SonsSdk.GlobalInput._keyRegistrations.RemoveAt(index);
                    RegistrationIds.Remove(key);
                    global::SonsSdk.GlobalInput._keySnapshot = null;
                }
            }
        }

        public void Initialize()
        {
            // Keep the SDK delegate identities used by subscription deduplication.
            GlobalEvents.OnUpdate.Subscribe(global::SonsSdk.GlobalInput.OnUpdate);
            SdkEvents.OnGameActivated.Subscribe(global::SonsSdk.GlobalInput.OnGameActivated);
            SdkEvents.OnWorldExited.Subscribe(OnWorldExited);
        }

        public void OnGameActivated()
        {
            global::SonsSdk.GlobalInput._useAction = null;
            global::SonsSdk.GlobalInput._useAction = global::Sons.Input.InputSystem.InputMapping.@default.Use;
        }

        private static void OnWorldExited() => global::SonsSdk.GlobalInput._useAction = null;

        public void OnUpdate()
        {
            // Dispatch this snapshot only, but honor removals before each callback.
            global::SonsSdk.GlobalInput.KeyRegistration[] registrations;
            long[] registrationIds;
            lock (SyncRoot)
            {
                if (global::SonsSdk.GlobalInput._keySnapshot == null)
                {
                    registrations = global::SonsSdk.GlobalInput._keyRegistrations.ToArray();
                    registrationIds = registrations.Length == 0 ? Array.Empty<long>() : new long[registrations.Length];
                    for (var index = 0; index < registrations.Length; index++)
                        registrationIds[index] = RegistrationIds[registrations[index].Key];
                    SnapshotRegistrationIds = registrationIds;
                    global::SonsSdk.GlobalInput._keySnapshot = registrations;
                }

                registrations = global::SonsSdk.GlobalInput._keySnapshot;
                registrationIds = SnapshotRegistrationIds;
            }
            for (var index = 0; index < registrations.Length; index++)
            {
                var registration = registrations[index];
                // A preceding callback may have acquired or released focus this frame.
                if (InputBindings.HasFocus)
                    break;

                try
                {
                    if (Input.GetKeyDown(registration.Key))
                    {
                        foreach (var callback in Delegate.EnumerateInvocationList(registration.Action))
                        {
                            if (InputBindings.HasFocus || !IsRegistered(registration.Key, registrationIds[index], registrations))
                                break;

                            try
                            {
                                using var capture = PerformanceCapture.MeasureCallback(callback, "input");
                                callback();
                            }
                            catch (Exception exception)
                            {
                                global::SonsSdk.GlobalInput.ReportFailure("key " + registration.Key, exception);
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    global::SonsSdk.GlobalInput.ReportFailure("key " + registration.Key, exception);
                }
            }

            try
            {
                var useAction = global::SonsSdk.GlobalInput._useAction;
                if (CanInvokeUseAction(useAction) && useAction.WasPerformedThisFrame())
                    DispatchUseAction(useAction);
            }
            catch (Exception exception)
            {
                global::SonsSdk.GlobalInput.ReportFailure("Use action", exception);
            }
        }

        private static void DispatchUseAction(InputAction useAction)
        {
            var useEvent = global::SonsSdk.GlobalInput.OnUsePerformed;
            Action<LemonAction> invoke = callback =>
            {
                if (CanInvokeUseAction(useAction))
                    callback();
            };
            foreach (var subscriber in useEvent.BeginInvocation())
            {
                if (!CanInvokeUseAction(useAction))
                    break;

                useEvent.InvokeSubscriber(subscriber, invoke);
            }
        }

        private static bool IsRegistered(KeyCode key, long registrationId, global::SonsSdk.GlobalInput.KeyRegistration[] snapshot)
        {
            lock (SyncRoot)
            {
                if (ReferenceEquals(global::SonsSdk.GlobalInput._keySnapshot, snapshot))
                    return true;

                // Re-registering the same delegate still starts a new lifetime.
                return RegistrationIds.TryGetValue(key, out var currentId) && currentId == registrationId;
            }
        }

        private static bool CanInvokeUseAction(InputAction action) =>
            !InputBindings.HasFocus && ReferenceEquals(global::SonsSdk.GlobalInput._useAction, action) &&
            action is not null && !action.WasCollected && action.enabled;
    }
}
