#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Session;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Session;

[RegisterPacketProcessor]
public class PlayerDisconnectedProcessor : PacketProcessor<PlayerDisconnected>
{
    protected override void ProcessPacket(PlayerDisconnected packet, NebulaConnection conn)
    {
        Multiplayer.Session.NumPlayers = packet.NumPlayers;
        Multiplayer.Session.World.DestroyRemotePlayerModel(packet.PlayerId);
        Multiplayer.Session.PowerTowers.RemovePlayer(packet.PlayerId);
        Multiplayer.Session.BattleVisuals.RemoveOwner(packet.PlayerId);
        Multiplayer.Session.Life.Remove(packet.PlayerId);
    }
}
