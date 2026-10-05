using NebulaAPI.Packets;
using NebulaModel.Authority;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat.Mecha;
using NebulaWorld;
using NebulaWorld.Combat;

namespace NebulaNetwork.PacketProcessors.Combat.Mecha;

[RegisterPacketProcessor]
public class PlayerLifeProcessor : PacketProcessor<PlayerLifePacket>
{
    protected override void ProcessPacket(PlayerLifePacket packet, NebulaConnection conn)
    {
        if (packet.Life == null) return;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.Acknowledgement || packet.PlayerSnapshot == null) return;
            var data = (PlayerData)player.Data;
            // A10: in host authority mode a client life snapshot is never adopted nor rebroadcast.
            // A terminal receipt names the received revision so the client stops retrying, but no
            // world, ledger or remote-model state moves. Death/respawn moves via commands in A11.
            if (AuthorityLocalOptions.Mode == AuthorityMode.HostAuthority &&
                HostResourcePolicy.ShouldRefuseLegacyOverwrite(HostResourcePacketKind.LifeSnapshot, isHostAuthority: true))
            {
                if (Multiplayer.Session.Life.RecordSnapshotRefusal(player.Id))
                    Log.Warn("[authority] client life snapshot refused; acknowledging receipt without adopting its state");
                conn.SendPacket(PlayerLifePacket.CreateReceipt(player.Id, packet.Life.Revision));
                return;
            }
            packet.PlayerId = player.Id;
            if (packet.Life.Revision > data.Life.Revision && packet.Life.DeathCount >= data.Life.DeathCount)
            {
                PlayerLifeManager.StoreServer(data, packet.PlayerSnapshot);
                packet.Life = data.Life;
                packet.PlayerSnapshot = System.Array.Empty<byte>();
                Server.SendPacketExclude(packet, conn);
                Multiplayer.Session.Life.ApplyRemote(player.Id, data.Life);
            }
            conn.SendPacket(new PlayerLifePacket { PlayerId = player.Id, Life = data.Life, Acknowledgement = true });
        }
        else if (packet.Acknowledgement) Multiplayer.Session.Life.Acknowledge(packet.Life.Revision);
        else Multiplayer.Session.Life.ApplyRemote(packet.PlayerId, packet.Life);
    }
}
