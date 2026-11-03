// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class ServerConstruction
    {
        /// <summary>
        /// Runs before server routing of StructureCraftingNodeAddIngredient, including local sends.
        /// Rejection stops this dispatch, not earlier sender-side prediction or inventory changes.
        /// </summary>
        public static event Action<BlueprintIngredientRequest> IngredientAdding;

        private static void InstallRoutingHook(Hook hook, DispatchRoute route, Action<string> addStartupWarning)
        {
            PublishState(hook, ConstructionHookStatus.Installing,
                "Registering the blueprint routing hook.");
            try
            {
                ServerDispatchGate.Register(route);
            }
            catch (Exception error)
            {
                PublishState(hook, ConstructionHookStatus.Failed,
                    "Blueprint routing hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server blueprint " + hook + " policy hook failed: " + error);
                return;
            }

            PublishState(hook, ConstructionHookStatus.Installed,
                "Blueprint routing hook registration succeeded. Live routing and policy handling remain unverified.");
        }

        internal static DispatchVerdict RouteIngredientDispatch(Bolt.Event source)
        {
            var handlers = IngredientAdding;
            if (handlers == null)
                return DispatchVerdict.Forward;
            var ingredient = source.TryCast<StructureCraftingNodeAddIngredient>();
            if (ingredient == null)
                return DispatchVerdict.Forward;

            return DispatchRouted(ingredient, handlers,
                static value => new BlueprintIngredientRequest((StructureCraftingNodeAddIngredient)value));
        }

        private static DispatchVerdict DispatchRouted<TRequest>(Bolt.Event source, Action<TRequest> handlers,
            Func<Bolt.Event, TRequest> readRequest) where TRequest : ConstructionRequest
        {
            try
            {
                if (!BoltNetwork.isServer)
                    return DispatchVerdict.Forward;

                return Dispatch(source, handlers, readRequest) ? DispatchVerdict.Forward : DispatchVerdict.Reject;
            }
            catch (Exception error)
            {
                ReportDispatchFailure("Reading blueprint routing context failed ("
                    + error.GetType().FullName + ").");
                return DispatchVerdict.Reject;
            }
        }

        internal static void ReportReleaseFailure(Exception error) =>
            ReportDispatchFailure("Releasing rejected blueprint storage failed (" + error.GetType().FullName + ").");
    }

    /// <summary>Unvalidated ingredient request and routing copies. No native event or target is retained.</summary>
    public sealed class BlueprintIngredientRequest : ConstructionRequest
    {
        internal BlueprintIngredientRequest(StructureCraftingNodeAddIngredient source) : base(source)
        {
            RequestedTargetNetworkId = source.GetTargetNetworkIdValue().PackedValue;
            RequestedItemId = source.IngredientId;
            RequestedAmount = source.Amount;
            RoutingTargets = source.Targets;
            var entity = source.TargetEntity;
            RoutedEntityNetworkId = entity == null ? null : entity.NetworkId.PackedValue;
            var connection = source.TargetConnection;
            RoutedConnectionId = connection == null ? null : connection.ConnectionId;
        }

        /// <summary>Raw payload target, including zero or unresolved IDs; not proof of a crafting node.</summary>
        public ulong RequestedTargetNetworkId { get; }
        /// <summary>Claimed ingredient item ID, not a recipe ID or proof of possession.</summary>
        public int RequestedItemId { get; }
        /// <summary>Raw requested amount, not a count actually applied or removed from inventory.</summary>
        public int RequestedAmount { get; }
        /// <summary>Raw Bolt routing mode, separate from the payload target.</summary>
        public int RoutingTargets { get; }
        public ulong? RoutedEntityNetworkId { get; }
        public uint? RoutedConnectionId { get; }
    }
}
