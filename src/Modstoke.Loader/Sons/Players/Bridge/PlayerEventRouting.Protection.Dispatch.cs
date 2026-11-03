// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    internal static partial class PlayerEventRouting
    {
        [ThreadStatic] private static int _protectionPumpDepth;
        [ThreadStatic] private static long _protectionDispatchSequence, _protectionDispatchToken;
        [ThreadStatic] private static ProtectionLease _protectionDispatchLease;
        [ThreadStatic] private static ProtectionEvent _protectionDispatchEvent;

        internal struct ProtectionDispatchState
        {
            internal long Token, ParentToken;
            internal ProtectionLease Lease, ParentLease;
            internal ProtectionEvent Event, ParentEvent;
        }

        // Central prefix must call this even when another prefix already skipped the body.
        internal static void BeforeProtectionDispatch(Bolt.Event value, bool runOriginal,
            out ProtectionDispatchState state)
        {
            state = default;
            if (!_protectionPrepared) return;
            if (Environment.CurrentManagedThreadId != _thread)
            { FaultProtection("Native event dispatch ran outside the selected game thread."); return; }
            if (_protectionCapturing) FaultProtection("Native dispatch reentered event cutoff capture.");
            if (_protectionDispatchSequence == long.MaxValue)
            { FaultProtection("Native event dispatch scope identity exhausted."); return; }
            state = new ProtectionDispatchState
            {
                Token = ++_protectionDispatchSequence,
                ParentToken = _protectionDispatchToken,
                ParentLease = _protectionDispatchLease,
                ParentEvent = _protectionDispatchEvent,
                Lease = _protection
            };
            _protectionDispatchToken = state.Token;
            _protectionDispatchLease = state.Lease;
            _protectionDispatchEvent = null;
            var lease = state.Lease;
            if (lease == null || lease.Disposed || value == null) return;
            ProtectionEvent record = null;
            try
            {
                if (!lease.Pending.TryGetValue(value.Pointer, out record)) return;
                if (record.Dispatching)
                { lease.Fail("The same native cohort event reentered dispatch before completion."); return; }
                // Keep the record pending through every nested callback and the finalizer.
                state.Event = record;
                record.Dispatching = true;
                if (!runOriginal || record.Ordinal != lease.NextOrdinal
                    || !ProtectionQueueMatches(lease.QueuePointer) || !record.Matches(value))
                {
                    lease.Fail("A native cohort dispatch was skipped, reordered or changed identity before its body.");
                    return;
                }
                lease.NextOrdinal++;
                _protectionDispatchEvent = record;
            }
            catch (Exception error)
            { lease.Fail("Native cohort dispatch identity is unreadable (" + error.GetType().Name + ")."); }
            finally
            {
                // Native Dispatch's argument now owns the object's lifetime. Its body can
                // FreeStorage without clearing storage; no owned wrapper/handle survives it.
                if (ReferenceEquals(state.Event, record)) record?.ReleaseRoot();
            }
        }

        internal static void FinishProtectionDispatch(ProtectionDispatchState state, bool runOriginal,
            Exception error)
        {
            if (state.Token == 0) return;
            if (Environment.CurrentManagedThreadId != _thread || _protectionDispatchToken != state.Token)
            { FaultProtection("Native event finalizers did not unwind in exact scope order."); return; }
            try
            {
                if (state.Event != null)
                {
                    // Never dereference the event, its native handle or its payload here.
                    if (!runOriginal || error != null)
                        state.Lease.Fail("A captured native dispatch did not complete its original body normally.");
                    state.Lease.Pending.Remove(state.Event.Pointer);
                    state.Event.Dispatching = false;
                }
            }
            finally
            {
                _protectionDispatchToken = state.ParentToken;
                _protectionDispatchLease = state.ParentLease;
                _protectionDispatchEvent = state.ParentEvent;
            }
        }

        private static void BeforeProtectionPump(out bool __state)
        {
            __state = false;
            if (!_protectionPrepared) return;
            if (Environment.CurrentManagedThreadId != _thread)
            { FaultProtection("The native event pump ran outside the selected game thread."); return; }
            if (_protectionCapturing) FaultProtection("The native event pump reentered cutoff capture.");
            _protectionPumpDepth++;
            __state = true;
        }

        private static void FinishProtectionPump(bool __state, Exception __exception)
        {
            if (!__state) return;
            if (__exception != null) _protection?.Fail("Native event pump did not complete normally.");
            _protectionPumpDepth--;
        }

        private static void FaultProtection(string detail)
            => System.Threading.Interlocked.CompareExchange(ref _protectionFault, detail, null);
    }
}
