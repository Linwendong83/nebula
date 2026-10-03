#region

using NebulaAPI;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Factory;
using NebulaWorld;
using NebulaWorld.Factory;

#endregion

namespace NebulaNetwork.PacketProcessors.Factory;

[RegisterPacketProcessor]
public class KillEntityRequestProcessor : PacketProcessor<KillEntityRequest>
{
    protected override void ProcessPacket(KillEntityRequest packet, NebulaConnection conn)
    {
        // A19: in host authority mode an entity-death replay must not run vanilla KillEntityFinally
        // on the receiving peer — the same second-side-effect problem as the enemy kill replay.
        // The replica lifecycle carries entity removal. Legacy rooms run the path below unchanged.
        if (NebulaModel.Authority.AuthorityLocalOptions.Mode == NebulaModel.Authority.AuthorityMode.HostAuthority &&
            NebulaWorld.Authority.HostDeathPolicy.ShouldRefuseLegacyKillReplay(isHostAuthority: true))
        {
            NebulaModel.Logger.Log.Warn("[authority] refusing legacy kill replay: " +
                NebulaWorld.Authority.HostDeathPolicy.RefusalReason("KillEntityRequest"));
            return;
        }
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null) return;

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            Multiplayer.Session.Factories.TargetPlanet = packet.PlanetId;
            Multiplayer.Session.Factories.EventFactory = factory;

            if (!factory.planet.factoryLoaded)
            {
                // If planet is remote planet or not yet loaded, remove planet.physics that added by planetTimer
                var factoryManager = Multiplayer.Session.Factories as FactoryManager;
                if (factoryManager.RemovePlanetTimer(packet.PlanetId))
                {
                    factoryManager.UnloadPlanetData(packet.PlanetId);
                }
            }

            ref var entityPtr = ref factory.entityPool[packet.ObjId];
            if (entityPtr.id == packet.ObjId)
            {
                factory.KillEntityFinally(GameMain.mainPlayer, packet.ObjId, ref CombatStat.empty, packet.SpawnPrebuild);
            }

            Multiplayer.Session.Factories.TargetPlanet = NebulaModAPI.PLANET_NONE;
            Multiplayer.Session.Factories.EventFactory = null;
        }
    }
}
