using System;
using System.Collections.Generic;
using System.Reflection;

namespace RedLoader
{
    public abstract class MelonEventBase<T> where T : Delegate
    {
        private readonly List<MelonAction<T>> actions = new();
        private MelonAction<T>[] cachedActionsArray = new MelonAction<T>[0];
        private volatile bool disposed;
        public readonly bool oneTimeUse;

        public bool Disposed => disposed;

        public MelonEventBase(bool oneTimeUse = false)
        {
            this.oneTimeUse = oneTimeUse;
        }

        public bool CheckIfSubscribed(MethodInfo method, object obj = null)
        {
            lock (actions)
            {
                return actions.Exists(x => x.del.Method == method && (obj == null || x.del.Target == obj));
            }
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
            lock (actions)
            {
                if (Disposed)
                    return;

                var acts = MelonAction<T>.Get(action, priority, unsubscribeOnFirstInvocation, canInvoke);
                foreach (var a in acts)
                {
                    if (CheckIfSubscribed(a.del.Method, a.del.Target))
                        continue;

                    var index = 0;
                    while (index < actions.Count && actions[index].priority <= a.priority)
                        index++;

                    actions.Insert(index, a);
                    // Capture this entry, not its delegate: a later replacement or a
                    // pre-existing subscription must survive registration rollback.
                    rollback?.Add(() => Unsubscribe(a));
                    UpdateEnumerator();
                }
            }
        }

        private void Unsubscribe(MelonAction<T> action)
        {
            lock (actions)
            {
                if (actions.Remove(action))
                    UpdateEnumerator();
            }
        }

        public void Unsubscribe(T action)
        {
            var delegates = action.GetInvocationList();
            lock (actions)
            {
                if (actions.RemoveAll(entry => Array.Exists(delegates,
                        del => entry.del.Method == del.Method && ReferenceEquals(entry.del.Target, del.Target))) > 0)
                    UpdateEnumerator();
            }
        }

        public void UnsubscribeAll()
        {
            lock (actions)
            {
                actions.Clear();
                UpdateEnumerator();
            }
        }

        public void Unsubscribe(MethodInfo method, object obj = null)
        {
            lock (actions)
            {
                if (Disposed)
                    return;

                if (method.IsStatic)
                    obj = null;

                if (actions.RemoveAll(entry => entry.del.Method == method && (obj == null || entry.del.Target == obj)) > 0)
                    UpdateEnumerator();
            }
        }

        private void UpdateEnumerator()
        {
            cachedActionsArray = actions.ToArray();
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
            MelonAction<T>[] snapshot;
            lock (actions)
                snapshot = cachedActionsArray;

            return Array.ConvertAll(snapshot, entry => new MelonEventSubscriber
            {
                del = entry.del,
                unsubscribeOnFirstInvocation = entry.unsubscribeOnFirstInvocation,
                priority = entry.priority
            });
        }

        protected void Invoke(Action<T> delegateInvoker)
        {
            foreach (var subscriber in BeginInvocation())
                InvokeSubscriber(subscriber, delegateInvoker);
        }

        internal MelonAction<T>[] BeginInvocation()
        {
            lock (actions)
            {
                if (Disposed)
                    return Array.Empty<MelonAction<T>>();

                var snapshot = cachedActionsArray;
                // Claim one-shot delivery before calling mod code, which may invoke
                // this event again or replace a subscription with the same delegate.
                if (oneTimeUse)
                {
                    disposed = true;
                    actions.Clear();
                    UpdateEnumerator();
                }
                else if (actions.RemoveAll(entry => entry.unsubscribeOnFirstInvocation) > 0)
                    UpdateEnumerator();

                return snapshot;
            }
        }

        internal void InvokeSubscriber(MelonAction<T> subscriber, Action<T> delegateInvoker)
        {
            try
            {
                if (subscriber.canInvoke != null && !subscriber.canInvoke())
                    return;
                delegateInvoker(subscriber.del);
            }
            catch (Exception ex)
            {
                try
                {
                    RLog.Error(ex);
                }
                catch
                {
                    // Diagnostic listeners must not stop delivery to the other mods.
                }
            }
        }

        public void Dispose()
        {
            lock (actions)
            {
                disposed = true;
                actions.Clear();
                UpdateEnumerator();
            }
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
