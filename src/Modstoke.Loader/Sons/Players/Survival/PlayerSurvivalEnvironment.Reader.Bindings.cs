// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

namespace Modstoke.Sons
{
    internal sealed partial class PlayerSurvivalEnvironmentTemperature
    {
        internal bool MatchesReaderBinding(PlayerBridge bridge, PlayerCarrier carrier)
            => ReferenceEquals(_bridge, bridge) && ReferenceEquals(_carrier, carrier)
                && ReferenceEquals(_snapshot, carrier.RestoredSurvival);
    }

    internal sealed partial class PlayerSurvivalEnvironmentExposureServer
    {
        internal bool MatchesReaderBinding(PlayerBridge bridge, PlayerCarrier carrier,
            PlayerSurvivalEnvironmentClimate climate)
            => ReferenceEquals(_bridge, bridge) && ReferenceEquals(_carrier, carrier)
                && ReferenceEquals(_climate, climate) && ReferenceEquals(_survival, carrier.RestoredSurvival);
    }
}
