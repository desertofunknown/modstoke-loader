// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using AsmResolver.DotNet.Signatures;

namespace Modstoke.Preloader
{
    internal static class InteropManagedVectorLowering
    {
        internal static bool TryGetElement(InteropRewriteContext context, TypeSignature source,
            out TypeSignature element, InteropRestorationPlan plan = null)
        {
            element = null;
            if (source is not SzArrayTypeSignature vector) return false;
            plan ??= InteropRestorationPlan.For(context);
            var candidate = vector.BaseType;
            if (!InteropNativeArrayLowering.IsStrippedHandle(context, candidate) &&
                !plan.IsManagedReference(candidate) && !InteropManagedDictionaryLowering.IsRestoredValue(plan, candidate) &&
                !InteropManagedCollectionLowering.TryGetStorage(context, candidate, out _, out _, plan) &&
                !InteropManagedDictionaryLowering.TryGetStorage(context, candidate, out _, out _, out _, plan) &&
                !TryGetElement(context, candidate, out _, plan)) return false;
            element = candidate;
            return true;
        }

        internal static bool CanAssign(InteropRewriteContext context, TypeSignature source, TypeSignature destination) =>
            TryGetElement(context, source, out var from) && TryGetElement(context, destination, out var to) &&
            CanAssignElement(context, from, to);

        internal static bool CanAssignElement(InteropRewriteContext context, TypeSignature source, TypeSignature destination) =>
            SignatureComparer.Default.Equals(source, destination) ||
            source != null && destination != null && !source.IsValueType && !destination.IsValueType &&
            (InteropRestorationPlan.For(context).CanAssignManagedReference(source, destination) ||
                InteropManagedCollectionLowering.CanAssign(context, source, destination));
    }
}
