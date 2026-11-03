// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class VehiclePreservation
    {
        public sealed partial class CartSaveHandoff
        {
            private VehicleSaveHandoffResult _writerResult;

            /// <summary>
            /// Resume the intact source after confirmed cancellation, using its original activation,
            /// frozen and runner policy. Timers exclude the suspended interval. No callback replay.
            /// </summary>
            public VehicleSaveHandoffResult TryResumeCancelledSourceWriters()
                => ResumeWriters(false);

            /// <summary>
            /// Resume the exact new owner after known-value restoration and save resumption.
            /// The caller must first finish its cross-peer/reference barrier and external-writer
            /// contract. This local operation does not certify complete vehicle migration.
            /// </summary>
            public VehicleSaveHandoffResult TryResumeReplacementWriters()
                => ResumeWriters(true);

            private VehicleSaveHandoffResult ResumeWriters(bool replacement)
            {
                if (!Enter()) return Rejected("Writer resumption requires the selected game thread without reentry.");
                bool attempted = false;
                CartWriterHold target = null;
                try
                {
                    if (_writerResult != null) return _writerResult;
                    Require(_terminalResult?.Confirmed == true,
                        "Save cancellation or replacement save resumption must be confirmed first.");
                    RequireRegistry();
                    if (replacement)
                    {
                        Require(Stage == VehicleSaveHandoffStage.SavingResumed && _replacement != null
                            && !Live(_source.Entity) && !Live(_source.Saver), "No completed replacement save handoff exists.");
                        target = _replacementWriters;
                        Require(_replacement.Matches() && _replacement.Entity.isOwner && !_replacement.Cart.IsNetworkProxy,
                            "Only the exact actual owner can resume the replacement cart.");
                        int index = FindSlot(_snapshot, _replacement.Saver);
                        Require(_records[index].Pointer == _reservedRecord.Pointer
                            && _replacement.Saver._itemState.Pointer == _reservedRecord.Pointer
                            && _replacement.Saver._saveActive == _saveActive,
                            "Replacement save identity or policy changed before writer resumption.");
                        _replacementValues.RequireRestored(_replacement);
                        RequireSaveInputs(_replacement);
                        _sourceWriters.State.RequireUnchanged(_replacement, target.Components);
                    }
                    else
                    {
                        Require(Stage == VehicleSaveHandoffStage.Cancelled,
                            "Only an intact cancelled source can resume.");
                        target = _sourceWriters;
                        RequireSource(_saveActive);
                        int index = FindSlot(_snapshot, _source.Saver);
                        Require(_records[index].Pointer == _originalRecord.Pointer, "Original save registration changed.");
                        _replacementValues.RequireSourceUnchanged(_source);
                        _sourceWriters.State.RequireUnchanged(_source, target.Components);
                    }
                    target.RequireSuspended();
                    Require(!_reentered, "Reentry occurred before writer resumption.");
                    attempted = true;
                    Stage = VehicleSaveHandoffStage.Unconfirmed;
                    _occupants?.ResumeRadio(replacement);
                    target.Resume(_sourceWriters);
                    _occupants?.Unregister();
                    Stage = VehicleSaveHandoffStage.WritersResumed;
                    return _writerResult = new(true, true, Stage,
                        "Original local writer and activation policy restored; no complete cross-peer migration certification.");
                }
                catch (Exception error)
                {
                    if (attempted) target?.KeepSuspended();
                    var result = new VehicleSaveHandoffResult(attempted, false, Stage,
                        Failure(attempted ? "Writer resumption unconfirmed; keep quarantined and do not retry"
                            : "Writer resumption rejected", error));
                    if (attempted) _writerResult = result;
                    return result;
                }
                finally { _busy = false; }
            }
        }
    }
}
