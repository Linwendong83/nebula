namespace NebulaModel.Packets.Statistics;

public class KillStatisticsPacket
{
    public bool Snapshot { get; set; }
    public long FromTick { get; set; }
    public long ToTick { get; set; }
    public byte[] Data { get; set; }
}

public class KillStatisticsRequest
{
    public bool Subscribe { get; set; }
}
