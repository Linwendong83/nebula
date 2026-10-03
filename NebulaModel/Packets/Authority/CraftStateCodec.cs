#region

using System;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// One craft's canonical state, as the host publishes it (TASKS.md A13).
/// </summary>
/// <remarks>
/// <para>
/// Identity (<see cref="ObjectKey"/> with <see cref="PoolKind.GroundCraft"/> on a planet scope or
/// <see cref="PoolKind.SpaceCraft"/> in sector scope 0) travels in the lifecycle/state framing; this
/// struct is the opaque state blob inside it. Pose and hitpoints are absolute — never deltas — and
/// fleet/unit/drone/vehicle logic components deliberately do not travel: the client shows
/// Spawn/Pose/State/Despawn and never runs fleet orders, auto-targeting, ammo or destruction rules.
/// Those pools stay host-only, which is also why a client that never allocates logic components is
/// naturally rule-inert (its unit/fleet ids stay zero, so the vanilla combat ticks skip it).
/// </para>
/// <para>
/// <c>HasCombatStat == false</c> is an explicit statement (the host says this craft has no damage
/// record), not an absence. <c>Owner</c> is the host's craft owner (player id for fleet craft,
/// base owner for base fighters); two players' craft never share a slot and never overwrite each
/// other because every record is keyed by <see cref="ObjectKey"/>, not by owner. <c>FleetId</c> is
/// the host's fleet-component slot for display grouping only — never a wire identity.
/// </para>
/// <para>
/// Position is double-precision so one codec serves both ground (float in vanilla, lifted
/// losslessly) and space (double) craft. The layout is fixed and field-by-field, like the other
/// authority codecs: no reflection over names.
/// </para>
/// </remarks>
public readonly struct CraftState
{
    public CraftState(bool hasCombatStat, bool isDynamic, bool isSpace,
        short protoId, short modelIndex, short port, byte prototype, byte stateFlags,
        int astroId, int owner, int fleetId,
        double posX, double posY, double posZ,
        float rotX, float rotY, float rotZ, float rotW,
        float velX, float velY, float velZ,
        int hp, int hpMax, int hpRecover, int hpIncoming)
    {
        HasCombatStat = hasCombatStat;
        IsDynamic = isDynamic;
        IsSpace = isSpace;
        ProtoId = protoId;
        ModelIndex = modelIndex;
        Port = port;
        Prototype = prototype;
        StateFlags = stateFlags;
        AstroId = astroId;
        Owner = owner;
        FleetId = fleetId;
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
    }

    public bool HasCombatStat { get; }
    public bool IsDynamic { get; }
    public bool IsSpace { get; }
    public short ProtoId { get; }
    public short ModelIndex { get; }
    public short Port { get; }
    public byte Prototype { get; }
    public byte StateFlags { get; }
    public int AstroId { get; }
    public int Owner { get; }
    public int FleetId { get; }
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
}

/// <summary>Encoding of <see cref="CraftState"/> into one bounded state blob.</summary>
public static class CraftStateCodec
{
    public const byte Version = 1;

    private const byte HasCombatFlag = 1;
    private const byte IsDynamicFlag = 2;
    private const byte IsSpaceFlag = 4;

    /// <summary>Fixed wire size: version + flags + stateFlags + discriminants + linkage + pose + HP.</summary>
    public const int FixedSize = 1 + 1 + 1 + 7 + 12 + 52 + 16;

    public static bool TryEncode(in CraftState state, out byte[] data)
    {
        data = null;
        var flags = (byte)((state.HasCombatStat ? HasCombatFlag : 0) |
            (state.IsDynamic ? IsDynamicFlag : 0) |
            (state.IsSpace ? IsSpaceFlag : 0));
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(Version);
        writer.WriteByte(flags);
        writer.WriteByte(state.StateFlags);
        writer.WriteUShort(unchecked((ushort)state.ProtoId));
        writer.WriteUShort(unchecked((ushort)state.ModelIndex));
        writer.WriteUShort(unchecked((ushort)state.Port));
        writer.WriteByte(state.Prototype);
        writer.WriteInt(state.AstroId);
        writer.WriteInt(state.Owner);
        writer.WriteInt(state.FleetId);
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
        data = writer.ToArray();
        return data.Length == FixedSize && data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    public static bool TryDecode(byte[] source, int offset, int length, out CraftState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var version) || version != Version)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "craft version=" + version);
            return false;
        }
        if (!reader.TryReadByte(out var flags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing craft flags");
            return false;
        }
        if ((flags & ~(HasCombatFlag | IsDynamicFlag | IsSpaceFlag)) != 0)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "craft flags=" + flags);
            return false;
        }
        if (!reader.TryReadByte(out var stateFlags))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "missing state flags");
            return false;
        }
        if (!reader.TryReadUShort(out var protoRaw) || !reader.TryReadUShort(out var modelRaw) ||
            !reader.TryReadUShort(out var portRaw))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated discriminants");
            return false;
        }
        if (!reader.TryReadByte(out var prototype))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated prototype");
            return false;
        }
        if (!reader.TryReadInt(out var astroId) || !reader.TryReadInt(out var owner) ||
            !reader.TryReadInt(out var fleetId))
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
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "trailing bytes");
            return false;
        }
        state = new CraftState(
            (flags & HasCombatFlag) != 0, (flags & IsDynamicFlag) != 0, (flags & IsSpaceFlag) != 0,
            unchecked((short)protoRaw), unchecked((short)modelRaw), unchecked((short)portRaw),
            prototype, stateFlags, astroId, owner, fleetId,
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
            hp, hpMax, hpRecover, hpIncoming);
        return true;
    }
}
