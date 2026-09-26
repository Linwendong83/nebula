#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.GroundEnemy;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Combat.GroundEnemy;

[RegisterPacketProcessor]
public class DFGKillEnemyProcessor : PacketProcessor<DFGKillEnemyPacket>
{
    protected override void ProcessPacket(DFGKillEnemyPacket packet, NebulaConnection conn)
    {
        if (IsHost || !Multiplayer.Session.Generations.Matches(packet.PlanetId, packet.EnemyId, packet.Generation)) return;
        var factory = GameMain.galaxy.PlanetById(packet.PlanetId)?.factory;
        if (factory == null || packet.EnemyId >= factory.enemyPool.Length) return;

        ref var ptr = ref factory.enemyPool[packet.EnemyId];
        Multiplayer.Session.Enemies.RecordAuthoritativeRemoval(packet.PlanetId, packet.EnemyId, packet.Generation);
        using (Multiplayer.Session.Combat.IsIncomingRequest.On())
        {
            if (ptr.id > 0)
            {
                factory.KillEnemyFinally(packet.EnemyId, ref CombatStat.empty);
            }
            else if (ptr.isInvincible)
            {
                ptr.id = packet.EnemyId;
                ptr.isInvincible = false;
                factory.KillEnemyFinally(packet.EnemyId, ref CombatStat.empty);
            }
        }
    }
}
