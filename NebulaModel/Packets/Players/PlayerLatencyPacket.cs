namespace NebulaModel.Packets.Players;

/// <summary>Small RTT probe and host-owned latency snapshot for the player overlay.</summary>
public class PlayerLatencyPacket
{
    public PlayerLatencyPacket() { }

    public byte Kind { get; set; } // 0 probe, 1 reply, 2 snapshot
    public ushort PlayerId { get; set; }
    public long SentTicks { get; set; }
    public ushort[] PlayerIds { get; set; }
    public int[] Milliseconds { get; set; }
}
