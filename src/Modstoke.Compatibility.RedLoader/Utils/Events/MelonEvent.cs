// SPDX-License-Identifier: LGPL-2.1-only
// Event facade adapted from RedLoader/MelonLoader's MelonEvent implementation.
// Original implementation: RedLoader and MelonLoader contributors.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace RedLoader
{
    public abstract class MelonEventBase<T> where T : Delegate
    {
        private readonly Lazy<IMelonEventDriver<T>> driver;
        public readonly bool oneTimeUse;

        public bool Disposed => driver.Value.Disposed;

        public MelonEventBase(bool oneTimeUse = false)
        {
            this.oneTimeUse = oneTimeUse;
            driver = new Lazy<IMelonEventDriver<T>>(() => MelonEventDriverFactory<T>.Create(oneTimeUse));
        }

        public bool CheckIfSubscribed(MethodInfo method, object obj = null)
        {
            return driver.Value.CheckIfSubscribed(method, obj);
        }

        /// <summary>
        /// Subscribe to the event
        /// </summary>
        /// <param name="action">Callback for the event</param>
        /// <param name="priority">Order of the callback, higher will be called after lower priorities</param>
        /// <param name="unsubscribeOnFirstInvocation">Automatically unsubscribe after being called once</param>
        public void Subscribe(T action, int priority = 0, bool unsubscribeOnFirstInvocation = false)
        {
            Subscribe(action, priority, unsubscribeOnFirstInvocation, null);
        }

        internal void Subscribe(T action, int priority, bool unsubscribeOnFirstInvocation, List<Action> rollback, Func<bool> canInvoke = null)
        {
            driver.Value.Subscribe(action, priority, unsubscribeOnFirstInvocation, rollback, canInvoke);
        }

        public void Unsubscribe(T action)
        {
            driver.Value.Unsubscribe(action);
        }

        public void UnsubscribeAll()
        {
            driver.Value.UnsubscribeAll();
        }

        public void Unsubscribe(MethodInfo method, object obj = null)
        {
            driver.Value.Unsubscribe(method, obj);
        }

        public class MelonEventSubscriber
        {
            public T del;
            public bool unsubscribeOnFirstInvocation;
            public int priority;
            //public MelonAssembly melonAssembly;
        }
        public MelonEventSubscriber[] GetSubscribers()
        {
            return driver.Value.GetSubscribers();
        }

        protected void Invoke(Action<T> delegateInvoker)
        {
            driver.Value.Invoke(delegateInvoker);
        }

        internal MelonAction<T>[] BeginInvocation()
        {
            return driver.Value.BeginInvocation();
        }

        internal void InvokeSubscriber(MelonAction<T> subscriber, Action<T> delegateInvoker)
        {
            driver.Value.InvokeSubscriber(subscriber, delegateInvoker);
        }

        public void Dispose()
        {
            driver.Value.Dispose();
        }
    }

    #region Param Children
    public class MelonEvent : MelonEventBase<LemonAction>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke()
        {
            Invoke(x => x());
        }
    }
    public class MelonEvent<T1> : MelonEventBase<LemonAction<T1>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke(T1 arg1)
        {
            Invoke(x => x(arg1));
        }
    }
    public class MelonEvent<T1, T2> : MelonEventBase<LemonAction<T1, T2>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke(T1 arg1, T2 arg2)
        {
            Invoke(x => x(arg1, arg2));
        }
    }
    public class MelonEvent<T1, T2, T3> : MelonEventBase<LemonAction<T1, T2, T3>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3)
        {
            Invoke(x => x(arg1, arg2, arg3));
        }
    }
    public class MelonEvent<T1, T2, T3, T4> : MelonEventBase<LemonAction<T1, T2, T3, T4>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4)
        {
            Invoke(x => x(arg1, arg2, arg3, arg4));
        }
    }
    public class MelonEvent<T1, T2, T3, T4, T5> : MelonEventBase<LemonAction<T1, T2, T3, T4, T5>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }

        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5)
        {
            Invoke(x => x(arg1, arg2, arg3, arg4, arg5));
        }
    }
    public class MelonEvent<T1, T2, T3, T4, T5, T6> : MelonEventBase<LemonAction<T1, T2, T3, T4, T5, T6>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }
        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6)
        {
            Invoke(x => x(arg1, arg2, arg3, arg4, arg5, arg6));
        }
    }
    public class MelonEvent<T1, T2, T3, T4, T5, T6, T7> : MelonEventBase<LemonAction<T1, T2, T3, T4, T5, T6, T7>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }
        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7)
        {
            Invoke(x => x(arg1, arg2, arg3, arg4, arg5, arg6, arg7));
        }
    }
    public class MelonEvent<T1, T2, T3, T4, T5, T6, T7, T8> : MelonEventBase<LemonAction<T1, T2, T3, T4, T5, T6, T7, T8>>
    {
        public MelonEvent(bool oneTimeUse = false) : base(oneTimeUse) { }
        public void Invoke(T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8)
        {
            Invoke(x => x(arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8));
        }
    }
    #endregion
}
