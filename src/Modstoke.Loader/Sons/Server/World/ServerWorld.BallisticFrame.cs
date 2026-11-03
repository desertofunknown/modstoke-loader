// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System.Numerics;

namespace Modstoke.Sons
{
    /// <summary>Copied simulation settings for one server world frame, independent of any weapon prefab.</summary>
    public sealed class BallisticFrame
    {
        internal BallisticFrame(WorldRecordContext world, int frame,
            Ballistics.BallisticSettings settings, CollisionTriggers triggers)
        {
            World = world;
            Frame = frame;
            DeltaTime = UnityEngine.Time.deltaTime;
            MaximumDeltaTime = settings.MaximumDeltaTime;
            BulletDrop = settings.BulletDrop;
            BulletDrag = settings.BulletDrag;
            BallisticMaterials = settings.BallisticMaterials;
            var wind = settings.WindVelocity;
            WindVelocity = new Vector3(wind.x, wind.y, wind.z);
            var gravity = UnityEngine.Physics.gravity;
            Gravity = new Vector3(gravity.x, gravity.y, gravity.z);
            AirDensity = settings.AirDensity;
            Triggers = triggers;
        }

        internal WorldRecordContext World { get; }
        public int Frame { get; }
        public float DeltaTime { get; }
        public float MaximumDeltaTime { get; }
        public bool BulletDrop { get; }
        public bool BulletDrag { get; }
        public bool BallisticMaterials { get; }
        public Vector3 WindVelocity { get; }
        public Vector3 Gravity { get; }
        public float AirDensity { get; }
        public CollisionTriggers Triggers { get; }
    }
}
