#region

using NebulaAPI.GameState;
using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Players;
using NebulaWorld;

#endregion

namespace NebulaNetwork.PacketProcessors.Players;

[RegisterPacketProcessor]
public class PlayerMovementProcessor : PacketProcessor<PlayerMovement>
{
    protected override void ProcessPacket(PlayerMovement packet, NebulaConnection conn)
    {
        var valid = true;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                packet.PlayerId = player.Id;
                var changedPlanet = player.Data.LocalPlanetId != packet.LocalPlanetId;
                if (changedPlanet) Multiplayer.Session.BattleVisuals.LeavePlanet(player.Id, player.Data.LocalPlanetId);
                player.Data.LocalPlanetId = packet.LocalPlanetId;
                player.Data.UPosition = packet.UPosition;
                player.Data.Rotation = packet.Rotation;
                player.Data.BodyRotation = packet.BodyRotation;
                player.Data.LocalPlanetPosition = packet.LocalPlanetPosition;
                if (changedPlanet) Multiplayer.Session.BattleVisuals.SendInitial(conn, player.Data.LocalStarId, packet.LocalPlanetId);

                Server.SendPacketExclude(packet, conn);
            }
            else
            {
                valid = false;
            }
        }

        if (valid)
        {
            Multiplayer.Session.World.UpdateRemotePlayerRealtimeState(packet);
        }
    }
}
