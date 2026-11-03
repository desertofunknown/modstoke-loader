// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Il2CppInterop.Runtime.InteropTypes;
using global::Sons.Weapon;

namespace Modstoke.Sons;

public static partial class ServerWorld
{
    // Identifies the actual native callback's remote owner route, including a retained
    // old attachment. This grants no current player binding or permission to apply damage.
    internal static bool TryReadRemoteMeleeTarget(DamageNode node, out BoltConnection connection)
    {
        connection = null;
        if (!LiveBallisticObject(node) || !TryReadImpactCallback(node, out var nodeCallback, out _)
            || !IsImpactMethod<DamageController>(nodeCallback, "OnImpact")) return false;
        var controller = nodeCallback.Target?.TryCast<DamageController>();
        if (!LiveBallisticObject(controller) || !TryReadImpactCallback(controller, out var ownerCallback, out _)
            || !IsImpactMethod<BoltPlayerSetup>(ownerCallback, "SendPlayerHit")) return false;
        var setup = ownerCallback.Target?.TryCast<BoltPlayerSetup>();
        if (!LiveBallisticObject(setup) || !SameBallisticObject(setup._damageController, controller)) return false;

        // SendPlayerHit routes on setup.entity. Its public getter can find and bind a
        // parent lazily; suppression may use only the already-established cache.
        var entity = setup._entity;
        if (!LiveBallisticObject(entity) || !entity.isAttached) return false;
        var attachment = entity._entity;
        if (attachment is null || attachment.WasCollected || attachment.Pointer == IntPtr.Zero
            || attachment.IsOwner || !SameBallisticObject(attachment.UnityObject, entity)) return false;
        var source = attachment.Source;
        if (source is null || source.WasCollected || source.Pointer == IntPtr.Zero) return false;
        ulong networkId = attachment.NetworkId.PackedValue;

        // Everyone receives the native entity event, but health runs only for its owner.
        // Controller is a separate role. Source identifies that remote ownership route;
        // canonical player registration is needed for damage intake, not for exclusion.
        if (!TryReadImpactCallback(node, out var currentNodeCallback, out _)
            || !SameImpactDelegate(nodeCallback, currentNodeCallback)
            || !TryReadImpactCallback(controller, out var currentOwnerCallback, out _)
            || !SameImpactDelegate(ownerCallback, currentOwnerCallback)
            || !SameMeleeTargetObject(nodeCallback.Target, controller)
            || !SameMeleeTargetObject(ownerCallback.Target, setup)
            || !LiveBallisticObject(node) || !LiveBallisticObject(controller) || !LiveBallisticObject(setup)
            || !SameBallisticObject(setup._damageController, controller)
            || !SameBallisticObject(setup._entity, entity) || !entity.isAttached
            || !SameMeleeTargetObject(entity._entity, attachment) || attachment.IsOwner
            || !SameBallisticObject(attachment.UnityObject, entity)
            || attachment.NetworkId.PackedValue != networkId
            || !SameMeleeTargetObject(attachment.Source, source)) return false;
        connection = source;
        return true;
    }

    private static bool SameMeleeTargetObject(Il2CppObjectBase left, Il2CppObjectBase right)
        => left is not null && right is not null && !left.WasCollected && !right.WasCollected
            && left.Pointer != IntPtr.Zero && left.Pointer == right.Pointer;
}
