// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Reflection;
using System.Threading;
using Bolt;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Optional observation of Bolt entity control and detach calls; never a cancellation policy.</summary>
    public static class BoltEntities
    {
        public const string ServiceKey = "openacai.sons.boltEntities";
        private static readonly object HandlerLock = new();
        private static Action<BoltEntityChange>[] _handlers = Array.Empty<Action<BoltEntityChange>>();
        private static BoltEntityHookState _state = new(BoltEntityHookStatus.NotAttempted,
            "Entity lifecycle observation has not been registered.");
        private static int _installationAttempted;
        private static int _reportedFailure;
        private static int _attachedMask;
        private static int _controlMask;
        [ThreadStatic] private static ObservationScope _current;
        [ThreadStatic] private static bool _notifying;

        /// <summary>
        /// Synchronous completion observations, including unchanged calls and failures. Unsubscribe on unload.
        /// Calls already skipped before this prefix are not observed; later skips report Skipped.
        /// No subscribers means no snapshots or per-call allocations. Observer-triggered calls still run,
        /// but their notifications are suppressed. Handlers cannot cancel calls and their exceptions are isolated.
        /// </summary>
        public static event Action<BoltEntityChange> Changed
        {
            add { if (value != null) ChangeHandlers(value, true); }
            remove { if (value != null) ChangeHandlers(value, false); }
        }

        public static BoltEntityHookState State => Volatile.Read(ref _state);

        private static void ChangeHandlers(Action<BoltEntityChange> value, bool add)
        {
            lock (HandlerLock)
            {
                Action<BoltEntityChange> combined = null;
                foreach (var handler in _handlers) combined += handler;
                if (add) combined += value;
                else combined -= value;
                var invocation = combined?.GetInvocationList();
                var next = invocation == null ? Array.Empty<Action<BoltEntityChange>>()
                    : new Action<BoltEntityChange>[invocation.Length];
                for (int index = 0; index < next.Length; index++)
                    next[index] = (Action<BoltEntityChange>)invocation[index];
                Volatile.Write(ref _handlers, next);
            }
        }

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            HarmonyLib.Harmony harmony = null;
            try
            {
                if (support == null || !support.SafeForHarmonyPatches || !GameBuild.SupportsServerCore)
                {
                    SetState(BoltEntityHookStatus.Disabled,
                        "Entity lifecycle observation requires Harmony support and a recognized game build.");
                    return;
                }
                _attachedMask = EntityFlags.ATTACHED.bits;
                _controlMask = EntityFlags.HAS_CONTROL.bits;
                harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-bolt-entities");
                var token = new[] { typeof(IProtocolToken) };
                Patch(nameof(Entity.AssignControl), new[] { typeof(BoltConnection), typeof(IProtocolToken) });
                Patch(nameof(Entity.TakeControl), token);
                Patch(nameof(Entity.ReleaseControl), token);
                Patch(nameof(Entity.RevokeControl), token);
                Patch(nameof(Entity.Detach), Type.EmptyTypes);
                Patch(nameof(Entity.TakeControlInternal), token);
                Patch(nameof(Entity.ReleaseControlInternal), token);
                SetState(BoltEntityHookStatus.Installed,
                    "Seven control/detach boundaries registered; native delivery requires multiplayer qualification.");

                void Patch(string name, Type[] parameters)
                {
                    var target = AccessTools.DeclaredMethod(typeof(Entity), name, parameters)
                        ?? throw new MissingMethodException("Bolt.Entity." + name);
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(BoltEntities), nameof(BeforeOperation)),
                        postfix: new HarmonyMethod(typeof(BoltEntities), nameof(AfterOperation)),
                        finalizer: new HarmonyMethod(typeof(BoltEntities), nameof(FinishOperation)));
                }
            }
            catch (Exception error)
            {
                // Even if rollback fails, residual patches remain inert unless installation succeeded.
                Volatile.Write(ref _state, new(BoltEntityHookStatus.Failed,
                    "Entity lifecycle observation registration failed; callbacks are inactive."));
                try { harmony?.UnpatchSelf(); }
                catch (Exception cleanup) { Warn("Entity lifecycle unpatch failed: " + cleanup); }
                SetState(BoltEntityHookStatus.Failed, State.Detail);
                Warn("Entity lifecycle observation registration failed: " + error);
            }

            void Warn(string message)
            {
                try { warning?.Invoke(message); }
                catch (Exception) { }
            }
        }

        private static void BeforeOperation(Entity __instance, MethodBase __originalMethod, bool __runOriginal,
            out ObservationScope __state)
        {
            __state = null;
            var handlers = Volatile.Read(ref _handlers);
            if (handlers.Length == 0 || _notifying || !__runOriginal
                || State.Status != BoltEntityHookStatus.Installed) return;
            try
            {
                __state = new ObservationScope(__originalMethod.Name, handlers, _current);
                _current = __state;
                __state.Before = Capture(__instance);
            }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void AfterOperation(Entity __instance, bool __runOriginal, ObservationScope __state)
        {
            if (__state == null) return;
            __state.Returned = __runOriginal;
            try { if (__runOriginal) __state.After = Capture(__instance); }
            catch (Exception error) { ReportFailure(error); }
        }

        // A void finalizer observes exceptions without replacing or suppressing them.
        private static void FinishOperation(Exception __exception, ObservationScope __state)
        {
            if (__state == null || __state.Completed) return;
            __state.Completed = true;
            _notifying = true;
            try
            {
                var outcome = __exception != null ? BoltEntityOutcome.Faulted
                    : !__state.Returned ? BoltEntityOutcome.Skipped
                    : __state.Before == null || __state.After == null ? BoltEntityOutcome.ReturnedSnapshotUnavailable
                    : __state.Before == __state.After ? BoltEntityOutcome.ReturnedUnchanged
                    : BoltEntityOutcome.ReturnedChanged;
                var change = new BoltEntityChange(__state.Operation, __state.Depth,
                    __state.Parent?.Operation, outcome, __state.Before, __state.After,
                    __state.Returned && __state.Operation == nameof(Entity.Detach), __exception?.GetType().FullName);
                foreach (var handler in __state.Handlers)
                {
                    try { handler(change); }
                    catch (Exception error) { ReportFailure(error); }
                }
            }
            catch (Exception error) { ReportFailure(error); }
            finally
            {
                _current = __state.Parent;
                _notifying = false;
            }
        }

        private static BoltEntitySnapshot Capture(Entity entity)
        {
            // Read the retained Bolt.Entity backing fields, never detached BoltEntity getters.
            // The native HasControl/IsAttached getters are these same flag-mask reads.
            int flags = entity.Flags.bits;
            return new(entity.NetworkId.PackedValue, entity.PrefabId.Value, entity.IsOwner,
                (flags & _controlMask) != 0, (flags & _attachedMask) != 0,
                entity.Controller?.ConnectionId, entity.Source?.ConnectionId);
        }

        private static void SetState(BoltEntityHookStatus status, string detail)
        {
            var state = new BoltEntityHookState(status, detail);
            Volatile.Write(ref _state, state);
            try { Modding.SetService(ServiceKey, state); }
            catch (Exception error) { ReportFailure(error); }
        }

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Entity lifecycle observation could not complete ("
                + error.GetType().FullName + "). Further observation failures will not be logged."); }
            catch (Exception) { }
        }

        private sealed class ObservationScope(string operation, Action<BoltEntityChange>[] handlers,
            ObservationScope parent)
        {
            internal readonly string Operation = operation;
            internal readonly Action<BoltEntityChange>[] Handlers = handlers;
            internal readonly ObservationScope Parent = parent;
            internal readonly int Depth = parent == null ? 0 : parent.Depth + 1;
            internal BoltEntitySnapshot Before;
            internal BoltEntitySnapshot After;
            internal bool Returned;
            internal bool Completed;
        }
    }

    public enum BoltEntityHookStatus { NotAttempted, Disabled, Installed, Failed }

    public sealed record BoltEntityHookState(BoltEntityHookStatus Status, string Detail);

    public enum BoltEntityOutcome
    {
        ReturnedUnchanged, ReturnedChanged, ReturnedSnapshotUnavailable, Skipped, Faulted
    }

    /// <summary>
    /// Copied entity state, not an authority grant. Server role is not ownership. Controller and source
    /// IDs identify current connections only, not a requester or assignment target; IDs can be reused.
    /// </summary>
    public sealed record BoltEntitySnapshot(ulong NetworkId, int PrefabId, bool IsOwner,
        bool HasControl, bool IsAttached, uint? ControllerConnectionId, uint? SourceConnectionId);

    /// <summary>
    /// Nested calls report separately in completion order; depth zero is an outer observed operation.
    /// Changed/unchanged compares only the copied fields, not tokens or every native side effect.
    /// Before/After are null when unavailable. After is captured only at the normal-return postfix;
    /// later patches can still fault. ExceptionType describes the patched invocation, including other patches.
    /// DetachReturned records that postfix observation, not proof of destruction or successful later patches.
    /// </summary>
    public sealed record BoltEntityChange(string Operation, int NestingDepth, string ParentOperation,
        BoltEntityOutcome Outcome, BoltEntitySnapshot Before,
        BoltEntitySnapshot After, bool DetachReturned, string ExceptionType);
}
