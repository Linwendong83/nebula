using NebulaModel.DataStructures;

namespace NebulaModel.Packets.Combat.Mecha;

public class PlayerLifePacket
{
    public ushort PlayerId { get; set; }
    public PlayerLifeData Life { get; set; }
    public byte[] PlayerSnapshot { get; set; }
    public bool Acknowledgement { get; set; }
}
