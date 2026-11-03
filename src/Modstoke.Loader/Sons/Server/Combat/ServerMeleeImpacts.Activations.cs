// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Weapon;
using MeleeWeapon = global::Sons.Gameplay.MeleeWeapon;

namespace Modstoke.Sons;

/// <summary>
/// Opaque identity of an observed native component activation and its new cached payload.
/// One activation can contain multiple contacts; this is not an attack sequence or damage grant.
/// Native reads are game-thread-only, and an invalidated identity never becomes current again.
/// </summary>
public sealed class MeleeActivation
{
    internal MeleeActivation(ServerMeleeImpacts.SourceCapture source,
        WorldRecordContext world, int slot)
    { Source = source; World = world; Slot = slot; }

    internal ServerMeleeImpacts.SourceCapture Source { get; }
    internal WorldRecordContext World { get; }
    internal int Slot { get; }
    internal bool Retired { get; set; }
    public ulong ActorNetworkId => Source.ActorNetworkId;
    public int ActorInstanceId => Source.ActorInstanceId;
    public int WeaponInstanceId => Source.WeaponInstanceId;
    /// <summary>The raw value copied at birth; observing it does not grant a damage resource.</summary>
    public float InitialRawDamage => Source.OriginalDamage;
    public bool IsCurrent => ServerMeleeImpacts.ActivationCurrent(this);

    /// <summary>Matches only a current synchronous request with the same stable native source.</summary>
    public bool Matches(MeleeImpactRequest request)
        => ServerMeleeImpacts.ActivationMatches(this, request);

    /// <summary>
    /// Also requires unchanged raw damage, bitwise zero accumulation and native ID zero.
    /// Pristine values on a late contact cannot create an activation identity or a new grant.
    /// </summary>
    public bool IsPristine(MeleeImpactRequest request)
        => Matches(request) && request.NativeImpactId == 0
            && BitConverter.SingleToInt32Bits(request.AccumulatedDamage) == 0
            && BitConverter.SingleToInt32Bits(request.OriginalRawDamage) == BitConverter.SingleToInt32Bits(InitialRawDamage)
            && BitConverter.SingleToInt32Bits(request.RemainingRawDamage) == BitConverter.SingleToInt32Bits(InitialRawDamage);
}

public static partial class ServerMeleeImpacts
{
    private const int MaximumActivations = 256;
    private const int MaximumActivationDepth = 8;
    private static readonly MeleeActivation[] _activations = new MeleeActivation[MaximumActivations];
    [ThreadStatic] private static ActivationBirth _activationBirth;
    [ThreadStatic] private static int _activationDepth;

    private sealed class ActivationBirth
    {
        internal MeleeWeapon Weapon;
        // Keep the old payload alive across OnEnable so pointer reuse cannot look like replacement.
        internal MeleeImpactData PreviousData;
        internal WorldRecordContext World;
        internal ActivationBirth Previous;
        internal int PreviousDepth;
        internal bool Captured, Completed, Invalidated;
    }

    private static void BeforeActivation(MeleeWeapon __instance, out ActivationBirth __state)
    {
        __state = null;
        if (Environment.CurrentManagedThreadId != _thread) return;
        try
        {
            RetireWeaponActivation(__instance);
            __state = new ActivationBirth { Weapon = __instance, Previous = _activationBirth,
                PreviousDepth = _activationDepth };
            _activationDepth++;
            // Overflow gets its own restoration state, but never borrows an outer birth. Keep
            // the bounded outer chain accessible so SetDamage can still invalidate its entries.
            if (_activationDepth > MaximumActivationDepth) return;
            if (State.Status != MeleeImpactHookStatus.Installed
                || !BoltNetwork.isRunning || !BoltNetwork.isServer || !Live(__instance)
                || !WorldRecords.TryGetCurrent(out var world, out _)) return;
            var previous = __instance._cachedImpactData;
            if (previous is not null && !Native(previous)) return;
            __state.PreviousData = previous;
            __state.World = world;
            __state.Captured = true;
            _activationBirth = __state;
        }
        catch (Exception error) { Report(error); }
    }

    private static void AfterActivation(bool __runOriginal, ActivationBirth __state)
    {
        if (__state != null) __state.Completed = __runOriginal;
    }

    private static Exception FinishActivation(Exception __exception, ActivationBirth __state)
    {
        if (__state == null || Environment.CurrentManagedThreadId != _thread) return __exception;
        try
        {
            if (__exception != null || !__state.Captured || !__state.Completed || __state.Invalidated
                || State.Status != MeleeImpactHookStatus.Installed
                || !BoltNetwork.isRunning || !BoltNetwork.isServer
                || !WorldRecords.IsCurrent(__state.World) || !Live(__state.Weapon)) return __exception;
            var data = __state.Weapon._cachedImpactData;
            if (!Native(data) || (__state.PreviousData is not null && !Native(__state.PreviousData))
                || Same(data, __state.PreviousData)
                || !SourceCapture.TryCapture(__state.Weapon, data, out var source, requireMeleeActive: false)
                || source.OriginalDamage < 0f || !Bits(source.OriginalDamage, source.RemainingDamage)
                || !Bits(source.AccumulatedDamage, 0f) || source.NativeImpactId != 0) return __exception;
            int slot = FindActivationSlot();
            if (slot >= 0) _activations[slot] = new MeleeActivation(source, __state.World, slot);
        }
        catch (Exception error) { Report(error); }
        finally
        {
            _activationBirth = __state.Previous;
            _activationDepth = __state.PreviousDepth;
        }
        return __exception;
    }

    // SetDamage changes the existing payload in place. Even the same value invalidates provenance;
    // no refill or identity can be inferred from it, including during another hook's postfix.
    private static void BeforeSetDamage(MeleeWeapon __instance)
    {
        if (Environment.CurrentManagedThreadId != _thread) return;
        try { RetireWeaponActivation(__instance); }
        catch (Exception error) { Report(error); }
    }

    private static void RetireWeaponActivation(MeleeWeapon weapon)
    {
        for (var birth = _activationBirth; birth != null; birth = birth.Previous)
            if (Same(birth.Weapon, weapon)) birth.Invalidated = true;
        for (int index = 0; index < _activations.Length; index++)
        {
            var activation = _activations[index];
            if (activation != null && activation.Source.IsWeapon(weapon)) RetireActivation(activation);
        }
    }

    private static void RetireActivation(MeleeActivation activation)
    {
        activation.Retired = true;
        if (ReferenceEquals(_activations[activation.Slot], activation)) _activations[activation.Slot] = null;
    }

    private static int FindActivationSlot()
    {
        int available = -1;
        for (int index = 0; index < _activations.Length; index++)
        {
            var activation = _activations[index];
            if (activation != null) ActivationCurrent(activation);
            if (_activations[index] == null && available < 0) available = index;
        }
        return available;
    }

    internal static bool ActivationCurrent(MeleeActivation activation)
    {
        // A caller on another thread cannot read native state or retire a valid game-thread token.
        if (Environment.CurrentManagedThreadId != _thread || activation == null) return false;
        if (activation.Retired || !ReferenceEquals(_activations[activation.Slot], activation)) return false;
        try
        {
            if (State.Status == MeleeImpactHookStatus.Installed
                && BoltNetwork.isRunning && BoltNetwork.isServer
                && WorldRecords.IsCurrent(activation.World) && activation.Source.IsActivationCurrent()) return true;
        }
        catch (Exception error) { Report(error); }
        RetireActivation(activation);
        return false;
    }

    internal static bool ActivationMatches(MeleeActivation activation, MeleeImpactRequest request)
    {
        if (Environment.CurrentManagedThreadId != _thread || request == null || !request.IsCurrent()
            || !ActivationCurrent(activation) || !ReferenceEquals(activation.World, request.World)) return false;
        try
        {
            if (activation.Source.MatchesActivation(request.Source)) return true;
            // An unrelated request does not invalidate this token. A changed source on the same
            // weapon does, even when a list replacement preserved its count.
            if (request.WeaponInstanceId == activation.WeaponInstanceId) RetireActivation(activation);
        }
        catch (Exception error) { RetireActivation(activation); Report(error); }
        return false;
    }

    internal static bool TryGetActivation(MeleeImpactRequest request, out MeleeActivation activation)
    {
        activation = null;
        if (Environment.CurrentManagedThreadId != _thread || request == null) return false;
        for (int index = 0; index < _activations.Length; index++)
        {
            var candidate = _activations[index];
            if (candidate == null || candidate.WeaponInstanceId != request.WeaponInstanceId) continue;
            if (!ActivationMatches(candidate, request)) return false;
            activation = candidate;
            return true;
        }
        return false;
    }
}
