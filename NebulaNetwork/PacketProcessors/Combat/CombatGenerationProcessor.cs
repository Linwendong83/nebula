using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Combat;

[RegisterPacketProcessor]
public class CombatGenerationProcessor : PacketProcessor<CombatGenerationPacket>
{
    protected override void ProcessPacket(CombatGenerationPacket packet, NebulaConnection conn)
    {
        if (IsClient) Multiplayer.Session.Generations.Import(packet.Data);
    }
}
