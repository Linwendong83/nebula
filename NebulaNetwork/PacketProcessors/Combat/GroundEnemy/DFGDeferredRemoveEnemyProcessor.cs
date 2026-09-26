#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.GroundEnemy;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Combat.GroundEnemy;

[RegisterPacketProcessor]
public class DFGDeferredRemoveEnemyProcessor : PacketProcessor<DFGDeferredRemoveEnemyPacket>
{
    protected override void ProcessPacket(DFGDeferredRemoveEnemyPacket packet, NebulaConnection conn)
    {
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null) return;
        Multiplayer.Session.Enemies.RecordAuthoritativeRemoval(packet.PlanetId, packet.EnemyId,
            Multiplayer.Session.Generations.Peek(packet.PlanetId, packet.EnemyId));

        using (Multiplayer.Session.Combat.IsIncomingRequest.On())
        {
            factory.RemoveEnemyFinal(packet.EnemyId);
        }
    }
}
