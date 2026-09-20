#region

using NebulaAPI.Packets;
using NebulaModel;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Players;

[RegisterPacketProcessor]
internal class PlayerMechaDataProcessor : PacketProcessor<PlayerMechaData>
{
    protected override void ProcessPacket(PlayerMechaData packet, NebulaConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var player = Multiplayer.Session.Server.Players.Get(conn);
        if (player == null)
        {
            Log.Warn("Can't find the connected player for PlayerMechaData!");
            return;
        }

        //Find correct player for data to update, preserve sand count if syncing is enabled
        var data = (NebulaModel.DataStructures.PlayerData)player.Data;
        if (packet.Life == null || packet.Life.Revision <= data.Life.Revision) return;
        // A periodic inventory snapshot must not undo a confirmed death/respawn transition.
        if (packet.Life.DeathCount < data.Life.DeathCount) return;
        data.Life = packet.Life;
        var sandCount = player.Data.Mecha.SandCount;
        player.Data.Mecha = packet.Data;
        if (Config.Options.SyncSoil)
        {
            player.Data.Mecha.SandCount = sandCount;
        }
    }
}
