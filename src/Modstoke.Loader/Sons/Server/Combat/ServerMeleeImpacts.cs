// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Threading;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Weapon;
using UnityEngine;
using MeleeWeapon = global::Sons.Gameplay.MeleeWeapon;

namespace Modstoke.Sons;

/// <summary>Policy before qualified server-owned NPC melee contacts enter a direct damage node.</summary>
public static partial class ServerMeleeImpacts
{
    public const string ServiceKey = "openacai.sons.serverMeleeImpacts";
    public const int MaximumImpactsPerFrame = 128;
    private const int MaximumContactDepth = 8;
    private static int _attempted, _thread, _reported;
    private static int _budgetFrame;
    private static int _impactReads;
    private static WorldRecordContext _budgetWorld;
    private static MeleeImpactHookState _state = new(MeleeImpactHookStatus.NotAttempted,
        "NPC melee contact hooks have not been installed.");
    [ThreadStatic] private static ContactScope _contact;
    [ThreadStatic] private static int _contactDepth;
    [ThreadStatic] private static MeleeImpactRequest _dispatching;

    /// <summary>
    /// Runs before direct DamageNode.OnImpact mutation inside MeleeWeapon.OnTriggerEnter.
    /// Only qualified server-owned sources are reported. Unqualified contacts, exhausted budgets,
    /// nested policy callbacks and other damage routes keep their native behavior unless
    /// the invoked remote-player route belongs to a retained strict-combat connection.
    /// </summary>
    public static event Action<MeleeImpactRequest> Receiving;
    public static MeleeImpactHookState State => Volatile.Read(ref _state);
    internal static bool IsOwnerThread => Environment.CurrentManagedThreadId == _thread;

    internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> warning)
    {
        if (Interlocked.Exchange(ref _attempted, 1) != 0) return;
        _thread = Environment.CurrentManagedThreadId;
        if (support == null || !support.SafeForHarmonyPatches)
        {
            Publish(MeleeImpactHookStatus.Disabled, "Safe native patch support is unavailable.");
            return;
        }
        Publish(MeleeImpactHookStatus.Installing, "Registering NPC melee contact boundaries.");
        try
        {
            var contact = AccessTools.Method(typeof(MeleeWeapon), "OnTriggerEnter", new[] { typeof(Collider) });
            var impact = AccessTools.Method(typeof(DamageNode), "OnImpact", new[] { typeof(IImpactSender), typeof(IImpactData) });
            var activation = AccessTools.Method(typeof(MeleeWeapon), "OnEnable", Type.EmptyTypes);
            var setDamage = AccessTools.Method(typeof(MeleeWeapon), "SetDamage", new[] { typeof(float) });
            if (contact == null || impact == null || activation == null || setDamage == null)
                throw new MissingMethodException("NPC melee contact or activation boundaries are unavailable.");
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-melee-impacts");
            HarmonyMethod Hook(string name) => new(typeof(ServerMeleeImpacts), name) { priority = Priority.First };
            harmony.Patch(contact, prefix: Hook(nameof(BeforeContact)), finalizer: Hook(nameof(FinishContact)));
            harmony.Patch(impact, prefix: Hook(nameof(BeforeImpact)));
            harmony.Patch(activation, prefix: Hook(nameof(BeforeActivation)), postfix: Hook(nameof(AfterActivation)),
                finalizer: new HarmonyMethod(typeof(ServerMeleeImpacts), nameof(FinishActivation)) { priority = Priority.Last });
            harmony.Patch(setDamage, prefix: Hook(nameof(BeforeSetDamage)));
            Publish(MeleeImpactHookStatus.Installed,
                "NPC melee contact and activation hooks registered; multiplayer interception remains unqualified.");
        }
        catch (Exception error)
        {
            Publish(MeleeImpactHookStatus.Failed, "NPC melee hook registration failed; policy dispatch is disabled.");
            warning?.Invoke("NPC melee contact hook registration failed: " + error);
        }
    }

    private sealed class ContactScope
    {
        internal MeleeWeapon Weapon;
        internal Collider Collider;
    }

    private struct ContactState
    {
        internal bool Entered;
        internal ContactScope Previous;
        internal int PreviousDepth;
    }

    private static void BeforeContact(MeleeWeapon __instance, Collider __0, bool __runOriginal, out ContactState __state)
    {
        __state = default;
        if (!__runOriginal || Receiving == null || State.Status != MeleeImpactHookStatus.Installed
            || Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return;
            __state = new ContactState { Entered = true, Previous = _contact, PreviousDepth = _contactDepth };
            _contactDepth++;
            // An over-depth call must hide the outer contact, not borrow its provenance.
            _contact = null;
            if (_contactDepth <= MaximumContactDepth)
                _contact = new ContactScope { Weapon = __instance, Collider = __0 };
        }
        catch (Exception error) { Report(error); }
    }

    private static Exception FinishContact(Exception __exception, ContactState __state)
    {
        if (__state.Entered)
        {
            _contact = __state.Previous;
            _contactDepth = __state.PreviousDepth;
        }
        return __exception;
    }

    private static bool BeforeImpact(DamageNode __instance, IImpactSender __0, IImpactData __1, bool __runOriginal)
    {
        if (!__runOriginal || !IsOwnerThread) return true;
        bool ownedTarget = false;
        var previousDispatch = _dispatching;
        MeleeImpactRequest request = null;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isServer) return true;
            // This exclusion survives subscriber removal, unavailable observations and
            // exhausted budgets. The invoked node's callback route establishes the target;
            // collider ancestry and client-supplied impact fields do not.
            if (Combat.StrictCombatServerHits.HasOwners && Native(__0)
                && Native(__0.TryCast<MeleeWeapon>())
                && ServerWorld.TryReadRemoteMeleeTarget(__instance, out var connection))
            {
                uint connectionId = connection.ConnectionId;
                var session = ServerSessions.ResolveSource(connection);
                ownedTarget = Combat.StrictCombatServerHits.Blocks(connection, connectionId, session);
            }

            var handlers = Receiving;
            var contact = _contact;
            if (handlers == null || contact == null || _dispatching != null
                || State.Status != MeleeImpactHookStatus.Installed
                || !Same(__0, contact.Weapon)) return !ownedTarget;
            if (!WorldRecords.TryGetCurrent(out var world, out _)) return !ownedTarget;
            int frame = Time.frameCount;
            if (!ReferenceEquals(world, _budgetWorld) || frame != _budgetFrame)
            { _budgetWorld = world; _budgetFrame = frame; _impactReads = 0; }
            if (_impactReads >= MaximumImpactsPerFrame) return !ownedTarget;
            _impactReads++;
            if (!DirectContact(contact.Collider, __instance)) return !ownedTarget;
            var data = __1?.TryCast<MeleeImpactData>();
            if (!SourceCapture.TryCapture(contact.Weapon, data, out var source)) return !ownedTarget;
            request = new MeleeImpactRequest(contact, world, frame, contact.Collider, __instance, source);
            _dispatching = request;
            // DirectContact above already scanned the receivers for this callback.
            if (!request.IsCurrent(false)) return !ownedTarget;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                handler(request);
                if (request.IsRejected) return false;
                if (!request.IsCurrent(false)) { request.Reject(); return false; }
            }
            if (!request.IsCurrent()) { request.Reject(); return false; }
            // Keep the request usable for server-owned damage until policy has finished.
            // Mandatory native exclusion is independent of whether any policy accepted it.
            return !ownedTarget;
        }
        catch (Exception error)
        {
            request?.Reject();
            Report(error);
            // A failed observation cannot reopen an already identified owned target.
            // Unknown targets retain native behavior unless policy already received the request.
            return request == null && !ownedTarget;
        }
        finally
        {
            if (request != null)
            {
                request.Close();
                _dispatching = previousDispatch;
            }
        }
    }

    internal static bool Current(MeleeImpactRequest request, bool checkReceivers)
        => Environment.CurrentManagedThreadId == _thread && ReferenceEquals(_dispatching, request)
            && State.Status == MeleeImpactHookStatus.Installed
            && BoltNetwork.isRunning && BoltNetwork.isServer
            && WorldRecords.IsCurrent(request.World) && Time.frameCount == request.Frame
            && _contact != null && ReferenceEquals(_contact, request.Contact) && Same(_contact.Collider, request.Collider)
            && DirectContact(request.Collider, request.Node, checkReceivers);

    private static bool DirectContact(Collider collider, DamageNode node, bool checkReceivers = true)
    {
        if (!Live(collider) || !collider.enabled || !Live(node)) return false;
        var contactObject = collider.gameObject;
        if (!contactObject.activeInHierarchy || !Same(contactObject, node.gameObject)) return false;
        if (!checkReceivers) return true;
        var receivers = contactObject.GetComponents<IImpactReceiver>();
        return receivers != null && !receivers.WasCollected && receivers.Length == 1 && Same(receivers[0], node);
    }

    private static void Publish(MeleeImpactHookStatus status, string detail)
    {
        var state = new MeleeImpactHookState(status, detail);
        Volatile.Write(ref _state, state);
        Modding.SetService(ServiceKey, state);
    }

    internal static void Report(Exception error)
    {
        if (Interlocked.Exchange(ref _reported, 1) != 0) return;
        try { RuntimeEntryPoint.LogSource?.LogWarning("NPC melee contact observation failed (" + error.GetType().Name
            + "). Further observation failures will not be logged."); }
        catch { }
    }
}
