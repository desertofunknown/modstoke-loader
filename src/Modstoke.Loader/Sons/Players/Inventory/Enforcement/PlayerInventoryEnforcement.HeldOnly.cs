// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Inventory;
using global::Sons.StatSystem;
using TheForest.Items.Inventory;
using TheForest.Items.Special;
using TheForest.Utils;
using UnityEngine;
using HeldAuthority = Modstoke.Sons.PlayerHeldOnlyAuthority;

namespace Modstoke.Sons
{
    public sealed partial class PlayerInventoryEnforcement
    {
        private static HeldOnlySource _heldOnlySource;
        private static readonly Dictionary<IntPtr, HeldOnlySource> HeldSourceGuards = new();
        private HeldAuthority _heldOnlyAuthority;
        private HeldOnlySource _retainedHeldOnlySource;

        internal HeldAuthority HeldOnlyAuthority => _heldOnlyAuthority;
        internal HeldOnlySource RetainedHeldOnlySource => _retainedHeldOnlySource;

        /// <summary>Retain an already explicitly approved authority. This performs no approval or activation.</summary>
        internal bool TryBindHeldOnlyAuthority(PlayerBridge bridge, HeldAuthority authority, out string detail)
        {
            detail = "An already approved held authority and this exact prepared server inventory endpoint are required.";
            if (!OnThread || IsDisposed || IsFaulted || !ReferenceEquals(_bridge, bridge)
                || bridge?.IsServer != true || _authority?.HasAcceptedBaseline != true || !Current()
                || authority == null || authority.Plan != _baseline.Plan
                || !authority.TryRead(bridge, out _, out detail)) return false;
            if (_heldOnlyAuthority != null && !ReferenceEquals(_heldOnlyAuthority, authority))
            { detail = "This inventory endpoint already retains a different held authority."; return false; }
            _heldOnlyAuthority = authority;
            detail = "Exact approved held authority retained; available while prepared/held and later idle gameplay.";
            return true;
        }

        public bool HasPendingHeldOnlyCustody => HeldOnlyRoute?.HasPendingCustody == true
            || HeldOnlyRoute?.HasPendingForcedDrop == true
            || _heldOnlySource?.InventoryOwner == this && (_heldOnlySource.HasPendingCustody || _heldOnlySource.HasUnownedForcedCallback);

        /// <summary>Retains the actual source controller. Disposal never reopens its native writers.</summary>
        public sealed partial class HeldOnlySource : IDisposable
        {
            internal readonly int Thread = Environment.CurrentManagedThreadId;
            internal readonly int Load = SaveLifecycle.State.BeforeLoadCount;
            internal readonly PlayerInventory Inventory;
            internal readonly HeldOnlyItemController Controller;
            internal readonly GameObject Root;
            internal readonly FirstPersonCharacter Character;
            internal readonly Vitals Vitals;
            internal readonly Transform Camera;
            private readonly IntPtr _catalog;
            private readonly IntPtr[] _entries, _renderers;
            private readonly HeldAuthority.CatalogItem[] _definitions;
            internal PlayerInventoryEnforcement InventoryOwner;
            internal PlayerBridge Bridge;
            internal HeldAuthority.NativeCommand Command;
            internal ItemInstance LiftItem;
            internal HeldAuthority.RepresentativeIdentity LiftIdentity;
            internal readonly List<HeldRoutine> Routines = new();
            internal readonly List<HeldAuthority.NativeDropEffect> Drops = new();
            internal bool EntryPermit, EntryDone, StrengthSeen;
            internal int RemovalCount, MidActionPhases, DropStarts;
            internal float Strength;
            internal IntPtr Added, Removed;
            private ulong _lastTransaction;
            private HeldAuthority.Snapshot _settled;
            private HeldAuthority.NativeReceipt _receipt;
            private HeldAuthority.NativeCommand _lastCommand;
            private bool _inventoryBusyHeld;
            public HeldAuthority.Snapshot Baseline { get; }
            internal string SettledFingerprint => _settled.Fingerprint;
            internal bool MatchesSettledCustody(HeldAuthority.Snapshot snapshot) => _settled.MatchesLocalCustody(snapshot);
            public bool IsDisposed { get; private set; }
            public string FailureReason { get; private set; }
            public bool HasPendingCustody => Command != null;
            public bool IsNativeGuardHeld => Controller != null
                && HeldSourceGuards.TryGetValue(Controller.Pointer, out var guard) && ReferenceEquals(guard, this);

            internal HeldOnlySource(PlayerInventory inventory, HeldOnlyItemController controller, HeldAuthority.Snapshot snapshot)
            {
                Inventory = inventory; Controller = controller; Root = LocalPlayer.GameObject;
                Character = LocalPlayer.FpCharacter; Vitals = LocalPlayer.Vitals; Camera = LocalPlayer.MainCam.transform;
                Baseline = _settled = snapshot;
                _catalog = controller._heldOnlyItems.Pointer;
                _definitions = snapshot.GetCatalog();
                _entries = new IntPtr[_definitions.Length]; _renderers = new IntPtr[_definitions.Length];
                for (int i = 0; i < _entries.Length; i++)
                { _entries[i] = controller._heldOnlyItems[i].Pointer; _renderers[i] = controller._heldOnlyItems[i]._held.Pointer; }
            }

            public bool TryBind(PlayerBridge bridge, PlayerInventoryEnforcement inventory, out string detail)
            {
                detail = "Binding requires this exact retained client inventory and current non-owner bridge.";
                if (Bridge != null || !Lifetime(false) || bridge == null || bridge.IsServer || bridge.Plan != Baseline.Plan
                    || inventory == null || inventory._bridge != bridge || inventory._inventory?.Pointer != Inventory.Pointer
                    || inventory._root != Root.Pointer || !inventory.Current() || !bridge.TryReadCurrentTarget(out var target)
                    || target.isOwner || LocalPlayer.Entity?.Pointer != target.Pointer) return false;
                if (inventory._retainedHeldOnlySource != null && !ReferenceEquals(inventory._retainedHeldOnlySource, this)) return false;
                Bridge = bridge; InventoryOwner = inventory; inventory._retainedHeldOnlySource = this;
                bridge.Retired += Dispose;
                detail = "Exact held-only source retained; native writers stay fenced after disposal or failure.";
                return true;
            }

            /// <summary>
            /// Authenticated server command only. Lift takes the actual reserved source donor,
            /// never a new item made from an ID. Parent completes native equipment stow first;
            /// previous-hand restoration is a separate receipt-bound equipment operation.
            /// </summary>
            public bool TryExecute(HeldAuthority.NativeCommand command, ItemInstance liftDonor, out string detail)
            {
                detail = "Held-only execution requires an idle exact source and a new authenticated server command.";
                if (!Lifetime(true) || command == null || command.Before.Plan != Baseline.Plan || !command.ShapeValid()) return false;
                if (Command != null) return Command.Fingerprint == command.Fingerprint && FailureReason == null;
                if (_lastCommand?.Fingerprint == command.Fingerprint) return true; // Completed duplicate, never replay.
                if (command.Transaction <= _lastTransaction || command.Before.Fingerprint != _settled.Fingerprint
                    || ((!InventoryOwner.CanQueue || InventoryOwner.HasPendingCustody) && !CompoundRequestReady)
                    || PlayerLifecycle.IsInventoryIngressFenced(Inventory, out _)
                    || _heldCall != null || HasObservedHeldOnlyWork(Inventory, false)) return false;
                try
                {
                    if (!HeldAuthority.Snapshot.TryCaptureRetained(Bridge, Inventory, Controller, out var before, out detail)
                        || before.Fingerprint != command.Before.Fingerprint) return false;
                    if (HeldAuthority.IsForcedDrop(command.Kind)
                        && (!_compoundPrepared || _forcedHands == null || !ForcedHandIdentitiesCurrent()))
                    { detail = "Forced native entry requires its concrete previous-hand reservations before dispatch."; return false; }
                    if (command.Kind == HeldAuthority.MutationKind.Lift)
                    {
                        if (liftDonor == null || liftDonor.WasCollected
                            || !PlayerInventoryBaseline.MatchesInstance(command.After.InstanceEntry(), liftDonor)
                            || Inventory._equipmentSlots == null || Inventory._equipmentSlots.Length < 2
                            || Inventory._equipmentSlots[0] != null || !Inventory.IsSlotNextEmpty((global::Sons.Items.Core.EquipmentSlot)0))
                        { detail = "Lift needs the reserved donor and completed right-hand stow receipt."; return false; }
                        LiftIdentity = new HeldAuthority.RepresentativeIdentity(liftDonor);
                    }
                    else if (liftDonor != null) return false;
                    Command = command; LiftItem = liftDonor; _lastTransaction = command.Transaction;
                    if (!PlayerLifecycle.TryYieldSourceInputHoldForAcceptedWork(Bridge, out detail))
                        throw new InvalidOperationException("Accepted held-item action could not yield the source body hold: " + detail);
                    if (HeldAuthority.IsForcedDrop(command.Kind)) _dispatchedForcedCallbackEpoch = _forcedCallbackEpoch;
                    Added = Controller._ItemAddedEvent_k__BackingField?.Pointer ?? IntPtr.Zero;
                    Removed = Controller._ItemRemovedEvent_k__BackingField?.Pointer ?? IntPtr.Zero;
                    Routines.Clear(); Drops.Clear(); RemovalCount = DropStarts = MidActionPhases = 0;
                    Strength = 0; StrengthSeen = EntryDone = false; _receipt = null;
                    InventoryOwner._busy = _inventoryBusyHeld = true;
                    EntryPermit = true;
                    switch (command.Kind)
                    {
                        case HeldAuthority.MutationKind.Lift:
                            if (!Controller.Lift(command.After.State.ItemId, liftDonor))
                                throw new InvalidOperationException("Native lift declined after dispatch; retain donor custody.");
                            break;
                        case HeldAuthority.MutationKind.PutDown:
                            if (!Controller.PutDown(false, command.Drop, false, null, -1, -1f))
                                throw new InvalidOperationException("Native put-down declined after dispatch.");
                            break;
                        case HeldAuthority.MutationKind.Remove: Controller.RemoveHeldItem(false, command.Drop); break;
                        case HeldAuthority.MutationKind.DropAll: Controller.DropAll(); break;
                        case HeldAuthority.MutationKind.KnockDownDrop: Controller.IKnockDownReceiver_OnKnockDownStarted(); break;
                        case HeldAuthority.MutationKind.SwimmingDrop: Controller.IEnterSwimmingReceiver_OnEnterSwimming(); break;
                        case HeldAuthority.MutationKind.Settings:
                            Controller.SetInfiniteItemsHackSetting(command.After.State.InfiniteHack);
                            EntryPermit = true;
                            Controller.SetPlaceFullLoadHackSetting(command.After.State.PlaceFullLoadHack);
                            break;
                    }
                    if (EntryPermit || !EntryDone || FailureReason != null)
                        throw new InvalidOperationException(FailureReason ?? "Native entry permit was not consumed and returned.");
                    detail = "Native action executed once; delayed effects and aggregate settlement remain retained.";
                    return true;
                }
                catch (Exception error) { Fault(error.Message); detail = FailureReason; return false; }
                finally { EntryPermit = false; }
            }

            public bool TryCaptureReceipt(out HeldAuthority.NativeReceipt receipt, out string detail)
            {
                receipt = null;
                detail = FailureReason ?? "Exact native held-only effects have not all completed.";
                if (!Lifetime(true) || _heldCall != null) return false;
                if (Command == null)
                {
                    if (_lastCommand == null || _receipt == null) return false;
                    receipt = _receipt;
                    detail = "Previously completed native receipt retained; no action replayed.";
                    return true;
                }
                if (!EntryDone) return false;
                try
                {
                    foreach (var routine in Routines) if (!routine.Done) return false;
                    int removals = Command.Kind == HeldAuthority.MutationKind.DropAll || HeldAuthority.IsForcedDrop(Command.Kind) ? Command.Before.State.Count
                        : Command.Kind is HeldAuthority.MutationKind.PutDown or HeldAuthority.MutationKind.Remove ? 1 : 0;
                    if (RemovalCount != removals || Drops.Count != Command.DropCount || DropStarts != Command.DropCount) return false;
                    int hidden = Command.Before.State.Count - Command.After.State.Count;
                    int expectedRoutines = Command.DropCount + (removals == 0 ? 0 : hidden)
                        + (Command.Kind == HeldAuthority.MutationKind.Lift && Command.After.State.Count == 1 ? 1 : 0);
                    if (Routines.Count != expectedRoutines || Command.Kind == HeldAuthority.MutationKind.Lift
                        && (!StrengthSeen || Command.After.State.Count == 1 && MidActionPhases != 3)) return false;
                    if (HeldAuthority.IsForcedDrop(Command.Kind) && !ForcedCallbacksComplete)
                        throw new InvalidOperationException("Forced drop did not execute its exact native previous-hand callbacks.");
                    foreach (var effect in Drops) if (effect == null || !effect.Current()) throw new InvalidOperationException("Outgoing drop instance changed before settlement.");
                    if (!HeldAuthority.Snapshot.TryCaptureRetained(Bridge, Inventory, Controller, out var after, out detail)) return false;
                    if (after.Fingerprint != Command.After.Fingerprint)
                        throw new InvalidOperationException("Native held-only after-state differs from server reservation.");
                    int renderer = Math.Min(Controller._heldCount, Controller.GetHeldItem()?.GetActiveRenderersCount() ?? 0);
                    if (renderer < 0) throw new InvalidOperationException("Invalid native held renderer count.");
                    _receipt ??= new HeldAuthority.NativeReceipt(Command, after, Strength, renderer, Drops.ToArray());
                    receipt = _receipt;
                    detail = "Native entry, callbacks and retained delayed effects completed; no destination/stat acceptance claimed.";
                    return true;
                }
                catch (Exception error) { Fault(error.Message); detail = FailureReason; return false; }
            }

            /// <summary>Only the authenticated aggregate commit releases this source reservation.</summary>
            public bool TryAcknowledgeSettlement(ulong transaction, string commandFingerprint, out string detail)
            {
                detail = "Exact aggregate settlement and unchanged source completion are required.";
                if (!TryCaptureReceipt(out var receipt, out detail) || receipt.Command.Transaction != transaction
                    || receipt.Command.Fingerprint != commandFingerprint) return false;
                if (Command == null) return _lastCommand?.Fingerprint == commandFingerprint;
                if (!CompoundEquipmentSettled) return false;
                if (HeldAuthority.IsForcedDrop(Command.Kind) && _dispatchedForcedCallbackEpoch == _forcedCallbackEpoch)
                    _unownedForcedCallback = false;
                _settled = receipt.After; _lastCommand = Command; Command = null;
                LiftItem = null; LiftIdentity = null;
                ReleaseInventoryBusy();
                ClearCompoundEquipment();
                detail = "Exact source held-only reservation settled; its native guard remains installed.";
                return true;
            }

            internal bool Lifetime(bool bound, bool requireGameplay = true)
            {
                if (Thread != Environment.CurrentManagedThreadId || IsDisposed || FailureReason != null
                    || Load != SaveLifecycle.State.BeforeLoadCount || !Live(Root) || !Live(Inventory)
                    || !Live(Controller) || !Live(Character) || !Live(Vitals) || !Live(Camera)
                    || LocalPlayer.GameObject?.Pointer != Root.Pointer || LocalPlayer.Inventory?.Pointer != Inventory.Pointer
                    || LocalPlayer.FpCharacter?.Pointer != Character.Pointer || LocalPlayer.Vitals?.Pointer != Vitals.Pointer
                    || LocalPlayer.MainCam?.transform?.Pointer != Camera.Pointer
                    || Inventory.HeldOnlyItemController?.Pointer != Controller.Pointer || Controller._player?.Pointer != Inventory.Pointer
                    || Controller._logDropForce != Baseline.DropProfile.Force
                    || Controller._logDropUpForce != Baseline.DropProfile.UpForce
                    || !Controller.transform.IsChildOf(Root.transform)) return false;
                var catalog = Controller._heldOnlyItems;
                if (catalog == null || catalog.WasCollected || catalog.Pointer != _catalog || catalog.Length != _entries.Length) return false;
                for (int i = 0; i < _entries.Length; i++)
                {
                    var entry = catalog[i];
                    if (entry == null || entry.WasCollected || entry.Pointer != _entries[i] || entry._itemId != _definitions[i].ItemId
                        || entry._held == null || entry._held.WasCollected || entry._held.Pointer != _renderers[i]
                        || entry._held.Length != _definitions[i].HeldCapacity) return false;
                }
                if (bound && (Bridge == null || Bridge.IsServer || requireGameplay && !Bridge.IsGameplayActive || !Bridge.Peer.IsCurrent
                    || !Bridge.TryReadCurrentTarget(out var target) || target.isOwner || LocalPlayer.Entity?.Pointer != target.Pointer
                    || InventoryOwner == null || InventoryOwner.IsDisposed || InventoryOwner.IsFaulted || !InventoryOwner.Current())) return false;
                return Command == null || (Added == (Controller._ItemAddedEvent_k__BackingField?.Pointer ?? IntPtr.Zero)
                    && Removed == (Controller._ItemRemovedEvent_k__BackingField?.Pointer ?? IntPtr.Zero));
            }

            internal void Fault(string reason)
            { FailureReason ??= "Held-only custody uncertain; no replay/refund: " + reason; }

            private void ReleaseInventoryBusy()
            { if (_inventoryBusyHeld) { InventoryOwner._busy = false; _inventoryBusyHeld = false; } }

            public bool TryCancelBeforeRetirement(PlayerLifecycle lifecycle, out string detail)
            {
                detail = "Cancellation requires the unchanged paused original source, before any bridge binding or native dispatch.";
                if (!Lifetime(false) || Bridge != null || Command != null || _lastTransaction != 0 || _heldCall != null
                    || lifecycle == null || LocalPlayer.Entity?.Pointer != lifecycle.OriginalEntity?.Pointer
                    || LocalPlayer.Entity?.isOwner != true || !Baseline.TryValidateSource(lifecycle, Inventory, out detail)) return false;
                HeldSourceGuards.Remove(Controller.Pointer);
                if (ReferenceEquals(_heldOnlySource, this)) { _heldOnlySource = null; _heldIntent = null; }
                IsDisposed = true;
                detail = "Unchanged pre-retirement held-only guard cancelled; no item, action or effect was replayed.";
                return true;
            }

            public void Dispose()
            {
                if (Environment.CurrentManagedThreadId != Thread) throw new InvalidOperationException("Retire held-only source on its game thread.");
                if (IsDisposed) return;
                IsDisposed = true;
                if (Command != null) Fault("Source retired before aggregate settlement.");
                if (Bridge != null) Bridge.Retired -= Dispose;
                // Retain controller/routines/items and the guard, including unresolved outgoing
                // custody. A disconnected native coroutine must never regain write permission.
                if (Command == null) ReleaseInventoryBusy();
            }

            internal static bool Live(UnityEngine.Object value) => value != null && !value.WasCollected && value;
        }

        public static bool TryCaptureHeldOnlySource(BoltMigrationPlan plan, PlayerLifecycle lifecycle,
            PlayerInventory inventory, out HeldOnlySource source, out string detail)
        {
            source = null;
            detail = "Held-only hooks must precede source creation, and native actions must drain before capture.";
            if (!_heldHooksInstalled || Environment.CurrentManagedThreadId != _heldHookThread || _heldCall != null
                || HasObservedHeldOnlyWork(inventory)) return false;
            try
            {
                if (_heldOnlySource != null && _heldOnlySource.Load == SaveLifecycle.State.BeforeLoadCount
                    && HeldOnlySource.Live(_heldOnlySource.Root)) return false;
                if (!HeldAuthority.Snapshot.TryCaptureSource(plan, lifecycle, inventory, out var snapshot, out detail)
                    || LocalPlayer.Inventory?.Pointer != inventory.Pointer || !HeldOnlySource.Live(LocalPlayer.MainCam)) return false;
                var controller = inventory.HeldOnlyItemController.TryCast<HeldOnlyItemController>();
                if (HeldSourceGuards.ContainsKey(controller.Pointer))
                { detail = "This native controller still has a retained held-only lifetime guard."; return false; }
                var candidate = new HeldOnlySource(inventory, controller, snapshot);
                if (!candidate.Lifetime(false)) return false;
                _heldOnlySource?.Dispose();
                _heldIntent = null;
                HeldSourceGuards.Add(controller.Pointer, candidate);
                _heldOnlySource = source = candidate;
                detail = "Exact held-only controller captured under the lifecycle freeze; native write fence retained.";
                return true;
            }
            catch (Exception error) { detail = "Held-only source capture rejected: " + error.Message; return false; }
        }

        // Parent invokes these BEFORE its existing shared-writer prefix and returns false when
        // handled. No second detour, numeric replay or host-remote substitution is installed.
        internal static bool TryCaptureHeldOnlyStrength(Vitals vitals, float gain)
        {
            var source = _heldOnlySource;
            if (source?.Command?.Kind != HeldAuthority.MutationKind.Lift || vitals == null
                || vitals.Pointer != source.Vitals.Pointer || _heldCall?.Source != source) return false;
            if (!source.Lifetime(true) || _heldCall.Kind != HeldHook.Lift || source.StrengthSeen || !float.IsFinite(gain) || gain < 0)
                source.Fault("Unqualified native lift strength write.");
            else { source.StrengthSeen = true; source.Strength = gain; }
            return true;
        }

        internal static bool TryHandleHeldOnlyMidAction(FirstPersonCharacter character, bool value)
        {
            var source = _heldOnlySource;
            if (source == null || _heldCall?.Source != source || _heldCall.Routine?.Kind != HeldHook.LiftRoutine) return false;
            var routine = _heldCall.Routine;
            int bit = value && routine.Phase == 0 ? 1 : !value && routine.Phase == 3 ? 2 : 0;
            if (!source.Lifetime(true) || character?.Pointer != source.Character.Pointer || bit == 0 || (source.MidActionPhases & bit) != 0)
                source.Fault("Unqualified held-only mid-action phase.");
            else { source.MidActionPhases |= bit; character._isInMidAction = value; }
            return true; // Never run the original IPlayerState writer on the retained rig.
        }
    }
}
