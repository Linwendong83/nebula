#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.SpaceEnemy;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Combat.SpaceEnemy;

[RegisterPacketProcessor]
public class DFSRemoveEnemyDeferredProcessor : PacketProcessor<DFSRemoveEnemyDeferredPacket>
{
    protected override void ProcessPacket(DFSRemoveEnemyDeferredPacket packet, NebulaConnection conn)
    {
        var spaceSector = GameMain.spaceSector;
        if (packet.EnemyId <= 0 || packet.EnemyId >= spaceSector.enemyCursor) return;
        Multiplayer.Session.Enemies.RecordAuthoritativeRemoval(0, packet.EnemyId,
            Multiplayer.Session.Generations.Peek(0, packet.EnemyId));

        using (Multiplayer.Session.Enemies.IsIncomingRequest.On())
        {
            spaceSector.RemoveEnemyFinal(packet.EnemyId);
        }
    }
}
