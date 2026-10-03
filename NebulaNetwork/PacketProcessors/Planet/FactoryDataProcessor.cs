#region

using NebulaAPI.GameState;
using NebulaAPI.Packets;
using NebulaModel;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Planet;
using NebulaWorld;
using NebulaWorld.GameStates;

#endregion

namespace NebulaNetwork.PacketProcessors.Planet;

[RegisterPacketProcessor]
public class FactoryDataProcessor : PacketProcessor<FactoryData>
{
    protected override void ProcessPacket(FactoryData packet, NebulaConnection conn)
    {
        if (IsHost)
        {
            return;
        }
        // The whole fragment is received
        Multiplayer.Session.Generations.Import(packet.EnemyGenerations);
        GameStatesManager.FragmentSize = 0;

        // Stop packet processing until factory is imported and loaded
        Multiplayer.Session.Network.PacketProcessor.EnablePacketProcessing = false;

        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        Multiplayer.Session.Planets.PendingFactories.Add(packet.PlanetId, packet.BinaryData);
        Multiplayer.Session.Planets.PendingBuildAssignments[packet.PlanetId] = packet.BuildAssignments;
        Multiplayer.Session.Planets.PendingTerrainData.Add(packet.PlanetId, packet.TerrainModData);

        lock (PlanetModelingManager.fctPlanetReqList)
        {
            PlanetModelingManager.fctPlanetReqList.Enqueue(planet);
        }
    }
}
