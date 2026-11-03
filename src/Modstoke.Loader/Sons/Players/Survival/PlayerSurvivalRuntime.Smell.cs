// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using global::Sons.Ai.Vail.StimuliTypes;

namespace Modstoke.Sons
{
    public sealed partial class PlayerSurvivalRuntime
    {
        private PlayerStimuli _smellReceiver;
        private int _smellReceiverId;

        public bool HasSmellReceiver => !ReferenceEquals(_smellReceiver, null);

        /// <summary>Bind the actor's exact non-local AI stimuli receiver before activation.</summary>
        public bool TryBindSmellReceiver(PlayerStimuli receiver, out string detail)
        {
            detail = "An exact non-local actor stimuli receiver is required before survival activation.";
            if (IsDisposed || _activationAttempted || HasSmellReceiver
                || Environment.CurrentManagedThreadId != _thread) return false;
            try
            {
                RequireCurrent();
                if (!receiver || receiver.WasCollected || receiver._localPlayer
                    || !receiver.transform.IsChildOf(_carrier.Entity.transform)) return false;
                _smellReceiver = receiver;
                _smellReceiverId = receiver.GetInstanceID();
                detail = "Actor-local smell receiver bound without effect writes.";
                return true;
            }
            catch (Exception error) { detail = "Smell receiver refused: " + error.Message; return false; }
        }

        private void RequireSmellReceiver()
        {
            if (ReferenceEquals(_smellReceiver, null)) return;
            if (_smellReceiver.WasCollected || !_smellReceiver || _smellReceiver._localPlayer
                || _smellReceiver.GetInstanceID() != _smellReceiverId
                || !_smellReceiver.transform.IsChildOf(_carrier.Entity.transform))
                throw new InvalidOperationException("The exact actor smell receiver changed.");
        }

        private void AdvanceSmell(float elapsedSeconds)
        {
            _smellAge += elapsedSeconds;
            _stats._skunkSprayTime = UnityEngine.Time.time - _smellAge;
            if (ReferenceEquals(_smellReceiver, null)) return;
            RequireSmellReceiver();
            var flags = _smellReceiver._stimuliFlags;
            var next = _smellAge < 600f ? flags | PlayerStimuli.Flags.SkunkSmell
                : flags & ~PlayerStimuli.Flags.SkunkSmell;
            _smellReceiver._stimuliFlags = next;
            if (_smellReceiver._stimuliFlags != next)
                throw new InvalidOperationException("The actor smell receiver did not retain its exact flags.");
        }
    }
}
