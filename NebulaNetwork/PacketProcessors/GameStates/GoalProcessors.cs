using NebulaAPI.Packets;
using NebulaModel.Networking;
using NebulaModel.Packets;
using NebulaModel.Packets.GameStates;
using NebulaWorld;

namespace NebulaNetwork.PacketProcessors.GameStates;

[RegisterPacketProcessor]
public class GoalSnapshotProcessor : PacketProcessor<GoalSnapshotPacket>
{
    protected override void ProcessPacket(GoalSnapshotPacket packet, NebulaConnection conn)
    {
        if (IsClient && Multiplayer.Session.IsGameLoaded) Multiplayer.Session.Goals.Receive(packet);
    }
}

[RegisterPacketProcessor]
public class GoalCommandProcessor : PacketProcessor<GoalCommandPacket>
{
    protected override void ProcessPacket(GoalCommandPacket packet, NebulaConnection conn)
    {
        if (IsHost && Players.Get(conn) != null) Multiplayer.Session.Goals.Command(packet);
    }
}

[RegisterPacketProcessor]
public class GoalObservationProcessor : PacketProcessor<GoalObservationPacket>
{
    protected override void ProcessPacket(GoalObservationPacket packet, NebulaConnection conn)
    {
        if (!IsHost) return;
        var player = Players.Get(conn);
        if (player != null) Multiplayer.Session.Goals.Observe(player.Id, packet);
    }
}
