#region

using NebulaAPI.Packets;
using NebulaModel;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Session;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Session;

[RegisterPacketProcessor]
public class ServerStatusRequestProcessor : PacketProcessor<ServerStatusRequest>
{
    protected override void ProcessPacket(ServerStatusRequest packet, NebulaConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        // Keep the custom description bounded so a long host entry cannot bloat the probe packet.
        var description = Config.Options?.ServerDescription?.Trim() ?? string.Empty;
        if (description.Length > 200)
        {
            description = description.Substring(0, 200);
        }

        conn.SendPacket(new ServerStatusResponse(Multiplayer.Session.NumPlayers, Multiplayer.Session.IsGameLoaded,
            GameConfig.gameVersion.sig, Config.ModVersion, description));
    }
}
