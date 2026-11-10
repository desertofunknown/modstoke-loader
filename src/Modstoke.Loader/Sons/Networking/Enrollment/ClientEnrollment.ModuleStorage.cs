// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;

namespace Modstoke.Sons
{
    public static partial class ClientEnrollment
    {
        internal static ControlResult RequireModuleStorageForNextStart()
        {
            if (_ownerThreadId == 0) return ControlResult.Unavailable;
            if (!OnOwnerThread) return ControlResult.WrongThread;
            try
            {
                var socket = BoltCore._udpSocket;
                if (BoltNetwork.isRunning || socket != null && !socket.WasCollected
                    && socket.State != UdpKit.UdpSocketState.Shutdown)
                    return ControlResult.AlreadyStarting;
                var capability = ModuleStorage.ProtocolCapability;
                if (!_selected) return SelectGatedForNextStart(true, capability, capability);
                if (!ServerSessions.HooksInstalled || !InstallGateHooks())
                    return ControlResult.Unavailable;
                // Add this requirement without replacing an existing package admission policy.
                _localCapabilities |= capability;
                _requiredClientCapabilities |= capability;
                _gateSelected = true;
                return ControlResult.Success;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return ControlResult.Failed;
            }
        }

        // Called from the Bolt start hook, so it adds to whatever selection plugins made rather than replacing it.
        internal static bool AddContentForStart(bool server, bool requireClients)
        {
            if (!OnOwnerThread || _faulted) return false;
            if (!_selected)
            {
                _selected = true;
                _localCapabilities = 0;
                _requiredClientCapabilities = 0;
                _gateSelected = false;
                Entries.Clear();
            }
            _localCapabilities |= ContentAgreement.Capability;
            if (!requireClients) return true;
            _requiredClientCapabilities |= ContentAgreement.Capability;
            // Holding packets until the check passes is what keeps unchecked (vanilla) clients out.
            if (!_gateSelected && (!ServerSessions.HooksInstalled || !InstallGateHooks())) return false;
            _gateSelected = true;
            return true;
        }

        internal static bool ModuleStorageSelected => _selected && _gateSelected && !_faulted
            && (_localCapabilities & ModuleStorage.ProtocolCapability) != 0
            && (_requiredClientCapabilities & ModuleStorage.ProtocolCapability) != 0;
    }
}
