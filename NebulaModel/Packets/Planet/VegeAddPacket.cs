namespace NebulaModel.Packets.Planet;

public class VegeAddPacket
{
    public VegeAddPacket() { }

    public VegeAddPacket(int planetId, bool isVein, byte[] data, bool isPlanting = false)
    {
        PlanetId = planetId;
        IsVein = isVein;
        Data = data;
        IsPlanting = isPlanting;
    }

    public int PlanetId { get; set; }
    public bool IsVein { get; set; }
    public byte[] Data { get; set; }
    public bool IsPlanting { get; set; }
}
