#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Which player-combat intent a command carries (TASKS.md A11, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The wire category is a single value (<see cref="Category"/>); the action inside the payload
/// tells which intent it is. Fleet orders, construction options, build/dismantle/upgrade,
/// inventory transfers and subscribe/resync are different categories owned by later cards
/// (A13/A16/A18/W01/A20) and must not reuse this value.
/// </para>
/// <para>
/// Clients send intents only: aim/target, weapon slot/mode and an input-tick hint. They never send
/// a final damage, HP, kill or loot number — those are the host's to compute (DESIGN 5.1).
/// </para>
/// </remarks>
public enum PlayerCombatAction : byte
{
    Unknown = 0,

    /// <summary>One trigger pull with an ammo weapon (gauss/cannon/plasma/missile).</summary>
    PrimaryFire = 1,

    /// <summary>One laser discharge. Energy cost comes from host config, never from the client.</summary>
    LaserFire = 2,

    /// <summary>One bomb drop. Stock cost comes from the host ledger.</summary>
    BombDrop = 3,

    /// <summary>One shield burst. Energy cost comes from the host ledger.</summary>
    ShieldBurst = 4,

    /// <summary>Begin host-clocked continuous fire; the host ticks it until stop/expiry/disconnect.</summary>
    StartContinuous = 5,

    /// <summary>End host-clocked continuous fire opened by <see cref="StartContinuous"/>.</summary>
    StopContinuous = 6,

    /// <summary>Request respawn after death. No target, no weapon.</summary>
    Respawn = 7
}

/// <summary>
/// Which weapon family a combat intent uses. The host maps it to ledger costs, never trusting a
/// client-supplied amount.
/// </summary>
public enum PlayerWeaponKind : byte
{
    Unknown = 0,
    Gauss = 1,
    Cannon = 2,
    Plasma = 3,
    Missile = 4,
    Laser = 5,
    Bomb = 6,
    Shield = 7
}

/// <summary>
/// Why a combat intent was refused. The wire answer stays a <see cref="CommandResultCode"/>;
/// this reason is for logs, tests and the client's UI overlay (DESIGN 5.1 "拒绝码").
/// </summary>
public enum PlayerCombatRejectReason : byte
{
    None = 0,
    InvalidPayload = 1,
    UnknownAction = 2,
    Unauthorized = 3,
    NotAlive = 4,
    ExpiredInput = 5,
    FutureInput = 6,
    Cooldown = 7,
    BadTarget = 8,
    OutOfRange = 9,
    StaleRevision = 10,
    Insufficient = 11,
    AlreadyFiring = 12,
    NotFiring = 13,
    AlreadyAlive = 14,
    NoTargetRules = 15
}

/// <summary>
/// Host-side per-shot costs. Values are supplied by the adapter (game config/tech in production,
/// fixed test values in pure tests), never taken from a client packet.
/// </summary>
public readonly struct PlayerCombatCosts
{
    public PlayerCombatCosts(double laserEnergyPerShot, double shieldEnergyPerBurst,
        long cooldownTicksPerShot, long continuousTickInterval, long maxInputAgeTicks,
        long futureToleranceTicks)
    {
        LaserEnergyPerShot = laserEnergyPerShot;
        ShieldEnergyPerBurst = shieldEnergyPerBurst;
        CooldownTicksPerShot = cooldownTicksPerShot;
        ContinuousTickInterval = continuousTickInterval;
        MaxInputAgeTicks = maxInputAgeTicks;
        FutureToleranceTicks = futureToleranceTicks;
    }

    /// <summary>CoreEnergy per laser discharge.</summary>
    public double LaserEnergyPerShot { get; }

    /// <summary>CoreEnergy per shield burst.</summary>
    public double ShieldEnergyPerBurst { get; }

    /// <summary>Host ticks a trigger pull (or burst, bomb, laser shot) cools down.</summary>
    public long CooldownTicksPerShot { get; }

    /// <summary>Host ticks between continuous-fire ticks.</summary>
    public long ContinuousTickInterval { get; }

    /// <summary>A client input older than hostTick - this is expired.</summary>
    public long MaxInputAgeTicks { get; }

    /// <summary>A client input ahead of the host tick by more than this is invalid.</summary>
    public long FutureToleranceTicks { get; }

    /// <summary>Initial tuning for tests and the first adapter; A22 measures real fire rates.</summary>
    public static PlayerCombatCosts Default => new(
        laserEnergyPerShot: 5.0,
        shieldEnergyPerBurst: 20.0,
        cooldownTicksPerShot: 30,
        continuousTickInterval: 30,
        maxInputAgeTicks: 600,
        futureToleranceTicks: 60);
}

/// <summary>
/// One decoded combat intent: what the client asked for, with nothing trusted.
/// </summary>
public readonly struct PlayerCombatRequest
{
    public PlayerCombatRequest(PlayerCombatAction action, PlayerWeaponKind weapon, int ammoItemId,
        long inputTick, long expectedRevision, int protoId, int nearStarId)
    {
        Action = action;
        Weapon = weapon;
        AmmoItemId = ammoItemId;
        InputTick = inputTick;
        ExpectedRevision = expectedRevision;
        ProtoId = protoId;
        NearStarId = nearStarId;
    }

    public PlayerCombatAction Action { get; }

    public PlayerWeaponKind Weapon { get; }

    /// <summary>Ammo/bomb item id; 0 when the action uses no item pool.</summary>
    public int AmmoItemId { get; }

    /// <summary>Client's observed host tick, used only for expiry, never for ordering.</summary>
    public long InputTick { get; }

    /// <summary>Ledger revision the client saw for the balance this intent spends.</summary>
    public long ExpectedRevision { get; }

    /// <summary>Bomb proto id for <see cref="PlayerCombatAction.BombDrop"/>; 0 otherwise.</summary>
    public int ProtoId { get; }

    /// <summary>Star the bomb is dropped near; 0 when not a bomb.</summary>
    public int NearStarId { get; }
}

/// <summary>
/// Wire category and payload codec for player-combat intents (TASKS.md A11, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Layout v1 (31 bytes, little-endian via <see cref="AuthorityPayloadWriter"/>):
/// version byte (1), action byte, weapon byte, ammoItemId int, inputTick long,
/// expectedRevision long, extraA int (bomb protoId, else 0), extraB int (bomb nearStarId, else 0).
/// </para>
/// <para>
/// Bounds: every enum value must be defined, ids non-negative, ticks/revisions non-negative, no
/// trailing bytes, and the whole payload within <see cref="AuthorityLimits.CommandPayloadMaxBytes"/>.
/// A target object travels in the envelope's <c>TargetKey</c>, not here: fire actions require it,
/// bomb/burst/respawn forbid it.
/// </para>
/// </remarks>
public static class PlayerCombatCommand
{
    /// <summary>Wire category for every player-combat intent. Later cards use other values.</summary>
    public const byte Category = 0x20;

    /// <summary>Payload version this codec writes and the only one it accepts.</summary>
    public const byte PayloadVersion = 1;

    /// <summary>True when the action needs an envelope target key.</summary>
    public static bool RequiresTarget(PlayerCombatAction action)
    {
        switch (action)
        {
            case PlayerCombatAction.PrimaryFire:
            case PlayerCombatAction.LaserFire:
            case PlayerCombatAction.StartContinuous:
                return true;
            default:
                return false;
        }
    }

    public static bool TryEncode(in PlayerCombatRequest request, out byte[] payload)
    {
        payload = null;
        if (!IsEncodable(request)) return false;
        var writer = new AuthorityPayloadWriter();
        writer.WriteByte(PayloadVersion);
        writer.WriteByte((byte)request.Action);
        writer.WriteByte((byte)request.Weapon);
        writer.WriteInt(request.AmmoItemId);
        writer.WriteLong(request.InputTick);
        writer.WriteLong(request.ExpectedRevision);
        writer.WriteInt(request.ProtoId);
        writer.WriteInt(request.NearStarId);
        payload = writer.ToArray();
        return payload.Length <= AuthorityLimits.CommandPayloadMaxBytes;
    }

    public static bool TryDecode(byte[] payload, out PlayerCombatRequest request, out AuthorityReject reject)
    {
        request = default;
        reject = AuthorityReject.Accepted;
        if (payload == null || payload.Length > AuthorityLimits.CommandPayloadMaxBytes)
        {
            reject = new AuthorityReject(AuthorityRejectCode.PayloadTooLarge, "combat payload oversize");
            return false;
        }
        if (!AuthorityPayloadReader.TryCreate(payload, 0, payload.Length, out var reader, out reject))
            return false;
        if (!reader.TryReadByte(out var version) || version != PayloadVersion)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "combat version=" + version);
            return false;
        }
        if (!reader.TryReadByte(out var actionRaw) || !Enum.IsDefined(typeof(PlayerCombatAction), actionRaw) ||
            (PlayerCombatAction)actionRaw == PlayerCombatAction.Unknown)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "combat action=" + actionRaw);
            return false;
        }
        if (!reader.TryReadByte(out var weaponRaw) || !Enum.IsDefined(typeof(PlayerWeaponKind), weaponRaw) ||
            (PlayerWeaponKind)weaponRaw == PlayerWeaponKind.Unknown)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "combat weapon=" + weaponRaw);
            return false;
        }
        if (!reader.TryReadInt(out var ammoItemId) || !reader.TryReadLong(out var inputTick) ||
            !reader.TryReadLong(out var expectedRevision) || !reader.TryReadInt(out var protoId) ||
            !reader.TryReadInt(out var nearStarId))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "truncated combat payload");
            return false;
        }
        if (!reader.EndOfPayload)
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "combat trailing bytes");
            return false;
        }
        var candidate = new PlayerCombatRequest((PlayerCombatAction)actionRaw, (PlayerWeaponKind)weaponRaw,
            ammoItemId, inputTick, expectedRevision, protoId, nearStarId);
        if (!IsEncodable(candidate))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope, "combat fields out of range");
            return false;
        }
        if (!IsActionWeaponConsistent(candidate))
        {
            reject = new AuthorityReject(AuthorityRejectCode.MalformedEnvelope,
                "combat action=" + candidate.Action + " weapon=" + candidate.Weapon);
            return false;
        }
        request = candidate;
        return true;
    }

    private static bool IsEncodable(in PlayerCombatRequest request)
    {
        if (request.Action == PlayerCombatAction.Unknown || request.Weapon == PlayerWeaponKind.Unknown)
            return false;
        if (!Enum.IsDefined(typeof(PlayerCombatAction), request.Action) ||
            !Enum.IsDefined(typeof(PlayerWeaponKind), request.Weapon))
            return false;
        if (request.AmmoItemId < 0 || request.InputTick < 0 || request.ExpectedRevision < 0 ||
            request.ProtoId < 0 || request.NearStarId < 0)
            return false;
        return IsActionWeaponConsistent(request);
    }

    /// <summary>
    /// Which weapon family each action accepts. Ammo weapons need an item id; energy weapons and
    /// shield must not carry one; bombs carry a proto id instead.
    /// </summary>
    public static bool IsActionWeaponConsistent(in PlayerCombatRequest request)
    {
        switch (request.Action)
        {
            case PlayerCombatAction.PrimaryFire:
                return IsAmmoWeapon(request.Weapon) && request.AmmoItemId > 0 && request.ProtoId == 0;
            case PlayerCombatAction.LaserFire:
                return request.Weapon == PlayerWeaponKind.Laser && request.AmmoItemId == 0 &&
                       request.ProtoId == 0;
            case PlayerCombatAction.BombDrop:
                return request.Weapon == PlayerWeaponKind.Bomb && request.ProtoId > 0 &&
                       request.AmmoItemId == 0;
            case PlayerCombatAction.ShieldBurst:
                return request.Weapon == PlayerWeaponKind.Shield && request.AmmoItemId == 0 &&
                       request.ProtoId == 0;
            case PlayerCombatAction.StartContinuous:
            case PlayerCombatAction.StopContinuous:
                return (IsAmmoWeapon(request.Weapon) || request.Weapon == PlayerWeaponKind.Laser) &&
                       request.ProtoId == 0 &&
                       (request.Weapon == PlayerWeaponKind.Laser ? request.AmmoItemId == 0 : request.AmmoItemId > 0);
            case PlayerCombatAction.Respawn:
                // Respawn carries no weapon meaning; the sender uses Shield as a neutral placeholder
                // so the weapon field stays defined. No ids travel with it.
                return request.Weapon == PlayerWeaponKind.Shield && request.AmmoItemId == 0 &&
                       request.ProtoId == 0;
            default:
                return false;
        }
    }

    public static bool IsAmmoWeapon(PlayerWeaponKind weapon)
    {
        switch (weapon)
        {
            case PlayerWeaponKind.Gauss:
            case PlayerWeaponKind.Cannon:
            case PlayerWeaponKind.Plasma:
            case PlayerWeaponKind.Missile:
                return true;
            default:
                return false;
        }
    }
}
