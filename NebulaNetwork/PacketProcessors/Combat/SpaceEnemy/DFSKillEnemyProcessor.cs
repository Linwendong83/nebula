#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.SpaceEnemy;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Combat.SpaceEnemy;

[RegisterPacketProcessor]
public class DFSKillEnemyProcessor : PacketProcessor<DFSKillEnemyPacket>
{
    protected override void ProcessPacket(DFSKillEnemyPacket packet, NebulaConnection conn)
    {
        if (IsHost || !Multiplayer.Session.Generations.Matches(0, packet.EnemyId, packet.Generation)) return;
        var spaceSector = GameMain.spaceSector;
        var hive = GameMain.spaceSector.GetHiveByAstroId(packet.OriginAstroId);
        if (hive == null || packet.EnemyId < 0 || packet.EnemyId >= spaceSector.enemyCursor) return;

        ref var ptr = ref spaceSector.enemyPool[packet.EnemyId];
        Multiplayer.Session.Enemies.RecordAuthoritativeRemoval(0, packet.EnemyId, packet.Generation);
        using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
        {
            if (ptr.id > 0)
            {
                spaceSector.KillEnemyFinal(packet.EnemyId, ref CombatStat.empty);
            }
            else if (ptr.isInvincible)
            {
                ptr.id = packet.EnemyId;
                ptr.isInvincible = false;
                spaceSector.KillEnemyFinal(packet.EnemyId, ref CombatStat.empty);
            }
        }
    }
}
