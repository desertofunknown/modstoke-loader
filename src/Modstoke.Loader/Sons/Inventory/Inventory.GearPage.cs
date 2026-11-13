// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using HarmonyLib;
using Modstoke.Loader;
using global::Sons.Cutscenes;
using global::Sons.Gui;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using TheForest.Items.Inventory;
using TheForest.Utils;
using UnityEngine;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;
using SavedPlayerState = global::Sons.Player.PlayerState;

namespace Modstoke.Sons
{
    /// <summary>The part of the mod gear page an item lies in.</summary>
    public enum MatCategory
    {
        Device,
        WeaponPart,
        Tool,
        Consumable,
        Other
    }

    // The mod gear page: the roll bag with the game's own items put away and mod items laid out in their
    // place. The game's inventory mode key steps through the pages: the game's items, mod gear while the
    // player carries any, then the game's building materials while creative mode is on. The crafting mat
    // stays put between the first two, so a mod item can combine with one of the game's.
    public static partial class Inventory
    {
        private const int GearDonorItemId = 412;
        // Between items in a row, and between rows: a near row hides some of the one behind it.
        private const float GearGap = 0.045f, GearRowGap = 0.06f;
        private const string ModePrompt = "screen.toggleInventoryMode";
        // A turned item points its right-hand end away from the player.
        private static readonly Quaternion GearTurn = Quaternion.Euler(0, -90, 0);
        // Where the inventory camera rests, in the bag's layout space. A tall item's top appears to lean away
        // from the point under the camera, so its row moves back by half that lean to look centred in its pocket.
        private static readonly Vector3 GearEye = new(0.024f, 1.015f, 0.81f);

        // The roll bag's pockets in its layout space (metres; +X right of the screen, +Z away from the player):
        // the fabric between its zip strips and dividers, taken from the bag's meshes less a 12 mm margin. A
        // category fills its own pockets in this order, nearest the crafting mat first; any spills onto the flaps.
        private static readonly GearPocket[] GearPockets =
        {
            // Left of the crafting mat: two columns of four pockets.
            new(MatCategory.Device, -0.881f, -0.625f, 1.487f, 1.686f),
            new(MatCategory.Device, -1.268f, -1.015f, 1.487f, 1.686f),
            new(MatCategory.Device, -0.881f, -0.625f, 1.230f, 1.441f),
            new(MatCategory.Device, -1.268f, -1.015f, 1.230f, 1.441f),
            new(MatCategory.Device, -0.881f, -0.625f, 1.732f, 1.940f),
            new(MatCategory.Device, -1.268f, -1.015f, 1.732f, 1.940f),
            new(MatCategory.Device, -0.881f, -0.625f, 1.010f, 1.183f),
            new(MatCategory.Device, -1.268f, -1.015f, 1.010f, 1.183f),
            // Right of it: two columns the length of the bag.
            new(MatCategory.WeaponPart, 0.647f, 0.885f, 1.030f, 1.950f),
            new(MatCategory.WeaponPart, 1.012f, 1.248f, 1.020f, 1.950f),
            // Above it, in front of the weapon cases.
            new(MatCategory.Tool, -0.496f, -0.235f, 1.490f, 1.930f),
            new(MatCategory.Tool, -0.111f, 0.138f, 1.490f, 1.930f),
            new(MatCategory.Tool, 0.264f, 0.526f, 1.490f, 1.930f),
            // The next two columns to the right.
            new(MatCategory.Consumable, 1.374f, 1.588f, 1.020f, 1.950f),
            new(MatCategory.Consumable, 1.714f, 1.935f, 1.020f, 1.950f),
            // The open panel on the far left, then the far right pockets.
            new(MatCategory.Other, -1.780f, -1.395f, 1.210f, 1.720f),
            new(MatCategory.Other, 2.082f, 2.303f, 1.539f, 1.730f),
            new(MatCategory.Other, 2.082f, 2.303f, 1.272f, 1.455f),
            new(MatCategory.Other, 2.082f, 2.303f, 1.772f, 1.950f),
            new(MatCategory.Other, 2.082f, 2.303f, 1.020f, 1.230f),
            // Lower centre flap, two rows of four; the last of the first row stops short of the light switch.
            new(null, -0.520f, -0.274f, 0.687f, 0.918f),
            new(null, -0.233f, -0.039f, 0.687f, 0.918f),
            new(null, 0.045f, 0.243f, 0.687f, 0.918f),
            new(null, 0.287f, 0.520f, 0.687f, 0.890f),
            new(null, -0.520f, -0.274f, 0.345f, 0.576f),
            new(null, -0.233f, -0.039f, 0.345f, 0.576f),
            new(null, 0.045f, 0.243f, 0.345f, 0.576f),
            new(null, 0.287f, 0.520f, 0.345f, 0.576f),
            // Lower right flap.
            new(null, 1.200f, 1.451f, 0.585f, 0.858f),
            new(null, 1.490f, 1.693f, 0.585f, 0.858f),
            new(null, 1.735f, 1.951f, 0.585f, 0.858f),
            new(null, 1.990f, 2.220f, 0.585f, 0.858f),
        };

        private static readonly Dictionary<int, GearPlace> GearPlaces = new();
        private static readonly Dictionary<IntPtr, GearPage> GearPages = new();
        private static bool _gearHookAttempted, _gearHookInstalled, _gearShown, _gearOpen, _gearPrompt;
        private static bool _gearFailureLogged, _gearFullLogged;

        /// <summary>Creates an inactive counted inventory group on the mod gear page, in a place the SDK picks.</summary>
        /// <remarks>
        /// Places follow category, then item id, and are packed into the bag's pockets from each item's footprint
        /// (metres across the screen and towards the player), so one set of mods always lays out the same way.
        /// The SDK owns Root's position and turn: it moves Root when other items register, and lays an item
        /// lengthwise in a long pocket, or where it is too wide to lie across. Lift and turn the visuals on Item.
        /// Everything else is as for <see cref="TryCreateLayout"/>, without an anchor.
        /// </remarks>
        public static bool TryCreateGearLayout(ItemData definition, MatCategory category, Vector2 footprint,
            out InventoryLayout layout, out string status)
        {
            layout = null;
            if (!Enum.IsDefined(category) || !(footprint.x > 0) || !(footprint.y > 0) || footprint.x > 2 || footprint.y > 2)
            {
                status = "A gear place needs a known category and a footprint of up to two metres each way.";
                return false;
            }
            if (!TryCreateGearLayout(definition, out layout, out status)) return false;
            SetGearPlace(layout.ItemId, category, footprint, Vector2.zero, 0);
            return true;
        }

        private static bool TryCreateGearLayout(ItemData definition, out InventoryLayout layout, out string status)
        {
            layout = null;
            if (!TryGetCurrentInventory(out var inventory, out status)) return false;
            InventoryLayoutItemGroup donor = null;
            try
            {
                var mat = MatContainer(inventory);
                foreach (var group in (mat != null ? mat.gameObject : inventory.gameObject)
                    .GetComponentsInChildren<InventoryLayoutItemGroup>(true))
                    if (InventoryLayout.Live(group) && group._itemId == GearDonorItemId)
                    {
                        donor = group;
                        break;
                    }
            }
            catch (Exception error)
            {
                status = "Could not search the inventory mat: " + error.Message;
                return false;
            }
            if (donor == null)
            {
                status = "The inventory mat has no group for item " + GearDonorItemId + " to take hover and audio settings from.";
                return false;
            }
            return TryCreateLayout(definition, donor, true, out layout, out status);
        }

        /// <summary>The gear page's root for an inventory whose mat groups live under the container.</summary>
        private static bool TryGetGearPage(PlayerInventory inventory, Transform container, out GearPage page, out string status)
        {
            status = "";
            EnsureGearPageHook();
            if (GearPages.TryGetValue(inventory.Pointer, out page))
            {
                if (InventoryLayout.Live(page.Root) && InventoryLayout.Same(page.Container, container)) return true;
                if (InventoryLayout.Live(page.Root)) Object.Destroy(page.Root);
                GearPages.Remove(inventory.Pointer);
                page = null;
            }
            var system = container.parent;
            var cutscene = InventoryLayout.Live(system) ? system.GetComponent<InventoryCutscene>() : null;
            if (!InventoryLayout.Live(cutscene) || !InventoryLayout.Same(cutscene._inventoryLayoutItemGroupsRoot, container))
            {
                status = "The inventory mat's own item groups could not be told apart, so it has no gear page.";
                return false;
            }
            // A sibling of the game's groups, as its building materials are, so one page shows at a time.
            var root = new GameObject("Mod gear layout groups");
            root.SetActive(false);
            root.layer = container.gameObject.layer;
            root.transform.SetParent(system, false);
            root.transform.localPosition = container.localPosition;
            root.transform.localRotation = container.localRotation;
            root.transform.localScale = container.localScale;
            page = new GearPage(root, container, cutscene);
            GearPages[inventory.Pointer] = page;
            return true;
        }

        /// <summary>The inventory's gear page root, once a layout has been made on it; otherwise null.</summary>
        internal static Transform GearRoot(PlayerInventory inventory)
            => inventory is not null && GearPages.TryGetValue(inventory.Pointer, out var page)
                && InventoryLayout.Live(page.Root) ? page.Root.transform : null;

        private static void SetGearPlace(int itemId, MatCategory category, Vector2 size, Vector2 centre, float height)
        {
            if (GearPlaces.TryGetValue(itemId, out var place) && place.Category == category && place.Size == size
                && place.Centre == centre && place.Height == height)
            {
                MoveToGearPlace(itemId);
                return;
            }
            GearPlaces[itemId] = new GearPlace(itemId, category, size, centre, height);
            ArrangeGearPage();
        }

        // Every known item keeps its place whether or not the player carries one, as the game's own items do.
        private static void ArrangeGearPage()
        {
            foreach (var pocket in GearPockets) pocket.Clear();
            var places = new List<GearPlace>(GearPlaces.Values);
            places.Sort((a, b) => a.Category != b.Category ? a.Category.CompareTo(b.Category) : a.ItemId.CompareTo(b.ItemId));
            foreach (var place in places)
            {
                if (TakeGearPocket(place)) continue;
                place.Turned = false;
                place.Position = Array.Find(GearPockets, pocket => pocket.Category == place.Category).Middle;
                if (_gearFullLogged) continue;
                _gearFullLogged = true;
                RuntimeEntryPoint.LogSource?.LogWarning("The mod gear page has no pocket left for item " + place.ItemId
                    + "; it and any after it overlap the first items of their category.");
            }
            foreach (var pocket in GearPockets) pocket.Settle();
            foreach (var layout in LayoutOwners.Values)
                if (layout.OnGearPage) MoveToGearPlace(layout);
        }

        // Its category's pockets, then the flaps, then whatever another category has left.
        private static bool TakeGearPocket(GearPlace place)
        {
            foreach (var pocket in GearPockets)
                if (pocket.Category == place.Category && pocket.TryTake(place)) return true;
            foreach (var pocket in GearPockets)
                if (pocket.Category is null && pocket.TryTake(place)) return true;
            foreach (var pocket in GearPockets)
                if (pocket.Category is { } other && other != place.Category && pocket.TryTake(place)) return true;
            return false;
        }

        private static void MoveToGearPlace(int itemId)
        {
            foreach (var layout in LayoutOwners.Values)
                if (layout.OnGearPage && layout.ItemId == itemId) MoveToGearPlace(layout);
        }

        private static void MoveToGearPlace(InventoryLayout layout)
        {
            if (!GearPlaces.TryGetValue(layout.ItemId, out var place) || !InventoryLayout.Live(layout.Root)) return;
            var centre = place.Turned ? new Vector2(-place.Centre.y, place.Centre.x) : place.Centre;
            var root = layout.Root.transform;
            root.localPosition = new Vector3(place.Position.x - centre.x, 0, place.Position.y - centre.y);
            root.localRotation = place.Turned ? GearTurn : Quaternion.identity;
        }

        private static void EnsureGearPageHook()
        {
            if (_gearHookAttempted || HarmonySupport.CreateState()?.SafeForHarmonyPatches != true) return;
            _gearHookAttempted = true;
            var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-inventory-gear-page");
            try
            {
                var target = AccessTools.DeclaredMethod(typeof(InventoryCutscene), nameof(InventoryCutscene.OnToggleInventoryMode),
                    new[] { typeof(InputAction.CallbackContext) })
                    ?? throw new MissingMethodException(typeof(InventoryCutscene).FullName, nameof(InventoryCutscene.OnToggleInventoryMode));
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Inventory), nameof(BeforeToggleInventoryMode)) { priority = Priority.Last });
                _gearHookInstalled = true;
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The mod gear page could not take the inventory mode key, "
                    + "so mod items share the mat with the game's items: " + error);
                return;
            }
            try
            {
                var open = AccessTools.DeclaredMethod(typeof(InventoryCutscene), nameof(InventoryCutscene.Play), Type.EmptyTypes)
                    ?? throw new MissingMethodException(typeof(InventoryCutscene).FullName, nameof(InventoryCutscene.Play));
                harmony.Patch(open, prefix: new HarmonyMethod(typeof(Inventory), nameof(BeforeInventoryOpens)));
            }
            catch (Exception error)
            {
                RuntimeEntryPoint.LogSource?.LogWarning("The inventory can still reopen on the building page with creative mode off: "
                    + error);
            }
        }

        // The game keeps the building page in the player's saved state and reopens on it without asking whether
        // creative mode is still on, where its own mode key no longer acts. Forget the page before it is read.
        private static void BeforeInventoryOpens()
        {
            try
            {
                if (!Building.TryReadCreativeMode(out var creative) || creative
                    || !SavedPlayerState.TryGetInstance(out var state)) return;
                var page = InventoryCutscene.InCreativeInventoryMode;
                if (state.GetBool(page)) state.Set(page, false);
            }
            catch (Exception error)
            {
                ReportGearPageFailure(error);
            }
        }

        // The game's handler only acts while creative mode is on: it stashes the crafting mat and replays the
        // roll-out between its own items and the building materials. Creative mode is a live game setting an
        // admin can switch during play, read each time the inventory opens, so every press reads the cutscene.
        private static bool BeforeToggleInventoryMode(InventoryCutscene __instance, InputAction.CallbackContext __0, bool __runOriginal)
        {
            try
            {
                if (!__runOriginal || __instance is null || !__0.performed || LocalPlayer.IsInMidAction) return true;
                if (__instance._inConstructionMaterialMode)
                {
                    // With creative mode off this is a page that could not be forgotten when the inventory
                    // opened, and the game refuses to leave it. Let this press through; the replay reads the
                    // setting anew.
                    __instance._allowConstructionMaterialMode = true;
                    return true;
                }
                if (!TryGetCurrentInventory(out var inventory, out _) || !GearPages.TryGetValue(inventory.Pointer, out var page)
                    || !InventoryLayout.Live(page.Root) || !InventoryLayout.Live(page.Container)
                    || !InventoryLayout.Same(page.Cutscene, __instance))
                    return true;
                if (_gearShown)
                {
                    ShowGearPage(page, false);
                    // The building page comes next while creative mode is on; otherwise this was the last page.
                    return __instance._allowConstructionMaterialMode;
                }
                if (!CarriesGear(inventory)) return true;
                ShowGearPage(page, true);
                return false;
            }
            catch (Exception error)
            {
                ReportGearPageFailure(error);
                return true;
            }
        }

        private static void ShowGearPage(GearPage page, bool show)
        {
            page.Container.gameObject.SetActive(!show);
            page.Root.SetActive(show);
            _gearShown = show;
        }

        private static bool CarriesGear(PlayerInventory inventory)
        {
            foreach (var entry in LayoutOwners)
                if (entry.Key.Inventory == inventory.Pointer && entry.Value.OnGearPage
                    && InventoryLayout.Live(entry.Value.Root) && entry.Value.Root.activeSelf)
                    return true;
            return false;
        }

        /// <summary>Opens the inventory on the game's page each time and keeps the mode key's prompt honest.</summary>
        private static void TickGearPage()
        {
            if (GearPages.Count == 0) return;
            try
            {
                if (!TryGetCurrentInventory(out var inventory, out _) || !GearPages.TryGetValue(inventory.Pointer, out var page))
                {
                    _gearShown = _gearOpen = false;
                    return;
                }
                if (!InventoryLayout.Live(page.Root) || !InventoryLayout.Live(page.Container) || !InventoryLayout.Live(page.Cutscene))
                {
                    GearPages.Remove(inventory.Pointer);
                    _gearShown = _gearOpen = false;
                    return;
                }
                if (inventory._currentView != PlayerInventory.PlayerViews.Inventory)
                {
                    if (!_gearOpen) return;
                    _gearOpen = false;
                    if (_gearShown) ShowGearPage(page, false);
                    SetModePrompt(false);
                    return;
                }
                _gearOpen = true;
                var cutscene = page.Cutscene;
                if (!_gearHookInstalled)
                {
                    var shared = !cutscene._inConstructionMaterialMode;
                    if (page.Root.activeSelf != shared) page.Root.SetActive(shared);
                    return;
                }
                // The game shows the prompt itself while creative mode is on.
                if (!cutscene._allowConstructionMaterialMode)
                    SetModePrompt(!LocalPlayer.IsInMidAction
                        && (_gearShown || cutscene._inConstructionMaterialMode || CarriesGear(inventory)));
            }
            catch (Exception error)
            {
                ReportGearPageFailure(error);
            }
        }

        private static void SetModePrompt(bool shown)
        {
            if (shown == _gearPrompt) return;
            _gearPrompt = shown;
            UiManager.SetActive(ModePrompt, shown);
        }

        private static void ReportGearPageFailure(Exception error)
        {
            if (_gearFailureLogged) return;
            _gearFailureLogged = true;
            RuntimeEntryPoint.LogSource?.LogWarning("The mod gear page stopped following the inventory: " + error);
        }

        private sealed class GearPage
        {
            internal readonly GameObject Root;
            internal readonly Transform Container;
            internal readonly InventoryCutscene Cutscene;

            internal GearPage(GameObject root, Transform container, InventoryCutscene cutscene)
            {
                Root = root;
                Container = container;
                Cutscene = cutscene;
            }
        }

        private sealed class GearPlace
        {
            internal readonly int ItemId;
            internal readonly MatCategory Category;
            // Footprint on the mat before any turn, and where its middle lies from the layout root.
            internal readonly Vector2 Size, Centre;
            // How far it stands above the mat; zero where the mod gave a footprint alone.
            internal readonly float Height;
            internal Vector2 Position;
            internal bool Turned;

            internal GearPlace(int itemId, MatCategory category, Vector2 size, Vector2 centre, float height)
            {
                ItemId = itemId;
                Category = category;
                Size = size;
                Centre = centre;
                Height = height;
            }

            internal Vector2 Lying => Turned ? new Vector2(Size.y, Size.x) : Size;
        }

        // One pocket of the bag. Items lie in rows, and the rows sit in the middle of the pocket as the game's
        // own items do.
        private sealed class GearPocket
        {
            internal readonly MatCategory? Category;
            private readonly float _left, _right, _near, _far;
            // In a pocket much longer than it is wide, long items lie along it, as the game's axes and spears do.
            private readonly bool _lengthwise;
            private readonly List<GearRow> _rows = new();

            internal GearPocket(MatCategory? category, float left, float right, float near, float far)
            {
                Category = category;
                _left = left;
                _right = right;
                _near = near;
                _far = far;
                _lengthwise = far - near >= 1.5f * (right - left);
            }

            internal Vector2 Middle => new((_left + _right) / 2, (_near + _far) / 2);

            internal void Clear() => _rows.Clear();

            internal bool TryTake(GearPlace place)
            {
                float width = _right - _left + 0.001f, depth = _far - _near + 0.001f;
                var size = place.Size;
                var turned = _lengthwise ? size.x > size.y : size.x > width && size.y <= width;
                if (turned) size = new Vector2(size.y, size.x);
                if (size.x > width || size.y > depth) return false;
                var rows = 0f;
                foreach (var row in _rows) rows += row.Depth + GearRowGap;
                var last = _rows.Count > 0 ? _rows[^1] : null;
                if (last != null && last.Width + GearGap + size.x <= width)
                {
                    if (rows - GearRowGap - last.Depth + Math.Max(last.Depth, size.y) > depth) return false;
                    last.Width += GearGap + size.x;
                    last.Depth = Math.Max(last.Depth, size.y);
                }
                else
                {
                    if (rows + size.y > depth) return false;
                    last = new GearRow { Width = size.x, Depth = size.y };
                    _rows.Add(last);
                }
                last.Places.Add(place);
                last.Height = Math.Max(last.Height, place.Height);
                place.Turned = turned;
                return true;
            }

            internal void Settle()
            {
                float rows = -GearRowGap, height = 0;
                foreach (var row in _rows)
                {
                    rows += row.Depth + GearRowGap;
                    height = Math.Max(height, row.Height);
                }
                var middle = Middle;
                var far = middle.y + rows / 2 - Lean(height, middle.y - GearEye.z, (_far - _near - rows) / 2);
                foreach (var row in _rows)
                {
                    var x = middle.x - row.Width / 2 - Lean(row.Height, middle.x - GearEye.x, (_right - _left - row.Width) / 2);
                    foreach (var place in row.Places)
                    {
                        var size = place.Lying;
                        place.Position = new Vector2(x + size.x / 2, far - row.Depth / 2);
                        x += size.x + GearGap;
                    }
                    far -= row.Depth + GearRowGap;
                }
            }

            // Half of how far the top of an item this tall appears to lean, within the room the pocket has left.
            private static float Lean(float height, float fromEye, float room)
            {
                room = Math.Max(room, 0);
                return Math.Clamp(height * fromEye / (2 * GearEye.y), -room, room);
            }
        }

        private sealed class GearRow
        {
            internal readonly List<GearPlace> Places = new();
            internal float Width, Depth, Height;
        }
    }
}
