// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Alex Cooper

using System;
using HarmonyLib;
using Modstoke.Loader;
using UdpKit;

namespace Modstoke.Sons
{
    public static partial class Datagrams
    {
        private static bool InstallHooks()
        {
            if (_patchAttempted)
                return _hooksInstalled;
            _patchAttempted = true;
            if (_support == null || !_support.SafeForHarmonyPatches)
                return false;
            try
            {
                var receive = AccessTools.Method(typeof(BoltConnection), nameof(BoltConnection.PacketReceived),
                    new[] { typeof(UdpPacket) });
                var delivered = AccessTools.Method(typeof(BoltCore), "Udp_PacketDelivered", new[] { typeof(UdpEvent) });
                var lost = AccessTools.Method(typeof(BoltCore), "Udp_PacketLost", new[] { typeof(UdpEvent) });
                if (receive == null || delivered == null || lost == null)
                    throw new MissingMethodException("A datagram packet hook is unavailable.");

                var harmony = new HarmonyLib.Harmony(RuntimeEntryPoint.PluginGuid + ".sons-datagrams");
                // Enrollment runs first; datagrams are handled before ordinary SDK packet hooks.
                harmony.Patch(receive, prefix: new HarmonyMethod(typeof(Datagrams), nameof(BeforeDatagram))
                    { priority = Priority.High });
                harmony.Patch(delivered, prefix: new HarmonyMethod(typeof(Datagrams), nameof(BeforeCompletion))
                    { priority = Priority.First });
                harmony.Patch(lost, prefix: new HarmonyMethod(typeof(Datagrams), nameof(BeforeCompletion))
                    { priority = Priority.First });
                _hooksInstalled = true;
                return true;
            }
            catch (Exception error)
            {
                ReportFailure(error);
                return false;
            }
        }
    }
}
