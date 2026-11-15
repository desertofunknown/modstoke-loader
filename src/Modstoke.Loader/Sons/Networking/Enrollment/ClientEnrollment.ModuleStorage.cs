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
                NetworkFailures.Report("Modstoke client enrollment", error);
                return ControlResult.Failed;
            }
        }

        // Called from the Bolt start hook after the per-start reset, so it only ever lasts for this start and
        // adds to whatever selection plugins made. A client that takes part always holds its gameplay until the
        // host admits it; a host gates only when it refuses clients that don't take part.
        internal static bool AddContentForStart(bool server, bool requireClients)
        {
            if (!OnOwnerThread || _faulted) return false;
            var gate = !server || requireClients;
            if (gate && (server && !ServerSessions.HooksInstalled || !InstallGateHooks())) return false;
            _contentSelected = true;
            _contentRequired = requireClients;
            _contentGate = gate;
            return true;
        }

        internal static bool ModuleStorageSelected => _selected && _gateSelected && !_faulted
            && (_localCapabilities & ModuleStorage.ProtocolCapability) != 0
            && (_requiredClientCapabilities & ModuleStorage.ProtocolCapability) != 0;
    }
}
