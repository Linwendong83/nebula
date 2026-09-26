namespace NebulaModel.Packets.Factory.RayReceiver;

public class RayReceiverChangeLensPacket
{
    public RayReceiverChangeLensPacket() { }

    public RayReceiverChangeLensPacket(int generatorId, PowerGeneratorComponent generator, int planetId)
    {
        GeneratorId = generatorId;
        CatalystId = generator.catalystId;
        CurrentCatalystId = generator.curCatalystId;
        CatalystCount = generator.catalystCount;
        CatalystInc = generator.catalystInc;
        CatalystMask = generator.catalystMask;
        CatalystIncLevel = generator.catalystIncLevel;
        LensCount = generator.catalystPoint;
        LensInc = generator.catalystIncPoint;
        PlanetId = planetId;
    }

    public int GeneratorId { get; set; }
    public int LensCount { get; set; }
    public int LensInc { get; set; }
    public int CatalystId { get; set; }
    public int CurrentCatalystId { get; set; }
    public short CatalystCount { get; set; }
    public short CatalystInc { get; set; }
    public short CatalystMask { get; set; }
    public byte CatalystIncLevel { get; set; }
    public int PlanetId { get; set; }
}
