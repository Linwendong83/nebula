using NebulaModel.DataStructures;

namespace NebulaModel.Packets.Combat.Mecha;

public class PlayerLifePacket
{
    public long CombatRevision { get; set; }
    public double CoreEnergyDebitAcknowledged { get; set; }
    public long LastCombatCommand { get; set; }
    public int[] DebitItemsAcknowledged { get; set; } = System.Array.Empty<int>();
    public int[] DebitTotalsAcknowledged { get; set; } = System.Array.Empty<int>();
    public ushort PlayerId { get; set; }
    public PlayerLifeData Life { get; set; }
    public byte[] PlayerSnapshot { get; set; }
    public bool Acknowledgement { get; set; }

    /// <summary>A terminal receipt for a snapshot, without adopting its life or inventory facts.</summary>
    public static PlayerLifePacket CreateReceipt(ushort playerId, long receivedRevision) => new()
    {
        PlayerId = playerId,
        Life = new PlayerLifeData { Revision = receivedRevision },
        Acknowledgement = true
    };
}
