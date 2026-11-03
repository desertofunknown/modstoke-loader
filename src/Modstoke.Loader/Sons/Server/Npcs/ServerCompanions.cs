// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>Policy for remote Kelvin interaction requests before native dispatch.</summary>
    public static partial class ServerCompanions
    {
        private static ServerCompanionState _state = new(CompanionHookStatus.NotAttempted,
            "Companion request hooks have not been installed.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static bool _publishing;
        [ThreadStatic] private static HashSet<IntPtr> _dispatching;

        /// <summary>
        /// Synchronous remote RobbyOrderEvent policy. No subscribers leaves native behavior unchanged.
        /// Subscribers may reject or require native request validation. Local host calls, server-created
        /// notifications and direct component calls are outside this boundary.
        /// </summary>
        public static event Action<CompanionRequest> Receiving;
        public static ServerCompanionState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                PublishState(CompanionHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                return;
            }
            _ownerThread = Environment.CurrentManagedThreadId;
            PublishState(CompanionHookStatus.Installing, "Registering the remote companion request boundary.");
            try
            {
                var method = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) }) ?? throw new MissingMethodException("Bolt.EventDispatcher", "Dispatch(Event)");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-companions");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ServerCompanions), nameof(BeforeDispatch))
                { priority = Priority.Last });
            }
            catch (Exception error)
            {
                PublishState(CompanionHookStatus.Failed, "Companion registration failed; partial hooks remain inactive.");
                addStartupWarning?.Invoke("Companion request hook failed: " + error);
                return;
            }
            PublishState(CompanionHookStatus.Installed,
                "Remote companion request hook registered. Multiplayer behavior requires qualification; this is not a job queue.");
        }

        private static bool BeforeDispatch(Bolt.Event __0, bool __runOriginal)
        {
            if (!__runOriginal) return false; // The earlier rejecting prefix owns cleanup.
            var handlers = Receiving;
            if (handlers == null || State.Status != CompanionHookStatus.Installed || __0 == null) return true;
            var evnt = __0.TryCast<RobbyOrderEvent>();
            if (evnt == null) return true;
            var active = _dispatching ??= new HashSet<IntPtr>();
            var pointer = __0.Pointer;
            if (!active.Add(pointer)) return false; // Only the outer dispatch owns this storage.
            bool forward = false;
            CompanionRequest request = null;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) { forward = true; return true; }
                var source = evnt.RaisedBy;
                // Native server broadcasts use this same event. Missing source never identifies a human host.
                if (source == null) { forward = true; return true; }
                if (Environment.CurrentManagedThreadId != _ownerThread || _publishing) return false;
                var context = new RequestContext(evnt, source, ServerSessions.ResolveSource(source));
                request = context.Request;
                _publishing = true;
                try
                {
                    foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                    {
                        handler(request);
                        if (request.IsRejected) return false;
                    }
                    if (request.ValidationRequired && !request.PassesRequiredValidation()) return false;
                    forward = true;
                    return true;
                }
                finally { _publishing = false; }
            }
            catch (Exception error) { request?.Reject(); ReportFailure(error, "The dispatch was denied."); return false; }
            finally
            {
                request?.Close();
                if (!forward)
                {
                    try { evnt.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error, "The rejected event's storage was not released."); }
                }
                active.Remove(pointer);
            }
        }

        private static void PublishState(CompanionHookStatus status, string detail) =>
            Volatile.Write(ref _state, new ServerCompanionState(status, detail));

        private static void ReportFailure(Exception error, string outcome)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Companion request policy failed (" + error.GetType().FullName
                + "). " + outcome + " Further failures will not be logged."); }
            catch (Exception) { }
        }
    }
}
