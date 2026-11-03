// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Gameplay;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    public static partial class ServerCompanions
    {
        private sealed class RequestContext
        {
            private readonly RobbyOrderEvent _event;
            private readonly BoltConnection _source;
            private PlayerHandle _player;
            private BoltEntity _target;
            private Bolt.Entity _attachment;
            private Robby _robby;

            internal RequestContext(RobbyOrderEvent evnt, BoltConnection source, ServerSession session)
            {
                _event = evnt;
                _source = source;
                Request = new CompanionRequest(source.ConnectionId, session,
                    evnt.GettargetNetworkIdValue().PackedValue, evnt.GetplayerEntityNetworkIdValue().PackedValue,
                    evnt.orderEventType, evnt.orderType, evnt.orderSubType1, evnt.orderSubType2, evnt.Targets, Validate);
            }

            internal CompanionRequest Request { get; }

            private CompanionValidationStatus Validate()
            {
                try
                {
                    if (Environment.CurrentManagedThreadId != _ownerThread || !BoltNetwork.isRunning || !BoltNetwork.isServer)
                        return CompanionValidationStatus.ContextChanged;
                    var fields = ValidateRequestFields(Request);
                    if (fields != CompanionValidationStatus.Valid)
                        return fields;
                    var source = _event.RaisedBy;
                    var session = Request.SourceSession;
                    if (source == null || source.WasCollected || source.Pointer != _source.Pointer
                        || source.ConnectionId != Request.SourceConnectionId || session == null || !session.IsCurrent
                        || !ReferenceEquals(ServerSessions.ResolveSource(source), session))
                        return CompanionValidationStatus.SessionUnavailable;
                    if (_event.Targets != Request.RoutingTargets || _event.orderEventType != Request.RequestedEventType
                        || _event.orderType != Request.RequestedOrderType || _event.orderSubType1 != Request.RequestedSubType1
                        || _event.orderSubType2 != Request.RequestedSubType2
                        || _event.GettargetNetworkIdValue().PackedValue != Request.RequestedTargetNetworkId
                        || _event.GetplayerEntityNetworkIdValue().PackedValue != Request.RequestedPlayerNetworkId)
                        return CompanionValidationStatus.ContextChanged;

                    if (_player == null && !ServerWorld.TryCapturePlayerBinding(session, out _player, out _))
                        return CompanionValidationStatus.PlayerUnavailable;
                    if (!ServerWorld.TryReadPlayerBinding(_player, out var player, out _))
                        return CompanionValidationStatus.PlayerUnavailable;
                    var claimedPlayer = _event.playerEntity;
                    if (Request.RequestedPlayerNetworkId != player.NetworkId || claimedPlayer == null
                        || claimedPlayer.WasCollected || !claimedPlayer || !claimedPlayer.isAttached
                        || claimedPlayer.Pointer != _player.Player.Pointer)
                        return CompanionValidationStatus.PlayerMismatch;

                    var target = _event.target;
                    if (target == null || target.WasCollected || !target || !target.isAttached
                        || target.networkId.PackedValue != Request.RequestedTargetNetworkId)
                        return CompanionValidationStatus.TargetUnavailable;
                    if (!target.isOwner) return CompanionValidationStatus.TargetNotServerOwned;
                    var attachment = target._entity;
                    if (attachment == null || attachment.WasCollected
                        || !MultiplayerUtilities.TryGetComponentInEntityChildren<Robby>(target, out var robby, false)
                        || robby == null || robby.WasCollected || !robby)
                        return CompanionValidationStatus.TargetUnavailable;
                    if (_target is not null && (_target.WasCollected || !_target
                        || target.Pointer != _target.Pointer || _attachment.WasCollected
                        || attachment.Pointer != _attachment.Pointer || _robby.WasCollected || !_robby
                        || robby.Pointer != _robby.Pointer))
                        return CompanionValidationStatus.ContextChanged;
                    _target = target;
                    _attachment = attachment;
                    _robby = robby;

                    return Request.RequestedEventType == (int)Robby.OrderEventType.GiveOrders
                        ? ValidateOrder(robby, Request) : CompanionValidationStatus.Valid;
                }
                catch (Exception error)
                {
                    ReportFailure(error, "The request could not be validated.");
                    return CompanionValidationStatus.Failed;
                }
            }
        }

        // Checks that need only the copied request run first, so an unavailable read never hides them.
        private static CompanionValidationStatus ValidateRequestFields(CompanionRequest request)
        {
            if (request.RoutingTargets != (int)Bolt.GlobalTargets.OnlyServer)
                return CompanionValidationStatus.InvalidRouting;
            if (request.RequestedEventType < (int)Robby.OrderEventType.WaitForOrders
                || request.RequestedEventType > (int)Robby.OrderEventType.FinishOrderNoNote)
                return CompanionValidationStatus.InvalidEvent;
            if (request.RequestedEventType != (int)Robby.OrderEventType.GiveOrders || IsDataFreeOrder(request))
                return CompanionValidationStatus.Valid;
            if (request.RequestedOrderType < 0 || request.RequestedOrderType > (int)Robby.OrderType.MaintainBase)
                return CompanionValidationStatus.InvalidOrder;
            if ((Robby.OrderType)request.RequestedOrderType == Robby.OrderType.Get
                && (request.RequestedSubType2 < (int)Robby.DropLocationType.DropHere
                    || request.RequestedSubType2 > (int)Robby.DropLocationType.FillLogSled))
                return CompanionValidationStatus.InvalidSubtype;
            return CompanionValidationStatus.Valid;
        }

        // These native branches do not index order data or consume the supplied subtypes.
        private static bool IsDataFreeOrder(CompanionRequest request) =>
            (Robby.OrderType)request.RequestedOrderType is Robby.OrderType.Follow or Robby.OrderType.TakeBreak
                or Robby.OrderType.Equip;

        // Request fields were checked by ValidateRequestFields; this checks the indexed order data.
        private static CompanionValidationStatus ValidateOrder(Robby robby, CompanionRequest request)
        {
            if (IsDataFreeOrder(request))
                return CompanionValidationStatus.Valid;
            var data = robby._orderData;
            if (data == null || data.WasCollected || !data) return CompanionValidationStatus.OrderDataUnavailable;
            switch ((Robby.OrderType)request.RequestedOrderType)
            {
                case Robby.OrderType.Get: return ValidateEntry(data._getItemOrders, request.RequestedSubType1);
                case Robby.OrderType.Build: return ValidateEntry(data._buildItemOrders, request.RequestedSubType1);
                case Robby.OrderType.StayHere: return ValidateEntry(data._stayOrders, request.RequestedSubType1);
                case Robby.OrderType.ClearArea: return ValidateEntry(data._clearAreaOrders, request.RequestedSubType1);
                // Native MaintainBase selects entry zero, irrespective of the supplied subtype.
                case Robby.OrderType.MaintainBase: return ValidateEntry(data._maintainOrders, 0);
                default: return CompanionValidationStatus.InvalidOrder;
            }
        }

        private static CompanionValidationStatus ValidateEntry<T>(Il2CppSystem.Collections.Generic.List<T> orders, int index)
            where T : Il2CppObjectBase
        {
            if (orders == null || orders.WasCollected) return CompanionValidationStatus.OrderDataUnavailable;
            if (index < 0 || index >= orders.Count) return CompanionValidationStatus.InvalidSubtype;
            var entry = orders[index];
            return entry == null || entry.WasCollected ? CompanionValidationStatus.OrderDataUnavailable
                : CompanionValidationStatus.Valid;
        }
    }
}
