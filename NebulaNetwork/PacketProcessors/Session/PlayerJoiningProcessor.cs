#region

using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Session;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Session;

[RegisterPacketProcessor]
public class PlayerJoiningProcessor : PacketProcessor<PlayerJoining>
{
    protected override void ProcessPacket(PlayerJoining packet, NebulaConnection conn)
    {
        Multiplayer.Session.NumPlayers = packet.NumPlayers;
        // While this client is still loading, the completion snapshot spawns the same model.
        // Announcing here as well would show that player joining twice.
        Multiplayer.Session.World.SpawnRemotePlayerModel(packet.PlayerData, Multiplayer.Session.IsGameLoaded);
        Multiplayer.Session.World.OnPlayerJoining(packet.PlayerData.Username);
    }
}
