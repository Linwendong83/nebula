using System;

namespace NebulaModel.Packets.Combat;

public class CombatEnemyStateResponsePacket
{
    public int AstroId { get; set; }
    public int EnemyId { get; set; }
    public long ExpectedGeneration { get; set; }
    public long RequestId { get; set; }
    public long Generation { get; set; }
    public bool Alive { get; set; }
    public bool HasCombatStat { get; set; }
    public int OriginAstroId { get; set; }
    public short ProtoId { get; set; }
    public short ModelIndex { get; set; }
    public short Owner { get; set; }
    public short Port { get; set; }
    public bool Dynamic { get; set; }
    public int Hp { get; set; }
    public int HpMax { get; set; }
    public int HpRecover { get; set; }
    public int HpIncoming { get; set; }
    public byte[] Snapshot { get; set; } = Array.Empty<byte>();
}
