using NebulaAPI.Packets;
using NebulaModel.DataStructures;
using NebulaModel.Logger;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.Combat;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.Combat;

/// <summary>Receives the host's visual-only samples. Clients cannot publish world effects.</summary>
[RegisterPacketProcessor]
public class BattleVisualProcessor : PacketProcessor<BattleVisualPacket>
{
    protected override void ProcessPacket(BattleVisualPacket packet, NebulaConnection conn)
    {
        if (IsHost || !Multiplayer.Session.IsGameLoaded || !packet.WorldEffects || packet.Owner != 0) return;
        if (packet.StarId <= 0 || packet.Tick < 0 || packet.Sequence <= 0) return;
        try
        {
            var frame = BattleVisualFrame.Import(packet.Data);
            // Fleet pool membership is replicated by the authority adapters, not by visual packets.
            if (frame.Units.Count != 0 || packet.UnitsFull || packet.PlanetId != 0) return;
            Multiplayer.Session.BattleVisuals.Receive(packet, frame);
        }
        catch (System.Exception error)
        {
            Log.Warn("Invalid host battle visual frame: " + error.Message);
        }
    }
}
