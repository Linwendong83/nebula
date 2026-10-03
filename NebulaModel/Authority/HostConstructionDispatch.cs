#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Why a repair drone was not dispatched for one (owner, target) pair (TASKS.md A16, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The order below follows DESIGN 8.2: <c>NoDamageRecord, OutOfRange, SpeedTooHigh, Disabled,
/// NoIdleDrone, NoEnergy, DemandCovered, TargetNotReady, WrongGeneration</c>. <see cref="None"/>
/// means dispatchable. Evaluation is first-failure-wins in the order implemented by
/// <see cref="HostConstructionDispatch.EvaluateRepair"/>, so a pair that fails several gates
/// always reports the same reason on every host.
/// </para>
/// <para>
/// Owner eligibility (unknown, offline, dead, virtual server, stale seat) and both construction
/// switches map to <see cref="Disabled"/>: the owner cannot serve work this tick. A missing
/// damage record maps to <see cref="NoDamageRecord"/>; a record that names another generation
/// maps to <see cref="WrongGeneration"/>, never to an implicit retarget.
/// </para>
/// </remarks>
public enum ConstructionDispatchReason : byte
{
    /// <summary>The pair passed every gate and may reserve a drone.</summary>
    None = 0,

    /// <summary>No combat/construct damage record exists for the target.</summary>
    NoDamageRecord = 1,

    /// <summary>Owner is on another planet or the target is outside the owner's range.</summary>
    OutOfRange = 2,

    /// <summary>Mecha owner moves faster than the vanilla 20 m/s repair gate.</summary>
    SpeedTooHigh = 3,

    /// <summary>Owner cannot serve work: unknown, offline, dead, virtual, stale seat, or a switch is off.</summary>
    Disabled = 4,

    /// <summary>The owner's budget has no idle drone slot.</summary>
    NoIdleDrone = 5,

    /// <summary>The owner has no energy to fly or repair with.</summary>
    NoEnergy = 6,

    /// <summary>The target needs no more repairers (full HP, or demand covered).</summary>
    DemandCovered = 7,

    /// <summary>The target or its record is malformed (bad kind, bad HP bounds, NaN inputs).</summary>
    TargetNotReady = 8,

    /// <summary>The requested target generation does not match the damage record's generation.</summary>
    WrongGeneration = 9
}

/// <summary>
/// One repair owner's host-accepted state for candidate filtering (TASKS.md A16, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Planet, position and switches come from the host's accepted presence — <see cref="HostPlayerRegistry"/>
/// for players, the base source for battle bases — never from the host's own local planet. That is
/// what makes "只有客户端在另一星球仍能成为候选" structural: the service has no host-planet field
/// to read. Range is the vanilla <c>buildArea</c> (mecha) or <c>baseBuildRange</c> (base); speed is
/// the host-accepted scalar velocity used by the vanilla 20 m/s gate (bases ignore it).
/// </para>
/// <para>
/// Energy is a boolean here on purpose: the production adapter reads the real core/base energy
/// (A10 ledger balances and the vanilla module pools) and answers sufficient/insufficient, while
/// tests inject the value directly. The dispatch rule never sees a raw energy number.
/// </para>
/// </remarks>
public readonly struct RepairOwnerSnapshot : IEquatable<RepairOwnerSnapshot>
{
    public RepairOwnerSnapshot(ConstructionOwnerKey owner, int planetId,
        float posX, float posY, float posZ, float speed,
        bool isAvailable, bool repairEnabled, bool droneEnabled,
        float range, bool energySufficient)
    {
        Owner = owner;
        PlanetId = planetId;
        PosX = posX;
        PosY = posY;
        PosZ = posZ;
        Speed = speed;
        IsAvailable = isAvailable;
        RepairEnabled = repairEnabled;
        DroneEnabled = droneEnabled;
        Range = range;
        EnergySufficient = energySufficient;
    }

    /// <summary>Which seat would fly the drone.</summary>
    public ConstructionOwnerKey Owner { get; }

    /// <summary>Host-accepted planet of the owner, or 0 when unknown.</summary>
    public int PlanetId { get; }

    public float PosX { get; }

    public float PosY { get; }

    public float PosZ { get; }

    /// <summary>Host-accepted scalar speed. Only mecha owners are gated by it.</summary>
    public float Speed { get; }

    /// <summary>Registry eligibility: online, alive, and allowed to own drone work.</summary>
    public bool IsAvailable { get; }

    /// <summary>Construction repair switch, as the host last accepted it.</summary>
    public bool RepairEnabled { get; }

    /// <summary>Master construction-drone switch.</summary>
    public bool DroneEnabled { get; }

    /// <summary>Vanilla build area (mecha) or base build range (base).</summary>
    public float Range { get; }

    /// <summary>Whether the owner can pay flight and repair energy this tick.</summary>
    public bool EnergySufficient { get; }

    public bool IsMecha => Owner.IsPlayer;

    public bool Equals(RepairOwnerSnapshot other) =>
        Owner.Equals(other.Owner) && PlanetId == other.PlanetId &&
        PosX == other.PosX && PosY == other.PosY && PosZ == other.PosZ && Speed == other.Speed &&
        IsAvailable == other.IsAvailable && RepairEnabled == other.RepairEnabled &&
        DroneEnabled == other.DroneEnabled && Range == other.Range &&
        EnergySufficient == other.EnergySufficient;

    public override bool Equals(object obj) => obj is RepairOwnerSnapshot other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Owner.GetHashCode();
            hash = (hash * 397) ^ PlanetId;
            hash = (hash * 397) ^ PosX.GetHashCode();
            hash = (hash * 397) ^ PosY.GetHashCode();
            hash = (hash * 397) ^ PosZ.GetHashCode();
            hash = (hash * 397) ^ Speed.GetHashCode();
            hash = (hash * 397) ^ IsAvailable.GetHashCode();
            hash = (hash * 397) ^ RepairEnabled.GetHashCode();
            hash = (hash * 397) ^ DroneEnabled.GetHashCode();
            hash = (hash * 397) ^ Range.GetHashCode();
            hash = (hash * 397) ^ EnergySufficient.GetHashCode();
            return hash;
        }
    }

    public override string ToString() =>
        Owner + "|planet=" + PlanetId + "|speed=" + Speed + "|range=" + Range;
}

/// <summary>
/// One damaged building's host-accepted record for candidate filtering (TASKS.md A16, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The production adapter builds these from the real <c>combatStat/constructStat</c> pools without
/// mutating them (the same read-only contract as the A08 snapshot adapter); tests build them by
/// hand. <see cref="Target"/> is the full <see cref="ObjectKey"/> including generation: a caller
/// that names another generation of the same slot gets <see cref="ConstructionDispatchReason.WrongGeneration"/>,
/// never an implicit slide onto the new object.
/// </para>
/// <para>
/// Demand is intentionally not stored here. The caller supplies the vanilla
/// <c>GetRepairDroneDemand</c> value for the record so this file never copies the vanilla
/// multiplier: a game update cannot silently diverge from a formula hand-copied here (A15 contract).
/// </para>
/// </remarks>
public readonly struct RepairDamageSnapshot : IEquatable<RepairDamageSnapshot>
{
    public RepairDamageSnapshot(ObjectKey target, int planetId,
        int hp, int hpMax, float damageRate, int currentRepairers,
        bool hasCombatStat, bool hasConstructStat)
    {
        Target = target;
        PlanetId = planetId;
        Hp = hp;
        HpMax = hpMax;
        DamageRate = damageRate;
        CurrentRepairers = currentRepairers;
        HasCombatStat = hasCombatStat;
        HasConstructStat = hasConstructStat;
    }

    /// <summary>Full entity key including generation.</summary>
    public ObjectKey Target { get; }

    /// <summary>Planet the damaged entity lives on.</summary>
    public int PlanetId { get; }

    public int Hp { get; }

    public int HpMax { get; }

    public float DamageRate { get; }

    /// <summary>Live repair tasks holding a repairer slot on exactly this key (ledger-derived).</summary>
    public int CurrentRepairers { get; }

    public bool HasCombatStat { get; }

    public bool HasConstructStat { get; }

    public int MissingHp => HpMax - Hp;

    public bool Equals(RepairDamageSnapshot other) =>
        Target.Equals(other.Target) && PlanetId == other.PlanetId &&
        Hp == other.Hp && HpMax == other.HpMax && DamageRate == other.DamageRate &&
        CurrentRepairers == other.CurrentRepairers &&
        HasCombatStat == other.HasCombatStat && HasConstructStat == other.HasConstructStat;

    public override bool Equals(object obj) => obj is RepairDamageSnapshot other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Target.GetHashCode();
            hash = (hash * 397) ^ PlanetId;
            hash = (hash * 397) ^ Hp;
            hash = (hash * 397) ^ HpMax;
            hash = (hash * 397) ^ DamageRate.GetHashCode();
            hash = (hash * 397) ^ CurrentRepairers;
            hash = (hash * 397) ^ HasCombatStat.GetHashCode();
            hash = (hash * 397) ^ HasConstructStat.GetHashCode();
            return hash;
        }
    }

    public override string ToString() =>
        Target + "|hp=" + Hp + "/" + HpMax + "|repairers=" + CurrentRepairers;
}

/// <summary>
/// Pure repair candidate filter and scorer (TASKS.md A16).
/// </summary>
/// <remarks>
/// <para>
/// Every gate reads the candidate owner's planet, position, range, speed and switches — never the
/// host's local planet. There is no host-planet field on this type at all, so "不读主机本地
/// planet 决定远端资格" is a construction property, not a convention.
/// </para>
/// <para>
/// Check order is fixed and documented so coincident failures report deterministically:
/// shape → generation → damage record → owner eligibility → speed → range → demand coverage →
/// energy → budget. Demand coverage precedes energy and budget because "already enough repairers"
/// is the accurate diagnosis even when the owner is also dry or fully booked.
/// </para>
/// </remarks>
public static class HostConstructionDispatch
{
    /// <summary>Vanilla mecha repair gate: entityId == 0 and player.speed &gt; 20 skips repair.</summary>
    public const float MechaSpeedLimit = 20f;

    /// <summary>True when the reason means "may reserve a drone".</summary>
    public static bool IsDispatchable(ConstructionDispatchReason reason) =>
        reason == ConstructionDispatchReason.None;

    /// <summary>
    /// Decides whether one owner may repair one target this tick.
    /// </summary>
    /// <param name="owner">The candidate owner's host-accepted state.</param>
    /// <param name="target">The requested entity key, generation included.</param>
    /// <param name="damage">The host's damage record for the target's generation.</param>
    /// <param name="distanceSquared">Squared distance from the owner to the target.</param>
    /// <param name="demand">Vanilla <c>GetRepairDroneDemand</c> value for this record.</param>
    /// <param name="idleDrones">Idle drone slots in the owner's budget.</param>
    /// <returns><see cref="ConstructionDispatchReason.None"/> when dispatchable, else the blocking reason.</returns>
    public static ConstructionDispatchReason EvaluateRepair(
        in RepairOwnerSnapshot owner,
        in ObjectKey target,
        in RepairDamageSnapshot damage,
        float distanceSquared,
        float demand,
        int idleDrones)
    {
        // 1. Target and record shapes. A repair task only ever addresses Entity keys.
        if (!target.IsValid || target.Kind != PoolKind.Entity)
            return ConstructionDispatchReason.TargetNotReady;
        if (!damage.Target.IsValid || damage.Target.Kind != PoolKind.Entity)
            return ConstructionDispatchReason.TargetNotReady;
        if (damage.PlanetId <= 0 || damage.HpMax <= 0)
            return ConstructionDispatchReason.TargetNotReady;
        if (float.IsNaN(distanceSquared) || distanceSquared < 0f)
            return ConstructionDispatchReason.TargetNotReady;
        if (float.IsNaN(demand) || float.IsInfinity(demand))
            return ConstructionDispatchReason.TargetNotReady;

        // 2. Generation: the request must name exactly the record's generation. A new generation
        // of the same slot is a different object and is never repaired implicitly.
        if (!target.Epoch.Equals(damage.Target.Epoch) ||
            target.Scope != damage.Target.Scope ||
            target.NativeId != damage.Target.NativeId ||
            target.Generation != damage.Target.Generation)
            return ConstructionDispatchReason.WrongGeneration;
        if (target.Scope != damage.PlanetId)
            return ConstructionDispatchReason.TargetNotReady;

        // 3. Damage record presence. HasCombatStat=false is the canonical "host is whole" signal,
        // not an entity that does not exist.
        if (!damage.HasCombatStat || !damage.HasConstructStat)
            return ConstructionDispatchReason.NoDamageRecord;

        // 4. Owner eligibility. Unknown, offline, dead, virtual-server and stale-seat owners all
        // report Disabled; the caller distinguishes them in logs, not in the reason code.
        if (!owner.Owner.IsValid)
            return ConstructionDispatchReason.Disabled;
        if (!owner.IsAvailable || !owner.RepairEnabled || !owner.DroneEnabled)
            return ConstructionDispatchReason.Disabled;

        // 5. Vanilla speed gate, mecha owners only. Bases do not move.
        if (owner.IsMecha && owner.Speed > MechaSpeedLimit)
            return ConstructionDispatchReason.SpeedTooHigh;

        // 6. Planet and range, both from the candidate owner's state.
        if (owner.PlanetId != damage.PlanetId)
            return ConstructionDispatchReason.OutOfRange;
        if (owner.Range <= 0f)
            return ConstructionDispatchReason.OutOfRange;
        var reach = (double)owner.Range * owner.Range;
        if ((double)distanceSquared > reach)
            return ConstructionDispatchReason.OutOfRange;

        // 7. Demand coverage precedes resource gates: "already enough" stays accurate when the
        // owner is also dry or fully booked. The first drone always goes (CurrentRepairers == 0
        // never reports covered), matching DetermineLaunch's "repairerCount < 1 && num < 1 → 1".
        // Full HP is covered regardless of the repairer count: the stat should already be gone.
        if (damage.Hp >= damage.HpMax)
            return ConstructionDispatchReason.DemandCovered;
        if (damage.CurrentRepairers > 0 && demand <= 0f)
            return ConstructionDispatchReason.DemandCovered;

        // 8. Resources: energy before budget, matching the vanilla launch order (base energy is
        // checked before an idle drone is consumed).
        if (!owner.EnergySufficient)
            return ConstructionDispatchReason.NoEnergy;
        if (idleDrones <= 0)
            return ConstructionDispatchReason.NoIdleDrone;

        return ConstructionDispatchReason.None;
    }

    /// <summary>
    /// Squared distance between two host-accepted positions. Kept here so tests and the game
    /// adapter use the same arithmetic the range check applies.
    /// </summary>
    public static float DistanceSquared(float ax, float ay, float az, float bx, float by, float bz)
    {
        var dx = ax - bx;
        var dy = ay - by;
        var dz = az - bz;
        return dx * dx + dy * dy + dz * dz;
    }
}
