#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;
using NebulaModel.Utils;

#endregion

namespace NebulaNetwork.PacketProcessors.Players;

[RegisterPacketProcessor]
public class PlayerSandCountProcessor : PacketProcessor<PlayerSandCount>
{
    protected override void ProcessPacket(PlayerSandCount packet, NebulaConnection conn)
    {
        if (IsHost) return;

        var player = GameMain.mainPlayer;
        var originalSandCount = player.sandCount;

        player.SetHiddenProperty(nameof(Player.sandCount), player.sandCount + packet.SandCount);
        if (player.sandCount != originalSandCount)
        {
            UIRoot.instance.uiGame.OnSandCountChanged(player.sandCount, player.sandCount - originalSandCount, (ESandSource)0);
        }
    }
}
