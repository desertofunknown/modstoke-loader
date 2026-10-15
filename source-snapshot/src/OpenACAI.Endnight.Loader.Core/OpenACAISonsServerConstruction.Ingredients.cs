// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using OpenACAI.Endnight.Loader;

namespace OpenACAI.Sons
{
    public static partial class OpenACAISonsServerConstruction
    {
        /// <summary>
        /// Runs before server routing of StructureCraftingNodeAddIngredient, including local sends.
        /// Rejection stops this dispatch, not earlier sender-side prediction or inventory changes.
        /// </summary>
        public static event Action<OpenACAISonsBlueprintIngredientRequest> IngredientAdding;

        private static void InstallRoutingHook(Hook hook, string prefixName, Action<string> addStartupWarning)
        {
            PublishState(hook, OpenACAISonsConstructionHookStatus.Installing,
                "Registering the blueprint routing hook.");
            try
            {
                var target = AccessTools.Method(typeof(Bolt.EventDispatcher), nameof(Bolt.EventDispatcher.Dispatch),
                    new[] { typeof(Bolt.Event) });
                if (target == null)
                    throw new MissingMethodException(typeof(Bolt.EventDispatcher).FullName, "Dispatch(Event)");

                var prefix = new HarmonyMethod(typeof(OpenACAISonsServerConstruction), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(OpenACAIEndnightRuntime.PluginGuid + ".sons-blueprint-routing." + hook);
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(hook, OpenACAISonsConstructionHookStatus.Failed,
                    "Blueprint routing hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server blueprint " + hook + " policy hook failed: " + error);
                return;
            }

            PublishState(hook, OpenACAISonsConstructionHookStatus.Installed,
                "Blueprint routing hook registration succeeded. Live routing and policy handling remain unverified.");
        }

        private static bool BeforeIngredientDispatch(Bolt.Event __0)
        {
            var handlers = IngredientAdding;
            if (handlers == null)
                return true;
            var source = __0?.TryCast<StructureCraftingNodeAddIngredient>();
            if (source == null)
                return true;

            return DispatchRouted(source, handlers,
                static value => new OpenACAISonsBlueprintIngredientRequest((StructureCraftingNodeAddIngredient)value));
        }

        private static bool DispatchRouted<TRequest>(Bolt.Event source, Action<TRequest> handlers,
            Func<Bolt.Event, TRequest> readRequest) where TRequest : OpenACAISonsConstructionRequest
        {
            var forward = false;
            try
            {
                if (!BoltNetwork.isServer)
                {
                    forward = true;
                    return true;
                }

                forward = Dispatch(source, handlers, readRequest);
                return forward;
            }
            catch (Exception error)
            {
                ReportDispatchFailure("Reading blueprint routing context failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                if (!forward)
                {
                    // Skipped routing never transfers this event's storage to native listeners or channels.
                    try { source.FreeStorage(); }
                    catch (Exception error)
                    {
                        ReportDispatchFailure("Releasing rejected blueprint storage failed ("
                            + error.GetType().FullName + ").");
                    }
                }
            }
        }
    }

    /// <summary>Unvalidated ingredient request and routing copies. No native event or target is retained.</summary>
    public sealed class OpenACAISonsBlueprintIngredientRequest : OpenACAISonsConstructionRequest
    {
        internal OpenACAISonsBlueprintIngredientRequest(StructureCraftingNodeAddIngredient source) : base(source)
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
