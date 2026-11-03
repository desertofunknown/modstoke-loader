// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Modstoke.Loader;

namespace Modstoke.Sons
{
    /// <summary>
    /// Server policy before native ChangeIdObjectState routing. Does not cover host direct updates,
    /// save restoration, or native event completion; rejection cannot undo sender-local state.
    /// </summary>
    public static class ServerNamedStates
    {
        private const int MaxRegistryEntries = 4096;
        private const int MaxNameLength = 256;
        private static ServerNamedStateState _state = new(NamedStateHookStatus.NotAttempted,
            "Named-state request hooks have not been installed.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static bool _publishing;
        [ThreadStatic] private static HashSet<IntPtr> _dispatching;

        /// <summary>
        /// Runs synchronously before server dispatch, including routes that would forward to clients.
        /// Required unresolved context, rejection, or handler exceptions skip dispatch when subscribed.
        /// No subscribers leaves native behavior unchanged. This is not state-completion notification.
        /// </summary>
        public static event Action<NamedStateRequest> Changing;

        public static ServerNamedStateState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                PublishState(NamedStateHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                return;
            }
            _ownerThread = Environment.CurrentManagedThreadId;
            PublishState(NamedStateHookStatus.Installing, "Registering named-state dispatch policy.");
            try
            {
                if (Il2CppClassPointerStore<ChangeIdObjectState>.NativeClassPtr == IntPtr.Zero)
                    throw new InvalidOperationException("The native ChangeIdObjectState class is unavailable.");
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-named-states");
                var method = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) }) ?? throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(ServerNamedStates), nameof(BeforeDispatch))
                    { priority = Priority.Last });
            }
            catch (Exception error)
            {
                PublishState(NamedStateHookStatus.Failed, "Named-state registration failed; hooks remain inactive.");
                addStartupWarning?.Invoke("Named-state request hooks failed: " + error);
                return;
            }
            PublishState(NamedStateHookStatus.Installed,
                "Named-state dispatch policy registered. Native operation requires qualification.");
        }

        /// <summary>
        /// Explicit game-thread, server-only read of up to 4096 registry entries with unique ordinal
        /// names of 1..256 UTF-16 characters. Copies metadata only; indices are not durable identity,
        /// and registered names do not prove live components, doors, keycards, or valid state values.
        /// </summary>
        public static bool TryReadRegistry(out NamedStateRegistrySnapshot snapshot,
            out NamedStateReadStatus status)
        {
            snapshot = null;
            status = NamedStateReadStatus.Unavailable;
            if (State.Status != NamedStateHookStatus.Installed) return false;
            if (Environment.CurrentManagedThreadId != _ownerThread)
            {
                status = NamedStateReadStatus.WrongThread;
                return false;
            }
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer)
                {
                    status = NamedStateReadStatus.NotServer;
                    return false;
                }
                var registry = global::Sons.Multiplayer.IdObject.NamedKeys;
                if (registry == null) return false;
                status = NamedStateReadStatus.InvalidRegistry;
                int count = registry.Length;
                if (count > MaxRegistryEntries) return false;
                var entries = new NamedStateDescriptor[count];
                var names = new HashSet<string>(StringComparer.Ordinal);
                for (int index = 0; index < count; index++)
                {
                    string name = registry[index];
                    if (!IsValidName(name) || !names.Add(name)) return false;
                    entries[index] = new NamedStateDescriptor(index, name);
                }
                snapshot = new NamedStateRegistrySnapshot(entries, DateTimeOffset.UtcNow);
                status = NamedStateReadStatus.Success;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                status = NamedStateReadStatus.InvalidRegistry;
                return false;
            }
        }

        private static bool BeforeDispatch(Bolt.Event __0, bool __runOriginal)
        {
            // Another prefix owns its rejection and storage cleanup.
            if (!__runOriginal) return false;
            var handlers = Changing;
            if (handlers == null || State.Status != NamedStateHookStatus.Installed || __0 == null) return true;
            if (IL2CPP.il2cpp_object_get_class(__0.Pointer) != Il2CppClassPointerStore<ChangeIdObjectState>.NativeClassPtr)
                return true;
            var change = __0.Cast<ChangeIdObjectState>();
            var active = _dispatching ??= new HashSet<IntPtr>();
            var pointer = __0.Pointer;
            if (!active.Add(pointer)) return false; // The outer dispatch owns storage.
            bool forward = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) { forward = true; return true; }
                if (!OnOwnerThread() || _publishing) return false;
                var connection = __0.RaisedBy;
                var session = ServerSessions.ResolveSource(connection);
                if (connection != null && (session == null || !session.IsCurrent)) return false;
                int index = change.Id;
                if (!TryReadName(index, out var name)) return false;
                var request = new NamedStateRequest(index, change.StateValue, name,
                    connection == null ? NamedStateOrigin.ServerLocalDispatch : NamedStateOrigin.RemoteDispatch,
                    session, __0.Targets, DateTimeOffset.UtcNow);
                if (!Publish(request, handlers)) return false;
                // Callbacks may change membership or the registry. Revalidate the exact decision context.
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer
                    || !TryReadName(index, out var currentName) || !string.Equals(name, currentName, StringComparison.Ordinal)
                    || change.Id != request.RequestedIndex || change.StateValue != request.RequestedValue
                    || __0.Targets != request.RoutingTargets) return false;
                var currentConnection = __0.RaisedBy;
                if (connection == null)
                {
                    if (currentConnection != null) return false;
                }
                else if (currentConnection == null || currentConnection.Pointer != connection.Pointer
                    || !ReferenceEquals(session, ServerSessions.ResolveSource(currentConnection))
                    || !session.IsCurrent) return false;
                forward = true;
                return true;
            }
            catch (Exception error) { ReportFailure(error); return false; }
            finally
            {
                if (!forward)
                {
                    // Native channels/listeners never take ownership when this prefix rejects dispatch.
                    try { __0.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error); }
                }
                active.Remove(pointer);
            }
        }

        private static bool TryReadName(int index, out string name)
        {
            name = null;
            var registry = global::Sons.Multiplayer.IdObject.NamedKeys;
            if (registry == null || registry.Length > MaxRegistryEntries || index < 0 || index >= registry.Length)
                return false;
            string value = registry[index];
            if (!IsValidName(value)) return false;
            // The array indexer already marshals a fresh managed copy.
            name = value;
            return true;
        }

        private static bool IsValidName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > MaxNameLength) return false;
            foreach (char value in name)
            {
                if (char.IsControl(value) || char.IsSurrogate(value) || value == '<' || value == '>') return false;
                var category = char.GetUnicodeCategory(value);
                if (category == UnicodeCategory.Format || category == UnicodeCategory.LineSeparator
                    || category == UnicodeCategory.ParagraphSeparator) return false;
            }
            return true;
        }

        private static bool Publish(NamedStateRequest request, Action<NamedStateRequest> handlers)
        {
            if (_publishing) { request.Close(); return false; }
            _publishing = true;
            try
            {
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected) return false;
                }
                return true;
            }
            catch (Exception error) { ReportFailure(error); return false; }
            finally { request.Close(); _publishing = false; }
        }

        private static bool OnOwnerThread()
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) return true;
            ReportFailure(new InvalidOperationException("Named-state callback is outside the SDK game thread."));
            return false;
        }

        private static void PublishState(NamedStateHookStatus status, string detail) =>
            Volatile.Write(ref _state, new ServerNamedStateState(status, detail));

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Named-state policy denied an operation (" + error.GetType().FullName + "). Further failures will not be logged."); }
            catch (Exception) { }
        }
    }
}
