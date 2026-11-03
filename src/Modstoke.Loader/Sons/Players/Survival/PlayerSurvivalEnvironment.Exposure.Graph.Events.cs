// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using System.Collections.Generic;
using Endnight.Utilities;
using global::Sons.Areas;
using global::Sons.World;
using TheForest.World;
using UnityEngine;
using UnityEngine.Events;
using static Modstoke.Sons.PlayerSurvivalEnvironmentExposure;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerExposureGraph
    {
        private int _compileDepth;
        private static Action<int> Join(params Action<int>[] calls) => argument =>
        { foreach (var call in calls) call(argument); };

        private Action<int> Delegates(Il2CppSystem.Delegate value)
        {
            if (value == null) { _definition.Write(0); return _ => { }; }
            if (value.WasCollected) throw new InvalidOperationException("Exposure delegate history retired.");
            var list = value.GetInvocationList();
            if (list == null || list.Length > 256) throw new InvalidOperationException("Exposure callback list is unavailable or too large.");
            _definition.Write(list.Length);
            var calls = new Action<int>[list.Length];
            for (int i = 0; i < calls.Length; i++)
            {
                var callback = list[i];
                if (callback == null || callback.WasCollected || callback.Method == null)
                    throw new InvalidOperationException("Exposure callback identity is unavailable.");
                calls[i] = Compile(callback.Target?.TryCast<UnityEngine.Object>(),
                    callback.Method.DeclaringType.FullName, callback.Method.Name);
            }
            return Join(calls);
        }

        private Action<int> UnityCalls(UnityEventBase value)
        {
            if (value == null) { _definition.Write(0); return _ => { }; }
            if (value.WasCollected || value.m_Calls?.m_RuntimeCalls?.Count > 0)
                throw new InvalidOperationException("Exposure UnityEvent has runtime listeners without a scene-serializable mapping.");
            var list = value.m_PersistentCalls?.m_Calls;
            int count = list?.Count ?? 0;
            if (count > 256) throw new InvalidOperationException("Exposure UnityEvent exceeds its callback bound.");
            _definition.Write(count);
            var calls = new List<Action<int>>(count);
            for (int i = 0; i < count; i++)
            {
                var call = list[i];
                if (call == null || call.WasCollected) throw new InvalidOperationException("Missing persistent exposure callback.");
                _definition.Write((int)call.m_CallState);
                if (call.m_CallState == UnityEventCallState.Off) continue;
                _definition.Write((int)call.m_Mode);
                string type = call.m_Target?.GetIl2CppType().FullName;
                var operation = Compile(call.m_Target, type, call.m_MethodName);
                switch (call.m_Mode)
                {
                    case PersistentListenerMode.Void:
                        calls.Add(_ => operation(0));
                        break;
                    case PersistentListenerMode.Bool:
                        if (call.m_Arguments == null) throw new InvalidOperationException("Missing exposure boolean argument.");
                        bool flag = call.m_Arguments.m_BoolArgument;
                        _definition.Write(flag); calls.Add(_ => operation(flag ? 1 : 0));
                        break;
                    default:
                        throw new InvalidOperationException("Exposure persistent callback has an unsupported argument mode: " + call.m_Mode);
                }
            }
            return Join(calls.ToArray());
        }

        private Action<int> Compile(UnityEngine.Object target, string type, string method)
        {
            var operation = CompileOperation(target, type, method);
            var component = target?.TryCast<Component>() ?? target?.TryCast<GameObject>()?.transform;
            return component == null ? operation : argument => { Check(component); operation(argument); };
        }

        private Action<int> CompileOperation(UnityEngine.Object target, string type, string method)
        {
            Text(type ?? ""); Text(method ?? "");
            if (++_compileDepth > 64)
            { _compileDepth--; throw new InvalidOperationException("Cyclic native exposure callback definition."); }
            try
            {
                if (target == null && type == typeof(CaveEntranceManager).FullName)
                {
                    return method switch
                    {
                        nameof(CaveEntranceManager.OnCaveEnter) => area => SetCave(true, area),
                        nameof(CaveEntranceManager.OnCaveExit) => area => SetCave(false, area),
                        nameof(CaveEntranceManager.OnDeepCaveEnter) => _ => SetDeep(true),
                        nameof(CaveEntranceManager.OnDeepCaveExit) => _ => SetDeep(false),
                        nameof(CaveEntranceManager.SetIsInDeepCaves) => value => SetDeep(value != 0),
                        _ => throw Unsupported(type, method)
                    };
                }
                if (!Alive(target)) throw new InvalidOperationException("Exposure callback has no live target: " + type + "." + method);
                if (target.TryCast<GameObject>() is { } gameObject && method == nameof(GameObject.SetActive))
                    return ActivationCall(gameObject);
                var component = target.TryCast<Component>();
                if (component == null) throw Unsupported(type, method);
                Pin(component);
                if (target.TryCast<DirectionalTrigger>() is { } directional)
                {
                    if (!_directions.TryGetValue(directional.Pointer, out var node)) throw Unsupported(type, method);
                    return method switch
                    {
                        nameof(DirectionalTrigger.OnAEnter) => _ => node.Enter(true),
                        nameof(DirectionalTrigger.OnBEnter) => _ => node.Enter(false),
                        nameof(DirectionalTrigger.OnAExit) => _ => node.Exit(true),
                        nameof(DirectionalTrigger.OnBExit) => _ => node.Exit(false),
                        _ => throw Unsupported(type, method)
                    };
                }
                if (target.TryCast<CaveEntrance>() is { } entrance)
                {
                    _definition.Write((int)entrance._areaId); Pin(entrance._trigger);
                    if (method is nameof(CaveEntrance.TriggerEnter) or nameof(CaveEntrance.TriggerExit))
                    {
                        if (!_directions.TryGetValue(entrance._trigger.Pointer, out var direction)) throw Unsupported(type, method);
                        bool enter = method == nameof(CaveEntrance.TriggerEnter);
                        return _ => { direction.Reset(); (enter ? direction.ExitA : direction.ExitB)(0); };
                    }
                    bool entering = method == nameof(CaveEntrance.OnCaveEnter);
                    if (!entering && method != nameof(CaveEntrance.OnCaveExit)) throw Unsupported(type, method);
                    var calls = Delegates(entering ? entrance._onCaveEnteredEvent : entrance._onCaveExitedEvent);
                    int area = (int)entrance._areaId;
                    return _ => calls(area);
                }
                if (target.TryCast<CaveSystem>() is { } cave)
                {
                    if (method == nameof(CaveSystem.OnCaveEnter)) return Delegates(cave._onCaveEnteredEvent);
                    if (method == nameof(CaveSystem.OnCaveExit)) return Delegates(cave._onCaveExitedEvent);
                    if (method == nameof(CaveSystem.SetInDeepCave))
                    {
                        var enter = Delegates(cave._onDeepCaveEnteredEvent);
                        var exit = Delegates(cave._onDeepCaveExitedEvent);
                        return value => (value != 0 ? enter : exit)(0);
                    }
                    if (method is nameof(CaveSystem.DoForcedEnter) or nameof(CaveSystem.DoForcedExit))
                        return ForcedCave(cave, method == nameof(CaveSystem.DoForcedEnter));
                    throw Unsupported(type, method);
                }
                if (target.TryCast<LinkActivateDirectionalTrigger>() is { } link)
                {
                    if (method is nameof(LinkActivateDirectionalTrigger.OnExitedAEvent) or nameof(LinkActivateDirectionalTrigger.OnEnter))
                        return Link(link, true);
                    if (method is nameof(LinkActivateDirectionalTrigger.OnExitedBEvent) or nameof(LinkActivateDirectionalTrigger.OnExit))
                        return Link(link, false);
                    throw Unsupported(type, method);
                }
                if (target.TryCast<UnityEventDirectionalTrigger>() is { } unity)
                {
                    Pin(unity._source);
                    return method switch
                    {
                        nameof(UnityEventDirectionalTrigger.OnExitedAEvent) => UnityCalls(unity._onExitedAEvent),
                        nameof(UnityEventDirectionalTrigger.OnExitedBEvent) => UnityCalls(unity._onExitedBEvent),
                        _ => throw Unsupported(type, method)
                    };
                }
                if (target.TryCast<DeepCaveActivator>() is { } deep && method == nameof(DeepCaveActivator.OnDeepCavesChanged))
                {
                    var enter = Join(ToggleList(deep._objectsToActivateEnterDeepCaves, true),
                        ToggleList(deep._objectsToDeactivateEnterDeepCaves, false), UnityCalls(deep._onEnterDeepCaves));
                    var exit = Join(ToggleList(deep._objectsToActivateExitDeepCaves, true),
                        ToggleList(deep._objectsToDeactivateExitDeepCaves, false), UnityCalls(deep._onExitDeepCaves));
                    return value => (value != 0 ? enter : exit)(0);
                }
                throw Unsupported(type, method);
            }
            finally { _compileDepth--; }
        }

        private Action<int> ForcedCave(CaveSystem cave, bool enter)
        {
            var entrances = cave._entrances;
            if (entrances == null || entrances.WasCollected) throw new InvalidOperationException("Cave entrance graph is unavailable.");
            _definition.Write(entrances.Count);
            var calls = new List<Action<int>>();
            for (int i = entrances.Count - 1; i >= 0; i--)
            {
                var entrance = entrances[i];
                if (!Alive(entrance)) throw new InvalidOperationException("Cave entrance graph contains an unresolved retired entry.");
                calls.Add(Compile(entrance, typeof(CaveEntrance).FullName,
                    enter ? nameof(CaveEntrance.TriggerEnter) : nameof(CaveEntrance.TriggerExit)));
            }
            if (!enter) calls.Add(Delegates(cave._onDeepCaveExitedEvent));
            return Join(calls.ToArray());
        }
        private Action<int> Link(LinkActivateDirectionalTrigger link, bool enter)
        {
            var visited = new HashSet<nint>();
            while (Alive(link._controllerTrigger))
            {
                if (!visited.Add(link.Pointer)) throw new InvalidOperationException("Cyclic exposure link controller.");
                Pin(link._controllerTrigger); link = link._controllerTrigger;
            }
            _definition.Write(link._mirror);
            if (enter) return Join(ToggleList(link._onEnterActivate, true), ToggleList(link._onEnterDeactivate, false));
            return link._mirror
                ? Join(ToggleList(link._onEnterActivate, false), ToggleList(link._onEnterDeactivate, true))
                : Join(ToggleList(link._onExitActivate, true), ToggleList(link._onExitDeactivate, false));
        }
        private Action<int> ToggleList(Il2CppSystem.Collections.Generic.List<GameObject> list, bool value)
        {
            if (list == null) { _definition.Write(0); return _ => { }; }
            if (list.WasCollected || list.Count > 2048) throw new InvalidOperationException("Exposure activation list is unavailable.");
            _definition.Write(list.Count); _definition.Write(value);
            var calls = new Action<int>[list.Count];
            for (int i = 0; i < calls.Length; i++)
            {
                if (!Alive(list[i])) throw new InvalidOperationException("Exposure activation list has no exact live scene target.");
                calls[i] = ActivationCall(list[i]);
            }
            return _ => { foreach (var call in calls) call(value ? 1 : 0); };
        }

        private Action<int> ActivationCall(GameObject target)
        {
            Pin(target.transform);
            if (!_activation.TryGetValue(target.Pointer, out var node))
            {
                // One native weather lifecycle per activation gives an evidenced order.
                // Multi-writer Unity lifecycle order and nested graph producer activation
                // are not inferred from hierarchy traversal order.
                var overrides = target.GetComponentsInChildren<WeatherSystemWetnessOverride>(true);
                if (overrides.Length > 1) throw new InvalidOperationException("An activation target contains multiple weather writers with unresolved native lifecycle order.");
                if (target.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new InvalidOperationException("Exposure activation changes native collision geometry requiring its actor-qualified owner.");
                foreach (var behaviour in target.GetComponentsInChildren<MonoBehaviour>(true))
                    if (behaviour.TryCast<WeatherSystemWetnessOverride>() == null)
                        throw new InvalidOperationException("Exposure activation includes another native behaviour requiring its real owner: " + behaviour.GetIl2CppType().FullName);
                if (!_activeSelf.ContainsKey(target.Pointer))
                    _activeSelf.Add(target.Pointer, Flag(_source && target.activeSelf));
                node = new Activation { Target = target, Active = _activeSelf[target.Pointer] };
                if (overrides.Length == 1)
                {
                    node.Override = overrides[0]; Pin(node.Override);
                    node.OverrideEnabled = Flag(_source && node.Override.enabled);
                    _definition.Write(node.Override._wetnessOverride); _definition.Write(node.Override._rainOverride);
                    var ancestors = new List<GameObject>();
                    for (var t = node.Override.transform; Alive(t); t = t.parent)
                    {
                        Pin(t); ancestors.Add(t.gameObject);
                        if (!_activeSelf.ContainsKey(t.gameObject.Pointer))
                            _activeSelf.Add(t.gameObject.Pointer, Flag(_source && t.gameObject.activeSelf));
                    }
                    _definition.Write(ancestors.Count); node.Ancestors = ancestors.ToArray();
                }
                _activation.Add(target.Pointer, node);
            }
            return value =>
            {
                Check(node.Target.transform);
                if (node.Override != null) Check(node.Override);
                bool active = value != 0;
                if (active == _activeSelf[node.Target.Pointer]) return;
                bool wasActive = EffectiveActive(node);
                node.Active = active; _activeSelf[node.Target.Pointer] = active;
                if (!Alive(node.Override) || !node.OverrideEnabled) return;
                bool nowActive = EffectiveActive(node);
                if (wasActive == nowActive) return;
                int index = _order.FindIndex(x => Same(x, node.Override));
                if (index >= 0) _order.RemoveAt(index);
                if (nowActive) _order.Add(node.Override);
            };
        }

        private bool EffectiveActive(Activation node)
        {
            if (!node.OverrideEnabled || node.Ancestors == null) return false;
            foreach (var ancestor in node.Ancestors)
                if (!Alive(ancestor) || !_activeSelf[ancestor.Pointer]) return false;
            return true;
        }

        private Action<int, int> AreaListeners(params Il2CppSystem.Delegate[] groups)
        {
            var listeners = new List<Action<int, int>>();
            foreach (var group in groups)
            {
                var callbacks = group?.GetInvocationList();
                int count = callbacks?.Length ?? 0;
                if (count > 256) throw new InvalidOperationException("Area listener graph exceeds its bound.");
                _definition.Write(count);
                for (int i = 0; i < count; i++)
                {
                    var callback = callbacks[i];
                    var receiver = callback.Target?.TryCast<CaveChangeActivate>();
                    if (!Alive(receiver) || callback.Method?.Name != nameof(CaveChangeActivate.OnAreaMaskChanged))
                        throw new InvalidOperationException("Area callback needs a concrete owner: " + callback.Method?.DeclaringType?.FullName + "." + callback.Method?.Name);
                    Pin(receiver); int filter = (int)receiver._areaFilter;
                    _definition.Write(filter); _definition.Write(receiver._mirror);
                    var enter = Join(ToggleList(receiver._enableOnEnteredLocal, true),
                        ToggleList(receiver._disableOnEnteredLocal, false), UnityCalls(receiver._triggerOnEnteredLocal));
                    var exit = receiver._mirror
                        ? Join(ToggleList(receiver._enableOnEnteredLocal, false), ToggleList(receiver._disableOnEnteredLocal, true), UnityCalls(receiver._triggerOnExitedLocal))
                        : Join(ToggleList(receiver._enableOnExitedLocal, true), ToggleList(receiver._disableOnExitedLocal, false), UnityCalls(receiver._triggerOnExitedLocal));
                    listeners.Add((oldArea, newArea) =>
                    {
                        bool was = MatchArea(filter, oldArea), now = MatchArea(filter, newArea);
                        if (now && !was) enter(0);
                        else if (was && !now) exit(0);
                    });
                }
            }
            return (oldArea, newArea) => { foreach (var listener in listeners) listener(oldArea, newArea); };
        }
        private static bool MatchArea(int filter, int value)
            => filter == 0x7E01FE ? value != 0 : filter == 0 ? value == 0 : (filter & value) != 0;
        private static Exception Unsupported(string type, string method)
            => new InvalidOperationException("Exposure callback has no concrete native translation: " + type + "." + method);
    }
}
