// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using UnityEngine.InputSystem;

namespace Modstoke.Sons
{
    /// <summary>Policy at native printer event routing and local laptop interaction boundaries.</summary>
    public static class ServerPrinters
    {
        private static ServerPrinterState _state = new(PrinterHookStatus.NotAttempted,
            "Printer request hooks have not been installed.");
        private static int _installationAttempted;
        private static int _ownerThread;
        private static int _reportedFailure;
        [ThreadStatic] private static bool _publishing;
        [ThreadStatic] private static HashSet<IntPtr> _dispatching;

        /// <summary>
        /// Runs synchronously before server dispatch of recipe/start events, local StartPrint(true), or
        /// the local recipe-cycle callback. These are separate boundaries, not completed production.
        /// Any rejection or handler exception skips that invocation. No subscribers leaves native behavior.
        /// </summary>
        public static event Action<PrinterRequest> Receiving;

        public static ServerPrinterState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0) return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                PublishState(PrinterHookStatus.Disabled, support?.Detail ?? "Harmony support is unavailable.");
                return;
            }
            _ownerThread = Environment.CurrentManagedThreadId;
            PublishState(PrinterHookStatus.Installing, "Registering printer request boundaries.");
            try
            {
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-printers");
                Patch(typeof(Resin3dPrinterLaptop), nameof(Resin3dPrinterLaptop.StartPrint), new[] { typeof(bool) }, nameof(BeforeLocalStart));
                Patch(typeof(Resin3dPrinterLaptop), nameof(Resin3dPrinterLaptop.OnCycle3dPrints),
                    new[] { typeof(InputAction.CallbackContext) }, nameof(BeforeLocalCycle));
                Patch(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch), new[] { typeof(Bolt.Event) }, nameof(BeforeDispatch));
                Patch(typeof(Bolt.Event), nameof(Bolt.Event.Send), Type.EmptyTypes, nameof(BeforeSend));

                void Patch(Type type, string name, Type[] arguments, string prefix)
                {
                    var method = AccessTools.Method(type, name, arguments) ?? throw new MissingMethodException(type.FullName, name);
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(ServerPrinters), prefix) { priority = Priority.Last });
                }
            }
            catch (Exception error)
            {
                PublishState(PrinterHookStatus.Failed, "Printer registration failed; partial hooks remain inactive.");
                addStartupWarning?.Invoke("Printer request hooks failed: " + error);
                return;
            }
            PublishState(PrinterHookStatus.Installed,
                "Printer request hooks registered. Native operation and dedicated scene availability require qualification.");
        }

        private static bool BeforeDispatch(Bolt.Event __0, bool __runOriginal)
        {
            // Another prefix owns its rejection and storage cleanup. Never free that event again.
            if (!__runOriginal) { PrinterDevices.ForgetDispatch(__0); return false; }
            var handlers = Receiving;
            if (State.Status != PrinterHookStatus.Installed || __0 == null) return true;
            if (handlers == null && !PrinterDevices.HasPendingDispatch(__0)) return true;
            var recipe = __0.TryCast<Resin3dPrinterSetRecipeIdEvent>();
            var start = recipe == null ? __0.TryCast<Resin3dPrinterStartPrintEvent>() : null;
            if (recipe == null && start == null) return true;
            var active = _dispatching ??= new HashSet<IntPtr>();
            if (!active.Add(__0.Pointer)) return false; // The outer invocation still owns this event's storage.
            bool forward = false;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) { forward = true; return true; }
                if (!OnOwnerThread() || _publishing) return false;
                if (PrinterDevices.TryTakeDispatch(__0, out var attributed, out var stillAuthorized))
                {
                    forward = attributed != null && Publish(attributed, handlers) && stillAuthorized();
                    return forward;
                }
                if (handlers == null) { forward = true; return true; }
                var connection = __0.RaisedBy;
                var session = ServerSessions.ResolveSource(connection);
                if (connection != null && (session == null || !session.IsCurrent)) return false;
                var request = new PrinterRequest(
                    recipe != null ? PrinterOperation.RecipeSelection : PrinterOperation.PrintStart,
                    connection == null ? PrinterOrigin.ServerLocalDispatch : PrinterOrigin.RemoteDispatch,
                    session, recipe != null ? recipe.GetTargetNetworkIdValue().PackedValue : start.GetTargetNetworkIdValue().PackedValue,
                    recipe != null ? recipe.RecipeId : null, __0.Targets, DateTimeOffset.UtcNow);
                forward = Publish(request, handlers);
                return forward;
            }
            catch (Exception error) { ReportFailure(error); return false; }
            finally
            {
                if (!forward)
                {
                    // Retire attribution when this boundary owns rejection; the native queue cannot use it again.
                    if (Environment.CurrentManagedThreadId == _ownerThread)
                    {
                        try { PrinterDevices.ForgetDispatch(__0); }
                        catch (Exception error) { ReportFailure(error); }
                    }
                    // Skipping dispatch prevents native channels/listeners from taking ownership of storage.
                    try { __0.FreeStorage(); }
                    catch (Exception error) { ReportFailure(error); }
                }
                active.Remove(__0.Pointer);
            }
        }

        private static bool BeforeLocalStart(Resin3dPrinterLaptop __instance, bool __0, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            // Native network delivery uses false and is gated before Dispatch. Save restoration uses
            // Resin3dPrinter.StartPrint(float), which is deliberately outside this interaction boundary.
            return !__0 || BeforeLocalInteraction(__instance, PrinterOperation.PrintStart);
        }

        private static bool BeforeLocalCycle(Resin3dPrinterLaptop __instance, bool __runOriginal) =>
            __runOriginal && BeforeLocalInteraction(__instance, PrinterOperation.LocalRecipeCycle);

        private static bool BeforeLocalInteraction(Resin3dPrinterLaptop laptop, PrinterOperation operation)
        {
            var handlers = Receiving;
            if (State.Status != PrinterHookStatus.Installed) return true;
            if (handlers == null && !PrinterDevices.HasActiveInvocation) return true;
            try
            {
                if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
                if (!OnOwnerThread() || _publishing || laptop == null || laptop.WasCollected || !laptop) return false;
                var entity = laptop.entity;
                ulong id = entity != null && !entity.WasCollected && entity && entity.isAttached ? entity.networkId.PackedValue : 0;
                if (PrinterDevices.TryReadInvocation(laptop, operation, out var attributed))
                    return PrinterDevices.FinishInvocationAuthorization(attributed != null && Publish(attributed, handlers));
                if (handlers == null) return true;
                var request = new PrinterRequest(operation, PrinterOrigin.ServerLocalInteraction,
                    null, id, null, null, DateTimeOffset.UtcNow);
                // A callback or updateNetwork=true invocation is not proof of human host input.
                return Publish(request, handlers);
            }
            catch (Exception error) { ReportFailure(error); return false; }
        }

        private static bool Publish(PrinterRequest request, Action<PrinterRequest> handlers)
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
                return (request.SourceSession == null || request.SourceSession.IsCurrent)
                    && (request.SourceHostPlayer == null || request.SourceHostPlayer.IsCurrent);
            }
            catch (Exception error) { ReportFailure(error); return false; }
            finally { request.Close(); _publishing = false; }
        }

        internal static bool AuthorizeDeviceCommand(PrinterRequest request) =>
            request != null && State.Status == PrinterHookStatus.Installed
            && OnOwnerThread() && Publish(request, Receiving);

        private static bool BeforeSend(Bolt.Event __instance, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (State.Status != PrinterHookStatus.Installed || PrinterDevices.ObserveSend(__instance)) return true;
            // This event was never queued; native receivers cannot take ownership of its storage.
            try { __instance.FreeStorage(); }
            catch (Exception error) { ReportFailure(error); }
            return false;
        }

        private static bool OnOwnerThread()
        {
            if (Environment.CurrentManagedThreadId == _ownerThread) return true;
            ReportFailure(new InvalidOperationException("Printer callback is outside the SDK game thread."));
            return false;
        }

        private static void PublishState(PrinterHookStatus status, string detail) =>
            Volatile.Write(ref _state, new ServerPrinterState(status, detail));

        private static void ReportFailure(Exception error)
        {
            if (Interlocked.Exchange(ref _reportedFailure, 1) != 0) return;
            try { RuntimeEntryPoint.LogSource?.LogWarning("Printer policy denied an operation (" + error.GetType().FullName + "). Further failures will not be logged."); }
            catch (Exception) { }
        }
    }
}
