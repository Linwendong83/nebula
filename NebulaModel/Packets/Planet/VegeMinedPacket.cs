namespace NebulaModel.Packets.Planet;

public class VegeMinedPacket
{
    public VegeMinedPacket() { }

    public VegeMinedPacket(int planetId, int vegeId, int amount, bool isVein, bool isCollected = false)
    {
        PlanetId = planetId;
        VegeId = vegeId;
        Amount = amount;
        IsVein = isVein;
        IsCollected = isCollected;
    }

    public int PlanetId { get; set; }
    public int VegeId { get; set; }
    public int Amount { get; set; } // the current amount, if 0 remove vege
    public bool IsVein { get; set; }
    public bool IsCollected { get; set; }
}
