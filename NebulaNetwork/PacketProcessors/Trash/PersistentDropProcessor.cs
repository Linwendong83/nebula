using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Trash;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Trash;

[RegisterPacketProcessor]
public class PersistentDropProcessor : PacketProcessor<PersistentDropPacket>
{
    protected override void ProcessPacket(PersistentDropPacket packet, NebulaConnection conn)
    {
        if (!Multiplayer.Session.IsGameLoaded) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.Acknowledgement) return;
            var owner = ((PlayerData)player.Data).PersistentId;
            if (!Multiplayer.Session.PropertyTransactions.OwnsCommittedOperation(packet.OperationId, owner)) return;
            Multiplayer.Session.Drops.Accept(packet, owner);
            conn.SendPacket(new PersistentDropPacket
            { OperationId = packet.OperationId, Ordinal = packet.Ordinal, Acknowledgement = true });
        }
        else Multiplayer.Session.Drops.Receive(packet);
    }
}
