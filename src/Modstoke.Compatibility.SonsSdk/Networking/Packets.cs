using System.Reflection;
using Bolt;
using Endnight.Extensions;
using HarmonyLib;
using RedLoader;
using RedLoader.Utils;
using Sons.Multiplayer;
using UdpKit;
using UnityEngine;

namespace SonsSdk.Networking;

internal interface ISdkPacketDriver
{
    void Initialize();
    void Register(Packets.NetEvent netEvent);
    bool Unregister(Packets.NetEvent netEvent);
    void HandlePacket(UdpPacket packet, BoltConnection fromConnection);
    void SendPacket(Packets.EventPacket packet, uint? skipConnection);
    Packets.EventPacket GetPacket(int typeId, int size, GlobalTargets targets);
    Packets.EventPacket GetPacket(int typeId, int size, uint targetConnectionId);
}

internal interface ISdkPacketFallbackDriver : ISdkPacketDriver
{
    UdpPacket ClonePacket(UdpPacket packet);
    bool PacketReceived(BoltConnection instance, UdpPacket packet);
    bool PacketDelivered(BoltConnection instance, Packet packet);
    bool ChatEventPatch(CoopServerCallbacks instance, ChatEvent evnt);
    void Patch();
}

public static class Packets
{
    /// <summary>Whether this runtime enforces each event's client-relay policy.</summary>
    public static bool SupportsClientRelayPolicy => ClientRelayPolicy.Value;

    private static readonly Lazy<bool> ClientRelayPolicy = new(() => Assembly.Load("Modstoke.Loader")
        .GetType("Modstoke.Loader.Packets", false)
        ?.GetProperty("EnforcesClientRelayPolicy", BindingFlags.Public | BindingFlags.Static)
        ?.GetValue(null) is true);

    internal static readonly Dictionary<int, NetRegistration> RegisteredEvents = new();

    private static readonly Lazy<ISdkPacketFallbackDriver> FallbackDriver = new(() =>
    {
        var type = Assembly.Load("Modstoke.Loader")
            .GetType("Modstoke.Loader.PacketFallback", true);
        var method = type.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            ?? throw new MissingMethodException(type.FullName, "Create");
        return ((Func<ISdkPacketFallbackDriver>)method.CreateDelegate(typeof(Func<ISdkPacketFallbackDriver>)))();
    });
    internal static ISdkPacketFallbackDriver Fallback => FallbackDriver.Value;

    /// <summary>
    /// Gets called when a chat message arrives. Returning true means you handled the message and none of the other handlers get called.
    /// If you just want to read the message, return false so the chat event gets sent to all other handlers as well.
    /// </summary>
    public static event Func<ChatEvent, bool> OnChatReceived;

    internal static Func<ChatEvent, bool> ChatHandlers => OnChatReceived;

    internal static void Init()
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Packets.Initialize();
            return;
        }

        Fallback.Initialize();
    }

    public static void Register(NetEvent ev)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Packets.Register(ev);
            return;
        }

        Fallback.Register(ev);
    }

    public static bool Unregister(NetEvent ev)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Packets.Unregister(ev);

        return Fallback.Unregister(ev);
    }

    internal static UdpPacket ClonePacket(UdpPacket packet)
    {
        return Fallback.ClonePacket(packet);
    }

    /// <summary>
    /// Send it to the matching NetEvent. Also relay it to the clients / target BoltConnection if necessary.
    /// </summary>
    /// <param name="packet"></param>
    /// <param name="fromConnection"></param>
    internal static void HandlePacket(UdpPacket packet, BoltConnection fromConnection)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Packets.HandlePacket(packet, fromConnection);
            return;
        }

        Fallback.HandlePacket(packet, fromConnection);
    }

    internal static void SendPacket(EventPacket packet, uint? skipConn = null)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
        {
            driver.Packets.SendPacket(packet, skipConn);
            return;
        }

        Fallback.SendPacket(packet, skipConn);
    }

    internal static EventPacket GetPacket(int typeId, int size, GlobalTargets targets)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Packets.GetPacket(typeId, size, targets);

        return Fallback.GetPacket(typeId, size, targets);
    }
    
    internal static EventPacket GetPacket(int typeId, int size, uint targetConnectionId)
    {
        var driver = SdkEvents.BeginLifecycleSetup();
        if (driver != null)
            return driver.Packets.GetPacket(typeId, size, targetConnectionId);

        return Fallback.GetPacket(typeId, size, targetConnectionId);
    }
    
    public abstract class NetEvent
    {
        public abstract string Id { get; }

        /// <summary>
        /// Whether the server may forward a client's payload to other clients.
        /// Server-authoritative protocols should return false and send their own
        /// validated replies. The default preserves existing SDK packet behavior.
        /// </summary>
        public virtual bool AllowClientRelay => true;

        private int _cachedIdHash = -1;
        private readonly HashSet<uint> _relayFailureConnections = new();

        public abstract void Read(UdpPacket packet, BoltConnection fromConnection);

        protected EventPacket NewPacket(int size, GlobalTargets targets) 
            => GetPacket(GetIdHash(), size, targets);

        protected EventPacket NewPacket(int size, uint targetConnection) 
            => GetPacket(GetIdHash(), size, targetConnection);

        protected EventPacket NewPacket(int size, BoltConnection targetConnection)
            => NewPacket(size, targetConnection.ConnectionId);

        protected void Send(EventPacket packet)
        {
            SendPacket(packet);
        }

        protected bool TryRelay<T>(UdpPacket packet, BoltConnection fromConnection) where T : MonoBehaviour, IPacketReader
        {
            var entity = packet.ReadBoltEntity();
            if (!entity)
            {
                ReportRelayFailure(fromConnection, "entity not found");
                return false;
            }

            if (!entity.TryGetComponent<T>(out var comp))
            {
                ReportRelayFailure(fromConnection, $"component {typeof(T).Name} not found on object");
                return false;
            }

            comp.ReadPacket(packet, fromConnection);
            return true;
        }

        // Despawned entities and bogus remote ids are routine, so report once per connection.
        private void ReportRelayFailure(BoltConnection fromConnection, string reason)
        {
            var connectionId = fromConnection?.ConnectionId ?? 0;
            if (_relayFailureConnections.Add(connectionId))
                RLog.Warning($"Packet relaying failed for {Id} from connection {connectionId}: {reason}");
        }

        public int GetIdHash()
        {
            if (_cachedIdHash == -1)
                _cachedIdHash = LoaderUtils.HashString(Id);
            return _cachedIdHash;
        }
    }

    public record EventPacket(UdpPacket Packet, GlobalTargets Targets, uint TargetConnectionId);
    
    internal class NetRegistration
    {
        public int IdHash;
        public readonly Action<UdpPacket, BoltConnection> ReadFunc;
        public readonly bool AllowClientRelay;

        public NetRegistration(int idHash, Action<UdpPacket, BoltConnection> readFunc)
            : this(idHash, readFunc, true) { }

        public NetRegistration(int idHash, Action<UdpPacket, BoltConnection> readFunc, bool allowClientRelay)
        {
            IdHash = idHash;
            ReadFunc = readFunc;
            AllowClientRelay = allowClientRelay;
        }
    }
    
    public interface IPacketReader
    {
        public void ReadPacket(UdpPacket packet, BoltConnection fromConnection);
    }

    internal class NetworkingPatches
    {
        [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketReceived), typeof(UdpPacket))]
        [HarmonyPrefix]
        private static bool PacketReceived(BoltConnection __instance, UdpPacket udpPacket)
        {
            return Fallback.PacketReceived(__instance, udpPacket);
        }

        [HarmonyPatch(typeof(BoltConnection), nameof(BoltConnection.PacketDelivered), typeof(Packet))]
        [HarmonyPrefix]
        private static bool PacketDelivered(BoltConnection __instance, Packet packet)
        {
            return Fallback.PacketDelivered(__instance, packet);
        }
        
        [HarmonyPatch(typeof(CoopServerCallbacks), nameof(CoopServerCallbacks.OnEvent), typeof(ChatEvent))]
        [HarmonyPrefix]
        public static bool ChatEventPatch(CoopServerCallbacks __instance, ChatEvent evnt)
        {
            return Fallback.ChatEventPatch(__instance, evnt);
        }
        
        public static void Patch()
        {
            Fallback.Patch();
        }
    }
}
