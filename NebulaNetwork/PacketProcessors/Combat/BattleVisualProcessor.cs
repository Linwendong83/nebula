using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Combat;

[RegisterPacketProcessor]
public class BattleVisualProcessor : PacketProcessor<BattleVisualPacket>
{
    protected override void ProcessPacket(BattleVisualPacket packet, NebulaConnection conn)
    {
        if (!Multiplayer.Session.IsGameLoaded || packet.Sequence <= 0 || packet.StarId <= 0) return;
        var frame = BattleVisualFrame.Import(packet.Data);
        if (IsHost)
        {
            var player = Players.Get(conn);
            if (player == null || packet.WorldEffects || packet.StarId != player.Data.LocalStarId ||
                packet.PlanetId != 0 && packet.PlanetId != player.Data.LocalPlanetId) return;
            packet.Owner = player.Id;
            if (frame.Effects.Exists(x => x.Kind >= BattleEffectKind.TurretMissile)) return;
            Multiplayer.Session.BattleVisuals.Relay(packet, frame);
        }
        else Multiplayer.Session.BattleVisuals.Receive(packet, frame);
    }
}
