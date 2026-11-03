// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    public sealed partial class PlayerAuthorityDynamics
    {
        private PlayerSurvivalRuntime _survival;
        private bool _survivalPrepared;

        internal bool TryBindSurvival(PlayerSurvivalRuntime survival,
            PlayerCarrier carrier, PlayerBridge bridge)
        {
            RequireCurrent();
            if (_survivalPrepared || _survival != null || !ReferenceEquals(_carrier, carrier) || !ReferenceEquals(_bridge, bridge)) return false;
            _survivalPrepared = true;
            _survival = survival;
            return true;
        }

        internal bool SurvivalCurrent(PlayerSurvivalRuntime survival)
        {
            RequireCurrent();
            return ReferenceEquals(_survival, survival);
        }

        internal void ReleaseSurvival(PlayerSurvivalRuntime survival)
        {
            if (ReferenceEquals(_survival, survival)) _survival = null;
        }

        /// <summary>
        /// The aggregate/motor must defer, not discard, stamina actions while this is true.
        /// This property alone does not stop the independently owned stamina coroutine.
        /// </summary>
        public bool MustDeferSurvivalWriters => _survival?.MustDeferStatWriters == true;
    }
}
