#region

using System;
using NebulaModel.Authority;

#endregion

namespace NebulaModel.Packets.Authority;

/// <summary>
/// Which visual event a host effect record carries (TASKS.md A14, explicit wire enum).
/// </summary>
/// <remarks>
/// <para>
/// The kinds cover the three DESIGN 3 rows the card owns — world (turret/base), player
/// (mecha muzzle/projectile/beam/bomb/burst) and craft (fleet fighter) effects — without
/// carrying vanilla field-name strings or opaque native payloads. A renamed vanilla field
/// therefore cannot silently change the wire format, the same rule A12/A13 apply to enemy
/// and craft states.
/// </para>
/// <para>
/// Hit flashes (<see cref="HitFlash"/>/<see cref="ShieldHit"/>) are display-only: they mark
/// where the host says something landed. Damage itself settles in A19; an effect record never
/// carries a damage number, an HP delta, or a kill claim.
/// </para>
/// </remarks>
public enum AuthorityEffectKind : byte
{
    Unknown = 0,

    /// <summary>One muzzle flash for a single ammo trigger pull (player-caused).</summary>
    MechaMuzzle = 1,

    /// <summary>One ballistic projectile in flight (player-caused).</summary>
    MechaProjectile = 2,

    /// <summary>One laser / continuous-beam discharge (player-caused).</summary>
    MechaBeam = 3,

    /// <summary>One bomb drop (player-caused).</summary>
    BombFall = 4,

    /// <summary>One shield burst discharge (player-caused).</summary>
    ShieldBurst = 5,

    /// <summary>One world turret / base-defense shot (host autonomous).</summary>
    TurretShot = 6,

    /// <summary>One craft shot, player fleet or base fighter (host autonomous).</summary>
    CraftShot = 7,

    /// <summary>Impact flash where the host says a shot landed (host, display-only).</summary>
    HitFlash = 8,

    /// <summary>Shield-absorb flash (host, display-only, no HP change).</summary>
    ShieldHit = 9,

    /// <summary>Lancer-style beam sweep (host autonomous).</summary>
    BeamSweep = 10
}

/// <summary>
/// One host-decided visual event: id, kind, style, cause, start tick and life, plus the
/// caster/target anchors the presentation needs to place it (DESIGN 5.2 EffectBatch).
/// </summary>
/// <remarks>
/// <para>
/// Caster/target travel as <see cref="ObjectKey"/> values that may each be invalid. A
/// player-caused muzzle has neither (the caster is a player, not a pool object) and is
/// anchored by <see cref="CauseConnection"/>/<see cref="CauseSequence"/> plus
/// <see cref="TransactionId"/> instead; an autonomous turret shot is anchored by a valid
/// caster key with no cause. At least one anchor must hold, otherwise the record is
/// unplaceable and the codec refuses it.
/// </para>
/// <para>
/// <see cref="Style"/> is an explicit visual variant (ammo item id, bomb proto id, hit
/// subtype), never a field name. <see cref="TransactionId"/> groups the event with the
/// command outcome that caused it (and later with A19 damage); it is zero for autonomous
/// world effects that no command caused.
/// </para>
/// </remarks>
public readonly struct EffectState
{
    public EffectState(long effectId, AuthorityEffectKind kind, int style, long transactionId,
        long startTick, int life, ulong causeConnection, long causeSequence,
        in ObjectKey caster, in ObjectKey target)
    {
        EffectId = effectId;
        Kind = kind;
        Style = style;
        TransactionId = transactionId;
        StartTick = startTick;
        Life = life;
        CauseConnection = causeConnection;
        CauseSequence = causeSequence;
        Caster = caster;
        Target = target;
    }

    /// <summary>Host-monotonic visual event id, unique per epoch. Never reused for another event.</summary>
    public long EffectId { get; }

    public AuthorityEffectKind Kind { get; }

    /// <summary>Visual variant (ammo/bomb proto, hit subtype). Non-negative, zero when unused.</summary>
    public int Style { get; }

    /// <summary>Ledger transaction that caused this event, or zero for autonomous world effects.</summary>
    public long TransactionId { get; }

    /// <summary>Host tick the event starts on. Never a wall clock.</summary>
    public long StartTick { get; }

    /// <summary>Event life in ticks. Expiry removes the shell; there is no other end signal.</summary>
    public int Life { get; }

    /// <summary>Causing client's connection epoch value, or zero for autonomous effects.</summary>
    public ulong CauseConnection { get; }

    /// <summary>Causing client's command sequence, or zero for autonomous effects.</summary>
    public long CauseSequence { get; }

    /// <summary>Caster object, or an invalid key when the caster is a player or absent.</summary>
    public ObjectKey Caster { get; }

    /// <summary>Target object, or an invalid key when the event targets nothing.</summary>
    public ObjectKey Target { get; }

    /// <summary>True when this record names its client prediction (player-caused, first shot).</summary>
    public bool HasCause => CauseConnection != 0 && CauseSequence > 0;
}

/// <summary>
/// Initial presentation tuning for host effects (DESIGN 10). Measured tuning belongs to A22.
/// </summary>
public static class AuthorityEffectDefaults
{
    /// <summary>Maximum event life the codec accepts, matching the legacy frame's ceiling.</summary>
    public const int MaxLifeTicks = 360000;

    /// <summary>Default life per kind, in ticks at 60 Hz.</summary>
    public static int LifeFor(AuthorityEffectKind kind)
    {
        switch (kind)
        {
            case AuthorityEffectKind.MechaMuzzle:
                return 12;
            case AuthorityEffectKind.MechaProjectile:
            case AuthorityEffectKind.TurretShot:
            case AuthorityEffectKind.CraftShot:
                return 600;
            case AuthorityEffectKind.MechaBeam:
            case AuthorityEffectKind.HitFlash:
            case AuthorityEffectKind.ShieldHit:
                return 30;
            case AuthorityEffectKind.BombFall:
                return 3600;
            case AuthorityEffectKind.ShieldBurst:
                return 60;
            case AuthorityEffectKind.BeamSweep:
                return 120;
            default:
                return 30;
        }
    }
}

/// <summary>
/// Fixed-layout encoding of <see cref="EffectState"/> (TASKS.md A14, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Layout v1, little-endian via <see cref="AuthorityPayloadWriter"/>: version byte (1),
/// kind byte, style int, effectId long, transactionId long, startTick long, life int,
/// causeConnection long-as-ulong, causeSequence long, then the caster and target keys each
/// as a length-prefixed <see cref="ObjectKey"/> blob. The whole record is 132 bytes, well
/// under <see cref="AuthorityLimits.StateRecordMaxBytes"/>, which is what keeps a batch of
/// effects shippable without a new size ceiling.
/// </para>
/// <para>
/// Unknown kinds, out-of-range lives, unanchored records, truncation and trailing bytes are
/// all refused. <see cref="AuthorityEffectKind.Unknown"/> is never encoded.
/// </para>
/// </remarks>
public static class EffectStateCodec
{
    /// <summary>Payload version this codec writes and the only one it accepts.</summary>
    public const byte PayloadVersion = 1;

    public static bool TryEncode(in EffectState state, out byte[] data)
    {
        data = null;
        if (!IsEncodable(in state)) return false;
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(PayloadVersion);
        writer.WriteByte((byte)state.Kind);
        writer.WriteInt(state.Style);
        writer.WriteLong(state.EffectId);
        writer.WriteLong(state.TransactionId);
        writer.WriteLong(state.StartTick);
        writer.WriteInt(state.Life);
        writer.WriteLong((long)state.CauseConnection);
        writer.WriteLong(state.CauseSequence);
        var casterBytes = new byte[AuthorityLimits.ObjectKeyBytes];
        ObjectKeyCodec.WriteTo(casterBytes, 0, state.Caster);
        if (!writer.TryWriteBytes(casterBytes, AuthorityLimits.ObjectKeyBytes)) return false;
        var targetBytes = new byte[AuthorityLimits.ObjectKeyBytes];
        ObjectKeyCodec.WriteTo(targetBytes, 0, state.Target);
        if (!writer.TryWriteBytes(targetBytes, AuthorityLimits.ObjectKeyBytes)) return false;
        data = writer.ToArray();
        return data.Length <= AuthorityLimits.StateRecordMaxBytes;
    }

    public static bool TryDecode(byte[] source, int offset, int length, out EffectState state,
        out AuthorityReject reject)
    {
        state = default;
        reject = AuthorityReject.Accepted;
        if (!AuthorityPayloadReader.TryCreate(source, offset, length, out var reader, out reject)) return false;
        if (!reader.TryReadByte(out var version) || version != PayloadVersion)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "effect version=" + version);
            return false;
        }
        if (!reader.TryReadByte(out var kindRaw) || !Enum.IsDefined(typeof(AuthorityEffectKind), kindRaw) ||
            (AuthorityEffectKind)kindRaw == AuthorityEffectKind.Unknown)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "effect kind=" + kindRaw);
            return false;
        }
        if (!reader.TryReadInt(out var style) || !reader.TryReadLong(out var effectId) ||
            !reader.TryReadLong(out var transactionId) || !reader.TryReadLong(out var startTick) ||
            !reader.TryReadInt(out var life) || !reader.TryReadLong(out var causeConnectionRaw) ||
            !reader.TryReadLong(out var causeSequence))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated effect");
            return false;
        }
        if (!reader.TryReadBytes(AuthorityLimits.ObjectKeyBytes, out var casterBytes) ||
            casterBytes.Length != AuthorityLimits.ObjectKeyBytes ||
            !reader.TryReadBytes(AuthorityLimits.ObjectKeyBytes, out var targetBytes) ||
            targetBytes.Length != AuthorityLimits.ObjectKeyBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated effect keys");
            return false;
        }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "effect trailing bytes");
            return false;
        }
        var candidate = new EffectState(effectId, (AuthorityEffectKind)kindRaw, style, transactionId,
            startTick, life, (ulong)causeConnectionRaw, causeSequence,
            ObjectKeyCodec.ReadRaw(casterBytes, 0), ObjectKeyCodec.ReadRaw(targetBytes, 0));
        if (!IsEncodable(in candidate))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "effect fields out of range");
            return false;
        }
        state = candidate;
        return true;
    }

    private static bool IsEncodable(in EffectState state)
    {
        if (state.Kind == AuthorityEffectKind.Unknown ||
            !Enum.IsDefined(typeof(AuthorityEffectKind), state.Kind))
            return false;
        if (state.EffectId <= 0 || state.Style < 0 || state.TransactionId < 0 || state.StartTick < 0)
            return false;
        if (state.Life <= 0 || state.Life > AuthorityEffectDefaults.MaxLifeTicks)
            return false;
        if ((state.CauseConnection == 0) != (state.CauseSequence <= 0))
            return false;
        if (state.CauseSequence < 0)
            return false;
        // At least one anchor: a placed caster, a placed target, a ledger cause, or the
        // client prediction this event merges with. An event with none is unplaceable.
        if (!state.Caster.IsValid && !state.Target.IsValid && state.TransactionId <= 0 && !state.HasCause)
            return false;
        return true;
    }
}
