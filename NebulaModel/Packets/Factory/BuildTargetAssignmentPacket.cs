using NebulaModel.DataStructures;

namespace NebulaModel.Packets.Factory;

public class BuildTargetAssignmentPacket
{
    public int PlanetId { get; set; }
    public int PrebuildId { get; set; }
    public long Generation { get; set; }
    public BuildOwnerKind OwnerKind { get; set; }
    public int OwnerId { get; set; }
    public bool Launched { get; set; }
    public bool Removed { get; set; }
    public bool Canceled { get; set; }

    public BuildTargetAssignmentPacket() { }

    public BuildTargetAssignmentPacket(BuildTargetClaim claim, bool removed = false, bool canceled = false)
    {
        PlanetId = claim.PlanetId;
        PrebuildId = claim.PrebuildId;
        Generation = claim.Generation;
        OwnerKind = claim.OwnerKind;
        OwnerId = claim.OwnerId;
        Launched = claim.Launched;
        Removed = removed;
        Canceled = canceled;
    }

    public BuildTargetClaim ToClaim() =>
        new(PlanetId, PrebuildId, Generation, OwnerKind, OwnerId, Launched);
}

public class BuildTargetAssignmentReplyPacket
{
    public int PlanetId { get; set; }
    public int PrebuildId { get; set; }
    public long Generation { get; set; }
    public bool Accepted { get; set; }

    public BuildTargetAssignmentReplyPacket() { }

    public BuildTargetAssignmentReplyPacket(int planetId, int prebuildId, long generation, bool accepted)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        Generation = generation;
        Accepted = accepted;
    }
}

public class BuildTargetReadyPacket
{
    public int PlanetId { get; set; }
    public int PrebuildId { get; set; }

    public BuildTargetReadyPacket() { }

    public BuildTargetReadyPacket(int planetId, int prebuildId)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
    }
}

public class BuildTargetBaseReleaseAckPacket
{
    public int PlanetId { get; set; }
    public int PrebuildId { get; set; }
    public long Generation { get; set; }

    public BuildTargetBaseReleaseAckPacket() { }

    public BuildTargetBaseReleaseAckPacket(int planetId, int prebuildId, long generation)
    {
        PlanetId = planetId;
        PrebuildId = prebuildId;
        Generation = generation;
    }
}

public class BuildDroneLaunchPacket
{
    public ushort PlayerId { get; set; }
    public int PlanetId { get; set; }
    public int TargetObjectId { get; set; }
    public int Next1ObjectId { get; set; }
    public int Next2ObjectId { get; set; }
    public int Next3ObjectId { get; set; }
    public long TargetGeneration { get; set; }
    public long Next1Generation { get; set; }
    public long Next2Generation { get; set; }
    public long Next3Generation { get; set; }
    public int DronePriority { get; set; }

    public BuildDroneLaunchPacket() { }

    public BuildDroneLaunchPacket(ushort playerId, int planetId, int targetObjectId, int next1ObjectId,
        int next2ObjectId, int next3ObjectId, long targetGeneration, long next1Generation,
        long next2Generation, long next3Generation, int dronePriority)
    {
        PlayerId = playerId;
        PlanetId = planetId;
        TargetObjectId = targetObjectId;
        Next1ObjectId = next1ObjectId;
        Next2ObjectId = next2ObjectId;
        Next3ObjectId = next3ObjectId;
        TargetGeneration = targetGeneration;
        Next1Generation = next1Generation;
        Next2Generation = next2Generation;
        Next3Generation = next3Generation;
        DronePriority = dronePriority;
    }
}
