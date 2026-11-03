// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Modstoke.Loader;
using UnityEngine;

namespace Modstoke.Sons
{
    public sealed partial class PlayerMigrationAdapter
    {
        private int _sourceEnvironmentSetupFrame = -1;
        private PlayerWaterContactAuthority _sourceWater;
        private PlayerSurvivalEnvironmentExposure _sourceExposure;
        private PlayerWaterContactAuthority.ServerContinuation _serverWater;
        private PlayerSurvivalEnvironmentExposureServer _serverExposure;
        private PlayerWaterPresentation _waterPresentation;
        private bool _sourceWaterReleased, _sourceEnvironmentSealed;

        private bool TryPrepareSourceEnvironment(out string detail)
        {
            detail = "Source environment hooks already prepared.";
            if (_sourceEnvironmentSetupFrame >= 0) return true;
            var support = HarmonySupport.CreateState();
            if (!PlayerWaterContactAuthority.TryPrepareSourceCapture(support, out detail)
                || !PlayerSurvivalEnvironmentExposure.EnsureExposureHooks(support, out detail)) return false;
            _sourceEnvironmentSetupFrame = Time.frameCount;
            return true;
        }

        private bool TryCaptureSourceEnvironment(PlayerSurvivalSnapshot survival, out string detail)
        {
            detail = "Source environment capture requires the original paused water owner.";
            if (_sourceWater == null || _sourceWaterReleased || _sourceEnvironmentSealed
                || !survival.TryCaptureWaterContacts(_sourceWater, out _, out detail)) return false;
            if (_sourceExposure == null)
                return PlayerSurvivalEnvironmentExposure.TryCaptureSource(_lifecycle, survival,
                    HarmonySupport.CreateState(), out _sourceExposure, out detail);
            return _sourceExposure.TryValidateHeldBaseline(out detail)
                && survival.TryBindExposureBaseline(_sourceExposure.Baseline, out detail);
        }

        private bool SourceEnvironmentHeld(out string detail)
        {
            detail = "Source water history is not held.";
            return _sourceWater != null && !_sourceWaterReleased && !_sourceEnvironmentSealed
                && _sourceWater.IsCurrent(out detail)
                && (_sourceExposure == null || _sourceExposure.TryValidateHeldBaseline(out detail));
        }

        // The kernel calls only after validating the exact all-peer Ready receipt and
        // recapturing the complete digest, immediately before native retirement.
        internal bool TrySealSourceEnvironment(BoltMigrationSession session, out string detail)
        {
            if (!Enter(out detail)) return false;
            try
            {
                Require(Role == BoltMigrationRole.Source && SessionMatches(session)
                    && session.Status == BoltMigrationStatus.Prepared && SourceHeld()
                    && _sourceExposure != null && !_sourceEnvironmentSealed,
                    "Environment handoff requires the exact prepared original and authenticated retirement boundary.");
                Require(RetainExactSession(session), "The source environment belongs to a different kernel session.");
                Require(_sourceArmour != null
                    && _sourceArmour.TryValidateSource(_lifecycle, _sourceInventory.Baseline, out detail), detail);
                if (!SourceEnvironmentHeld(out detail)
                    || !_sourceExposure.TryCompleteSourceHandoff(_snapshot.Survival.ExposureBaseline.CaptureId, out detail)) return false;
                _sourceEnvironmentSealed = true;
                detail = "Exact environment baseline sealed for this retirement; source writer fences remain held.";
                return true;
            }
            catch (Exception error)
            {
                detail = Failure("Source environment sealing rejected", error);
                return false;
            }
            finally { Exit(detail); }
        }

        private bool TryReleaseCancelledEnvironment(out string detail)
        {
            detail = "No source environment hold requires release.";
            if (_sourceWater != null && !_sourceWaterReleased)
            {
                // Cancellation preserves accepted native contact changes. A stale export
                // is discarded, never replayed over the original sensor's current state.
                if (_sourceWater.IsCurrent(out _))
                { if (!_sourceWater.TryResumeSource(out detail)) return false; }
                else if (!_sourceWater.TryDiscardUncommittedCapture(out detail)) return false;
                _sourceWaterReleased = true;
            }
            _sourceExposure?.Dispose();
            return true;
        }
    }
}
