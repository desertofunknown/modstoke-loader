// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Preloader
{
    // Generated methods keep this descriptor private to their locals/evaluation stack. It roots
    // the owning wrapper, never an interior native pointer or a detached copy of the value.
    public sealed class InteropReferenceLocation
    {
        public InteropReferenceLocation(object owner, int index)
            : this(owner, index, null, null)
        {
        }

        public InteropReferenceLocation(object owner, int index, Type fieldOwner, string fieldPointerName)
            : this(owner, index, fieldOwner, fieldPointerName, false)
        {
        }

        public InteropReferenceLocation(object owner, int index, Type fieldOwner, string fieldPointerName, bool isReadOnly)
        {
            Owner = owner;
            Index = index;
            FieldOwner = fieldOwner;
            FieldPointerName = fieldPointerName;
            IsReadOnly = isReadOnly;
        }

        public object Owner { get; }
        public int Index { get; }
        public bool IsReadOnly { get; }
        internal Type FieldOwner { get; }
        internal string FieldPointerName { get; }
        internal InteropReferenceLocation Parent { get; }
        internal int RectangularRank { get; }
        internal long LinearIndex { get; }

        internal InteropReferenceLocation(object owner, long index, int rank)
        {
            Owner = owner;
            LinearIndex = index;
            RectangularRank = rank;
        }

        internal InteropReferenceLocation(InteropReferenceLocation parent, Type fieldOwner, string fieldPointerName)
            : this(parent, fieldOwner, fieldPointerName, false)
        {
        }

        internal InteropReferenceLocation(InteropReferenceLocation parent, Type fieldOwner, string fieldPointerName, bool isReadOnly)
        {
            ArgumentNullException.ThrowIfNull(parent);
            Parent = parent;
            Owner = parent.Owner;
            FieldOwner = fieldOwner;
            FieldPointerName = fieldPointerName;
            IsReadOnly = parent.IsReadOnly || isReadOnly;
        }
    }
}
