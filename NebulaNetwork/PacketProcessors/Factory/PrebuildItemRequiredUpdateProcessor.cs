#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Factory;
using NebulaModel.Packets.Players;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Factory;

[RegisterPacketProcessor]
public class PrebuildItemRequiredUpdateProcessor : PacketProcessor<PrebuildItemRequiredUpdate>
{
    protected override void ProcessPacket(PrebuildItemRequiredUpdate packet, NebulaConnection conn)
    {
        // A18: in host authority mode a client-asserted material placement never rewrites the host
        // prebuild. Materials travel as host ledger spends attached to the finishing build task;
        // applying the old claim here would let any client green any site. Legacy rooms run the
        // path below unchanged.
        if (NebulaModel.Authority.AuthorityLocalOptions.Mode == NebulaModel.Authority.AuthorityMode.HostAuthority &&
            NebulaWorld.Authority.HostConstructionPolicy.ShouldRefuseClientMaterialClaim(isHostAuthority: true))
        {
            NebulaModel.Logger.Log.Warn("[authority] refusing client material claim in host authority mode: " +
                NebulaWorld.Authority.HostConstructionPolicy.BuildSuppressionReason("PrebuildItemRequired"));
            return;
        }
        var planet = GameMain.galaxy.PlanetById(packet.PlanetId);
        if (planet.factory == null || packet.PrebuildId >= planet.factory.prebuildCursor)
        {
            return;
        }
        var factory = planet.factory;

        ref var ptr = ref factory.prebuildPool[packet.PrebuildId];
        if (ptr.id != packet.PrebuildId || ptr.itemRequired == 0)
        {
            //Prebuild not exist or the prebuild has satisfied the item requirement (green)
            if (IsHost)
            {
                //Refund the item back to the player
                var itemId = ptr.protoId;
                conn.SendPacket(new PlayerGiveItemPacket(itemId, packet.ItemCount, 0));
            }
            return;
        }

        using (Multiplayer.Session.Factories.IsIncomingRequest.On())
        {
            //From ConstructionModuleComponent.PlaceItems
            ptr.itemRequired = 0;
            factory.NotifyPrebuildChange(ptr.id, 3);
            if (factory.planet.factoryLoaded || factory.planet.factingCompletedStage >= 3)
            {
                factory.AlterPrebuildModelState(ptr.id);
            }
            factory.constructionSystem.AddBuildTargetToModules(ptr.id, ref ptr.pos);
        }
    }
}
