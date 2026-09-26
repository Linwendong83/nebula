namespace NebulaModel.Packets.Combat;

public class CombatEnemyStateRequestPacket
{
    public int AstroId { get; set; }
    public int EnemyId { get; set; }
    public long ExpectedGeneration { get; set; }
    public long RequestId { get; set; }
    public bool IncludeSnapshot { get; set; }
}
