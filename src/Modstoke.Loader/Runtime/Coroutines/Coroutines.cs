// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using RedLoader;
using RedLoader.Unity.IL2CPP.UnityEngine;
using RedLoader.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace Modstoke.Loader
{
    internal sealed class Coroutines : ICoroutineDriver
    {
        private static readonly global::Modstoke.Loader.Coroutines Driver = new();
        private static readonly Dictionary<IntPtr, RunningCoroutine> Running = new();
        private static RunningCoroutine[] _runningSnapshot = Array.Empty<RunningCoroutine>();
        private static int _ownerThreadId;
        private static bool _observingLifecycle;
        private static bool _stopping;

        private Coroutines() { }

        internal static void Install()
        {
            global::RedLoader.Coroutines.SetDriver(Driver);
        }

        public Coroutine Start(IEnumerator routine)
        {
            if (!PrepareStart(routine))
                return null;

            return StartOnHost(routine);
        }

        public void Stop(Coroutine coroutineToken)
        {
            if (coroutineToken == null)
                return;
            RequireOwnerThread();
            if (StopOwned(coroutineToken))
                return;
            if (!CanStop())
                return;

            if (GlobalBehaviour.Instance == null)
                throw new NotSupportedException("Support module must be initialized before stopping coroutines");

            GlobalBehaviour.Instance.StopCoroutine(coroutineToken);
        }

        internal static bool PrepareStart(IEnumerator routine)
        {
            ArgumentNullException.ThrowIfNull(routine);
            RequireOwnerThread();
            if (_stopping)
                throw new InvalidOperationException("Coroutines cannot be started while the application is quitting.");
            if (LegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            LegacyRedLoaderCompatibility.TryInitializeSupportModule(null);
            if (LegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            LegacyRedLoaderCompatibility.QueueCoroutineStart(routine);
            RuntimeEntryPoint.TraceCompat("Queued RedLoader coroutine start because support module was not initialized.");
            return false;
        }

        internal static bool CanStop()
        {
            RequireOwnerThread();
            if (LegacyRedLoaderCompatibility.IsSupportModuleReady())
                return true;

            RuntimeEntryPoint.TraceCompat("Blocked RedLoader coroutine stop because support module is not initialized yet.");
            return false;
        }

        internal static void InitializeOwnerThread()
        {
            RequireOwnerThread();
            _ownerThreadId = Environment.CurrentManagedThreadId;
            if (_observingLifecycle)
                return;
            GlobalEvents.OnUpdate.Subscribe(CleanupDestroyedOwners);
            GlobalEvents.OnApplicationQuit.Subscribe(Shutdown);
            _observingLifecycle = true;
        }

        internal static void RequireOwnerThread()
        {
            if (_ownerThreadId != 0 && _ownerThreadId != Environment.CurrentManagedThreadId)
                throw new InvalidOperationException("Coroutine operations must run on the Unity owner thread.");
        }

        internal static bool TryStartObserved(IEnumerator routine, Action<bool, Exception> completed,
            out Coroutine token, out string problem)
        {
            ArgumentNullException.ThrowIfNull(routine);
            ArgumentNullException.ThrowIfNull(completed);
            token = null;
            problem = "Coroutine observation requires the initialized Unity owner thread.";
            if (!_observingLifecycle || _ownerThreadId != Environment.CurrentManagedThreadId)
                return false;
            if (_stopping)
            {
                problem = "Coroutines cannot be started while the application is quitting.";
                return false;
            }

            GlobalBehaviour owner;
            try
            {
                owner = GlobalBehaviour.Instance;
                if (owner == null || !owner.gameObject.activeInHierarchy)
                {
                    problem = "The coroutine host is unavailable or inactive.";
                    return false;
                }
            }
            catch (Exception error)
            {
                problem = "The coroutine host could not be read: " + error.Message;
                return false;
            }

            var managed = ManagedEnumerator.CreateRedLoader(routine);
            managed.ObserveCompletion(completed);
            try { token = StartOnHost(owner, managed); }
            catch (Exception error)
            {
                managed.RecordFailure(error);
                try { managed.Dispose(); }
                catch (Exception cleanupError) { managed.RecordFailure(cleanupError); }
            }
            finally { managed.ReleaseCompletionNotification(); }
            problem = null;
            return true;
        }

        internal static Coroutine StartOnHost(IEnumerator routine)
        {
            ArgumentNullException.ThrowIfNull(routine);
            RequireOwnerThread();
            if (_stopping)
                throw new InvalidOperationException("Coroutines cannot be started while the application is quitting.");
            var owner = GlobalBehaviour.Instance;
            if (owner == null)
                throw new NotSupportedException("Support module must be initialized before starting coroutines");

            InitializeOwnerThread();
            var managed = ManagedEnumerator.CreateRedLoader(routine);
            return StartOnHost(owner, managed);
        }

        private static Coroutine StartOnHost(GlobalBehaviour owner, ManagedEnumerator managed)
        {
            var running = new RunningCoroutine(owner, managed);
            managed.OnCompleted(() => Forget(running));
            try
            {
                var token = owner.StartCoroutine(managed.WrapToIl2Cpp());
                running.Token = token;
                if (token == null)
                {
                    if (!managed.IsFinished)
                        managed.RecordFailure(new InvalidOperationException("Unity did not start the coroutine."));
                    managed.Dispose();
                }
                else if (_stopping || owner == null)
                    StopRunning(running);
                else if (!managed.IsFinished)
                {
                    Running.Add(token.Pointer, running);
                    _runningSnapshot = null;
                }
                return token;
            }
            catch (Exception error)
            {
                managed.RecordFailure(error);
                try { managed.Dispose(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("Coroutine start and cleanup failed.", error, cleanupError);
                }
                throw;
            }
        }

        internal static bool StopOwned(Coroutine token)
        {
            RequireOwnerThread();
            if (token == null)
                return true;
            if (!Running.Remove(token.Pointer, out var running))
                return false;
            _runningSnapshot = null;

            StopRunning(running);
            return true;
        }

        private static void StopRunning(RunningCoroutine running)
        {
            Exception stopError = null;
            try
            {
                if (running.Owner != null && running.Token != null)
                    running.Owner.StopCoroutine(running.Token);
            }
            catch (Exception error)
            {
                stopError = error;
                running.Routine.RecordFailure(error);
            }

            try { running.Routine.Dispose(); }
            catch (Exception cleanupError)
            {
                if (stopError != null)
                    throw new AggregateException("Coroutine stop and cleanup failed.", stopError, cleanupError);
                throw;
            }
            if (stopError != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopError).Throw();
        }

        private static void Forget(RunningCoroutine running)
        {
            var token = running.Token;
            if (token != null && Running.TryGetValue(token.Pointer, out var current)
                && ReferenceEquals(current, running))
            {
                Running.Remove(token.Pointer);
                _runningSnapshot = null;
            }
        }

        private static void CleanupDestroyedOwners()
        {
            RequireOwnerThread();
            if (Running.Count == 0)
                return;
            // User cleanup can start/stop other routines; process only this pass's snapshot.
            var snapshot = GetRunningSnapshot();
            GlobalBehaviour lastOwner = null;
            var ownerAlive = false;
            foreach (var running in snapshot)
            {
                if (!ReferenceEquals(lastOwner, running.Owner))
                {
                    lastOwner = running.Owner;
                    ownerAlive = lastOwner != null && lastOwner.gameObject.activeInHierarchy;
                }
                if (ownerAlive)
                    continue;
                Forget(running);
                try { running.Routine.Dispose(); }
                catch (Exception error) { RuntimeEntryPoint.TraceCompat("Destroyed coroutine owner cleanup failed: " + error); }
            }
        }

        private static void Shutdown()
        {
            RequireOwnerThread();
            _stopping = true;
            var snapshot = GetRunningSnapshot();
            Running.Clear();
            _runningSnapshot = Array.Empty<RunningCoroutine>();
            foreach (var running in snapshot)
            {
                try { StopRunning(running); }
                catch (Exception error) { RuntimeEntryPoint.TraceCompat("Coroutine shutdown cleanup failed: " + error); }
            }
        }

        private static RunningCoroutine[] GetRunningSnapshot()
        {
            if (_runningSnapshot == null)
            {
                _runningSnapshot = new RunningCoroutine[Running.Count];
                Running.Values.CopyTo(_runningSnapshot, 0);
            }
            return _runningSnapshot;
        }

        private sealed class RunningCoroutine
        {
            internal readonly GlobalBehaviour Owner;
            internal readonly ManagedEnumerator Routine;
            internal Coroutine Token;

            internal RunningCoroutine(GlobalBehaviour owner, ManagedEnumerator routine)
            {
                Owner = owner;
                Routine = routine;
            }
        }
    }
}
