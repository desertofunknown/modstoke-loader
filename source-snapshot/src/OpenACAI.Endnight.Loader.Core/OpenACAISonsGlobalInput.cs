// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Linq;
using RedLoader;
using SonsSdk;
using UnityEngine;

namespace OpenACAI.Endnight.Loader
{
    internal sealed class OpenACAISonsGlobalInput : ISdkGlobalInputDriver
    {
        internal static readonly OpenACAISonsGlobalInput Instance = new();

        private OpenACAISonsGlobalInput() { }

        public bool RegisterKey(KeyCode key, Action action)
        {
            if (GlobalInput._keyRegistrations.Any(registration => registration.Key == key))
                return false;

            GlobalInput._keyRegistrations.Add(new GlobalInput.KeyRegistration
            {
                Key = key,
                Action = action
            });
            GlobalInput._keySnapshot = null;
            return true;
        }

        public void UnregisterKey(KeyCode key)
        {
            if (GlobalInput._keyRegistrations.RemoveAll(registration => registration.Key == key) > 0)
                GlobalInput._keySnapshot = null;
        }

        public void UnregisterKey(KeyCode key, Action action)
        {
            if (GlobalInput._keyRegistrations.RemoveAll(registration =>
                    registration.Key == key && ReferenceEquals(registration.Action, action)) > 0)
                GlobalInput._keySnapshot = null;
        }

        public void Initialize()
        {
            // Keep the SDK delegate identities used by subscription deduplication.
            GlobalEvents.OnUpdate.Subscribe(GlobalInput.OnUpdate);
            SdkEvents.OnGameActivated.Subscribe(GlobalInput.OnGameActivated);
        }

        public void OnGameActivated()
        {
            GlobalInput._useAction = global::Sons.Input.InputSystem.InputMapping.@default.Use;
        }

        public void OnUpdate()
        {
            // Callback mutations affect the next dispatch, not this snapshot.
            var registrations = GlobalInput._keySnapshot ??= GlobalInput._keyRegistrations.ToArray();
            foreach (var registration in registrations)
            {
                try
                {
                    if (Input.GetKeyDown(registration.Key))
                        registration.Action();
                }
                catch (Exception exception)
                {
                    GlobalInput.ReportFailure("key " + registration.Key, exception);
                }
            }

            try
            {
                if (GlobalInput._useAction != null && GlobalInput._useAction.WasPerformedThisFrame())
                    GlobalInput.OnUsePerformed.Invoke();
            }
            catch (Exception exception)
            {
                GlobalInput.ReportFailure("Use action", exception);
            }
        }
    }
}
