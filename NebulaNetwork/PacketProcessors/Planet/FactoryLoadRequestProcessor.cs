#region

using NebulaAPI.Networking;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameStates;
using NebulaModel.Packets.Planet;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Planet;

[RegisterPacketProcessor]
public class FactoryLoadRequestProcessor : PacketProcessor<FactoryLoadRequest>
{
    protected override void ProcessPacket(FactoryLoadRequest packet, NebulaConnection conn)
    {
        if (IsClient)
        {
            return;
        }

        var planet = GameMain.galaxy.PlanetById(packet.PlanetID);
        var factory = GameMain.data.GetOrCreateFactory(planet);
        GameMain.galaxy.astrosFactory[planet.id] = factory;
        OnPlanetFactoryLoad(planet);
        Multiplayer.Session.BuildDispatch.SeedForSnapshot(factory);

        using (var writer = new BinaryUtils.Writer())
        {
            factory.Export(writer.BinaryWriter.BaseStream, writer.BinaryWriter);
            var data = writer.CloseAndGetBytes();
            var assignments = Multiplayer.Session.BuildDispatch.ExportSnapshot(packet.PlanetID);
            conn.SendPacket(new FragmentInfo(data.Length + planet.data.modData.Length + assignments.Length));
            conn.SendPacket(new FactoryData(packet.PlanetID, data, planet.data.modData)
            { EnemyGenerations = Multiplayer.Session.Generations.Export(packet.PlanetID), BuildAssignments = assignments });
        }

        // Update syncing player data (Connected player will be update by movement packets)
        var player = Multiplayer.Session.Server.Players.Get(conn, EConnectionStatus.Syncing);
        if (player != null)
        {
            player.Data.LocalPlanetId = packet.PlanetID;
            player.Data.LocalStarId = GameMain.galaxy.PlanetById(packet.PlanetID).star.id;
            // Subscription eligibility reads this accepted location directly.
        }
    }

    static void OnPlanetFactoryLoad(PlanetData planet)
    {
        // Realize planet bases before sending the factory data. This is the host lifecycle
        // transaction of the load: bases must exist as objects before any snapshot or replication
        // read of this factory, and it is kept as its own step so that reading never mixes with
        // mutating (TASKS.md A08: "factory realize 单独作为主机事务，不混入 snapshot 读取").
        // Damaged state ships as canonical replication state; a load request never heals.
        RealizeDarkFogPlanetBases(planet);
    }

    static void RealizeDarkFogPlanetBases(PlanetData planet)
    {
        var spaceSector = GameMain.data.spaceSector;
        var enemyDFHiveSystem = spaceSector.dfHives[planet.star.index];
        while (enemyDFHiveSystem != null)
        {
            for (var i = 0; i < enemyDFHiveSystem.relays.cursor; i++)
            {
                var dfrelayComponent = enemyDFHiveSystem.relays[i];
                if (dfrelayComponent?.targetAstroId == planet.astroId && dfrelayComponent.baseState == 1 && dfrelayComponent.stage == 2)
                {
                    dfrelayComponent.RealizePlanetBase(spaceSector);
                }
            }
            enemyDFHiveSystem = enemyDFHiveSystem.nextSibling;
        }
    }

}
