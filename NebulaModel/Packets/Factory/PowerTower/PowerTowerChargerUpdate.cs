namespace NebulaModel.Packets.Factory.PowerTower;

public class PowerTowerChargerUpdate
{
    public PowerTowerChargerUpdate() { }

    public PowerTowerChargerUpdate(ushort playerId, int planetId, int[] nodeIds)
    {
        PlayerId = playerId;
        PlanetId = planetId;
        NodeIds = nodeIds;
    }

    public ushort PlayerId { get; set; }
    public int PlanetId { get; set; }
    public int[] NodeIds { get; set; } = [];
}
