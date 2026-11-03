// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    /// <summary>An opaque retained entry surface, valid only while its world and native identities remain live.</summary>
    public sealed class BallisticSurface
    {
        internal BallisticSurface(CollisionHit entryHit,
            Ballistics.BallisticObject ballisticObject, Ballistics.BallisticMaterial material)
        {
            EntryHit = entryHit;
            BallisticObject = ballisticObject;
            BallisticObjectInstanceId = ballisticObject is null ? 0 : ballisticObject.GetInstanceID();
            Material = material;
            MaterialInstanceId = material.GetInstanceID();
            HasBallisticObject = ballisticObject is not null;
        }

        internal CollisionHit EntryHit { get; }
        internal Ballistics.BallisticObject BallisticObject { get; }
        internal int BallisticObjectInstanceId { get; }
        internal Ballistics.BallisticMaterial Material { get; }
        internal int MaterialInstanceId { get; }
        public bool HasBallisticObject { get; }
    }

    /// <summary>Copied material values. Finite native values are preserved without range clamping.</summary>
    public sealed class BallisticMaterial
    {
        internal BallisticMaterial(Ballistics.BallisticMaterial material)
        {
            EnergyLossPerUnit = material.energyLossPerUnit;
            StopOnImpact = material.stopOnImpact;
            Spread = material.spread;
            SpreadRicochet = material.spreadRicochet;
            RicochetProbabilityMin = material.ricochetProbabilityMin;
            RicochetProbabilityMax = material.ricochetProbabilityMax;
            Bounciness = material.bounciness;
            IsLiquid = material.isLiquid;
            SkipImpactObjects = material.doNotTriggerSpawningOfProjectileImpactObjects;
        }

        public float EnergyLossPerUnit { get; }
        public bool StopOnImpact { get; }
        public float Spread { get; }
        public float SpreadRicochet { get; }
        public float RicochetProbabilityMin { get; }
        public float RicochetProbabilityMax { get; }
        public float Bounciness { get; }
        public bool IsLiquid { get; }
        public bool SkipImpactObjects { get; }
    }
}
