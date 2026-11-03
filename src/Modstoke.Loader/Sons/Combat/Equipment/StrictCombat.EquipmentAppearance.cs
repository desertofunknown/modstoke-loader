// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using global::Sons.Gameplay;
using global::Sons.Inventory;
using global::Sons.Items.Core;
using UnityEngine;

namespace Modstoke.Sons.Combat
{
    // Retains concrete presentation receivers. No module initialization or general
    // consumer dispatch is permitted in this step.
    internal sealed class StrictCombatEquipmentAppearance
    {
        private readonly StrictCombatPreparedEquipment _reservation;
        private readonly StrictCombatEquipmentRenderer _renderer;
        private readonly Action _requireOperation;
        private readonly GameObject _loaded;
        private readonly ItemInstance _item;
        private readonly ItemData _data;
        private readonly VisualVariantItemInstanceModule _variant;
        private readonly ItemPlatingItemInstanceModule _plating;
        private readonly int _variantNumber;
        private readonly bool _isPlated, _hasVariant, _canBePlated;
        private readonly List<Action> _checks = new();
        private readonly List<(Action Write, Action Read)> _calls = new();
        private int _frame, _completed, _componentCount;
        private ulong _revision;
        private bool _attempted, _mutationAttempted, _applied, _uncertain;

        private StrictCombatEquipmentAppearance(StrictCombatPreparedEquipment reservation,
            StrictCombatEquipmentRenderer renderer, Action requireOperation, GameObject loaded)
        {
            _reservation = reservation;
            _renderer = renderer;
            _requireOperation = requireOperation;
            _loaded = loaded;
            _item = reservation.Source.Item;
            _data = _item._data;
            _hasVariant = _data._hasVisualVariant;
            _canBePlated = _data._canBePlated;
            var modules = RetainList(() => _item._modules, 32);
            foreach (var module in modules)
            {
                Require(Valid(module));
                var variant = module.TryCast<VisualVariantItemInstanceModule>();
                if (variant is not null)
                {
                    Require(_variant is null);
                    _variant = variant;
                }
                var plating = module.TryCast<ItemPlatingItemInstanceModule>();
                if (plating is not null)
                {
                    Require(_plating is null);
                    _plating = plating;
                }
            }
            var controllers = _loaded.GetComponentsInChildren<VisualVariantController>(true);
            var updaters = _loaded.GetComponentsInChildren<ItemPlatingRendererUpdater>(true);
            Require(Valid(controllers) && controllers.Length <= 32 && Valid(updaters) && updaters.Length <= 32);
            var retainedControllers = Copy(controllers);
            var retainedUpdaters = Copy(updaters);
            _checks.Add(() => Match(_loaded.GetComponentsInChildren<VisualVariantController>(true), retainedControllers));
            _checks.Add(() => Match(_loaded.GetComponentsInChildren<ItemPlatingRendererUpdater>(true), retainedUpdaters));
            Require(_variant is not null || (!_hasVariant && controllers.Length == 0));
            Require(_plating is not null || (!_canBePlated && updaters.Length == 0));
            Require(!_hasVariant || controllers.Length > 0);
            Require(!_canBePlated || updaters.Length > 0);
            _variantNumber = _variant is null ? 0 : _variant._variantNum;
            _isPlated = _plating is not null && _plating._isPlated;
            var variantTargets = new HashSet<nint>();
            foreach (var controller in retainedControllers)
            {
                RetainComponent(controller);
                var visuals = RetainArray(() => controller._visuals, 64);
                Require(visuals.Length > 0);
                var readbacks = new List<Action>();
                foreach (var visual in visuals)
                {
                    Require(Valid(visual));
                    var variants = RetainArray(() => visual._variants, 64);
                    int selectedIndex = Math.Max(0, _variantNumber);
                    Require(selectedIndex < variants.Length);
                    var selected = variants[selectedIndex];
                    Require(Valid(selected));
                    var material = selected._material;
                    var mesh = selected._mesh;
                    Require(Live(material) && (mesh is null || Live(mesh)));
                    _checks.Add(() => Require(Live(material) && Same(selected._material, material)
                        && SameOptional(selected._mesh, mesh) && (mesh is null || Live(mesh))));
                    var primary = visual._renderer;
                    _checks.Add(() => Require(Same(visual._renderer, primary)));
                    CaptureVariantTarget(primary, material, mesh, variantTargets, readbacks);
                    var lods = RetainArray(() => visual._lods, 64);
                    foreach (var lod in lods)
                        CaptureVariantTarget(lod, material, mesh, variantTargets, readbacks);
                }
                _calls.Add((() => _variant.ApplyVariant(controller), () =>
                {
                    foreach (var readback in readbacks) readback();
                }));
            }
            foreach (var updater in retainedUpdaters)
            {
                RetainComponent(updater);
                var targets = RetainList(() => updater._renderers, 64);
                Require(targets.Length > 0);
                foreach (var target in targets) RetainComponent(target);
                _calls.Add((() => updater.ModuleRefreshed(_item, _plating), () =>
                {
                    int property = Shader.PropertyToID("_GoldAmount");
                    var block = new MaterialPropertyBlock();
                    foreach (var target in targets)
                    {
                        block.Clear();
                        target.GetPropertyBlock(block);
                        Require(block.GetFloat(property) == (_isPlated ? 1f : 0f));
                    }
                }));
            }
        }

        internal bool IsUncertain => _uncertain;
        internal bool IsApplied => _applied && !_uncertain;

        internal static bool TryCapture(StrictCombatPreparedEquipment reservation,
            StrictCombatEquipmentRenderer renderer, Action requireOperation,
            out StrictCombatEquipmentAppearance appearance)
        {
            appearance = null;
            try
            {
                if (reservation is null || renderer is null || requireOperation is null
                    || !reservation.IsCurrentOwnership) return false;
                int frame = Time.frameCount;
                if (!reservation.Source.Owner.TryRead(out _, out var accepted)) return false;
                ulong revision = accepted.Revision;
                requireOperation();
                if (renderer.Read(out var loaded) != StrictCombatEquipmentRendererStatus.Ready) return false;
                var candidate = new StrictCombatEquipmentAppearance(reservation, renderer, requireOperation, loaded);
                candidate._frame = frame;
                candidate._revision = revision;
                candidate.RequireCurrent();
                appearance = candidate;
                return true;
            }
            catch (Exception) { return false; }
        }

        internal bool Apply()
        {
            if (_attempted || _uncertain || !_reservation.IsCurrentOwnership) return false;
            _attempted = true;
            try
            {
                BeginFrame();
                RequireCurrent();
                foreach (var call in _calls)
                {
                    RequireCurrent();
                    _mutationAttempted = true;
                    call.Write();
                    _completed++;
                    RequireCurrent();
                }
                RequireCurrent();
                _applied = true;
                return true;
            }
            catch (Exception)
            {
                RetireIfMutated();
                return false;
            }
        }

        internal bool ReferencesCurrent()
        {
            if (!IsApplied || !_reservation.Source.Owner.IsCurrentNativeHealthOwner(_reservation.Source.Life)) return false;
            try { BeginFrame(); RequireCurrent(); return true; }
            catch (Exception) { RetireIfMutated(); return false; }
        }

        private void RetireIfMutated()
        {
            if (!_mutationAttempted) return;
            _uncertain = true;
            _reservation.Retire();
            _reservation.Source.Life.Retire();
        }

        private void BeginFrame()
        {
            Require(_reservation.IsCurrentOwnership);
            _requireOperation();
            Require(_reservation.Source.Owner.TryRead(out _, out var accepted));
            _revision = accepted.Revision;
            _frame = Time.frameCount;
        }

        private void RequireCurrent()
        {
            Require(!_uncertain && _reservation.IsCurrentOwnership);
            _requireOperation();
            RequireFrame();
            Require(_renderer.Read(out var loaded) == StrictCombatEquipmentRendererStatus.Ready && Same(loaded, _loaded));
            Require(Live(_loaded) && Same(_reservation.Source.Item, _item) && Same(_item._data, _data)
                && _data._hasVisualVariant == _hasVariant && _data._canBePlated == _canBePlated
                && Same(_reservation.Controller._itemInstance, _item)
                && Same(_item._ItemObject_k__BackingField, _reservation.ItemObject)
                && (_variant is null || (Valid(_variant) && _variant._variantNum == _variantNumber))
                && (_plating is null || (Valid(_plating) && _plating._isPlated == _isPlated)));
            foreach (var check in _checks) check();
            for (int i = 0; i < _completed; i++) _calls[i].Read();
            _requireOperation();
            RequireFrame();
        }

        private void RequireFrame()
            => Require(_reservation.IsCurrentOwnership && Time.frameCount == _frame
                && _reservation.Source.Owner.TryRead(out _, out var accepted) && accepted.Revision == _revision);

        private void CaptureVariantTarget(Renderer target, Material material, Mesh mesh,
            HashSet<nint> seen, List<Action> readbacks)
        {
            RetainComponent(target);
            // Overlapping variant receivers can overwrite one another; unsupported.
            Require(seen.Add(target.Pointer));
            MeshFilter filter = null;
            if (mesh is not null)
            {
                filter = target.GetComponent<MeshFilter>();
                RetainComponent(filter);
                Require(Same(filter.gameObject, target.gameObject));
                var retainedFilter = filter;
                _checks.Add(() => Require(Same(target.GetComponent<MeshFilter>(), retainedFilter)));
            }
            readbacks.Add(() => Require(Same(target.sharedMaterial, material)
                && (mesh is null || Same(filter.sharedMesh, mesh))));
        }

        private void RetainComponent(Component component)
        {
            Require(Live(component) && ++_componentCount <= 256);
            var transform = component.transform;
            var parent = transform.parent;
            var gameObject = component.gameObject;
            Require(Live(transform) && (Same(transform, _loaded.transform) || transform.IsChildOf(_loaded.transform)));
            var ancestor = transform;
            int depth = 0;
            while (!Same(ancestor, _loaded.transform))
            {
                Require(Live(ancestor) && depth++ < 64);
                var child = ancestor;
                var retainedParent = child.parent;
                Require(Live(retainedParent));
                _checks.Add(() => Require(Live(child) && Live(retainedParent) && Same(child.parent, retainedParent)));
                ancestor = retainedParent;
            }
            _checks.Add(() => Require(Live(component) && Live(transform) && Live(gameObject)
                && Same(component.gameObject, gameObject) && Same(component.transform, transform)
                && SameOptional(transform.parent, parent)
                && (Same(transform, _loaded.transform) || transform.IsChildOf(_loaded.transform))));
        }

        private T[] RetainArray<T>(Func<Il2CppReferenceArray<T>> read, int limit) where T : Il2CppObjectBase
        {
            var array = read();
            Require(Valid(array) && array.Length <= limit);
            var entries = Copy(array);
            _checks.Add(() => { Require(Same(read(), array)); Match(array, entries); });
            return entries;
        }

        private T[] RetainList<T>(Func<Il2CppSystem.Collections.Generic.List<T>> read, int limit) where T : Il2CppObjectBase
        {
            var list = read();
            Require(Valid(list) && list.Count <= limit);
            var entries = new T[list.Count];
            for (int i = 0; i < entries.Length; i++) entries[i] = list[i];
            _checks.Add(() =>
            {
                Require(Same(read(), list) && list.Count == entries.Length);
                for (int i = 0; i < entries.Length; i++) Require(SameOptional(list[i], entries[i]));
            });
            return entries;
        }

        private static T[] Copy<T>(Il2CppArrayBase<T> array) where T : Il2CppObjectBase
        {
            var entries = new T[array.Length];
            for (int i = 0; i < entries.Length; i++) entries[i] = array[i];
            return entries;
        }

        private static void Match<T>(Il2CppArrayBase<T> array, T[] entries) where T : Il2CppObjectBase
        {
            Require(Valid(array) && array.Length == entries.Length);
            for (int i = 0; i < entries.Length; i++) Require(SameOptional(array[i], entries[i]));
        }

        private static void Require(bool condition)
        {
            if (!condition) throw new InvalidOperationException("The retained equipment appearance is unavailable or changed.");
        }
        private static bool Valid(Il2CppObjectBase value) => value is not null && !value.WasCollected && value.Pointer != 0;
        private static bool Live(UnityEngine.Object value) => Valid(value) && value;
        private static bool Same(Il2CppObjectBase first, Il2CppObjectBase second)
            => Valid(first) && Valid(second) && first.Pointer == second.Pointer;
        private static bool SameOptional(Il2CppObjectBase first, Il2CppObjectBase second)
            => first is null ? second is null : Same(first, second);
    }
}
