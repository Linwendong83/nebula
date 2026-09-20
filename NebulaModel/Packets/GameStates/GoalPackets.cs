namespace NebulaModel.Packets.GameStates;

public class GoalSnapshotPacket
{
    public long Version { get; set; }
    public byte[] Data { get; set; }
}

public class GoalCommandPacket
{
    public bool ChangeLevel { get; set; }
    public int Value { get; set; }
}

public class GoalObservationPacket
{
    public int GoalId { get; set; }
    public long Sequence { get; set; }
    public long Value { get; set; }
    public long Target { get; set; }
    public bool Complete { get; set; }
}
