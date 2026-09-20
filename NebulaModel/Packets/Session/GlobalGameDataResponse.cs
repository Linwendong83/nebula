namespace NebulaModel.Packets.Session;

public class GlobalGameDataResponse
{
    public GlobalGameDataResponse() { }

    public GlobalGameDataResponse(EDataType dataType, byte[] binaryData)
    {
        DataType = dataType;
        BinaryData = binaryData;
    }

    public enum EDataType : byte
    {
        History = 1,
        GalacticTransport,
        SpaceSector,
        MilestoneSystem,
        TrashSystem,
        GalacticDigital,
        Ready,
        Session,
        Goals,
        KillStatistics
    }

    public EDataType DataType { get; set; }
    public byte[] BinaryData { get; set; }
}
