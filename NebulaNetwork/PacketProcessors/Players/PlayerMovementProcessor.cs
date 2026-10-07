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
        if (!packet.HasFinitePose() || packet.LocalPlanetId > 0 && GameMain.galaxy.PlanetById(packet.LocalPlanetId) == null) return;
        var valid = true;
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player != null)
            {
                packet.PlayerId = player.Id;
                var changedPlanet = player.Data.LocalPlanetId != packet.LocalPlanetId;
                var data = (NebulaModel.DataStructures.PlayerData)player.Data;
                var elapsed = (GameMain.gameTick - data.AcceptedMovementTick) / 60.0;
                if (!changedPlanet && data.AcceptedMovementTick > 0 && elapsed > 0 && elapsed <= 1)
                {
                    data.AcceptedVelocityU = new NebulaAPI.DataStructures.Double3(
                        (packet.UPosition.x - data.UPosition.x) / elapsed, (packet.UPosition.y - data.UPosition.y) / elapsed, (packet.UPosition.z - data.UPosition.z) / elapsed);
                    data.AcceptedVelocityL = new NebulaAPI.DataStructures.Float3(
                        (float)((packet.LocalPlanetPosition.x - data.LocalPlanetPosition.x) / elapsed),
                        (float)((packet.LocalPlanetPosition.y - data.LocalPlanetPosition.y) / elapsed),
                        (float)((packet.LocalPlanetPosition.z - data.LocalPlanetPosition.z) / elapsed));
                }
                else { data.AcceptedVelocityU = default; data.AcceptedVelocityL = default; }
                data.AcceptedMovementTick = GameMain.gameTick; data.AcceptedMovementState = packet.MovementState;
                data.AcceptedWarping = (packet.Flags & PlayerMovement.EFlags.warping) != 0;
                packet.BuildArea = UnityEngine.Mathf.Clamp(packet.BuildArea, 0, GameMain.mainPlayer.mecha.buildArea);
                packet.ConstructionDroneCount = System.Math.Max(0, System.Math.Min(packet.ConstructionDroneCount, GameMain.mainPlayer.mecha.constructionModule.droneCount));
                if (changedPlanet) Multiplayer.Session.BattleVisuals.LeavePlanet(player.Id, player.Data.LocalPlanetId);
                player.Data.LocalPlanetId = packet.LocalPlanetId;
                player.Data.UPosition = packet.UPosition;
                player.Data.Rotation = packet.Rotation;
                player.Data.BodyRotation = packet.BodyRotation;
                player.Data.LocalPlanetPosition = packet.LocalPlanetPosition;
                Multiplayer.Session.BuildDispatch.UpdateRemoteBuilder(player.Id, packet.BuildArea,
                    packet.ConstructionDroneCount, packet.ConstructionDronesEnabled,
                    packet.CanLaunchConstructionDrone);
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
