using NebulaAPI.GameState;
using NebulaAPI.Networking;
using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameStates;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.GameStates;

[RegisterPacketProcessor]
public class MetadataOperationProcessor : PacketProcessor<MetadataOperationPacket>
{
    protected override void ProcessPacket(MetadataOperationPacket packet, NebulaConnection conn)
    {
        if (!Multiplayer.Session.IsGameLoaded || packet.WorldId != SaveManager.WorldId) return;
        if (IsHost)
        {
            var player = Players.Get(conn, EConnectionStatus.Connected);
            if (player == null || packet.Message is MetadataMessage.Quote or MetadataMessage.Result) return;
            Multiplayer.Session.PropertyTransactions.HandleServer(packet, (PlayerData)player.Data, conn.SendPacket);
        }
        else Multiplayer.Session.PropertyTransactions.Receive(packet);
    }
}

[RegisterPacketProcessor]
public class PropertyHistoryProcessor : PacketProcessor<PropertyHistoryPacket>
{
    private long sequence;
    protected override void ProcessPacket(PropertyHistoryPacket packet, NebulaConnection conn)
    {
        if (IsHost || packet.Sequence <= sequence || packet.Consumption?.Length != 6) return;
        sequence = packet.Sequence;
        for (var i = 0; i < 6; i++)
            GameMain.history.SetPropertyItemConsumption(PropertySystem.matrixIds[i], packet.Consumption[i]);
        GameMain.history.hasUsedPropertyBanAchievement = packet.BanAchievement;
    }
}
