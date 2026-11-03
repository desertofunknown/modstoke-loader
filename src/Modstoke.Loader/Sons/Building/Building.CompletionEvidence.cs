// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Construction;
using global::Sons.Crafting.Structures;

namespace Modstoke.Sons
{
    public static partial class Building
    {
        internal static event Action<BlueprintCompletionObservation, IReadOnlyList<BoltEntity>, IReadOnlyList<Structure>> CompletionIdentities;
        internal static bool NeedsCompletionIdentities => CompletionIdentities != null;

        // Called with aligned scope objects BEFORE clearing them or dispatching public handlers.
        // Public snapshots stay managed-only; the observation handle owns these native roots.
        internal static void RetainCompletionIdentities(BlueprintCompletionObservation source,
            IReadOnlyList<BoltEntity> entities, IReadOnlyList<Structure> structures)
        {
            var handlers = CompletionIdentities;
            if (handlers == null || !OnNativeThread) return;
            foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            {
                try { handler(source, entities, structures); }
                catch { /* Evidence must never alter completion or its original exception. */ }
            }
        }
    }

    public sealed partial class ConstructionObservation
    {
        private BlueprintCompletionObservation _identitySource;
        private BoltEntity[] _completionEntities = Array.Empty<BoltEntity>();
        private Structure[] _completionStructures = Array.Empty<Structure>();
        private RuntimeStructureDatabase _completionDatabase;

        private void CaptureCompletionIdentities(BlueprintCompletionObservation source,
            IReadOnlyList<BoltEntity> entities, IReadOnlyList<Structure> structures)
        {
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread || !BlueprintNetworkId.HasValue
                || source.BlueprintNetworkId != BlueprintNetworkId || source.RecipeId != RecipeId || !SameSystem()
                || !BoltNetwork.isRunning || !BoltNetwork.isServer) return;
            ClearCompletionIdentities();
            if (entities.Count != source.CreatedEntities.Count || structures.Count != source.RegisteredStructures.Count) return;
            var retainedEntities = new BoltEntity[Math.Min(4096, entities.Count)];
            var retainedStructures = new Structure[Math.Min(4096, structures.Count)];
            for (int i = 0; i < retainedEntities.Length; i++) retainedEntities[i] = entities[i];
            for (int i = 0; i < retainedStructures.Length; i++) retainedStructures[i] = structures[i];
            _completionDatabase = RuntimeStructureDatabase._instance;
            _completionEntities = retainedEntities;
            _completionStructures = retainedStructures;
            _identitySource = source;
        }

        private void ClearCompletionIdentities()
        {
            _identitySource = null;
            _completionDatabase = null;
            _completionEntities = Array.Empty<BoltEntity>();
            _completionStructures = Array.Empty<Structure>();
        }

        /// <summary>
        /// Reads only results supplied by the matching server completion hook. With the identity
        /// retention hook installed, delayed-attach objects can become readable without a search.
        /// Free-form readback always requires retained exact objects, the original database/world
        /// and current server authority. Missing hooks never fall back to type/position matching.
        /// Native structure-list membership and an assigned save ID are not disk-save or replication acknowledgments.
        /// No completion command, inventory mutation, listener or polling task is installed.
        /// </summary>
        public bool TryReadAuthoritativeCompletion(out CompletionReadback readback)
        {
            readback = null;
            if (IsDisposed || System.Environment.CurrentManagedThreadId != _ownerThread) return false;
            try
            {
                var source = LastServerObservation;
                if (source == null || !SameSystem() || !BoltNetwork.isRunning || !BoltNetwork.isServer
                    || !_recipe || !_system._recipeDatabase.TryGetRecipeById(RecipeId, out var recipe) || recipe != _recipe) return false;
                var results = new CompletionEntityReadback[Math.Min(4096, source.CreatedEntities.Count)];
                for (int i = 0; i < results.Length; i++) results[i] = ReadCompletionEntity(source.CreatedEntities[i],
                    ReferenceEquals(source, _identitySource) && i < _completionEntities.Length ? _completionEntities[i] : null);
                var structures = new CompletionFreeFormReadback[Math.Min(4096, source.RegisteredStructures.Count)];
                for (int i = 0; i < structures.Length; i++) structures[i] = ReadCompletionStructure(source.RegisteredStructures[i],
                    ReferenceEquals(source, _identitySource) && i < _completionStructures.Length ? _completionStructures[i] : null);
                readback = new(source, results, structures);
                return true;
            }
            catch { return false; }
        }

        private CompletionEntityReadback ReadCompletionEntity(BlueprintEntityResult source, BoltEntity retained)
        {
            try
            {
                if (source.ReadStatus != ConstructionResultReadStatus.Available || !source.PrefabId.HasValue
                    || !ScrewStructureManager._instance || ScrewStructureManager._instance._database != _system._recipeDatabase)
                    return new(source, ConstructionResultReadStatus.Unavailable);
                var entity = retained;
                if (ReferenceEquals(entity, null) && source.NetworkId.HasValue && source.NetworkId.Value != 0)
                    entity = BoltNetwork.FindEntity(new Bolt.NetworkId(source.NetworkId.Value));
                if (!entity || !entity.isAttached || entity.networkId.PackedValue == 0
                    || (source.NetworkId.HasValue && entity.networkId.PackedValue != source.NetworkId.Value)
                    || BoltNetwork.FindEntity(entity.networkId) != entity || entity.prefabId.Value != source.PrefabId.Value)
                    return new(source, ConstructionResultReadStatus.Unavailable);
                var screw = entity.GetComponent<ScrewStructure>();
                var electric = entity.GetComponent<ElectricDeviceScrewStructure>();
                var structure = screw ? screw.TryCast<IScrewStructure>() : electric ? electric.TryCast<IScrewStructure>() : null;
                if (structure == null || structure.Recipe != _recipe)
                    return new(source, ConstructionResultReadStatus.Unavailable);
                var identity = structure.TryCast<IScrewStructureSaveID>();
                if (identity == null) return new(source, ConstructionResultReadStatus.Unavailable);
                int saveId = identity.SaveId;
                var registeredStructures = ScrewStructureManager._instance._structures;
                if (registeredStructures == null)
                    return new(source, ConstructionResultReadStatus.Unavailable);
                // TryGetStructureBySaveID is load-only in the pinned native body. Never call it
                // during gameplay or treat its index-based save IDs as a live identity registry.
                bool registered = registeredStructures.Contains(structure);
                return new(source, ConstructionResultReadStatus.Available, entity.isOwner, saveId, registered, entity.networkId.PackedValue);
            }
            catch { return new(source, ConstructionResultReadStatus.Failed); }
        }

        private CompletionFreeFormReadback ReadCompletionStructure(BlueprintFreeFormResult source, Structure retained)
        {
            try
            {
                if (source.ReadStatus != ConstructionResultReadStatus.Available || !retained
                    || !Building.NativeFreeFormStructureSupported(retained)
                    || !_completionDatabase || RuntimeStructureDatabase._instance != _completionDatabase
                    || source.InstanceId == 0 || source.InstanceId > int.MaxValue
                    || retained.TypeID != source.TypeId || retained.InstanceID != source.InstanceId
                    || !RuntimeStructureDatabase.TryGetStructure(source.TypeId, (int)source.InstanceId, out var current)
                    || current != retained) return new(source, ConstructionResultReadStatus.Unavailable);
                var root = retained.ElementRoot;
                if (!root || root.childCount > 4096) return new(source, ConstructionResultReadStatus.Unavailable);
                var profiles = new List<ushort>();
                for (int i = 0; i < root.childCount; i++)
                {
                    var element = root.GetChild(i).GetComponent<StructureElement>();
                    if (!element) continue;
                    if (!element._profile) return new(source, ConstructionResultReadStatus.Unavailable);
                    profiles.Add(element._profile._id);
                }
                return new(source, ConstructionResultReadStatus.Available, profiles.ToArray());
            }
            catch { return new(source, ConstructionResultReadStatus.Failed); }
        }
    }

    /// <summary>Fresh bounded readback of previously observed server factory results, not proof that construction succeeded.</summary>
    public sealed class CompletionReadback
    {
        internal CompletionReadback(BlueprintCompletionObservation source, CompletionEntityReadback[] entities,
            CompletionFreeFormReadback[] structures)
        { Source = source; Entities = Array.AsReadOnly(entities); Structures = Array.AsReadOnly(structures); ObservedAt = DateTimeOffset.UtcNow; }
        public BlueprintCompletionObservation Source { get; }
        public IReadOnlyList<CompletionEntityReadback> Entities { get; }
        public IReadOnlyList<CompletionFreeFormReadback> Structures { get; }
        public bool ResultsTruncated => Structures.Count != Source.RegisteredStructures.Count || Entities.Count != Source.CreatedEntities.Count;
        public DateTimeOffset ObservedAt { get; }
    }

    public sealed class CompletionEntityReadback
    {
        internal CompletionEntityReadback(BlueprintEntityResult source,
            ConstructionResultReadStatus status, bool? serverOwnsEntity = null, int? saveId = null, bool? nativeStructureRegistered = null,
            ulong? currentNetworkId = null)
        { Source = source; ReadStatus = status; ServerOwnsEntity = serverOwnsEntity; SaveId = saveId; NativeStructureRegistered = nativeStructureRegistered; CurrentNetworkId = currentNetworkId; }
        public BlueprintEntityResult Source { get; }
        public ConstructionResultReadStatus ReadStatus { get; }
        public bool? ServerOwnsEntity { get; }
        /// <summary>Observed native field value, potentially unassigned or stale; not a stable ID or proof that a save was written.</summary>
        public int? SaveId { get; }
        /// <summary>The native manager's structure list contains the exact live structure at read time.</summary>
        public bool? NativeStructureRegistered { get; }
        /// <summary>Current attached ID, including attachment of an exact retained factory result after the original observation.</summary>
        public ulong? CurrentNetworkId { get; }
    }

    /// <summary>Fresh exact database membership and element readback, not a completed conversion, save or peer acknowledgment.</summary>
    public sealed class CompletionFreeFormReadback
    {
        internal CompletionFreeFormReadback(BlueprintFreeFormResult source,
            ConstructionResultReadStatus status, ushort[] profileIds = null)
        { Source = source; ReadStatus = status; ElementProfileIds = Array.AsReadOnly(profileIds ?? Array.Empty<ushort>()); }
        public BlueprintFreeFormResult Source { get; }
        public ConstructionResultReadStatus ReadStatus { get; }
        public IReadOnlyList<ushort> ElementProfileIds { get; }
    }
}
