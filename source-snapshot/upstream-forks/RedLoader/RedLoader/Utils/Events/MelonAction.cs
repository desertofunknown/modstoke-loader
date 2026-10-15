using System;
using System.Collections.Generic;

namespace RedLoader
{
    internal class MelonAction<T> where T : Delegate
    {
        internal readonly T del;
        internal readonly bool unsubscribeOnFirstInvocation;
        internal readonly int priority;
        internal readonly Func<bool> canInvoke;

        //internal readonly MelonAssembly melonAssembly;

        private MelonAction(T singleDel, int priority, bool unsubscribeOnFirstInvocation, Func<bool> canInvoke)
        {
            del = singleDel;
            //melonAssembly = MelonAssembly.GetMelonAssemblyOfMember(del.Method, del.Target);
            this.priority = priority;
            this.unsubscribeOnFirstInvocation = unsubscribeOnFirstInvocation;
            this.canInvoke = canInvoke;
        }

        internal static List<MelonAction<T>> Get(T del, int priority = 0, bool unsubscribeOnFirstInvocation = false, Func<bool> canInvoke = null)
        {
            var mets = del.GetInvocationList();
            var result = new List<MelonAction<T>>();
            foreach (var met in mets)
            {
                // if (met.Target != null && met.Target is ModBase melon && !melon.Registered)
                //     continue;

                result.Add(new MelonAction<T>((T)met, priority, unsubscribeOnFirstInvocation, canInvoke));
            }
            return result;
        }
    }
}
