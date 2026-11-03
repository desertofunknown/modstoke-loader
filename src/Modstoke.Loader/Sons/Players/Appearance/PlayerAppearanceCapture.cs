// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using global::Sons.Wearable.Armour;
using global::Sons.Wearable.Clothing;
using global::Sons.Wearable.Race;
using TheForest.Utils;
using UnityEngine;

namespace Modstoke.Sons
{
    // Reads which renderers make up the local player's look: the race head and arms, clothing, armour and any
    // other skinned body beside the rig, such as a mod's custom body. Read-only; it never applies a race,
    // clothing or armour and holds only references for one stage build.
    internal sealed class PlayerAppearanceCapture
    {
        private readonly PlayerRaceSystem _race;
        private readonly PlayerClothingSystem _clothing;
        private readonly PlayerArmourSystem _armour;
        private readonly List<IntPtr> _signature;

        private PlayerAppearanceCapture(Transform skeletonRoot, Transform space, PlayerRaceSystem race,
            PlayerClothingSystem clothing, PlayerArmourSystem armour)
        {
            SkeletonRoot = skeletonRoot;
            Space = space;
            _race = race;
            _clothing = clothing;
            _armour = armour;
            _signature = Signature();
        }

        internal Transform SkeletonRoot { get; }
        // The rig root's parent. Parts are placed relative to it.
        internal Transform Space { get; }
        internal List<Source> Sources { get; } = new();

        internal static bool TryRead(out PlayerAppearanceCapture capture, out string status)
        {
            capture = null;
            if (!TryReadRig(out var race, out var root, out var space, out status)) return false;
            var clothing = LocalPlayer.ClothingSystem;
            var stats = LocalPlayer.Stats;
            var armour = Live(stats) ? stats.ArmourSystem : null;
            if (!Live(clothing)) clothing = null;
            if (!Live(armour)) armour = null;
            if (race._modelCountToApply > 0 || clothing != null && (Count(clothing._clothingIdsCurrentlyLoading) > 0
                || Count(clothing._delayedBlendShapeDataToApply) > 0))
            {
                status = "look-settling";
                return false;
            }

            var result = new PlayerAppearanceCapture(root, space, race, clothing, armour);
            var claimed = new HashSet<IntPtr>();
            var named = new List<Source>();
            int expressions = -1;
            try { expressions = race.GetExpressionBlendsStartIndex(); }
            catch { }
            AddSource(named, claimed, "race.head", race._head, expressions);
            AddSource(named, claimed, "race.arms", race._arms, -1);
            if (clothing != null)
            {
                var hookups = clothing._clothingHookupData;
                for (int i = 0; hookups != null && i < hookups.Count; i++)
                {
                    var hookup = hookups[i];
                    if (hookup != null && Live(hookup.Instance))
                        AddSource(named, claimed, "clothing." + hookup.Slot, hookup.Instance.gameObject, -1);
                }
            }
            if (armour != null)
            {
                var slots = armour._armourSlotData;
                for (int i = 0; slots != null && i < slots.Count; i++)
                {
                    var slot = slots[i];
                    if (slot != null && Live(slot.ArmourInstance))
                        AddSource(named, claimed, "armour." + slot.Slot, slot.ArmourInstance.gameObject, -1);
                }
            }

            // Skinned bodies beside the rig, grouped by their top object under the space. Anything under Hips
            // is a held or attached item, not body.
            var hips = PreviewSkeleton.FindChild(root, "Hips");
            var bodies = new Dictionary<IntPtr, Source>();
            foreach (var renderer in space.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!Live(renderer) || claimed.Contains(renderer.Pointer)) continue;
                if (hips != null && renderer.transform.IsChildOf(hips)) continue;
                var top = renderer.transform;
                while (top.parent != null && top.parent.Pointer != space.Pointer) top = top.parent;
                if (top.parent == null || top.Pointer == root.Pointer) continue;
                if (!bodies.TryGetValue(top.Pointer, out var body))
                {
                    body = new Source(UniqueId(named, bodies.Values, "body." + top.name), top.gameObject, -1);
                    bodies.Add(top.Pointer, body);
                }
                claimed.Add(renderer.Pointer);
                body.Add(renderer);
            }
            foreach (var body in bodies.Values)
                if (body.Enabled.Count > 0 || body.Skipped.Count > 0) result.Sources.Add(body);
            result.Sources.AddRange(named);
            if (result.Sources.TrueForAll(source => source.Enabled.Count == 0))
            {
                status = "The local player has no visible renderers to capture.";
                return false;
            }
            capture = result;
            status = "";
            return true;
        }

        // The live rig without a capture, for parts that carry no bones of their own.
        internal static bool TryReadRig(out PlayerRaceSystem race, out Transform root, out Transform space, out string status)
        {
            race = null;
            root = space = null;
            if (!LocalPlayer.IsInWorld)
            {
                status = "The local player is not in the world.";
                return false;
            }
            var player = LocalPlayer.GameObject;
            race = LocalPlayer.RaceSystem;
            root = Live(race) ? race._animationRoot : null;
            if (!Live(player) || !Live(root) || !root.IsChildOf(player.transform))
            {
                status = "Waiting for the player model to load.";
                return false;
            }
            space = root.parent != null ? root.parent : root;
            status = "";
            return true;
        }

        // Cheap per-frame check for a source that appeared, vanished or was replaced since the capture.
        internal bool Changed()
        {
            if (!Live(_race) || !Live(SkeletonRoot) || !Live(Space) || !Live(LocalPlayer.RaceSystem)
                || LocalPlayer.RaceSystem.Pointer != _race.Pointer || _race._animationRoot == null
                || _race._animationRoot.Pointer != SkeletonRoot.Pointer)
                return true;
            var current = Signature();
            if (current.Count != _signature.Count) return true;
            for (int i = 0; i < current.Count; i++)
                if (current[i] != _signature[i]) return true;
            return false;
        }

        private List<IntPtr> Signature()
        {
            var signature = new List<IntPtr>(16)
            {
                (IntPtr)Space.childCount,
                (IntPtr)SkeletonRoot.childCount,
                Pointer(_race._head),
                Pointer(_race._arms)
            };
            if (Live(_clothing))
            {
                var hookups = _clothing._clothingHookupData;
                for (int i = 0; hookups != null && i < hookups.Count; i++)
                    signature.Add(hookups[i] != null ? Pointer(hookups[i].Instance) : IntPtr.Zero);
            }
            if (Live(_armour))
            {
                var slots = _armour._armourSlotData;
                for (int i = 0; slots != null && i < slots.Count; i++)
                    signature.Add(slots[i] != null ? Pointer(slots[i].ArmourInstance) : IntPtr.Zero);
            }
            return signature;
        }

        private static void AddSource(List<Source> sources, HashSet<IntPtr> claimed, string id, GameObject owner,
            int expressionOffset)
        {
            if (!Live(owner)) return;
            var source = new Source(UniqueId(sources, Array.Empty<Source>(), id), owner, expressionOffset);
            foreach (var renderer in owner.GetComponentsInChildren<Renderer>(true))
            {
                if (!Live(renderer) || !claimed.Add(renderer.Pointer)) continue;
                source.Add(renderer);
            }
            if (source.Enabled.Count > 0 || source.Skipped.Count > 0) sources.Add(source);
        }

        private static string UniqueId(IEnumerable<Source> first, IEnumerable<Source> second, string id)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in first) used.Add(source.PartId);
            foreach (var source in second) used.Add(source.PartId);
            string candidate = id;
            for (int i = 2; used.Contains(candidate); i++) candidate = id + "." + i;
            return candidate;
        }

        private static int Count<T>(Il2CppSystem.Collections.Generic.List<T> list) => list != null ? list.Count : 0;
        private static IntPtr Pointer(Component value) => Live(value) ? value.Pointer : IntPtr.Zero;
        private static IntPtr Pointer(GameObject value) => Live(value) ? value.Pointer : IntPtr.Zero;
        private static bool Live(UnityEngine.Object value) => value is not null && !value.WasCollected && value;

        internal sealed class Source
        {
            internal Source(string partId, GameObject owner, int expressionOffset)
            {
                PartId = partId;
                Owner = owner;
                ExpressionOffset = expressionOffset;
            }

            internal string PartId { get; }
            internal GameObject Owner { get; }
            internal int ExpressionOffset { get; }
            // Only renderers drawn now are copied; hidden ones are watched so showing one triggers a rebuild.
            internal List<Renderer> Enabled { get; } = new();
            internal List<Renderer> Skipped { get; } = new();

            internal void Add(Renderer renderer)
            {
                bool drawable = renderer.TryCast<SkinnedMeshRenderer>() != null || renderer.TryCast<MeshRenderer>() != null;
                if (!drawable) return;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy) Enabled.Add(renderer);
                else Skipped.Add(renderer);
            }
        }
    }
}
