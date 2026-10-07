using NebulaAPI.Packets;
using NebulaModel.Authority;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Authority;
using NebulaWorld;
using NebulaWorld.Authority;

namespace NebulaNetwork.PacketProcessors.Authority;

[RegisterPacketProcessor]
public sealed class PlayerCombatStateProcessor : PacketProcessor<AuthorityPlayerCombatStatePacket>
{
    protected override void ProcessPacket(AuthorityPlayerCombatStatePacket packet, NebulaConnection conn)
    {
        if (IsHost || !AuthorityPacketAdmission.Admit(packet, AuthorityDirection.ServerToClient, conn, 0, out _)) return;
        Multiplayer.Session.AuthorityRuntime.TryEnqueueReplicaMessage(packet,
            new ApplyScope(new ScopeKey(PoolKind.PlayerCombat, 0), hostTick: packet.HostTick));
    }
}
