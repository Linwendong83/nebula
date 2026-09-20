namespace NebulaModel.Packets.Combat;

public class BattleVisualPacket
{
    public ushort Owner { get; set; }
    public bool WorldEffects { get; set; }
    public int StarId { get; set; }
    public int PlanetId { get; set; }
    public long Sequence { get; set; }
    public long Tick { get; set; }
    public bool UnitsFull { get; set; }
    public bool EffectsFull { get; set; }
    public bool Clear { get; set; }
    public byte[] Data { get; set; }
}
