namespace NebulaModel.Packets.GameStates;

public enum MetadataMessage : byte { Request, Quote, Commit, Result, Query, Cancel, Acknowledge }

public class MetadataOperationPacket
{
    public string WorldId { get; set; }
    public MetadataMessage Message { get; set; }
    public byte[] Transaction { get; set; }
}

public class PropertyHistoryPacket
{
    public long Sequence { get; set; }
    public int[] Consumption { get; set; }
    public bool BanAchievement { get; set; }
}
