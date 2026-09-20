using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Statistics;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Statistics;

[RegisterPacketProcessor]
public class KillStatisticsProcessor : PacketProcessor<KillStatisticsPacket>
{
    protected override void ProcessPacket(KillStatisticsPacket packet, NebulaConnection conn)
    {
        if (IsHost || !Multiplayer.Session.IsGameLoaded) return;
        if (packet.Snapshot) Multiplayer.Session.Kills.ApplySnapshot(packet.Data);
        else Multiplayer.Session.Kills.ApplyDelta(packet);
    }
}

[RegisterPacketProcessor]
public class KillStatisticsRequestProcessor : PacketProcessor<KillStatisticsRequest>
{
    protected override void ProcessPacket(KillStatisticsRequest packet, NebulaConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player == null) return;
        if (packet.Subscribe) Multiplayer.Session.Kills.Subscribe(player.Id, conn);
        else Multiplayer.Session.Kills.Unsubscribe(player.Id);
    }
}
