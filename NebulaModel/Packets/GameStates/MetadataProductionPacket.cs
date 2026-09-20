namespace NebulaModel.Packets.GameStates;

public class MetadataProductionPacket
{
    public string WorldId { get; set; }
    public long ClusterKey { get; set; }
    public long Sequence { get; set; }
    public int[] Earned { get; set; }
    public int[] WorldPeak { get; set; }
}

public class MetadataReceiptPacket
{
    public string WorldId { get; set; }
    public long Sequence { get; set; }
}
