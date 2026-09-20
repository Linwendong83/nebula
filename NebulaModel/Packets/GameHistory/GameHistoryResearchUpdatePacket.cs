namespace NebulaModel.Packets.GameHistory;

public class GameHistoryResearchUpdatePacket
{
    public GameHistoryResearchUpdatePacket() { }

    public GameHistoryResearchUpdatePacket(int techId, long hashUploaded, long hashNeeded, int techHashedFor10Frames,
        int techQueueLength) : this(techId, hashUploaded, hashNeeded, techHashedFor10Frames, techQueueLength, false)
    {
    }

    public GameHistoryResearchUpdatePacket(int techId, long hashUploaded, long hashNeeded, int techHashedFor10Frames,
        int techQueueLength, bool hasActiveAutomaticResearch)
    {
        TechId = techId;
        HashUploaded = hashUploaded;
        HashNeeded = hashNeeded;
        TechHashedFor10Frames = techHashedFor10Frames;
        TechQueueLength = (ushort)techQueueLength;
        HasActiveAutomaticResearch = hasActiveAutomaticResearch;
    }

    public int TechId { get; set; }
    public long HashUploaded { get; set; }
    public long HashNeeded { get; set; }
    public int TechHashedFor10Frames { get; set; }
    public ushort TechQueueLength { get; set; }
    public bool HasActiveAutomaticResearch { get; set; }
}
