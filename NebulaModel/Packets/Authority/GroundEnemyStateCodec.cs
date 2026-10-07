#region

using System;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// Which ground component an enemy carries (TASKS.md A12).
/// </summary>
/// <remarks>
/// <para>
/// The values are explicit wire discriminants, not reflection over field names. The old snapshot
/// described components as <c>(enemyField, poolField)</c> string pairs
/// (<c>EnemyManager.Snapshot.cs: groundComponents</c>); that table is the source of the member list
/// below, but the wire never carries a field name — only this enum. A renamed vanilla field cannot
/// silently change the protocol, which is the "禁止反射字段名成为唯一 schema" requirement.
/// </para>
/// <para>
/// Priority when several component ids are non-zero is fixed in
/// <c>GroundEnemyKinds.FromComponentIds</c>: base core first, then unit, builder, turret, shield,
/// connector, replicator. The host and the tests share that function, so the kind is deterministic
/// rather than "whichever reflection happened to find first".
/// </para>
/// </remarks>
public enum GroundEnemyKind : byte
{
    Unknown = 0,
    GroundUnit = 1,
    GroundBuilder = 2,
    GroundBase = 3,
    GroundTurret = 4,
    GroundShield = 5,
    GroundConnector = 6,
    GroundReplicator = 7
}

/// <summary>Derives a wire kind from vanilla ground component ids without reflection.</summary>
public static class GroundEnemyKinds
{
    public static GroundEnemyKind FromComponentIds(int dfGBaseId, int unitId, int builderId,
        int dfGTurretId, int dfGShieldId, int dfGConnectorId, int dfGReplicatorId)
    {
        if (dfGBaseId > 0) return GroundEnemyKind.GroundBase;
        if (unitId > 0) return GroundEnemyKind.GroundUnit;
        if (builderId > 0) return GroundEnemyKind.GroundBuilder;
        if (dfGTurretId > 0) return GroundEnemyKind.GroundTurret;
        if (dfGShieldId > 0) return GroundEnemyKind.GroundShield;
        if (dfGConnectorId > 0) return GroundEnemyKind.GroundConnector;
        if (dfGReplicatorId > 0) return GroundEnemyKind.GroundReplicator;
        return GroundEnemyKind.Unknown;
    }

    public static bool IsDefined(GroundEnemyKind kind) => kind >= GroundEnemyKind.GroundUnit &&
        kind <= GroundEnemyKind.GroundReplicator;
}

/// <summary>
/// One ground enemy's canonical state, as the host publishes it (TASKS.md A12).
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.GroundEnemy"/>) travels in the
/// lifecycle/state framing; this struct is the opaque state blob inside it. Pose and hitpoints are
/// absolute — never deltas — and component internals (hatred, cooldowns, shield energy, build
/// progress) deliberately do not travel: the client shows Spawn/Pose/State/Despawn and never runs
/// activation, base manufacture, AI, hatred or attack. Those pools stay host-only, which is also why
/// a client that never allocates logic components is naturally AI-inert (its unit/turret/shield ids
/// stay zero, so the vanilla ticks skip it).
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this enemy has no damage
/// record), not an absence — the same reading DESIGN 9.1 gives the factory combat state. Base
/// linkage (<c>BaseId</c>) names the host's base-component slot; the client resolves ordering against
/// the base-core enemies it has already applied and defers the child rather than guessing (the
/// parent-child inversion rule).
/// </para>
/// <para>
/// The layout is fixed and field-by-field: version, kind, flags, vanilla discriminants, linkage,
/// pose, hitpoints. No reflection over names, no per-record framing beyond the flags byte.
/// </para>
/// </remarks>
public readonly struct GroundEnemyState
{
    public GroundEnemyState(GroundEnemyKind kind, bool hasCombatStat, bool isDynamic,
        short protoId, short modelIndex, short owner, short port, byte stateFlags,
        int astroId, int originAstroId, int baseId, int builderIndex, int level,
        float posX, float posY, float posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming,
        float animationTime = 0, float prepareLength = 0, float workingLength = 0, uint animationState = 0, float animationPower = 0,
        float unitAnimation = 0, float unitDisturb = 0, float unitSteering = 0, float unitSpeed = 0)
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
        BaseId = baseId;
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
        AnimationTime = animationTime; PrepareLength = prepareLength; WorkingLength = workingLength;
        AnimationState = animationState; AnimationPower = animationPower;
        UnitAnimation = unitAnimation; UnitDisturb = unitDisturb; UnitSteering = unitSteering; UnitSpeed = unitSpeed;
    }

    public GroundEnemyKind Kind { get; }
    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Owner { get; }
    public short Port { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int OriginAstroId { get; }
    public int BaseId { get; }
    public int BuilderIndex { get; }
    public int Level { get; }
    public float PosX { get; }
    public float PosY { get; }
    public float PosZ { get; }
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
    public float UnitAnimation { get; }
    public float UnitDisturb { get; }
    public float UnitSteering { get; }
    public float UnitSpeed { get; }
}

/// <summary>Encoding of <see cref="GroundEnemyState"/> into one bounded state blob.</summary>
public static class GroundEnemyStateCodec
{
    public const byte Version = 3;

    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;

    /// <summary>Fixed wire size: version + kind + flags + stateFlags + discriminants + linkage + pose + HP.</summary>
    public const int FixedSize = 1 + 1 + 1 + 1 + 8 + 20 + 40 + 16 + 20 + 16;

    public static bool TryEncode(in GroundEnemyState state, out byte[] data)
    {
        data = null;
        if (!GroundEnemyKinds.IsDefined(state.Kind)) return false;
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
        writer.WriteInt(state.BaseId);
        writer.WriteInt(state.BuilderIndex);
        writer.WriteInt(state.Level);
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.PosX), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.PosY), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.PosZ), 0));
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
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.UnitAnimation), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.UnitDisturb), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.UnitSteering), 0));
        writer.WriteInt(BitConverter.ToInt32(BitConverter.GetBytes(state.UnitSpeed), 0));
        data = writer.ToArray();
        return data.Length == FixedSize && data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    public static bool TryDecode(byte[] source, int offset, int length, out GroundEnemyState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var version) || version != Version)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "ground enemy version=" + version);
            return false;
        }
        if (!reader.TryReadByte(out var kindRaw) || !GroundEnemyKinds.IsDefined((GroundEnemyKind)kindRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "ground enemy kind=" + kindRaw);
            return false;
        }
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing ground enemy flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "ground enemy flags=" + flags);
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
            !reader.TryReadInt(out var baseId) || !reader.TryReadInt(out var builderIndex) ||
            !reader.TryReadInt(out var level))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated linkage");
            return false;
        }
        if (!reader.TryReadInt(out var posXBits) || !reader.TryReadInt(out var posYBits) ||
            !reader.TryReadInt(out var posZBits) || !reader.TryReadInt(out var rotXBits) ||
            !reader.TryReadInt(out var rotYBits) || !reader.TryReadInt(out var rotZBits) ||
            !reader.TryReadInt(out var rotWBits) || !reader.TryReadInt(out var velXBits) ||
            !reader.TryReadInt(out var velYBits) || !reader.TryReadInt(out var velZBits))
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
        if (!reader.TryReadInt(out var animationTime) || !reader.TryReadInt(out var prepareLength) ||
            !reader.TryReadInt(out var workingLength) || !reader.TryReadInt(out var animationState) || !reader.TryReadInt(out var animationPower))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated animation"); return false;
        }
        if (!reader.TryReadInt(out var unitAnimation) || !reader.TryReadInt(out var unitDisturb) ||
            !reader.TryReadInt(out var unitSteering) || !reader.TryReadInt(out var unitSpeed))
        { reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated unit visual"); return false; }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new GroundEnemyState(
            (GroundEnemyKind)kindRaw, (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw),
            unchecked((short)ownerRaw), unchecked((short)portRaw), stateFlags,
            astroId, originAstroId, baseId, builderIndex, level,
            BitConverter.ToSingle(BitConverter.GetBytes(posXBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(posYBits), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(posZBits), 0),
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
            unchecked((uint)animationState), BitConverter.ToSingle(BitConverter.GetBytes(animationPower), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(unitAnimation), 0), BitConverter.ToSingle(BitConverter.GetBytes(unitDisturb), 0),
            BitConverter.ToSingle(BitConverter.GetBytes(unitSteering), 0), BitConverter.ToSingle(BitConverter.GetBytes(unitSpeed), 0));
        return true;
    }
}
