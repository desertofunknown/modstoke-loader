// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using Endnight.Utilities;
using global::Sons.Utilities;
using TheForest.Utils;

namespace Modstoke.Sons.Combat
{
    internal sealed partial class StrictCombatEquipmentController
    {
        private bool _equipAudioAttempted, _equipAudioRequested;

        // A returned local audio request does not establish audible playback.
        internal bool IsEquipAudioRequested => _equipAudioRequested;

        internal bool TryPlayEquipAudio()
        {
            if (Environment.CurrentManagedThreadId != _thread || !IsInitialized || _equipAudioAttempted) return false;
            _equipAudioAttempted = true;
            bool dispatched = false;
            try
            {
                BeginFrame();
                RequireEquipAudioCurrent();
                var position = _controllerTransform.position;
                Require(float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z));
                string audioEvent = "event:/SotF Events/player sounds/Inv/MouseOvers/mouseover_" + _itemName;
                var parameters = Array.Empty<Il2CppSystem.Object>();
                RequireEquipAudioCurrent();

                // Native's transform overload forwards this position with no network role.
                // Consume the attempt before dispatch; an uncertain request must never replay.
                dispatched = true;
                FMODCommon.PlayOneshot(audioEvent, position, FMODCommon.NetworkRole.None, parameters);
                RequireEquipAudioCurrent();
                _equipAudioRequested = true;
                return true;
            }
            catch (Exception)
            {
                if (dispatched) Fail();
                return false;
            }
        }

        private void RequireEquipAudioCurrent()
        {
            RequireCurrent();
            Require(string.Equals(_data._name, _itemName, StringComparison.Ordinal));
            RequireFrame();
        }
    }
}
