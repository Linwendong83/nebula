#region

using System;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// Which space component an enemy carries (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// The values are explicit wire discriminants, not reflection over field names. The old snapshot
/// described components as <c>(enemyField, poolField)</c> string pairs
/// (<c>EnemyManager.Snapshot.cs: spaceComponents</c>); that table is the source of the member list
/// below, but the wire never carries a field name — only this enum.
/// </para>
/// <para>
/// Priority when several component ids are non-zero is fixed in
/// <c>SpaceEnemyKinds.FromComponentIds</c>: relay and tinder (dynamic) first, then hive core,
/// unit, builder, turret, gamma, node, connector, replicator. The host and the tests share that
/// function, so the kind is deterministic. In vanilla a space enemy carries at most one of these,
/// so the priority only decides the reading when the pool is briefly inconsistent.
/// </para>
/// </remarks>
public enum SpaceEnemyKind : byte
{
    Unknown = 0,
    SpaceUnit = 1,
    SpaceBuilder = 2,
    SpaceCore = 3,
    SpaceNode = 4,
    SpaceConnector = 5,
    SpaceReplicator = 6,
    SpaceGamma = 7,
    SpaceTurret = 8,
    SpaceRelay = 9,
    SpaceTinder = 10
}

/// <summary>Derives a wire kind from vanilla space component ids without reflection.</summary>
public static class SpaceEnemyKinds
{
    public static SpaceEnemyKind FromComponentIds(int dfSCoreId, int dfSNodeId, int dfSConnectorId,
        int dfSReplicatorId, int dfSGammaId, int dfSTurretId, int dfTinderId, int dfRelayId,
        int unitId, int builderId)
    {
        if (dfRelayId > 0) return SpaceEnemyKind.SpaceRelay;
        if (dfTinderId > 0) return SpaceEnemyKind.SpaceTinder;
        if (dfSCoreId > 0) return SpaceEnemyKind.SpaceCore;
        if (unitId > 0) return SpaceEnemyKind.SpaceUnit;
        if (builderId > 0) return SpaceEnemyKind.SpaceBuilder;
        if (dfSTurretId > 0) return SpaceEnemyKind.SpaceTurret;
        if (dfSGammaId > 0) return SpaceEnemyKind.SpaceGamma;
        if (dfSNodeId > 0) return SpaceEnemyKind.SpaceNode;
        if (dfSConnectorId > 0) return SpaceEnemyKind.SpaceConnector;
        if (dfSReplicatorId > 0) return SpaceEnemyKind.SpaceReplicator;
        return SpaceEnemyKind.Unknown;
    }

    public static bool IsDefined(SpaceEnemyKind kind) => kind >= SpaceEnemyKind.SpaceUnit &&
        kind <= SpaceEnemyKind.SpaceTinder;
}

/// <summary>
/// One space enemy's canonical state, as the host publishes it (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.SpaceEnemy"/> in sector scope 0)
/// travels in the lifecycle/state framing; this struct is the opaque state blob inside it. Pose and
/// hitpoints are absolute — never deltas — and component internals (hatred, cooldowns, hive evolve,
/// relay dock state) deliberately do not travel: the client shows Spawn/Pose/State/Despawn and never
/// runs hive manufacture, AI, hatred or attack. Those pools stay host-only, which is also why a
/// client that never allocates logic components is naturally AI-inert.
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this enemy has no damage
/// record), not an absence — the same reading DESIGN 9.1 gives the other combat states. The hive
/// linkage (<c>OriginAstroId</c>) names the hive astro the enemy belongs to; the current
/// <c>AstroId</c> is where it is now. A relay that lands and becomes a ground base is a different
/// pool (GroundEnemy) under a new key — the space key despawns and the ground key spawns — so no
/// binding ever reinterprets one pool's slot as another's. Cross-astro migration inside the sector
/// keeps the same key: the sector scope is 0 and the astro ids are attributes, not identity
/// (DESIGN 4.1).
/// </para>
/// <para>
/// Position is double-precision (sector coordinates reach 1e9); rotation/velocity stay float like
/// vanilla. The layout is fixed and field-by-field: version, kind, flags, discriminants, linkage,
/// pose, hitpoints. No reflection over names.
/// </para>
/// </remarks>
public readonly struct SpaceEnemyState
{
    public SpaceEnemyState(SpaceEnemyKind kind, bool hasCombatStat, bool isDynamic,
        short protoId, short modelIndex, short owner, short port, byte stateFlags,
        int astroId, int originAstroId, int dockIndex, int builderIndex, int level,
        double posX, double posY, double posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming,
        float animationTime = 0, float prepareLength = 0, float workingLength = 0,
        uint animationState = 0, float animationPower = 0)
    {
        Kind = kind;
        HasCombatStat = hasCombatStat;
        IsDynamic = isDynamic;
        ProtoId = protoId;
        ModelIndex = modelIndex;
        Owner = owner;
        Port = port;
        StateFlags = stateFlags;
        AstroId = astroId;
        OriginAstroId = originAstroId;
        DockIndex = dockIndex;
        BuilderIndex = builderIndex;
        Level = level;
        PosX = posX;
        PosY = posY;
        PosZ = posZ;
        RotX = rotX;
        RotY = rotY;
        RotZ = rotZ;
        RotW = rotW;
        VelX = velX;
        VelY = velY;
        VelZ = velZ;
        Hp = hp;
        HpMax = hpMax;
        HpRecover = hpRecover;
        HpIncoming = hpIncoming;
        AnimationTime = animationTime;
        PrepareLength = prepareLength;
        WorkingLength = workingLength;
        AnimationState = animationState;
        AnimationPower = animationPower;
    }

    public SpaceEnemyKind Kind { get; }
    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Owner { get; }
    public short Port { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int OriginAstroId { get; }
    public int DockIndex { get; }
    public int BuilderIndex { get; }
    public int Level { get; }
    public double PosX { get; }
    public double PosY { get; }
    public double PosZ { get; }
    public float RotX { get; }
    public float RotY { get; }
    public float RotZ { get; }
    public float RotW { get; }
    public float VelX { get; }
    public float VelY { get; }
    public float VelZ { get; }
    public int Hp { get; }
    public int HpMax { get; }
    public int HpRecover { get; }
    public int HpIncoming { get; }
    public float AnimationTime { get; }
    public float PrepareLength { get; }
    public float WorkingLength { get; }
    public uint AnimationState { get; }
    public float AnimationPower { get; }
}

/// <summary>Encoding of <see cref="SpaceEnemyState"/> into one bounded state blob.</summary>
public static class SpaceEnemyStateCodec
{
    public const byte Version = 2;

    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;

    /// <summary>Fixed wire size: header, discriminants, linkage, pose, HP and display animation.</summary>
    public const int FixedSize = 1 + 1 + 1 + 1 + 8 + 20 + 52 + 16 + 20;

    public static bool TryEncode(in SpaceEnemyState state, out byte[] data)
    {
        data = null;
        if (!SpaceEnemyKinds.IsDefined(state.Kind)) return false;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) | (state.IsDynamic ? IsDynamicFlag : 0));
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(Version);
        writer.WriteByte((byte)state.Kind);
        writer.WriteByte(flags);
        writer.WriteByte(state.StateFlags);
        writer.WriteUShort(unchecked((ushort)state.ProtoId));
        writer.WriteUShort(unchecked((ushort)state.ModelIndex));
        writer.WriteUShort(unchecked((ushort)state.Owner));
        writer.WriteUShort(unchecked((ushort)state.Port));
        writer.WriteInt(state.AstroId);
        writer.WriteInt(state.OriginAstroId);
        writer.WriteInt(state.DockIndex);
        writer.WriteInt(state.BuilderIndex);
        writer.WriteInt(state.Level);
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosX));
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosY));
        writer.WriteLong(BitConverter.DoubleToInt64Bits(state.PosZ));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.RotX), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.RotY), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.RotZ), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.RotW), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.VelX), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.VelY), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.VelZ), 0));
        writer.WriteInt(state.Hp);
        writer.WriteInt(state.HpMax);
        writer.WriteInt(state.HpRecover);
        writer.WriteInt(state.HpIncoming);
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.AnimationTime), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.PrepareLength), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.WorkingLength), 0));
        writer.WriteInt(unchecked((int)state.AnimationState));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.AnimationPower), 0));
        data = writer.ToArray();
        return data.Length == FixedSize && data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    public static bool TryDecode(byte[] source, int offset, int length, out SpaceEnemyState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var version) || (version != 1 && version != Version))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "space enemy version=" + version);
            return false;
        }
        if (!reader.TryReadByte(out var kindRaw) || !SpaceEnemyKinds.IsDefined((SpaceEnemyKind)kindRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "space enemy kind=" + kindRaw);
            return false;
        }
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing space enemy flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "space enemy flags=" + flags);
            return false;
        }
        if (!reader.TryReadByte(out var stateFlags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing state flags");
            return false;
        }
        if (!reader.TryReadUShort(out var protoRaw) || !reader.TryReadUShort(out var modelRaw) ||
            !reader.TryReadUShort(out var ownerRaw) || !reader.TryReadUShort(out var portRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated discriminants");
            return false;
        }
        if (!reader.TryReadInt(out var astroId) || !reader.TryReadInt(out var originAstroId) ||
            !reader.TryReadInt(out var dockIndex) || !reader.TryReadInt(out var builderIndex) ||
            !reader.TryReadInt(out var level))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated linkage");
            return false;
        }
        if (!reader.TryReadLong(out var posXBits) || !reader.TryReadLong(out var posYBits) ||
            !reader.TryReadLong(out var posZBits))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated position");
            return false;
        }
        if (!reader.TryReadInt(out var rotXBits) || !reader.TryReadInt(out var rotYBits) ||
            !reader.TryReadInt(out var rotZBits) || !reader.TryReadInt(out var rotWBits) ||
            !reader.TryReadInt(out var velXBits) || !reader.TryReadInt(out var velYBits) ||
            !reader.TryReadInt(out var velZBits))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated pose");
            return false;
        }
        if (!reader.TryReadInt(out var hp) || !reader.TryReadInt(out var hpMax) ||
            !reader.TryReadInt(out var hpRecover) || !reader.TryReadInt(out var hpIncoming))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated hp");
            return false;
        }
        int animationTime = 0, prepareLength = 0, workingLength = 0, animationState = 0, animationPower = 0;
        if (version == Version && (!reader.TryReadInt(out animationTime) || !reader.TryReadInt(out prepareLength) ||
            !reader.TryReadInt(out workingLength) || !reader.TryReadInt(out animationState) ||
            !reader.TryReadInt(out animationPower)))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated animation");
            return false;
        }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new SpaceEnemyState(
            (SpaceEnemyKind)kindRaw, (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw),
            unchecked((short)ownerRaw), unchecked((short)portRaw), stateFlags,
            astroId, originAstroId, dockIndex, builderIndex, level,
            BitConverter.Int64BitsToDouble(posXBits),
            BitConverter.Int64BitsToDouble(posYBits),
            BitConverter.Int64BitsToDouble(posZBits),
            BitConverter.ToSingle(BitConverter.GetBytes(rotXBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(rotYBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(rotZBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(rotWBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(velXBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(velYBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(velZBits), 0),
            hp, hpMax, hpRecover, hpIncoming,
            BitConverter.ToSingle(BitConverter.GetBytes(animationTime), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(prepareLength), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(workingLength), 0),
            unchecked((uint)animationState),
            BitConverter.ToSingle(BitConverter.GetBytes(animationPower), 0));
        return true;
    }
}
