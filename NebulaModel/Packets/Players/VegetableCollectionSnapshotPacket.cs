using System;

namespace NebulaModel.Packets.Players;

public sealed class VegetableCollectionSnapshotPacket
{
    public VegetableCollectionSnapshotPacket() { }
    public VegetableCollectionSnapshotPacket(byte[] data, bool isAuthoritative = false)
    {
        Data = data;
        IsAuthoritative = isAuthoritative;
    }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public bool IsAuthoritative { get; set; }
}
