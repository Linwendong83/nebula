using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameStates;
using NebulaWorld;
using NebulaWorld.GameStates;

namespace NebulaNetwork.PacketProcessors.GameStates;

[RegisterPacketProcessor]
public class MetadataProductionProcessor : PacketProcessor<MetadataProductionPacket>
{
    protected override void ProcessPacket(MetadataProductionPacket packet, NebulaConnection conn)
    {
        if (IsHost || !Multiplayer.Session.IsGameLoaded || packet.WorldId != SaveManager.WorldId) return;
        if (MetadataManager.Apply(packet))
            conn.SendPacket(new MetadataReceiptPacket { WorldId = packet.WorldId, Sequence = packet.Sequence });
    }
}

[RegisterPacketProcessor]
public class MetadataReceiptProcessor : PacketProcessor<MetadataReceiptPacket>
{
    protected override void ProcessPacket(MetadataReceiptPacket packet, NebulaConnection conn)
    {
        if (!IsHost || packet.WorldId != SaveManager.WorldId) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.Metadata.Acknowledge(player.Id, packet.Sequence);
    }
}
