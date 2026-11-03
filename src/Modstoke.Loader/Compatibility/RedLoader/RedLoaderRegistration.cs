// SPDX-License-Identifier: GPL-3.0-or-later
// Registration behavior adapted from RedLoader's ModBase, derived from MelonLoader.
// Copyright (c) RedLoader and MelonLoader contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Reflection;
using RedLoader;

// Registration must retain the legacy metadata contract.
#pragma warning disable CS0618

namespace Modstoke.Loader;

internal static class RedLoaderRegistration
{
    private static readonly HashSet<Assembly> UnregisteringAssemblies = new(ReferenceEqualityComparer.Instance);
    private static readonly List<Registration> RegisteringMods = new();
    private static readonly Dictionary<string, Registration> HarmonyOwners = new(StringComparer.Ordinal);

    internal static IModRegistrationDriver Create(ModBase mod) => new Registration(mod);

    internal static List<Action> CaptureLifetime(ModBase mod)
    {
        var owner = mod.HarmonyInstance?.Id;
        return owner != null && HarmonyOwners.TryGetValue(owner, out var registration)
            ? registration.CaptureLifetime(mod)
            : null;
    }

    internal static bool IsLifetimeActive(ModBase mod, List<Action> lifetime) =>
        lifetime != null && ReferenceEquals(CaptureLifetime(mod), lifetime);

    internal static ModBase Find(string name, string author)
    {
        return ModBase._registeredMelons.Find(mod => mod.Registered && mod.Info != null &&
            !UnregisteringAssemblies.Contains(mod.ModAssembly) &&
            mod.Info.Name == name && mod.Info.Author == author);
    }

    private sealed class Registration : IModRegistrationDriver
    {
        private readonly ModBase _mod;
        private readonly IModRegistrationCallbacks _callbacks;
        private List<Action> _registrationRollback;
        private List<Action> _registeredCallbacks;
        private bool _unregistering;
        private bool _typedRegistrationAttempted;
        private bool _earlyInitializationStarted;
        private bool _teardownCompleted;
        private bool _registrationCancelled;
        private string _harmonyOwner;

        internal Registration(ModBase mod)
        {
            _mod = mod;
            _callbacks = (IModRegistrationCallbacks)mod;
        }

        public List<Action> Callbacks => _registeredCallbacks;

        public bool IsActive(List<Action> callbacks) =>
            callbacks != null && _mod.Registered && ReferenceEquals(_registeredCallbacks, callbacks) &&
            !UnregisteringAssemblies.Contains(_mod.ModAssembly);

        internal List<Action> CaptureLifetime(ModBase mod) =>
            ReferenceEquals(_mod, mod) && IsActive(_registeredCallbacks) ? _registeredCallbacks : null;

        public bool Register()
        {
            if (_mod.Registered || _registrationRollback != null || _unregistering ||
                UnregisteringAssemblies.Contains(_mod.ModAssembly))
                return false;

            var registered = false;
            _typedRegistrationAttempted = false;
            _earlyInitializationStarted = false;
            _teardownCompleted = false;
            _registrationCancelled = false;
            _registrationRollback = new List<Action>();
            RegisteringMods.Add(this);
            try
            {
                // Harmony unpatching is owner-wide, even for different mod instances.
                _harmonyOwner = _mod.HarmonyInstance?.Id ?? $"{_mod.ModAssembly.FullName}:{_mod.Info.Name}";
                if (!HarmonyOwners.TryAdd(_harmonyOwner, this))
                    return false;

                ModBase.OnMelonInitializing.Invoke(_mod);
                if (_registrationCancelled)
                    return false;
                _mod.LoggerInstance ??= new RLog.Instance(
                    string.IsNullOrEmpty(_mod.Info.Name) ? _mod.ID : _mod.Info.Name, _mod.ConsoleColor);
                _mod.HarmonyInstance ??= new HarmonyLib.Harmony(_harmonyOwner);
                if (_registrationCancelled)
                    return false;

                // Subscription eligibility must be live before invoking the virtual callbacks.
                // Each callback can unregister the mod, so recheck registration before the next step.
                _callbacks.SetRegistered(true);
                _registeredCallbacks = _registrationRollback;
                _callbacks.RegisterCallbacks();
                if (!_mod.Registered)
                    return false;
                _earlyInitializationStarted = true;
                _callbacks.EarlyInitialize();
                if (!_mod.Registered)
                    return false;

                _typedRegistrationAttempted = true;
                if (!_callbacks.RegisterInternal() || !_mod.Registered)
                    return false;

                ModBase._registeredMelons.Add(_mod);
                _callbacks.PrintLoadInfo();
                if (!_mod.Registered)
                    return false;

                _mod.OnRegister.Invoke();
                if (!_mod.Registered)
                    return false;
                ModBase.OnMelonRegistered.Invoke(_mod);
                if (!_mod.Registered)
                    return false;

                if (GlobalEvents.OnApplicationStart.Disposed)
                    InitializeMod();
                else
                    _callbacks.SubscribeInitialization();
                if (!_mod.Registered)
                    return false;

                if (GlobalEvents.OnApplicationLateStart.Disposed)
                    _callbacks.LateInitialize();
                else
                    _callbacks.SubscribeLateInitialization();
                if (!_mod.Registered)
                    return false;

                registered = true;
                return true;
            }
            catch (Exception error)
            {
                try
                {
                    RLog.Error($"Failed to register {_mod.MelonTypeName} '{_mod.ModAssembly.Location}': Melon failed to initialize!");
                    RLog.Error(error.ToString());
                }
                catch { /* Diagnostics must not change registration failure into an escaping exception. */ }
                return false;
            }
            finally
            {
                if (!registered)
                {
                    RollbackRegistration();
                    ReleaseHarmonyOwner();
                }
                _registrationRollback = null;
                RegisteringMods.Remove(this);
            }
        }

        private void ReleaseHarmonyOwner()
        {
            if (_harmonyOwner != null && HarmonyOwners.TryGetValue(_harmonyOwner, out var owner) &&
                ReferenceEquals(owner, this))
                HarmonyOwners.Remove(_harmonyOwner);
        }

        private void RollbackRegistration()
        {
            // A callback can explicitly unregister this lifetime before registration returns.
            if (_teardownCompleted)
                return;

            _callbacks.SetRegistered(false);
            var lifetimeStarted = _registeredCallbacks != null;
            _registeredCallbacks = null;
            for (var i = _registrationRollback.Count - 1; i >= 0; i--)
                TryCleanup("event subscription", _registrationRollback[i], rollback: true);
            _registrationRollback.Clear();

            if (_typedRegistrationAttempted)
                TryCleanup("typed registration", _callbacks.UnregisterInternal, rollback: true);
            ModBase._registeredMelons.RemoveAll(mod => ReferenceEquals(mod, _mod));
            if (_earlyInitializationStarted)
                TryCleanup("mod deinitialization", _callbacks.Deinitialize, rollback: true);
            if (lifetimeStarted)
                TryCleanup("Harmony patches", () => _mod.HarmonyInstance?.UnpatchSelf(), rollback: true);
        }

        private void TryCleanup(string operation, Action cleanup, bool rollback = false)
        {
            try { cleanup(); }
            catch (Exception error)
            {
                try
                {
                    RLog.Error(rollback
                        ? $"Registration rollback for '{_mod.ID}' could not remove {operation}: {error}"
                        : $"Unregistration for '{_mod.ID}' failed during {operation}: {error}");
                }
                catch { /* Diagnostics must not prevent the remaining cleanup. */ }
            }
        }

        public void InitializeMod()
        {
            if (!_mod.Registered || _unregistering || UnregisteringAssemblies.Contains(_mod.ModAssembly))
                return;

            try { _callbacks.Initialize(); }
            catch (Exception error)
            {
                try { _mod.LoggerInstance.Error(error); }
                catch { /* A failed observer must not abort the remaining initialization callbacks. */ }
            }
        }

        public void Unregister(string reason, bool silent)
        {
            if (!_mod.Registered || _unregistering || !UnregisteringAssemblies.Add(_mod.ModAssembly))
                return;

            try
            {
                var mods = ModBase._registeredMelons.FindAll(mod => ReferenceEquals(mod.ModAssembly, _mod.ModAssembly));
                foreach (var registration in RegisteringMods)
                {
                    var mod = registration._mod;
                    if (!ReferenceEquals(mod.ModAssembly, _mod.ModAssembly))
                        continue;
                    registration._registrationCancelled = true;
                    if (mod.Registered && !mods.Exists(candidate => ReferenceEquals(candidate, mod)))
                        mods.Add(mod);
                }
                // Initialization can unregister the current mod before registry publication.
                if (!mods.Exists(mod => ReferenceEquals(mod, _mod)))
                    mods.Add(_mod);

                foreach (var mod in mods)
                    mod.UnregisterInstance(reason, silent);
            }
            finally
            {
                UnregisteringAssemblies.Remove(_mod.ModAssembly);
            }
        }

        public void UnregisterInstance(string reason, bool silent)
        {
            if (!_mod.Registered || _unregistering)
                return;

            _unregistering = true;
            _teardownCompleted = true;
            _callbacks.SetRegistered(false);
            var callbacks = _registeredCallbacks;
            _registeredCallbacks = null;
            try
            {
                if (callbacks != null)
                {
                    for (var i = callbacks.Count - 1; i >= 0; i--)
                        TryCleanup("event subscription", callbacks[i]);
                    callbacks.Clear();
                }

                if (_typedRegistrationAttempted)
                    TryCleanup("typed registration", _callbacks.UnregisterInternal);
                ModBase._registeredMelons.RemoveAll(mod => ReferenceEquals(mod, _mod));
                TryCleanup("mod deinitialization", _callbacks.Deinitialize);
                TryCleanup("Harmony patches", () => _mod.HarmonyInstance?.UnpatchSelf());

                if (!silent)
                    TryCleanup("unload message", () => _callbacks.PrintUnloadInfo(reason));

                TryCleanup("instance notification", _mod.OnUnregister.Invoke);
                TryCleanup("global notification", () => ModBase.OnMelonUnregistered.Invoke(_mod));
            }
            finally
            {
                // An in-flight Register still owns its rollback until that stack unwinds.
                if (_registrationRollback == null)
                    ReleaseHarmonyOwner();
                _unregistering = false;
            }
        }

    }
}
