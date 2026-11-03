using Sons.Multiplayer;
using Bolt;

namespace SonsSdk.Networking;

public static class NetExtensions
{
    public static ulong GetSteamId(this Event evnt)
    {
        return MultiplayerUtilities.GetSteamId(evnt.RaisedBy);
    }
    
    public static bool HasSenderRole(this Event evnt, PlayerRoles role)
    {
        var roles = MultiplayerPlayerRoles._instance;
        if (roles == null || roles.WasCollected)
            return false;

        return roles.TryGetData(evnt.GetSteamId(), out var playerData) && ((int)playerData.GetRoleValue() >= (int)role);
    }

    public static BoltEntity Init(this BoltEntity entity, int id, string factory)
    {
        entity._prefabId = id;
        entity._serializerGuid = factory;
        return entity;
    }
}