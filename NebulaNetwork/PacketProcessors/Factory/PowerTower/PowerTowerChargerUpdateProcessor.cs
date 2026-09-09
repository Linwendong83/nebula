using System.Linq;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Factory.PowerTower;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Factory.PowerTower;

[RegisterPacketProcessor]
internal class PowerTowerChargerUpdateProcessor : PacketProcessor<PowerTowerChargerUpdate>
{
    protected override void ProcessPacket(PowerTowerChargerUpdate packet, NebulaConnection conn)
    {
        if (packet.NodeIds == null) return;

        var towers = Multiplayer.Session.PowerTowers;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null) return;

            // Attribute the snapshot to the connection, and reject towers already removed on the host.
            var powerSystem = packet.PlanetId > 0 ? GameMain.galaxy.PlanetById(packet.PlanetId)?.factory?.powerSystem : null;
            var nodes = powerSystem == null ? [] : packet.NodeIds.Where(id => id > 0 &&
                id < powerSystem.nodeCursor && id < powerSystem.nodePool.Length &&
                powerSystem.nodePool[id].id == id && powerSystem.nodePool[id].isCharger &&
                powerSystem.nodePool[id].coverRadius <= 20f).ToArray();
            var snapshot = new PowerTowerChargerUpdate(player.Id, packet.PlanetId, nodes);
            if (towers.ApplyRemoteState(snapshot))
            {
                Server.SendPacket(towers.GetPlayerState(player.Id));
            }
            return;
        }

        // Keep membership even if this client has not loaded the corresponding factory yet.
        towers.ApplyRemoteState(packet);
    }
}
