// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Numerics;
using System.Threading;
using Construction.Multiplayer;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Multiplayer;

namespace Modstoke.Sons
{
    /// <summary>Server policy and observations for native blueprint construction and crafted-structure relocation.</summary>
    public static partial class ServerConstruction
    {
        public const string ServiceKey = "openacai.sons.serverConstruction";

        private enum Hook { BlueprintPlacement, StructureRelocation, IngredientAddition, CompletionRequest, CompletionObservation }

        private static ServerConstructionState _state = new(
            ConstructionHookStatus.NotAttempted, "The blueprint-placement hook has not been installed.",
            ConstructionHookStatus.NotAttempted, "The structure-relocation hook has not been installed.",
            ConstructionHookStatus.NotAttempted, "The blueprint-ingredient hook has not been installed.",
            ConstructionHookStatus.NotAttempted, "The blueprint-completion request hook has not been installed.",
            ConstructionHookStatus.NotAttempted, "The blueprint-completion observation hooks have not been installed.");
        private static int _installationAttempted;
        private static int _ownerThreadId;
        private static int _reportedDispatchFailure;

        /// <summary>
        /// Runs before the server handles StructureCraftingNodeCreate, including placement from held relocation state.
        /// This does not cover host-local direct placement or free-form building.
        /// </summary>
        public static event Action<BlueprintPlacementRequest> BlueprintPlacing;

        /// <summary>
        /// Runs before the server handles DismantleScrewStructure and starts relocating a crafted structure.
        /// This is not a general destruction or structure-removal hook.
        /// </summary>
        public static event Action<StructureRelocationRequest> StructureRelocating;

        public static ServerConstructionState State => Volatile.Read(ref _state);

        internal static void InstallHarmonyPatches(HarmonySupportSnapshot support, Action<string> addStartupWarning)
        {
            if (Interlocked.Exchange(ref _installationAttempted, 1) != 0)
                return;
            if (support == null || !support.SafeForHarmonyPatches)
            {
                var detail = support?.Detail ?? "Harmony support information is unavailable.";
                PublishState(Hook.BlueprintPlacement, ConstructionHookStatus.Disabled, detail);
                PublishState(Hook.StructureRelocation, ConstructionHookStatus.Disabled, detail);
                PublishState(Hook.IngredientAddition, ConstructionHookStatus.Disabled, detail);
                PublishState(Hook.CompletionRequest, ConstructionHookStatus.Disabled, detail);
                PublishState(Hook.CompletionObservation, ConstructionHookStatus.Disabled, detail);
                addStartupWarning?.Invoke("Server construction policy hooks were not installed: " + detail);
                return;
            }

            _ownerThreadId = Environment.CurrentManagedThreadId;
            InstallHook(Hook.BlueprintPlacement, typeof(StructureCraftingNodeCreate), nameof(BeforeBlueprintPlacement), addStartupWarning);
            InstallHook(Hook.StructureRelocation, typeof(DismantleScrewStructure), nameof(BeforeStructureRelocation), addStartupWarning);
            InstallRoutingHook(Hook.IngredientAddition, DispatchRoute.ConstructionIngredients, addStartupWarning);
            InstallRoutingHook(Hook.CompletionRequest, DispatchRoute.ConstructionCompletion, addStartupWarning);
            InstallCompletionObservationHooks(addStartupWarning);
        }

        private static void InstallHook(Hook hook, Type eventType, string prefixName, Action<string> addStartupWarning)
        {
            PublishState(hook, ConstructionHookStatus.Installing, "Registering the construction hook.");
            try
            {
                var target = AccessTools.Method(typeof(FreeFormSystemNetworkProxyServer),
                    nameof(FreeFormSystemNetworkProxyServer.OnEvent), new[] { eventType });
                if (target == null)
                    throw new MissingMethodException(typeof(FreeFormSystemNetworkProxyServer).FullName, "OnEvent(" + eventType.Name + ")");

                var prefix = new HarmonyMethod(typeof(ServerConstruction), prefixName)
                {
                    priority = Priority.First,
                    before = new[] { "sonssdk_harmony" }
                };
                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-server-construction");
                harmony.Patch(target, prefix: prefix);
            }
            catch (Exception error)
            {
                PublishState(hook, ConstructionHookStatus.Failed,
                    "Construction hook registration failed; native patch state is uncertain.");
                addStartupWarning?.Invoke("Server construction " + hook + " policy hook failed: " + error);
                return;
            }

            PublishState(hook, ConstructionHookStatus.Installed,
                "Construction hook registration succeeded. This is not evidence of a received or blocked request.");
        }

        private static bool BeforeBlueprintPlacement(StructureCraftingNodeCreate __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, BlueprintPlacing,
                static source => new BlueprintPlacementRequest((StructureCraftingNodeCreate)source));

        private static bool BeforeStructureRelocation(DismantleScrewStructure __0, bool __runOriginal) =>
            __runOriginal && Dispatch(__0, StructureRelocating,
                static source => new StructureRelocationRequest((DismantleScrewStructure)source));

        private static bool Dispatch<TRequest>(Bolt.Event source, Action<TRequest> handlers, Func<Bolt.Event, TRequest> readRequest)
            where TRequest : ConstructionRequest
        {
            if (handlers == null)
                return true;
            if (Environment.CurrentManagedThreadId != _ownerThreadId)
            {
                ReportDispatchFailure("A construction callback ran outside the loader's game thread.");
                return false;
            }

            TRequest request = null;
            try
            {
                if (!BoltNetwork.isServer)
                    return true;

                request = readRequest(source);
                foreach (var handler in Delegate.EnumerateInvocationList(handlers))
                {
                    handler(request);
                    if (request.IsRejected)
                        return false;
                }

                return true;
            }
            catch (Exception error)
            {
                request?.Reject();
                ReportDispatchFailure("Reading construction context or running a policy handler failed ("
                    + error.GetType().FullName + ").");
                return false;
            }
            finally
            {
                request?.Close();
            }
        }

        private static void PublishState(Hook hook, ConstructionHookStatus status, string detail)
        {
            var previous = State;
            var state = new ServerConstructionState(
                hook == Hook.BlueprintPlacement ? status : previous.BlueprintPlacementStatus,
                hook == Hook.BlueprintPlacement ? detail : previous.BlueprintPlacementDetail,
                hook == Hook.StructureRelocation ? status : previous.StructureRelocationStatus,
                hook == Hook.StructureRelocation ? detail : previous.StructureRelocationDetail,
                hook == Hook.IngredientAddition ? status : previous.IngredientAdditionStatus,
                hook == Hook.IngredientAddition ? detail : previous.IngredientAdditionDetail,
                hook == Hook.CompletionRequest ? status : previous.CompletionRequestStatus,
                hook == Hook.CompletionRequest ? detail : previous.CompletionRequestDetail,
                hook == Hook.CompletionObservation ? status : previous.CompletionObservationStatus,
                hook == Hook.CompletionObservation ? detail : previous.CompletionObservationDetail);
            Volatile.Write(ref _state, state);
            Modding.SetService(ServiceKey, state);
        }

        private static void ReportDispatchFailure(string detail)
        {
            if (Interlocked.Exchange(ref _reportedDispatchFailure, 1) != 0)
                return;
            try
            {
                RuntimeEntryPoint.LogSource?.LogWarning("Server construction policy rejected a request: "
                    + detail + " Further dispatch failures will not be logged.");
            }
            catch (Exception) { }
        }
    }

    public enum ConstructionHookStatus
    {
        NotAttempted,
        Disabled,
        Installing,
        Installed,
        Failed
    }

    public sealed class ServerConstructionState
    {
        internal ServerConstructionState(ConstructionHookStatus blueprintPlacementStatus,
            string blueprintPlacementDetail, ConstructionHookStatus structureRelocationStatus, string structureRelocationDetail,
            ConstructionHookStatus ingredientAdditionStatus, string ingredientAdditionDetail,
            ConstructionHookStatus completionRequestStatus, string completionRequestDetail,
            ConstructionHookStatus completionObservationStatus, string completionObservationDetail)
        {
            BlueprintPlacementStatus = blueprintPlacementStatus;
            BlueprintPlacementDetail = blueprintPlacementDetail;
            StructureRelocationStatus = structureRelocationStatus;
            StructureRelocationDetail = structureRelocationDetail;
            IngredientAdditionStatus = ingredientAdditionStatus;
            IngredientAdditionDetail = ingredientAdditionDetail;
            CompletionRequestStatus = completionRequestStatus;
            CompletionRequestDetail = completionRequestDetail;
            CompletionObservationStatus = completionObservationStatus;
            CompletionObservationDetail = completionObservationDetail;
        }

        public ConstructionHookStatus BlueprintPlacementStatus { get; }
        public string BlueprintPlacementDetail { get; }
        public ConstructionHookStatus StructureRelocationStatus { get; }
        public string StructureRelocationDetail { get; }
        /// <summary>Pre-routing hook registration, not an observed request, resource debit or completed structure.</summary>
        public ConstructionHookStatus IngredientAdditionStatus { get; }
        public string IngredientAdditionDetail { get; }
        public ConstructionHookStatus CompletionRequestStatus { get; }
        public string CompletionRequestDetail { get; }
        /// <summary>Registration of all observation hooks, not proof of a completed or replicated structure.</summary>
        public ConstructionHookStatus CompletionObservationStatus { get; }
        public string CompletionObservationDetail { get; }
    }

    /// <summary>Managed source context and a synchronous decision window; no native objects survive dispatch.</summary>
    public abstract class ConstructionRequest
    {
        private readonly int _dispatchThreadId = Environment.CurrentManagedThreadId;
        private bool _acceptingDecisions = true;
        private bool _rejected;

        internal ConstructionRequest(Bolt.Event source)
        {
            var connection = source.RaisedBy;
            SourceConnectionId = connection == null ? null : connection.ConnectionId;
            if (connection != null && GameServerManager.GetRegisteredClient(connection, out var steamId)
                && steamId.m_SteamID != 0)
                RegisteredSteamId = steamId.m_SteamID;
            SourceSession = ServerSessions.ResolveSource(connection);
            ReceivedAt = DateTimeOffset.UtcNow;
            ReceivedUnityFrame = UnityEngine.Time.frameCount;
            ReceivedBoltFrame = BoltNetwork.frame;
        }

        /// <summary>Transport source ID, or null for a server-local event. IDs can be reused.</summary>
        public uint? SourceConnectionId { get; }
        /// <summary>Event-time game registration, not independent authentication or permission.</summary>
        public ulong? RegisteredSteamId { get; }
        /// <summary>Exact source lifetime when resolvable. Its metadata can change after dispatch.</summary>
        public ServerSession SourceSession { get; }
        public DateTimeOffset ReceivedAt { get; }
        /// <summary>Unity frame at dispatch, for checking that a read such as TryReadPlayerPose ran in the same frame.</summary>
        public int ReceivedUnityFrame { get; }
        /// <summary>Host BoltNetwork.frame at dispatch: a fixed-step count, not the sender's frame or a Unity frame.</summary>
        public int ReceivedBoltFrame { get; }
        public bool IsRejected => Volatile.Read(ref _rejected);

        internal bool IsDispatchOpen => Environment.CurrentManagedThreadId == _dispatchThreadId && _acceptingDecisions;

        /// <summary>
        /// Skips this native server operation, without undoing earlier relocation or client-side effects.
        /// Returns false outside synchronous dispatch or on another thread. Rejection cannot be undone.
        /// </summary>
        public bool Reject()
        {
            if (!IsDispatchOpen)
                return false;
            Volatile.Write(ref _rejected, true);
            return true;
        }

        internal virtual void Close()
        {
            _acceptingDecisions = false;
        }
    }

    /// <summary>Unvalidated StructureCraftingNodeCreate payload, distinct from free-form placement.</summary>
    public sealed class BlueprintPlacementRequest : ConstructionRequest
    {
        internal BlueprintPlacementRequest(StructureCraftingNodeCreate source) : base(source)
        {
            RequestedPrefabId = source.PrefabId.Value;
            var position = source.Position;
            RequestedPosition = new Vector3(position.x, position.y, position.z);
            var rotation = source.Rotation;
            RequestedRotation = new Quaternion(rotation.x, rotation.y, rotation.z, rotation.w);
            RequestedTreeId = Guid.Parse(source.TreeId.ToString());
            RequestedPlaceBuilt = source.PlaceBuilt;
            RequestedParentNetworkId = source.GetParentNetworkIdValue().PackedValue;
        }

        /// <summary>Requested network prefab, not an inventory item ID or proof of an allowed recipe.</summary>
        public int RequestedPrefabId { get; }
        /// <summary>Raw payload position. Native handling can transform it relative to the requested parent.</summary>
        public Vector3 RequestedPosition { get; }
        /// <summary>Raw quaternion, without normalization; native parent handling can transform it.</summary>
        public Quaternion RequestedRotation { get; }
        /// <summary>Requested supporting-tree locator. Empty means no locator was supplied.</summary>
        public Guid RequestedTreeId { get; }
        /// <summary>Request to use built relocation state, not proof that the sender holds a matching structure.</summary>
        public bool RequestedPlaceBuilt { get; }
        /// <summary>Raw parent network ID, even if unresolved. It does not establish access or valid attachment.</summary>
        public ulong RequestedParentNetworkId { get; }
    }

    /// <summary>Unvalidated DismantleScrewStructure request to start crafted-structure relocation.</summary>
    public sealed class StructureRelocationRequest : ConstructionRequest
    {
        internal StructureRelocationRequest(DismantleScrewStructure source) : base(source)
        {
            RequestedTargetNetworkId = source.GetTargetStructureNetworkIdValue().PackedValue;
        }

        /// <summary>Sender-selected structure, not proof of existence, proximity, ownership or removability.</summary>
        public ulong RequestedTargetNetworkId { get; }
    }
}
