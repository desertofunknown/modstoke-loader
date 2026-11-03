// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using UnityEngine;
using Point = System.Numerics.Vector3;

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentReader
    {
        private PlayerWaterContactAuthority.ServerContinuation _waterContacts;
        internal bool HasWaterContactOwner => _waterContacts != null;

        internal bool TryBindWaterContacts(PlayerWaterContactAuthority.ServerContinuation contacts,
            out string detail)
        {
            detail = "Water history must belong to this reader's exact restored carrier.";
            if (IsDisposed || _reading || Environment.CurrentManagedThreadId != _thread || Time.inFixedTimeStep
                || _waterContacts != null || contacts == null
                || !ReferenceEquals(contacts.Carrier, _carrier) || contacts.FailureReason != null) return false;
            _waterContacts = contacts;
            detail = "Exact carrier native water continuation bound; lifetime remains owned by the adapter.";
            return true;
        }

        internal bool TryReadWaterContacts(out PlayerWaterContactSample sample, out string detail)
        {
            sample = default;
            detail = "The reader has no exact-actor native water continuation.";
            if (IsDisposed || _waterContacts == null) return false;
            return _waterContacts.TryRead(out sample, out detail);
        }

        internal bool WaterContactsCurrent(PlayerWaterContactSample sample, int frame, out string detail)
        {
            detail = "The exact actor native water observation changed.";
            if (IsDisposed || sample.Frame != frame || _waterContacts == null || !_waterContacts.IsCurrentSample(sample)) return false;
            detail = "Exact actor native contact/cache observation is current; region loading remains separate.";
            return true;
        }

        private readonly record struct WaterObservation(WaterCoverage Coverage,
            OceanHeight Ocean, WaterColumn Volume, PlayerWaterRelation Relation);

        private bool TryReadWater(Vector3 position, out WaterObservation observation, out string detail)
        {
            observation = default;
            detail = "The exact approved water configuration is required.";
            if (!_water.Matches(_waterSettings)) return false;
            var point = new Point(position.x, position.y + _water.WaterlineOffset, position.z);
            if (!float.IsFinite(point.Y)) return false;
            if (!ServerWorld.TryReadWaterCoverage(point, _water.WaterLayerMask,
                _water.RequiredWaterTag, out var coverage, out var status))
            { detail = "Actor loaded-water coverage unavailable: " + status; return false; }
            OceanHeight ocean = null;
            WaterColumn volume = null;
            if (coverage.OceanVolumeCount > 0 && _waterSettings.OceanMode == OceanWaterMode.Include)
            {
                if (!ServerWorld.TryReadOceanHeight(point, _water.ObjectWidth, out ocean, out status))
                { detail = "Actor ocean surface unavailable: " + status; return false; }
            }
            else if (coverage.NonOceanVolumeCount > 0)
            {
                if (!ServerWorld.TryReadWaterVolume(point, _water.WaterLayerMask,
                    _water.BlockerLayerMask, _water.RequiredWaterTag, out volume, out status))
                { detail = "Actor non-ocean surface unavailable: " + status; return false; }
            }
            float? height = ocean?.Height ?? volume?.Height;
            var relation = !height.HasValue ? PlayerWaterRelation.NoApplicableLoadedVolume
                : point.Y < height.Value ? PlayerWaterRelation.BelowSurface
                : PlayerWaterRelation.AtOrAboveSurface;
            observation = new(coverage, ocean, volume, relation);
            detail = "Loaded point-water geometry sampled; native contact history and complete loading are not established.";
            return true;
        }

        private static bool WaterCurrent(in WaterObservation water, int frame, out string detail)
        {
            detail = "Actor water observation changed or spans different rendered frames.";
            if (water.Coverage == null || water.Coverage.Frame != frame) return false;
            if (!ServerWorld.IsCurrentWaterCoverage(water.Coverage, out var status)
                || (water.Ocean != null && (water.Ocean.Frame != frame
                    || !ServerWorld.IsCurrentOceanHeight(water.Ocean, out status)))
                || (water.Volume != null && (water.Volume.Frame != frame
                    || !ServerWorld.IsCurrentWaterColumn(water.Volume))))
            { detail += " " + status; return false; }
            detail = "Current actor waterline and qualified surface observed.";
            return true;
        }
    }
}
