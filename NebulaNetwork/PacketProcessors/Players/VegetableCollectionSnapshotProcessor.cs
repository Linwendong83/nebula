using NebulaAPI.Networking;
using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;
using NebulaWorld;
using NebulaModel.Logger;

namespace NebulaNetwork.PacketProcessors.Players;

[RegisterPacketProcessor]
internal sealed class VegetableCollectionSnapshotProcessor : PacketProcessor<VegetableCollectionSnapshotPacket>
{
    protected override void ProcessPacket(VegetableCollectionSnapshotPacket packet, NebulaConnection conn)
    {
        if (packet.Data == null || packet.Data.Length > VegetableCollectionState.MaxBytes) return;
        if (!IsHost)
        {
            if (!packet.IsAuthoritative) return;
            Multiplayer.Session.Vegetation.RestoreLocal(packet.Data);
            ((PlayerData)Multiplayer.Session.LocalPlayer.Data).VegetableCollectionData = packet.Data;
            return;
        }
        if (packet.IsAuthoritative) return;
        var player = Players.Get(conn, EConnectionStatus.Connected);
        if (player == null) return;
        var canonical = Multiplayer.Session.Vegetation.VerifySnapshot(player.Id, packet.Data);
        if (canonical == null) return;
        Log.Warn($"Vegetation collection mismatch for player {player.Id}; restoring server state.");
        conn.SendPacket(new VegetableCollectionSnapshotPacket(canonical, true));
    }
}
