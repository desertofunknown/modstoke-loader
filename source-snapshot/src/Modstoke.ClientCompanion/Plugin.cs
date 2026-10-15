using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Bolt;
using OpenACAI.Sons;

namespace Modstoke.ClientCompanion;

// Prototype client-only audit responder. No host Admin implementation is linked or copied.
[BepInPlugin("com.modstoke.sotf.clientcompanion", "Modstoke Client Companion", Version)]
public sealed class Plugin : BasePlugin
{
    public const string Version = "0.1.0-preview";
    private readonly HashSet<Guid> _seenNoticeIds = new();
    private readonly Queue<Guid> _noticeOrder = new();
    private readonly AuditRateLimiter _auditRateLimit = new();
    private ConfigEntry<bool>? _allowHostRequests;

    public override void Load()
    {
        _allowHostRequests = Config.Bind("InventoryAudit", "AllowHostRequests", false,
            "Allow a compatible multiplayer host to request and receive this player's item IDs and counts. Disabled by default.");
        OpenACAISonsServerNotices.Received += ReceiveNotice;
        Log.LogInfo("Client companion audit responder loaded. Inventory sharing is " +
            (_allowHostRequests.Value ? "enabled" : "disabled") + ".");
    }

    public override bool Unload()
    {
        OpenACAISonsServerNotices.Received -= ReceiveNotice;
        return true;
    }

    private void ReceiveNotice(OpenACAISonsServerNotice notice)
    {
        if (notice?.NoticeId is not Guid id || id == Guid.Empty ||
            !String.Equals(notice.Message, AuditProtocol.NoticeMessage, StringComparison.Ordinal) ||
            _allowHostRequests?.Value != true ||
            !OpenACAISonsServerNotices.ClientReady || !OpenACAISonsClientCommands.IsSupported ||
            HostAdminAssemblyPresent() || !_seenNoticeIds.Add(id)) return;

        _noticeOrder.Enqueue(id);
        if (_noticeOrder.Count > 128) _seenNoticeIds.Remove(_noticeOrder.Dequeue());
        if (!TryGetClientSession(out IntPtr serverPointer, out uint connectionId) ||
            !_auditRateLimit.TryBegin(serverPointer, connectionId, Environment.TickCount64))
        {
            Log.LogInfo("Audit request ignored: client session unavailable or local per-session disclosure limit reached.");
            return;
        }

        List<string> commands;
        if (!InventoryReader.TryRead(out var items))
            commands = new List<string> { AuditProtocol.Failure(id, "!unavailable", Version) };
        else if (!AuditProtocol.TryBuild(id, items, Version, out commands))
            commands = new List<string> { AuditProtocol.Failure(id, "!too-large", Version) };

        int queued = 0;
        try
        {
            foreach (string command in commands)
            {
                var result = OpenACAISonsClientCommands.Submit(command, out _);
                if (result != OpenACAISonsClientCommandSendResult.Queued)
                {
                    Log.LogWarning("Audit submission stopped after " + queued + " queued chunk(s): " + result +
                        ". Delivery is unconfirmed; no retry.");
                    return;
                }
                queued++;
            }
            Log.LogInfo("Audit queued " + queued + " chunk(s). Host receipt and execution are unconfirmed.");
        }
        catch (Exception error)
        {
            Log.LogWarning("Audit submission failed after " + queued + " queued chunk(s): " +
                error.GetType().Name + ". Outcome unknown; no retry.");
        }
    }

    private static bool HostAdminAssemblyPresent()
    {
        // Avoid two reporters on advanced client setups that also load the existing Admin DLL.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (String.Equals(assembly.GetName().Name, "OpenACAIAdminTool", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool TryGetClientSession(out IntPtr serverPointer, out uint connectionId)
    {
        serverPointer = IntPtr.Zero;
        connectionId = 0;
        try
        {
            if (!BoltNetwork.isRunning || !BoltNetwork.isClient || BoltNetwork.isServer || !BoltNetwork.isConnected)
                return false;
            var server = BoltNetwork.server;
            if (server == null || server.Pointer == IntPtr.Zero) return false;
            serverPointer = server.Pointer;
            connectionId = server.ConnectionId;
            return true;
        }
        catch { return false; }
    }
}
