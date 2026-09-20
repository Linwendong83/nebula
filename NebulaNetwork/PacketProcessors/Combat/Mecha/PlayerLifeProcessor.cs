using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.Mecha;
using NebulaWorld;
using NebulaWorld.Combat;

namespace NebulaNetwork.PacketProcessors.Combat.Mecha;

[RegisterPacketProcessor]
public class PlayerLifeProcessor : PacketProcessor<PlayerLifePacket>
{
    protected override void ProcessPacket(PlayerLifePacket packet, NebulaConnection conn)
    {
        if (packet.Life == null) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.Acknowledgement || packet.PlayerSnapshot == null) return;
            var data = (PlayerData)player.Data;
            packet.PlayerId = player.Id;
            if (packet.Life.Revision > data.Life.Revision && packet.Life.DeathCount >= data.Life.DeathCount)
            {
                PlayerLifeManager.StoreServer(data, packet.PlayerSnapshot);
                packet.Life = data.Life;
                packet.PlayerSnapshot = System.Array.Empty<byte>();
                Server.SendPacketExclude(packet, conn);
                Multiplayer.Session.Life.ApplyRemote(player.Id, data.Life);
            }
            conn.SendPacket(new PlayerLifePacket { PlayerId = player.Id, Life = data.Life, Acknowledgement = true });
        }
        else if (packet.Acknowledgement) Multiplayer.Session.Life.Acknowledge(packet.Life.Revision);
        else Multiplayer.Session.Life.ApplyRemote(packet.PlayerId, packet.Life);
    }
}
