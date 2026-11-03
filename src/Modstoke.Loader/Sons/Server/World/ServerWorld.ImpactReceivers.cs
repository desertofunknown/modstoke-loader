// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Weapon;
using TheForest.Utils;

namespace Modstoke.Sons
{
    public static partial class ServerWorld
    {
        internal readonly struct PlayerImpactRoute
        {
            internal PlayerImpactRoute(DamageCallbackMono receiver, DamageController controller,
                UnityEngine.Object owner, Il2CppSystem.Delegate nodeCallback, Il2CppSystem.Delegate ownerCallback)
            {
                Receiver = receiver;
                Controller = controller;
                Owner = owner;
                NodeCallback = nodeCallback;
                OwnerCallback = ownerCallback;
                _receiverInstanceId = receiver.GetInstanceID();
                _controllerInstanceId = controller.GetInstanceID();
                _ownerInstanceId = owner.GetInstanceID();
            }

            private readonly int _receiverInstanceId, _controllerInstanceId, _ownerInstanceId;
            private DamageCallbackMono Receiver { get; }
            private DamageController Controller { get; }
            private UnityEngine.Object Owner { get; }
            private Il2CppSystem.Delegate NodeCallback { get; }
            private Il2CppSystem.Delegate OwnerCallback { get; }
            internal DamageNode DirectNode => Receiver?.TryCast<DamageNode>();

            internal bool Matches(PlayerImpactRoute other)
                => _receiverInstanceId == other._receiverInstanceId
                    && _controllerInstanceId == other._controllerInstanceId
                    && _ownerInstanceId == other._ownerInstanceId
                    && SameBallisticObject(Receiver, other.Receiver)
                    && SameBallisticObject(Controller, other.Controller)
                    && SameBallisticObject(Owner, other.Owner)
                    && SameImpactDelegate(NodeCallback, other.NodeCallback)
                    && SameImpactDelegate(OwnerCallback, other.OwnerCallback);
        }

        private static bool TryReadPlayerImpactRoute(CollisionHit hit,
            PlayerHandle binding, out PlayerImpactRoute route,
            out CollisionReadStatus status)
        {
            route = default;
            status = CollisionReadStatus.ReceiverUnavailable;
            // Native projectile impacts enumerate this exact transform, including a compound
            // collider's rigidbody transform. Searching its children would invent receivers.
            var receivers = hit.SurfaceTransform.GetComponents<IImpactReceiver>();
            if (receivers == null || receivers.WasCollected) return false;
            int receiverCount = receivers.Length;
            if (receiverCount == 0) return false;
            // Additional receivers can run arbitrary UnityEvents or mutate the shared impact data.
            if (receiverCount != 1)
            { status = CollisionReadStatus.ReceiverAmbiguous; return false; }
            var receiver = receivers[0];
            return receiver != null && !receiver.WasCollected
                && TryReadPlayerImpactRoute(receiver, binding, out route, out status);
        }

        private static bool TryReadPlayerImpactRoute(IImpactReceiver receiver, PlayerHandle binding,
            out PlayerImpactRoute route, out CollisionReadStatus status)
        {
            route = default;
            status = CollisionReadStatus.ReceiverUnsupported;
            var node = receiver.TryCast<DamageNode>();
            DamageCallbackMono source;
            DamageController controller;
            Il2CppSystem.Delegate nodeCallback = null;
            if (node != null)
            {
                source = node;
                if (!LiveBallisticObject(node) || !TryReadImpactCallback(node, out nodeCallback, out status)) return false;
                if (!IsImpactMethod<DamageController>(nodeCallback, "OnImpact"))
                { status = CollisionReadStatus.ReceiverUnsupported; return false; }
                controller = nodeCallback.Target?.TryCast<DamageController>();
            }
            else
            {
                controller = receiver.TryCast<DamageController>();
                source = controller;
            }
            if (!LiveBallisticObject(controller))
            { status = CollisionReadStatus.ReceiverUnsupported; return false; }
            if (!TryReadImpactCallback(controller, out var ownerCallback, out status)) return false;

            UnityEngine.Object owner;
            if (binding.IsLocalPlayer)
            {
                if (!IsImpactMethod<PlayerStats>(ownerCallback, "OnImpact"))
                { status = CollisionReadStatus.ReceiverUnsupported; return false; }
                var stats = ownerCallback.Target?.TryCast<PlayerStats>();
                if (!SameBallisticObject(stats, LocalPlayer.Stats)
                    || !SameBallisticObject(stats._damageController, controller))
                { status = CollisionReadStatus.PlayerMismatch; return false; }
                owner = stats;
            }
            else
            {
                if (!IsImpactMethod<BoltPlayerSetup>(ownerCallback, "SendPlayerHit"))
                { status = CollisionReadStatus.ReceiverUnsupported; return false; }
                var setup = ownerCallback.Target?.TryCast<BoltPlayerSetup>();
                // The public entity getter can lazily bind a parent. Only accept the existing cache.
                if (!LiveBallisticObject(setup) || !SameBallisticObject(setup._damageController, controller)
                    || !MatchesPlayerAttachment(binding, setup._entity))
                { status = CollisionReadStatus.PlayerMismatch; return false; }
                owner = setup;
            }
            route = new PlayerImpactRoute(source, controller, owner, nodeCallback, ownerCallback);
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool TryReadImpactCallback(DamageCallbackMono source, out Il2CppSystem.Delegate callback,
            out CollisionReadStatus status)
        {
            callback = null;
            status = CollisionReadStatus.ReceiverUnavailable;
            var root = source._impactReceivers;
            if (root == null || root.WasCollected) return false;
            var entries = root.delegates;
            if (entries == null) callback = root;
            else
            {
                if (entries.WasCollected) return false;
                int entryCount = entries.Length;
                if (entryCount == 0) return false;
                if (entryCount != 1)
                { status = CollisionReadStatus.ReceiverAmbiguous; return false; }
                callback = entries[0];
                if (callback == null || callback.WasCollected) return false;
                // Do not let Method's last-entry behavior conceal another multicast route.
                var nested = callback.TryCast<Il2CppSystem.MulticastDelegate>();
                if (nested != null && nested.delegates != null)
                { status = CollisionReadStatus.ReceiverUnsupported; return false; }
            }
            status = CollisionReadStatus.Success;
            return true;
        }

        private static bool IsImpactMethod<T>(Il2CppSystem.Delegate callback, string name) where T : Il2CppObjectBase
        {
            var method = callback.Method;
            if (method == null || method.WasCollected || method.Name != name) return false;
            var declaringType = method.DeclaringType;
            return declaringType != null && !declaringType.WasCollected
                && declaringType.Pointer == Il2CppType.Of<T>().Pointer;
        }

        private static bool SameImpactDelegate(Il2CppSystem.Delegate first, Il2CppSystem.Delegate second)
            => first == null ? second == null
                : second != null && !first.WasCollected && !second.WasCollected && first.Pointer == second.Pointer;
    }
}
