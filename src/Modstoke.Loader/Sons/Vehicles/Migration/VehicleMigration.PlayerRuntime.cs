// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Bolt;
using UnityEngine;
using NativeMode = Modstoke.Sons.PlayerClientMovement.NativeMode;
using NativeModeKey = Modstoke.Sons.PlayerClientMovement.NativeModeKey;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        private static partial class CartOccupantWriterHooks
        {
            internal static void RequirePlayerRuntimeHeld(CartMigrationPlayer player)
            {
                // Animator speed is owned by this lease, but Animator itself is not a native
                // writer key. Local action and remote backing Entity are registered keys.
                var pointer = player.LocalAction != null ? player.LocalAction.Pointer : player.Entity._entity.Pointer;
                var hold = Find(pointer);
                Require(hold != null, "The exact existing occupant writer lease is missing.");
                hold.RequireHeld();
            }
        }

        private sealed partial class CartOccupantWriterHold
        {
            internal void ReholdForPlayerRuntime()
            {
                if (!_used) return;
                TerminalConflict ??= "Occupied player runtime is quarantined.";
                if (!_held)
                {
                    try { CartOccupantWriterHooks.Acquire(this); _held = true; }
                    catch (Exception) { /* Preserve the original clocks even if a writer gate cannot be reacquired. */ }
                }
                // Reuse the original snapshots even after a successful release followed by
                // a save/protocol failure. Never construct another replica/animation owner.
                try { _playerRig?.Rehold(); }
                catch (Exception) { /* Keep the other original clocks held after a partial native write. */ }
                foreach (var rig in _remotePlayerRigs)
                {
                    try { rig.Rehold(); }
                    catch (Exception) { /* The registered writer gate remains terminal. */ }
                }
                foreach (var animation in _animations)
                {
                    try { animation.Pause(); }
                    catch (Exception) { /* Retain every independent clock that can still be held. */ }
                }
            }
        }

        /// <summary>Exact existing player runtime, not a second player or replica owner.</summary>
        public sealed class CartPlayerRuntime
        {
            public BoltMigrationPlan PlayerPlan { get; }
            public ulong PlayerNetworkId { get; }
            public NativeModeKey OriginalSeat { get; }
            public PlayerBridge Bridge { get; }
            public PlayerClientMovement SourceMovement { get; }
            public PlayerGroundMovement ServerMovement { get; }

            public CartPlayerRuntime(PlayerBridge bridge, NativeModeKey originalSeat,
                PlayerClientMovement sourceMovement = null,
                PlayerGroundMovement serverMovement = null)
            {
                Bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
                PlayerPlan = bridge.Plan; PlayerNetworkId = bridge.NetworkId; OriginalSeat = originalSeat;
                SourceMovement = sourceMovement; ServerMovement = serverMovement;
            }

            /// <summary>Observer identity must come from the authenticated player registry.</summary>
            public CartPlayerRuntime(BoltMigrationPlan playerPlan, ulong playerNetworkId,
                NativeModeKey originalSeat)
            { PlayerPlan = playerPlan; PlayerNetworkId = playerNetworkId; OriginalSeat = originalSeat; }
        }

        public enum CartPlayerRuntimeBoundary { Admission, Held, Repaired, Cancel }

        // This parent-owned boundary is READ ONLY and must inspect real per-root ingress holds and
        // all-peer receipts. A null context means preflight, not a held transaction.
        public delegate bool ReadCartPlayerRuntimeBoundary(CartPlayerMigrationContext context,
            CartPlayerRuntime runtime, CartMigrationPlayer player, BoltEntity cart,
            CartPlayerRuntimeBoundary boundary, out string detail);
        /// <summary>
        /// Preserves occupied migrated players across one physical cart replacement. Native
        /// root/ingress and authenticated key-rebind transport remain mandatory parent bindings.
        /// </summary>
        public sealed class CartPlayerMigrationRuntime : ICartPlayerMigrationLifecycle
        {
            private readonly CartPlayerRuntime[] _runtimes;
            private readonly ReadCartPlayerRuntimeBoundary _readBoundary;
            private readonly Func<BoltMigrationSession> _readSession;
            private RuntimeHold _hold;
            public CartDriverAuthority DriverAuthority => _hold?.Driver;
            /// <summary>Cached local failure only; not a full native-root or peer-readiness claim.</summary>
            public bool IsQuarantined => _hold?.IsQuarantined == true;

            public CartPlayerMigrationRuntime(IReadOnlyList<CartPlayerRuntime> runtimes,
                ReadCartPlayerRuntimeBoundary readBoundary, Func<BoltMigrationSession> readCartSession)
            {
                if (runtimes == null || runtimes.Count is < 1 or > 4)
                    throw new ArgumentException("One exact runtime per occupied player is required.", nameof(runtimes));
                _runtimes = new CartPlayerRuntime[runtimes.Count];
                for (int i = 0; i < runtimes.Count; i++)
                    _runtimes[i] = runtimes[i] ?? throw new ArgumentException("A player runtime is null.", nameof(runtimes));
                _readBoundary = readBoundary ?? throw new ArgumentNullException(nameof(readBoundary));
                _readSession = readCartSession ?? throw new ArgumentNullException(nameof(readCartSession));
            }

            internal bool CanYieldActiveAuthority(BoltEntity original)
            {
                var binding = new Binding(original);
                bool found = false;
                var proxy = CartPlayerProxyAuthority.Find(binding.Cart);
                if (proxy != null) { proxy.RequireCanYield(_runtimes); found = true; }
                foreach (var passenger in CartPlayerPassengerAuthority.ForCart(binding.Cart))
                { passenger.RequireCanYield(Find(passenger._player)); found = true; }
                var driver = CartDriverAuthorityHooks.ForMigration(binding.Cart);
                if (driver == null) return found;
                foreach (var runtime in _runtimes)
                    if (runtime.OriginalSeat.SeatIndex == 0 && runtime.OriginalSeat.AnchorNetworkId == binding.Id
                        && runtime.ServerMovement != null && runtime.Bridge?.IsServer == true)
                        return runtime.ServerMovement.CanYieldCartMigrationDriver(driver, runtime.Bridge,
                            runtime.OriginalSeat, out _);
                return false;
            }

            public BoltMigrationAdmission ReadAdmission(BoltEntity original,
                IReadOnlyList<CartMigrationPlayer> players, out string detail)
            {
                try
                {
                    RequirePhase();
                    Require(_hold == null && Live(original) && original.isAttached, "This player provider is single-use.");
                    var binding = new Binding(original);
                    CartPlayerCallbackSettlement.RequireSettled(binding.Cart);
                    Require(players.Count == _runtimes.Length, "Every occupied player needs one exact runtime.");
                    var seen = new HashSet<ulong>();
                    foreach (var player in players)
                    {
                        Require(seen.Add(player.PlayerNetworkId), "Duplicate occupied player.");
                        var runtime = Find(player);
                        RequireEndpoint(runtime, player, original.networkId.PackedValue);
                        Require(_readBoundary(null, runtime, player, original,
                            CartPlayerRuntimeBoundary.Admission, out detail), detail);
                    }
                    detail = "Exact occupied player runtimes and parent-owned native boundaries admitted.";
                    return BoltMigrationAdmission.Ready;
                }
                catch (Exception error)
                { detail = Failure("Occupied player runtime admission pending", error); return BoltMigrationAdmission.Pending; }
            }

            public bool TryAcquire(CartPlayerMigrationContext context,
                out ICartPlayerMigrationHold hold, out string detail)
            {
                hold = null;
                try
                {
                    RequirePhase();
                    Require(context != null && context.Plan.IsValid && _hold == null,
                        "An exact unused occupied-cart plan is required.");
                    // Publish before acquiring even the first managed sampler hold.
                    _hold = new RuntimeHold(this, context); hold = _hold;
                    _hold.Acquire();
                    detail = "Existing player samplers held; original remote rigs remain owned by the cart occupant hold.";
                    return true;
                }
                catch (Exception error) { detail = Failure("Occupied player runtime acquisition unconfirmed", error); return false; }
            }

            private CartPlayerRuntime Find(CartMigrationPlayer player)
            {
                CartPlayerRuntime result = null;
                foreach (var runtime in _runtimes)
                    if (runtime.PlayerNetworkId == player.PlayerNetworkId)
                    { Require(result == null, "Duplicate player runtime binding."); result = runtime; }
                Require(result != null, "No exact current migrated-player runtime was supplied.");
                return result;
            }

            private static void RequirePhase()
                => Require(_busy && OnSelectedThread() && !_reentered && !Time.inFixedTimeStep,
                    "Player cart phases run inside the existing vehicle operation, outside native physics.");

            private static void RequireEndpoint(CartPlayerRuntime runtime, CartMigrationPlayer player, ulong cartId)
            {
                var key = runtime.OriginalSeat;
                Require(runtime.PlayerPlan.IsValid && runtime.PlayerNetworkId == player.PlayerNetworkId
                    && key.AnchorNetworkId == cartId && key.SeatIndex == player.Slot
                    && key.Mode == (player.Slot == 0 ? NativeMode.GolfCartDriver : NativeMode.GolfCartPassenger)
                    && key.TransitionSequence is > 0 and < uint.MaxValue,
                    "The retained player plan and committed physical cart seat differ.");
                if (runtime.Bridge != null)
                {
                    var bridge = runtime.Bridge;
                    Require(bridge.Plan == runtime.PlayerPlan && bridge.NetworkId == runtime.PlayerNetworkId
                        && bridge.IsGameplayActive && bridge.Peer.IsCurrent
                        && bridge.TryReadCurrentTarget(out var target) && SameEntity(target, player.Entity)
                        && bridge.IsCommittedMovementMode(key) && bridge.CartMigrationBoundaryDrained,
                        "The exact player bridge has an unfinished transition/physics boundary or changed identity.");
                    Require(bridge.IsServer
                        ? BoltNetwork.isServer && player.LocalAction == null && runtime.SourceMovement == null
                            && runtime.ServerMovement != null
                        : player.LocalAction != null && runtime.SourceMovement != null && runtime.ServerMovement == null,
                        "The occupied player bridge must bind its existing source sampler or yielded server motor.");
                }
                else Require(player.LocalAction == null && !BoltNetwork.isServer
                    && runtime.SourceMovement == null && runtime.ServerMovement == null,
                    "Only an authenticated remote observer can omit a player bridge.");
            }

            private sealed class RuntimeHold : ICartPlayerMigrationHold
            {
                private readonly CartPlayerMigrationRuntime _owner;
                private readonly List<(CartPlayerRuntime Runtime, CartMigrationPlayer Player)> _players = new();
                private Binding _replacement;
                private BoltMigrationSession _session;
                private bool _acquired, _prepared, _cancelPrepared, _finished, _quarantined, _rebindAttempted;
                internal CartDriverAuthority Driver;
                private CartPlayerProxyAuthority _proxy;
                private readonly List<CartPlayerPassengerAuthority> _passengers = new();
                private CartPlayerProxyAuthority _priorProxy;
                private readonly List<CartPlayerPassengerAuthority> _priorPassengers = new();
                public CartPlayerMigrationContext Context { get; }
                internal bool IsQuarantined
                {
                    get
                    {
                        if (_quarantined || Driver?.IsQuarantined == true || _proxy?.IsQuarantined == true
                            || _priorProxy?.IsQuarantined == true) return true;
                        foreach (var passenger in _passengers) if (passenger.IsQuarantined) return true;
                        foreach (var passenger in _priorPassengers) if (passenger.IsQuarantined) return true;
                        foreach (var pair in _players)
                            if (pair.Runtime.SourceMovement?.IsDisposed == true
                                || pair.Runtime.ServerMovement?.IsDisposed == true) return true;
                        return false;
                    }
                }

                internal RuntimeHold(CartPlayerMigrationRuntime owner, CartPlayerMigrationContext context)
                { _owner = owner; Context = context; }

                internal void Acquire()
                {
                    RequirePhase();
                    Require(Context.Players.Count == _owner._runtimes.Length, "Player runtime count changed.");
                    foreach (var player in Context.Players)
                    {
                        var runtime = _owner.Find(player);
                        RequireEndpoint(runtime, player, Context.Plan.OldNetworkId);
                        CartOccupantWriterHooks.RequirePlayerRuntimeHeld(player);
                        Require(_owner._readBoundary(Context, runtime, player, Context.OriginalCart,
                            CartPlayerRuntimeBoundary.Held, out var detail), detail);
                        _players.Add((runtime, player));
                        if (runtime.SourceMovement != null)
                            Require(runtime.SourceMovement.TryHoldCartMigration(Context, player, runtime.Bridge,
                                runtime.OriginalSeat, out detail), detail);
                        if (runtime.ServerMovement != null)
                            Require(runtime.ServerMovement.TryHoldCartMigration(Context, runtime.Bridge,
                                runtime.OriginalSeat, out detail), detail);
                    }
                    var original = new Binding(Context.OriginalCart);
                    _priorProxy = CartPlayerProxyAuthority.Find(original.Cart);
                    _priorProxy?.Yield(Context, _owner._runtimes);
                    foreach (var passenger in CartPlayerPassengerAuthority.ForCart(original.Cart))
                    {
                        _priorPassengers.Add(passenger);
                        passenger.Yield(Context, _owner.Find(passenger._player));
                    }
                    _acquired = true;
                    RequireHeld();
                }

                private void RequireHeld()
                {
                    RequirePhase();
                    Require(_acquired && !_finished && !_quarantined, "The occupied player hold is not active.");
                    _priorProxy?.RequireYielded(Context, false);
                    foreach (var passenger in _priorPassengers) passenger.RequireYielded(Context, false);
                    foreach (var pair in _players)
                    {
                        var runtime = pair.Runtime;
                        CartOccupantWriterHooks.RequirePlayerRuntimeHeld(pair.Player);
                        Require(runtime.Bridge == null || (runtime.Bridge.IsCommittedMovementMode(runtime.OriginalSeat)
                            && runtime.Bridge.CartMigrationBoundaryDrained), "The held player movement key or boundary changed.");
                        Require(_owner._readBoundary(Context, runtime, pair.Player, _replacement?.Entity ?? Context.OriginalCart,
                            CartPlayerRuntimeBoundary.Held, out var detail), detail);
                        if (runtime.SourceMovement != null)
                            Require(runtime.SourceMovement.IsCartMigrationHeld(Context), "The source sampler hold was lost.");
                        if (runtime.ServerMovement != null)
                            Require(runtime.ServerMovement.IsCartMigrationHeld(Context), "The yielded server motor hold was lost.");
                    }
                    if (_replacement != null) CartPlayerCallbackSettlement.RequireSettled(_replacement.Cart);
                }

                public bool TryValidateHeld(out string detail)
                {
                    try { RequireHeld(); detail = "Exact occupied player hold remains current."; return true; }
                    catch (Exception error) { detail = Failure("Occupied player hold rejected", error); return false; }
                }

                public bool TryRebind(BoltEntity replacement, out string detail)
                {
                    try
                    {
                        RequireHeld();
                        Require(!_rebindAttempted, "Player replacement preparation is single-use.");
                        _rebindAttempted = true; _replacement = new Binding(replacement);
                        _session = _owner._readSession();
                        Require(_session?.Snapshot?.Plan == Context.Plan && _session.Role == Context.Role,
                            "The current cart session must match this exact prepared plan and role.");
                        Require(_replacement.Id != Context.Plan.OldNetworkId && _replacement.Prefab == Context.Plan.PrefabId
                            && replacement.isOwner == (Context.Role == BoltMigrationRole.Destination),
                            "The physical replacement does not match this cart plan and role.");
                        CartPlayerCallbackSettlement.RequireSettled(_replacement.Cart);
                        foreach (var pair in _players)
                        {
                            var runtime = pair.Runtime; var player = pair.Player;
                            Require(_replacement.CartState.ConnectedSlots.GetValueNetworkId(player.Slot).PackedValue
                                == player.SeatNetworkId, "The replacement physical seat changed its retained player/ghost.");
                            var key = ReplacementKey(runtime);
                            if (runtime.SourceMovement != null)
                                Require(runtime.SourceMovement.TryPrepareCartMigrationRebind(Context, key,
                                    _replacement.Cart, out detail), detail);
                            if (player.Slot == 0 && runtime.Bridge?.IsServer == true)
                            {
                                Require(TryPrepareServerCartDriverAuthority(replacement, player.Entity,
                                    runtime.Bridge.Peer, out Driver, out detail), detail);
                                Require(TryPrepareMigratedServerCartDriverInput(Driver, runtime.Bridge,
                                    Context.Plan, runtime.OriginalSeat, key, out detail), detail);
                            }
                            else if (runtime.Bridge?.IsServer == true)
                                _passengers.Add(new CartPlayerPassengerAuthority(_replacement, player, runtime.Bridge, key));
                        }
                        if (!replacement.isOwner) _proxy = new CartPlayerProxyAuthority(_replacement, Context.Players);
                        RequireHeld();
                        detail = "Exact retained seat samplers and server/proxy authority prepared without native entry replay.";
                        return true;
                    }
                    catch (Exception error) { Quarantine(); detail = Failure("Player cart rebind unconfirmed", error); return false; }
                }

                private NativeModeKey ReplacementKey(CartPlayerRuntime runtime)
                    => runtime.OriginalSeat with { AnchorNetworkId = _replacement.Id,
                        TransitionSequence = runtime.OriginalSeat.TransitionSequence + 1 };

                public bool TryPrepareResume(out string detail)
                {
                    try
                    {
                        RequireHeld(); Require(_replacement != null, "No player cart replacement was bound.");
                        RequireSession();
                        _priorProxy?.RequireYielded(Context, true);
                        foreach (var passenger in _priorPassengers) passenger.RequireYielded(Context, true);
                        foreach (var pair in _players)
                        {
                            Require(_owner._readBoundary(Context, pair.Runtime, pair.Player, _replacement.Entity,
                                CartPlayerRuntimeBoundary.Repaired, out detail), detail);
                            if (pair.Runtime.SourceMovement != null)
                                Require(pair.Runtime.SourceMovement.IsCartMigrationPrepared(Context), "Source seat binding is not prepared.");
                            if (pair.Runtime.ServerMovement != null)
                                Require(pair.Runtime.ServerMovement.IsCartMigrationPrepared(Context),
                                    "The retained server motor or old driver retirement is not prepared.");
                            if (pair.Runtime.Bridge != null)
                            {
                                Require(pair.Runtime.Bridge.TryPrepareCartMigrationRebind(_session, Context, pair.Runtime.OriginalSeat,
                                    out var key, out detail), detail);
                                Require(key == ReplacementKey(pair.Runtime), "The repaired session produced a different seat key.");
                            }
                        }
                        if (Driver != null) Require(TryValidatePreparedServerCartDriverAuthority(Driver, out detail), detail);
                        _proxy?.RequirePrepared();
                        foreach (var passenger in _passengers) passenger.RequirePrepared();
                        _prepared = true; detail = "All exact local domains prepared; authenticated peer repair receipt checked.";
                        return true;
                    }
                    catch (Exception error) { detail = Failure("Player cart release pending", error); return false; }
                }

                public bool TryResume(out string detail)
                {
                    try
                    {
                        RequirePhase();
                        RequireSession();
                        Require(_prepared && !_finished && !_quarantined && _replacement.Matches()
                            && OccupantsFor(_replacement.Cart) == null && !CartWriterHooks.IsHeld(_replacement.Cart),
                            "Stock cart/occupant writers must release synchronously before player activation.");
                        foreach (var pair in _players)
                        {
                            var runtime = pair.Runtime; var key = ReplacementKey(runtime);
                            if (runtime.Bridge != null)
                            {
                                Require(runtime.Bridge.TryCommitCartMigrationRebind(_session, Context, runtime.OriginalSeat,
                                    out var committed, out detail), detail);
                                Require(committed == key, "The cart session committed a different physical seat.");
                            }
                            Require(runtime.Bridge == null || runtime.Bridge.IsCommittedMovementMode(key),
                                "Authenticated same-mode rebind did not select this exact replacement seat.");
                        }
                        _priorProxy?.Retire(Context);
                        foreach (var passenger in _priorPassengers) passenger.Retire(Context);
                        if (Driver != null) Require(TryActivatePreparedServerCartDriverAuthority(Driver, out detail), detail);
                        _proxy?.Activate();
                        foreach (var passenger in _passengers) passenger.Activate();
                        foreach (var pair in _players)
                        {
                            var runtime = pair.Runtime; var key = ReplacementKey(runtime);
                            if (runtime.ServerMovement != null)
                                Require(runtime.ServerMovement.TryResumeCartMigration(Context, key,
                                    pair.Player.Slot == 0 ? Driver : null, out detail), detail);
                            if (runtime.SourceMovement != null)
                                Require(runtime.SourceMovement.TryResumeCartMigration(Context, out detail), detail);
                        }
                        _finished = true;
                        detail = "Original player runtimes resumed on the authenticated replacement cart; ingress remains parent-owned.";
                        return true;
                    }
                    catch (Exception error) { Quarantine(); detail = Failure("Player cart activation unconfirmed", error); return false; }
                }

                public bool TryPrepareCancel(out string detail)
                {
                    try
                    {
                        RequirePhase();
                        Require(!_finished && !_quarantined && !_rebindAttempted && Live(Context.OriginalCart)
                            && Context.OriginalCart.isAttached && Context.OriginalCart.networkId.PackedValue == Context.Plan.OldNetworkId,
                            "Only the intact original can cancel before replacement preparation.");
                        foreach (var pair in _players)
                        {
                            Require(_owner._readBoundary(Context, pair.Runtime, pair.Player, Context.OriginalCart,
                                CartPlayerRuntimeBoundary.Cancel, out detail), detail);
                            Require(pair.Runtime.Bridge == null || pair.Runtime.Bridge.IsCommittedMovementMode(pair.Runtime.OriginalSeat),
                                "Original player key changed before cancellation.");
                        }
                        _cancelPrepared = true; detail = "Intact original player runtime cancellation prepared."; return true;
                    }
                    catch (Exception error) { detail = Failure("Player cart cancellation rejected", error); return false; }
                }

                public bool TryCancel(out string detail)
                {
                    try
                    {
                        RequirePhase(); Require(_cancelPrepared && !_finished && !_quarantined, "Cancellation is not prepared.");
                        foreach (var pair in _players)
                        {
                            if (pair.Runtime.SourceMovement != null)
                                Require(pair.Runtime.SourceMovement.TryCancelCartMigration(Context, out detail), detail);
                            if (pair.Runtime.ServerMovement != null)
                                Require(pair.Runtime.ServerMovement.TryCancelCartMigration(Context, out detail), detail);
                        }
                        _priorProxy?.CancelYield(Context);
                        foreach (var passenger in _priorPassengers) passenger.CancelYield(Context);
                        _finished = true; detail = "Original sampler and yielded server motor retained without seat replay."; return true;
                    }
                    catch (Exception error) { Quarantine(); detail = Failure("Player cart cancellation unconfirmed", error); return false; }
                }

                public void Quarantine()
                {
                    _quarantined = true;
                    try { Driver?.Quarantine("Occupied cart aggregate failed."); }
                    catch (Exception) { /* Continue quarantining the other retained runtimes. */ }
                    try { _proxy?.Quarantine(); }
                    catch (Exception) { /* The proxy registry remains terminal. */ }
                    try { _priorProxy?.Quarantine(); }
                    catch (Exception) { /* Retain the original policy if cancellation did not complete. */ }
                    foreach (var passenger in _priorPassengers)
                    {
                        try { passenger.Quarantine(); }
                        catch (Exception) { /* The original occupant lease remains terminal. */ }
                    }
                    foreach (var passenger in _passengers)
                    {
                        try { passenger.Quarantine(); }
                        catch (Exception) { /* Preserve all remaining passenger holds. */ }
                    }
                    foreach (var pair in _players)
                    {
                        try { pair.Runtime.SourceMovement?.QuarantineCartMigration(Context); }
                        catch (Exception) { /* Keep every other exact participant quarantined as well. */ }
                        try { pair.Runtime.ServerMovement?.QuarantineCartMigration(Context); }
                        catch (Exception) { /* The stock occupant lease is reheld by the adapter. */ }
                    }
                }

                private void RequireSession()
                    => Require(_session != null && ReferenceEquals(_owner._readSession(), _session)
                        && _session.Snapshot?.Plan == Context.Plan && _session.Role == Context.Role
                        && _session.NewNetworkId == _replacement.Id,
                        "The authenticated cart session or replacement identity changed.");
            }
        }
    }
}
