// SPDX-License-Identifier: LGPL-2.1-only
// Event contract adapted from RedLoader/MelonLoader's MelonEvent implementation.
// Original implementation: RedLoader and MelonLoader contributors.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RedLoader
{
    internal interface IMelonEventDriver<T> where T : Delegate
    {
        bool Disposed { get; }
        bool CheckIfSubscribed(MethodInfo method, object obj = null);
        void Subscribe(T action, int priority, bool unsubscribeOnFirstInvocation, List<Action> rollback, Func<bool> canInvoke = null);
        void Unsubscribe(T action);
        void Unsubscribe(MethodInfo method, object obj = null);
        void UnsubscribeAll();
        MelonEventBase<T>.MelonEventSubscriber[] GetSubscribers();
        void Invoke(Action<T> delegateInvoker);
        MelonAction<T>[] BeginInvocation();
        void InvokeSubscriber(MelonAction<T> subscriber, Action<T> delegateInvoker);
        void Dispose();
    }

    internal static class MelonEventDriverFactory<T> where T : Delegate
    {
        private static readonly Lazy<Func<bool, IMelonEventDriver<T>>> factory = new(BindFactory);

        internal static IMelonEventDriver<T> Create(bool oneTimeUse)
        {
            return factory.Value(oneTimeUse);
        }

        private static Func<bool, IMelonEventDriver<T>> BindFactory()
        {
            var assembly = Assembly.Load("Modstoke.Loader");
            var type = assembly.GetType("Modstoke.Loader.RedLoaderEventDispatcher", throwOnError: true);
            var method = type.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null, types: new[] { typeof(bool) }, modifiers: null)
                ?? throw new MissingMethodException(type.FullName, "Create");
            return method.MakeGenericMethod(typeof(T)).CreateDelegate<Func<bool, IMelonEventDriver<T>>>();
        }
    }
}
