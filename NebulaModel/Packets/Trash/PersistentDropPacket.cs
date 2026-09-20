namespace NebulaModel.Packets.Trash;

public class PersistentDropPacket
{
    public string OperationId { get; set; }
    public int Ordinal { get; set; }
    public int TrashId { get; set; }
    public byte[] Data { get; set; }
    public bool Acknowledgement { get; set; }
}
