// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using BepInExCollections = BepInEx.Unity.IL2CPP.Utils.Collections;
using RedLoaderCollections = RedLoader.Unity.IL2CPP.Utils.Collections;
using Il2CppIEnumerator = Il2CppSystem.Collections.IEnumerator;
using Il2CppObject = Il2CppSystem.Object;

namespace Modstoke.Loader
{
    internal sealed class ManagedEnumerator :
        RedLoaderCollections.IManagedEnumeratorDriver,
        BepInExCollections.IManagedEnumeratorDriver,
        IEnumerator,
        IDisposable
    {
        private static readonly Func<IEnumerator, RedLoaderCollections.IManagedEnumeratorDriver> RedLoaderFactory =
            routine => CreateRedLoader(routine);
        private static readonly Func<IEnumerator, BepInExCollections.IManagedEnumeratorDriver> BepInExFactory =
            routine => routine as ManagedEnumerator
                ?? new ManagedEnumerator(routine, child => new BepInExCollections.Il2CppManagedEnumerator(child));

        private readonly IEnumerator _routine;
        private readonly Func<IEnumerator, Il2CppObject> _wrapChild;
        private Il2CppObject _current;
        private bool _hasCurrent;
        private bool _positioned;
        private bool _finished;
        private bool _disposed;
        private bool _executing;
        private readonly ManagedEnumerator _parent;
        private ManagedEnumerator _child;
        private Action _onCompleted;
        private Action<bool, Exception> _onTerminated;
        private List<Exception> _observedFailures;
        private bool _completedNaturally;
        private bool _completionReleased;

        private ManagedEnumerator(IEnumerator routine, Func<IEnumerator, Il2CppObject> wrapChild,
            ManagedEnumerator parent = null)
        {
            _routine = routine ?? throw new ArgumentNullException(nameof(routine));
            _wrapChild = wrapChild;
            _parent = parent;
        }

        internal static ManagedEnumerator CreateRedLoader(IEnumerator routine)
            => routine as ManagedEnumerator
                ?? new ManagedEnumerator(routine, child => new RedLoaderCollections.Il2CppManagedEnumerator(child));

        internal bool IsFinished => _finished;

        internal void OnCompleted(Action callback) => _onCompleted = callback;

        internal void ObserveCompletion(Action<bool, Exception> callback) => _onTerminated = callback;

        internal void ReleaseCompletionNotification()
        {
            // StartCoroutine may advance and dispose synchronously before reporting a start failure.
            _completionReleased = true;
            NotifyCompletion();
        }

        internal void RecordFailure(Exception error)
        {
            var root = Root;
            if (root._onTerminated == null)
                return;
            // Cleanup exceptions can travel through several catch blocks. Keep each cause once.
            if (error is AggregateException aggregate && aggregate.InnerExceptions.Count != 0)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    root.RecordFailure(inner);
                return;
            }
            var failures = root._observedFailures ??= new();
            foreach (var failure in failures)
                if (ReferenceEquals(failure, error))
                    return;
            failures.Add(error);
        }

        private void NotifyCompletion()
        {
            if (!_completionReleased || !_disposed || _executing || _child != null || _onTerminated == null)
                return;
            var callback = _onTerminated;
            var failures = _observedFailures;
            _onTerminated = null;
            _observedFailures = null;
            Exception failure = failures == null ? null : failures.Count == 1 ? failures[0]
                : new AggregateException("Coroutine execution or cleanup failed.", failures);
            try { callback(_completedNaturally, failure); }
            catch (Exception error)
            {
                RuntimeEntryPoint.TraceCompat("Coroutine completion handler failed: " + error);
            }
        }

        object IEnumerator.Current => Current;

        internal static void InstallRedLoader()
        {
            RedLoaderCollections.ManagedEnumeratorDriverFactory.SetFactory(RedLoaderFactory);
        }

        internal static void InstallBepInEx()
        {
            BepInExCollections.ManagedEnumeratorDriverFactory.SetFactory(BepInExFactory);
        }

        public Il2CppObject Current
        {
            get
            {
                if (_finished || !_positioned)
                    return null;
                if (_hasCurrent)
                    return _current;

                BeginExecution();
                try
                {
                    _current = ConvertYield(_routine.Current);
                    _hasCurrent = true;
                    return _finished ? null : _current;
                }
                catch (Exception error)
                {
                    Fail(error);
                    throw;
                }
                finally { EndExecution(); }
            }
        }

        public bool MoveNext()
        {
            if (_finished)
                return false;

            BeginExecution();
            try
            {
                ClearCurrent();
                _child?.Dispose();
                _child = null;
                if (_finished)
                    return false;
                _positioned = _routine.MoveNext();
                if (!_positioned)
                {
                    _completedNaturally = !_finished;
                    _finished = true;
                }
                return _positioned && !_finished;
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
            finally { EndExecution(); }
        }

        public void Reset()
        {
            BeginExecution();
            try
            {
                ClearCurrent();
                // A successful explicit Reset may reuse a completed custom enumerator.
                // Rearm before calling user code so cancellation from Reset still wins.
                _finished = false;
                _disposed = false;
                _completedNaturally = false;
                _child?.Dispose();
                _child = null;
                if (!_finished)
                    _routine.Reset();
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
            finally { EndExecution(); }
        }

        private void BeginExecution()
        {
            for (var item = this; item != null; item = item._parent)
            {
                if (item._executing)
                    throw new InvalidOperationException("A coroutine enumerator cannot be advanced reentrantly.");
            }
            for (var item = _child; item != null; item = item._child)
            {
                if (item._executing)
                    throw new InvalidOperationException("A coroutine cannot advance while its child is executing.");
            }
            _executing = true;
        }

        private void EndExecution()
        {
            _executing = false;
            try
            {
                var root = Root;
                if (root._finished)
                    root.Dispose();
                else if (_finished)
                {
                    Dispose();
                    // A child's finally block may have cancelled the entire coroutine.
                    if (root._finished)
                        root.Dispose();
                }
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
        }

        private ManagedEnumerator Root
        {
            get
            {
                var root = this;
                while (root._parent != null)
                    root = root._parent;
                return root;
            }
        }

        private void Fail(Exception error)
        {
            RecordFailure(error);
            _executing = false;
            try { Root.Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Coroutine execution and cleanup failed.", error, cleanupError);
            }
        }

        private void ClearCurrent()
        {
            _current = null;
            _hasCurrent = false;
            _positioned = false;
        }

        public void Dispose()
        {
            if (_disposed && _child == null)
                return;
            // Mark the active chain before invoking user finally blocks. They may cancel again.
            var last = this;
            for (var item = this; item != null; item = item._child)
            {
                item._finished = true;
                last = item;
            }

            List<Exception> errors = null;
            // Immutable parent links preserve this chain even when cleanup changes child links.
            for (var item = last; item != _parent; item = item._parent)
            {
                // An iterator's finally blocks must not run inside its own MoveNext/Current.
                if (item._executing)
                    break;
                if (item._disposed)
                    continue;
                item._disposed = true;
                item.ClearCurrent();
                item._child = null;
                var completed = item._onCompleted;
                item._onCompleted = null;
                item._executing = true;
                try { (item._routine as IDisposable)?.Dispose(); }
                catch (Exception error)
                {
                    RecordFailure(error);
                    (errors ??= new()).Add(error);
                }
                try { completed?.Invoke(); }
                catch (Exception error)
                {
                    RecordFailure(error);
                    (errors ??= new()).Add(error);
                }
                finally { item._executing = false; }
            }
            NotifyCompletion();
            if (errors?.Count == 1)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors != null)
                throw new AggregateException("Coroutine cleanup failed.", errors);
        }

        private Il2CppObject ConvertYield(object value)
        {
            return value switch
            {
                Il2CppIEnumerator native => native.Cast<Il2CppObject>(),
                IEnumerator child => WrapChild(child),
                Il2CppObject native => native,
                string text => new Il2CppObject(IL2CPP.ManagedStringToIl2Cpp(text)),
                bool primitive => Box(primitive),
                char primitive => Box(primitive),
                sbyte primitive => Box(primitive),
                byte primitive => Box(primitive),
                short primitive => Box(primitive),
                ushort primitive => Box(primitive),
                int primitive => Box(primitive),
                uint primitive => Box(primitive),
                long primitive => Box(primitive),
                ulong primitive => Box(primitive),
                float primitive => Box(primitive),
                double primitive => Box(primitive),
                IntPtr primitive => Box(primitive),
                UIntPtr primitive => Box(primitive),
                null => null,
                _ => throw new NotSupportedException($"Type {value.GetType()} cannot be converted directly to an Il2Cpp object")
            };
        }

        private Il2CppObject WrapChild(IEnumerator routine)
        {
            for (var ancestor = this; ancestor != null; ancestor = ancestor._parent)
            {
                if (ReferenceEquals(routine, ancestor) || ReferenceEquals(routine, ancestor._routine))
                    throw new InvalidOperationException("A coroutine cannot yield itself or an active ancestor.");
            }

            _child = new ManagedEnumerator(routine, _wrapChild, this);
            return _wrapChild(_child);
        }

        private static unsafe Il2CppObject Box<T>(T value) where T : unmanaged
        {
            // Keep the value's storage valid through the native call, as the original adapter did.
            var pointer = IL2CPP.il2cpp_value_box(Il2CppClassPointerStore<T>.NativeClassPtr, (IntPtr)(&value));
            return new Il2CppObject(pointer);
        }
    }
}
