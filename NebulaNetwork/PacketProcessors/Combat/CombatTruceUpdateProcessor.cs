#region

using NebulaAPI.Packets;
using NebulaModel.DataStructures.Chat;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;
using NebulaWorld.MonoBehaviours.Local.Chat;

#endregion

namespace NebulaNetwork.PacketProcessors.Combat;

[RegisterPacketProcessor]
public class CombatTruceUpdateProcessor : PacketProcessor<CombatTruceUpdatePacket>
{
    protected override void ProcessPacket(CombatTruceUpdatePacket packet, NebulaConnection conn)
    {
        if (IsHost)
        {
            return; // Clients request a transaction; this packet is an authoritative result.
        }

        var truceTime = packet.TruceEndTime - (GameMain.gameTick + GameMain.history.dfTruceTimer);
        GameMain.history.AddTruceTime(truceTime);

        var userName = "";
        using (Multiplayer.Session.World.GetRemotePlayersModels(out var remotePlayersModels))
        {
            if (remotePlayersModels.TryGetValue(packet.PlayerId, out var player))
            {
                userName = player.Username;
            }
        }
        var second = (int)(GameMain.history.dfTruceTimer / 60L);
        var minute = second / 60;
        var hour = minute / 60;
        var message = string.Format("{0} set truce time to {1:D2}:{2:D2}:{3:D2}".Translate(),
            userName, hour, minute % 60, second % 60);
        ChatManager.Instance.SendChatMessage(message, ChatMessageType.BattleMessage);
    }
}
