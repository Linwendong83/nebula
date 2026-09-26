using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Combat;

[RegisterPacketProcessor]
public class CombatEnemyStateResponseProcessor : PacketProcessor<CombatEnemyStateResponsePacket>
{
    protected override void ProcessPacket(CombatEnemyStateResponsePacket packet, NebulaConnection conn)
    {
        if (IsHost) return;
        Multiplayer.Session.Enemies.ReceiveAuthoritativeState(packet);
    }
}
