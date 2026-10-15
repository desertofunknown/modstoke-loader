// SPDX-License-Identifier: GPL-3.0-or-later
// Native detour contracts adapted from BepInEx's BaseNativeDetour, DobbyDetour and FunchookDetour.
// Copyright (c) BepInEx contributors. Copyright (C) 2026 Alex Cooper.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;

namespace OpenACAI.Endnight.Loader.Preloader
{
    internal sealed class NativeDetour : IDetour
    {
        private readonly Delegate _targetDelegate;
        private readonly DobbyLibrary _dobby;
        private readonly FunchookLibrary _funchook;
        private readonly Dictionary<Type, Delegate> _trampolines = new Dictionary<Type, Delegate>();
        private IntPtr _funchookHandle;
        private bool _prepared;
        private bool _applyAttempted;
        private bool _disposed;
        private bool _processLifetime;
        private bool _applicationFailed;

        private NativeDetour(IntPtr original, Delegate target)
        {
            Target = original;
            _targetDelegate = target;
            Detour = Marshal.GetFunctionPointerForDelegate(target);
        }

        internal NativeDetour(IntPtr original, Delegate target, DobbyLibrary library) : this(original, target) => _dobby = library;
        internal NativeDetour(IntPtr original, Delegate target, FunchookLibrary library) : this(original, target) => _funchook = library;

        public IntPtr Target { get; }
        public IntPtr Detour { get; }
        public IntPtr OriginalTrampoline { get; private set; }
        internal bool IsValid => !_disposed && !CleanupFailed && !_applicationFailed;
        internal bool IsApplied { get; private set; }
        internal bool CleanupFailed { get; private set; }
        internal bool MayBeApplied => _applyAttempted;
        internal bool ProcessLifetime => _processLifetime;
        internal bool ApplicationFailed => _applicationFailed;

        public T GenerateTrampoline<T>() where T : Delegate => (T)GenerateTrampoline(typeof(T));

        internal void PrepareTrampoline()
        {
            lock (NativeDetourFactory.Sync)
                Prepare();
        }

        internal Delegate GenerateTrampoline(Type delegateType)
        {
            lock (NativeDetourFactory.Sync)
            {
                var wasPrepared = _prepared;
                Prepare();
                try
                {
                    if (!_trampolines.TryGetValue(delegateType, out var trampoline))
                    {
                        trampoline = Marshal.GetDelegateForFunctionPointer(OriginalTrampoline, delegateType);
                        _trampolines.Add(delegateType, trampoline);
                    }
                    return trampoline;
                }
                catch (Exception error)
                {
                    NativeDetourFactory.RecordFailure("trampoline", error);
                    // Earlier callers may already hold a trampoline or use the installed hook.
                    if (!wasPrepared)
                        CleanupAfterFailure(error);
                    throw;
                }
            }
        }

        public void Apply() => Apply(processLifetime: false);

        internal void ApplyForProcessLifetime() => Apply(processLifetime: true);

        private void Apply(bool processLifetime)
        {
            lock (NativeDetourFactory.Sync)
            {
                Prepare();
                if (IsApplied)
                    return;

                _processLifetime = processLifetime;
                // A failing install may already have changed native code. Cleanup must not assume otherwise.
                _applyAttempted = true;
                try
                {
                    if (_dobby != null)
                        EnsureSuccess("DobbyCommit", _dobby.Commit(Target));
                    else
                        EnsureSuccess("funchook_install", _funchook.Install(_funchookHandle, 0));
                    IsApplied = true;
                }
                catch (Exception error)
                {
                    _applicationFailed = true;
                    NativeDetourFactory.RecordFailure("apply", error);
                    // A startup gateway may already have callers using its published trampoline.
                    if (!_processLifetime)
                        CleanupAfterFailure(error);
                    throw;
                }
            }
        }

        private void Prepare()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (CleanupFailed)
                throw new InvalidOperationException("Native hook cleanup failed; this detour cannot be reused. Restart the game before retrying.");
            if (_applicationFailed)
                throw new InvalidOperationException("Native hook activation failed; its state is uncertain. Restart the game before retrying.");
            NativeDetourFactory.RequireCleanTarget(Target);
            if (_prepared)
                return;

            NativeDetourFactory.Retain(this);
            try
            {
                IntPtr trampoline;
                if (_dobby != null)
                {
                    EnsureSuccess("DobbyPrepare", _dobby.Prepare(Target, Detour, out trampoline));
                }
                else
                {
                    _funchookHandle = _funchook.Create();
                    if (_funchookHandle == IntPtr.Zero)
                        throw new OutOfMemoryException("funchook_create returned a null handle.");
                    trampoline = Target;
                    EnsureSuccess("funchook_prepare", _funchook.Prepare(_funchookHandle, ref trampoline, Detour));
                }
                _prepared = true;
                OriginalTrampoline = trampoline;
                if (trampoline == IntPtr.Zero)
                    throw new InvalidOperationException("The native detour provider returned a zero trampoline pointer.");
            }
            catch (Exception error)
            {
                NativeDetourFactory.RecordFailure("prepare", error);
                CleanupAfterFailure(error);
                throw;
            }
        }

        internal void Undo()
        {
            lock (NativeDetourFactory.Sync)
            {
                if (_disposed || !_applyAttempted)
                    return;
                if (_processLifetime)
                    throw new InvalidOperationException("A process-lifetime native gateway cannot be undone after activation was attempted.");

                try
                {
                    if (_dobby != null)
                    {
                        EnsureSuccess("DobbyDestroy", _dobby.Destroy(Target));
                        // Dobby removes its registry entry. Reapplying needs a new preparation and trampoline.
                        _prepared = false;
                        OriginalTrampoline = IntPtr.Zero;
                        _trampolines.Clear();
                        NativeDetourFactory.Release(this);
                    }
                    else
                    {
                        // Funchook retains its prepared trampoline for the next installation.
                        EnsureSuccess("funchook_uninstall", _funchook.Uninstall(_funchookHandle, 0));
                    }

                    _applyAttempted = false;
                    IsApplied = false;
                }
                catch (Exception error)
                {
                    CleanupFailed = true;
                    NativeDetourFactory.RecordFailure("cleanup", error);
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (NativeDetourFactory.Sync)
            {
                if (_disposed)
                    return;
                if (_processLifetime && _applyAttempted)
                    throw new InvalidOperationException("A process-lifetime native gateway cannot be disposed after activation was attempted.");

                try
                {
                    if (_dobby != null)
                    {
                        // DobbyPrepare owns a registry entry even when Commit was never called.
                        if (_prepared)
                            EnsureSuccess("DobbyDestroy", _dobby.Destroy(Target));
                    }
                    else if (_funchookHandle != IntPtr.Zero)
                    {
                        if (_applyAttempted)
                        {
                            EnsureSuccess("funchook_uninstall", _funchook.Uninstall(_funchookHandle, 0));
                            IsApplied = false;
                            _applyAttempted = false;
                        }
                        EnsureSuccess("funchook_destroy", _funchook.Destroy(_funchookHandle));
                        _funchookHandle = IntPtr.Zero;
                    }

                    _prepared = false;
                    _applyAttempted = false;
                    IsApplied = false;
                    CleanupFailed = false;
                    OriginalTrampoline = IntPtr.Zero;
                    _trampolines.Clear();
                    _disposed = true;
                    NativeDetourFactory.Release(this);
                    GC.KeepAlive(_targetDelegate);
                }
                catch (Exception error)
                {
                    // Retain both the callback and native storage if the provider cannot confirm removal.
                    CleanupFailed = true;
                    NativeDetourFactory.RecordFailure("cleanup", error);
                    throw;
                }
            }
        }

        private void CleanupAfterFailure(Exception originalError)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Native detour setup and cleanup failed. Resources remain rooted; restart the game before retrying.",
                    originalError, cleanupError);
            }
        }

        private void EnsureSuccess(string operation, int result)
        {
            if (result == 0)
                return;

            var detail = _funchook != null && _funchookHandle != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(_funchook.ErrorMessage(_funchookHandle)) : "";
            var message = $"{operation} failed with result {result}: {detail}";
            if (_funchook != null && result == 1)
                throw new OutOfMemoryException(message);
            throw new InvalidOperationException(message);
        }
    }
}
