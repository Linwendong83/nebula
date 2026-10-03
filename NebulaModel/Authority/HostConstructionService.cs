#region

using System;
using System.Collections.Generic;
using NebulaModel.DataStructures;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Host-side battle-base construction state for candidate filtering (TASKS.md A16, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Players resolve through <see cref="HostPlayerRegistry"/>; bases resolve through this interface
/// so both owner kinds flow through the same <see cref="HostConstructionDispatch.EvaluateRepair"/>
/// gates. The production implementation (A17) reads the real construction modules and battle-base
/// energy; tests inject a fake dictionary. A missing source leaves every base
/// <see cref="ConstructionDispatchReason.Disabled"/> — fail-closed, never assumed eligible.
/// </para>
/// </remarks>
public interface IHostConstructionBaseSource
{
    /// <summary>Returns the host-accepted snapshot for a battle-base owner.</summary>
    bool TryGetBaseSnapshot(ConstructionOwnerKey baseOwner, out RepairOwnerSnapshot snapshot);
}

/// <summary>
/// Host-side battle-base build state for candidate filtering (TASKS.md A18, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Players resolve through <see cref="HostPlayerRegistry"/> (build switch); bases resolve through
/// this interface so both owner kinds flow through the same
/// <see cref="HostBuildDispatch.EvaluateBuild"/> gates. A missing source leaves every base
/// <see cref="ConstructionDispatchReason.Disabled"/> — fail-closed, never assumed eligible.
/// </para>
/// </remarks>
public interface IHostBuildBaseSource
{
    /// <summary>Returns the host-accepted build snapshot for a battle-base owner.</summary>
    bool TryGetBuildBaseSnapshot(ConstructionOwnerKey baseOwner, out BuildOwnerSnapshot snapshot);
}

/// <summary>
/// One dispatchable (owner, prebuild) pair, in evaluation order.
/// </summary>
public readonly struct BuildDispatchCandidate : IEquatable<BuildDispatchCandidate>
{
    public BuildDispatchCandidate(BuildOwnerSnapshot owner, BuildTargetSnapshot target,
        ConstructionTaskKind kind, float distanceSquared)
    {
        Owner = owner;
        Target = target;
        Kind = kind;
        DistanceSquared = distanceSquared;
    }

    public BuildOwnerSnapshot Owner { get; }

    public BuildTargetSnapshot Target { get; }

    public ConstructionTaskKind Kind { get; }

    public float DistanceSquared { get; }

    public bool Equals(BuildDispatchCandidate other) =>
        Owner.Equals(other.Owner) && Target.Equals(other.Target) &&
        Kind == other.Kind && DistanceSquared == other.DistanceSquared;

    public override bool Equals(object obj) => obj is BuildDispatchCandidate other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Owner.GetHashCode();
            hash = (hash * 397) ^ Target.GetHashCode();
            hash = (hash * 397) ^ (int)Kind;
            hash = (hash * 397) ^ DistanceSquared.GetHashCode();
            return hash;
        }
    }

    public override string ToString() => Owner.Owner + "->" + Target.Target + "|" + Kind;
}

/// <summary>
/// One dispatchable (owner, damage) pair, in evaluation order.
/// </summary>
public readonly struct RepairDispatchCandidate : IEquatable<RepairDispatchCandidate>
{
    public RepairDispatchCandidate(RepairOwnerSnapshot owner, RepairDamageSnapshot damage,
        float distanceSquared, float demand)
    {
        Owner = owner;
        Damage = damage;
        DistanceSquared = distanceSquared;
        Demand = demand;
    }

    public RepairOwnerSnapshot Owner { get; }

    public RepairDamageSnapshot Damage { get; }

    public float DistanceSquared { get; }

    public float Demand { get; }

    public bool Equals(RepairDispatchCandidate other) =>
        Owner.Equals(other.Owner) && Damage.Equals(other.Damage) &&
        DistanceSquared == other.DistanceSquared && Demand == other.Demand;

    public override bool Equals(object obj) => obj is RepairDispatchCandidate other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Owner.GetHashCode();
            hash = (hash * 397) ^ Damage.GetHashCode();
            hash = (hash * 397) ^ DistanceSquared.GetHashCode();
            hash = (hash * 397) ^ Demand.GetHashCode();
            return hash;
        }
    }

    public override string ToString() => Owner.Owner + "->" + Damage.Target;
}

/// <summary>
/// The host's repair candidate and scoring runtime: registry presence plus the A15 task ledger
/// (TASKS.md A16, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The service never reads the host's local planet, never copies the vanilla repair multiplier,
/// and never invents a damage record. Distances and <c>GetRepairDroneDemand</c> values arrive as
/// arguments — the production adapter computes them from the real pools (calling the vanilla
/// methods), tests supply canned numbers — so a game balance update cannot silently diverge from
/// a formula hand-copied into this file.
/// </para>
/// <para>
/// Reservation flows through the <see cref="ConstructionTaskLedger"/>: a dispatchable pair calls
/// <c>TryAddTask</c> once, and a duplicate live task reports
/// <see cref="ConstructionDispatchReason.DemandCovered"/> without moving any counter, which is
/// what makes "相同任务重放不增 repairerCount" hold across the dispatch path too. Not
/// thread-safe: only the frame boundary touches it, like the ledger.
/// </para>
/// </remarks>
public sealed class HostConstructionService
{
    private readonly AuthorityEpoch epoch;
    private readonly HostPlayerRegistry registry;
    private readonly ConstructionTaskLedger ledger;
    private readonly IHostConstructionBaseSource bases;
    private readonly IHostBuildBaseSource buildBases;

    public HostConstructionService(AuthorityEpoch epoch, HostPlayerRegistry registry,
        ConstructionTaskLedger ledger, IHostConstructionBaseSource bases = null,
        IHostBuildBaseSource buildBases = null)
    {
        if (!epoch.IsValid) throw new ArgumentException("The service needs a world epoch.", nameof(epoch));
        this.epoch = epoch;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        this.bases = bases;
        this.buildBases = buildBases;
    }

    public AuthorityEpoch Epoch => epoch;

    public HostPlayerRegistry Registry => registry;

    public ConstructionTaskLedger Ledger => ledger;

    /// <summary>
    /// Builds a player owner's snapshot from registry presence plus caller-supplied game fields.
    /// </summary>
    /// <param name="owner">Player owner key (persistent id plus session seat).</param>
    /// <param name="range">Vanilla build area of the player's construction module.</param>
    /// <param name="droneEnabled">Master construction-drone switch of the player's module.</param>
    /// <param name="energySufficient">Whether the player's mecha can pay flight and repair energy.</param>
    /// <returns>False with <see cref="ConstructionDispatchReason.Disabled"/> when the registry has no eligible presence.</returns>
    public bool TryGetPlayerOwnerSnapshot(ConstructionOwnerKey owner, float range,
        bool droneEnabled, bool energySufficient, out RepairOwnerSnapshot snapshot)
    {
        snapshot = default;
        if (!owner.IsValid || !owner.IsPlayer) return false;
        if (!registry.TryGetByPersistent(owner.PersistentId, out var state) || state == null)
            return false;
        if (!state.IsOnline || !state.CanOwnDroneTask)
            return false;
        if (state.SessionPlayerId != owner.SessionPlayerId)
            return false;
        snapshot = new RepairOwnerSnapshot(owner, state.PlanetId,
            state.Pose.X, state.Pose.Y, state.Pose.Z, state.Pose.Velocity,
            isAvailable: true, repairEnabled: state.RepairEnabled, droneEnabled: droneEnabled,
            range: range, energySufficient: energySufficient);
        return true;
    }

    /// <summary>
    /// Resolves any owner kind to its snapshot: players via the registry, bases via the base source.
    /// </summary>
    public bool TryGetOwnerSnapshot(ConstructionOwnerKey owner, float range,
        bool droneEnabled, bool energySufficient, out RepairOwnerSnapshot snapshot)
    {
        snapshot = default;
        if (!owner.IsValid) return false;
        if (owner.IsPlayer)
            return TryGetPlayerOwnerSnapshot(owner, range, droneEnabled, energySufficient, out snapshot);
        if (bases == null) return false;
        return bases.TryGetBaseSnapshot(owner, out snapshot);
    }

    /// <summary>Idle drone slots for one owner: the ledger budget, or 0 when the owner has none.</summary>
    public int GetIdleDrones(ConstructionOwnerKey owner)
    {
        if (ledger.TryGetBudget(owner, out var budget) && budget != null) return budget.Idle;
        return 0;
    }

    /// <summary>
    /// Evaluates one (owner, target) pair, reading the idle count from the ledger budget.
    /// </summary>
    public ConstructionDispatchReason EvaluateRepair(in RepairOwnerSnapshot owner,
        in ObjectKey target, in RepairDamageSnapshot damage,
        float distanceSquared, float demand)
    {
        if (!target.IsValid || !target.Epoch.Equals(epoch)) return ConstructionDispatchReason.WrongGeneration;
        return HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage,
            distanceSquared, demand, GetIdleDrones(owner.Owner));
    }

    /// <summary>
    /// Collects every dispatchable (owner, damage) pair, sorted deterministically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Damages sort by <see cref="ConstructionSelection.CompareRepairCandidates"/> (missing HP,
    /// damage rate, current repairers, tech multiplier, stable target order); owners within one
    /// damage sort by <see cref="ConstructionOwnerKey.StableCompare"/>. Two hosts that see the same
    /// inputs dispatch in the same order.
    /// </para>
    /// </remarks>
    /// <returns>The number of dispatchable candidates appended to <paramref name="outCandidates"/>.</returns>
    public int CollectDispatchableRepairs(
        IReadOnlyList<RepairDamageSnapshot> damages,
        IReadOnlyList<RepairOwnerSnapshot> owners,
        Func<RepairOwnerSnapshot, RepairDamageSnapshot, float> distanceProvider,
        Func<RepairOwnerSnapshot, RepairDamageSnapshot, float> demandProvider,
        Func<RepairDamageSnapshot, float> techMultiplierProvider,
        List<RepairDispatchCandidate> outCandidates)
    {
        if (outCandidates == null) throw new ArgumentNullException(nameof(outCandidates));
        if (damages == null || owners == null) return 0;
        if (distanceProvider == null || demandProvider == null) return 0;

        var accepted = new List<RepairDispatchCandidate>();
        for (var d = 0; d < damages.Count; d++)
        {
            var damage = damages[d];
            var eligible = new List<(RepairOwnerSnapshot Owner, float Distance, float Demand)>();
            for (var o = 0; o < owners.Count; o++)
            {
                var owner = owners[o];
                var distance = distanceProvider(owner, damage);
                var demand = demandProvider(owner, damage);
                var target = damage.Target;
                if (EvaluateRepair(in owner, in target, in damage, distance, demand) ==
                    ConstructionDispatchReason.None)
                    eligible.Add((owner, distance, demand));
            }
            eligible.Sort((left, right) =>
            {
                var leftOwner = left.Owner.Owner;
                var rightOwner = right.Owner.Owner;
                return ConstructionOwnerKey.StableCompare(in leftOwner, in rightOwner);
            });
            foreach (var entry in eligible)
                accepted.Add(new RepairDispatchCandidate(entry.Owner, damage, entry.Distance, entry.Demand));
        }

        accepted.Sort((left, right) =>
        {
            var techLeft = techMultiplierProvider != null ? techMultiplierProvider(left.Damage) : 1f;
            var techRight = techMultiplierProvider != null ? techMultiplierProvider(right.Damage) : 1f;
            var a = new ConstructionSelection.RepairCandidateInput(left.Damage.Target,
                left.Damage.Hp, left.Damage.HpMax, left.Damage.DamageRate,
                left.Damage.CurrentRepairers, techLeft);
            var b = new ConstructionSelection.RepairCandidateInput(right.Damage.Target,
                right.Damage.Hp, right.Damage.HpMax, right.Damage.DamageRate,
                right.Damage.CurrentRepairers, techRight);
            var byDamage = ConstructionSelection.CompareRepairCandidates(in a, in b);
            if (byDamage != 0) return byDamage;
            var leftKey = left.Owner.Owner;
            var rightKey = right.Owner.Owner;
            return ConstructionOwnerKey.StableCompare(in leftKey, in rightKey);
        });

        outCandidates.AddRange(accepted);
        return accepted.Count;
    }

    /// <summary>
    /// Evaluates and, when dispatchable, reserves one drone slot through the ledger.
    /// </summary>
    /// <returns>
    /// <see cref="ConstructionDispatchReason.None"/> with <see cref="ConstructionTaskError.None"/>
    /// on success. A failed evaluation returns its reason and touches no counter; a duplicate live
    /// task returns <see cref="ConstructionDispatchReason.DemandCovered"/> with
    /// <see cref="ConstructionTaskError.DuplicateTask"/> and the existing key.
    /// </returns>
    public ConstructionDispatchReason TryReserveRepair(in RepairOwnerSnapshot owner,
        in ObjectKey target, in RepairDamageSnapshot damage,
        float distanceSquared, float demand, long hostTick, HostTransactionId transaction,
        out ConstructionTaskKey key, out ConstructionTaskError ledgerError)
    {
        key = default;
        ledgerError = ConstructionTaskError.None;
        var reason = EvaluateRepair(in owner, in target, in damage, distanceSquared, demand);
        if (reason != ConstructionDispatchReason.None) return reason;

        ledgerError = ledger.TryAddTask(owner.Owner, target, ConstructionTaskKind.Repair,
            hostTick, transaction, out key);
        switch (ledgerError)
        {
            case ConstructionTaskError.None:
                return ConstructionDispatchReason.None;
            case ConstructionTaskError.DuplicateTask:
                return ConstructionDispatchReason.DemandCovered;
            case ConstructionTaskError.NoIdleDrone:
                return ConstructionDispatchReason.NoIdleDrone;
            case ConstructionTaskError.WrongEpoch:
                return ConstructionDispatchReason.WrongGeneration;
            case ConstructionTaskError.KindTargetMismatch:
            case ConstructionTaskError.InvalidTarget:
                return ConstructionDispatchReason.TargetNotReady;
            case ConstructionTaskError.InvalidOwner:
                return ConstructionDispatchReason.Disabled;
            default:
                return ConstructionDispatchReason.TargetNotReady;
        }
    }

    /// <summary>
    /// Builds a player owner's build snapshot from registry presence plus caller-supplied game fields.
    /// </summary>
    /// <param name="owner">Player owner key (persistent id plus session seat).</param>
    /// <param name="range">Vanilla build area of the player's construction module.</param>
    /// <param name="droneEnabled">Master construction-drone switch of the player's module.</param>
    /// <param name="energySufficient">Whether the player's mecha can pay flight and build energy.</param>
    /// <returns>False when the registry has no eligible presence or the build switch is off.</returns>
    public bool TryGetBuildOwnerSnapshot(ConstructionOwnerKey owner, float range,
        bool droneEnabled, bool energySufficient, out BuildOwnerSnapshot snapshot)
    {
        snapshot = default;
        if (!owner.IsValid || !owner.IsPlayer) return false;
        if (!registry.TryGetByPersistent(owner.PersistentId, out var state) || state == null)
            return false;
        if (!state.IsOnline || !state.CanOwnDroneTask)
            return false;
        if (state.SessionPlayerId != owner.SessionPlayerId)
            return false;
        snapshot = new BuildOwnerSnapshot(owner, state.PlanetId,
            state.Pose.X, state.Pose.Y, state.Pose.Z, state.Pose.Velocity,
            isAvailable: true, constructEnabled: state.BuildEnabled, droneEnabled: droneEnabled,
            range: range, energySufficient: energySufficient);
        return true;
    }

    /// <summary>
    /// Resolves any owner kind to its build snapshot: players via the registry, bases via the
    /// build base source. A missing base source leaves every base fail-closed Disabled.
    /// </summary>
    public bool TryGetBuildOwnerSnapshotAny(ConstructionOwnerKey owner, float range,
        bool droneEnabled, bool energySufficient, out BuildOwnerSnapshot snapshot)
    {
        snapshot = default;
        if (!owner.IsValid) return false;
        if (owner.IsPlayer)
            return TryGetBuildOwnerSnapshot(owner, range, droneEnabled, energySufficient, out snapshot);
        if (buildBases == null) return false;
        return buildBases.TryGetBuildBaseSnapshot(owner, out snapshot);
    }

    /// <summary>
    /// Evaluates one (owner, prebuild) pair, reading the idle count from the ledger budget.
    /// </summary>
    public ConstructionDispatchReason EvaluateBuild(in BuildOwnerSnapshot owner,
        in ObjectKey target, in BuildTargetSnapshot snapshot, ConstructionTaskKind kind,
        float distanceSquared)
    {
        if (!target.IsValid || !target.Epoch.Equals(epoch)) return ConstructionDispatchReason.WrongGeneration;
        return HostBuildDispatch.EvaluateBuild(in owner, in target, in snapshot, kind,
            distanceSquared, GetIdleDrones(owner.Owner));
    }

    /// <summary>
    /// Collects every dispatchable (owner, prebuild) pair, sorted deterministically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per prebuild the placer wins when dispatchable
    /// (<see cref="ConstructionSelection.TryPickPlacerFirst"/>, the shipped "放置者可用时优先"
    /// rule); otherwise every dispatchable owner is emitted, ordered by the shipped
    /// <see cref="BuildCandidateScore"/> distance/base preference with the stable owner order as
    /// the tie-break. Prebuilds sort by stable target order (scope, slot, generation). Two hosts
    /// that see the same inputs dispatch in the same order.
    /// </para>
    /// </remarks>
    /// <returns>The number of dispatchable candidates appended to <paramref name="outCandidates"/>.</returns>
    public int CollectDispatchableBuilds(
        IReadOnlyList<BuildTargetSnapshot> targets,
        IReadOnlyList<BuildOwnerSnapshot> owners,
        Func<BuildOwnerSnapshot, BuildTargetSnapshot, float> distanceProvider,
        Func<BuildTargetSnapshot, ConstructionOwnerKey?> placerProvider,
        List<BuildDispatchCandidate> outCandidates)
    {
        if (outCandidates == null) throw new ArgumentNullException(nameof(outCandidates));
        if (targets == null || owners == null) return 0;
        if (distanceProvider == null) return 0;

        var accepted = new List<BuildDispatchCandidate>();
        for (var t = 0; t < targets.Count; t++)
        {
            var snapshot = targets[t];
            var kind = snapshot.IsDestroyed ? ConstructionTaskKind.Reconstruct : ConstructionTaskKind.Build;
            var eligible = new List<(BuildOwnerSnapshot Owner, float Distance)>();
            for (var o = 0; o < owners.Count; o++)
            {
                var owner = owners[o];
                var distance = distanceProvider(owner, snapshot);
                var target = snapshot.Target;
                if (EvaluateBuild(in owner, in target, in snapshot, kind, distance) ==
                    ConstructionDispatchReason.None)
                    eligible.Add((owner, distance));
            }
            if (eligible.Count == 0) continue;

            // Placer-first: the player who placed the prebuild serves it when available; other
            // owners and battle bases are only fallbacks. Availability filtering already happened
            // above; this fixes priority without reading any game pool.
            ConstructionOwnerKey? placer = placerProvider != null ? placerProvider(snapshot) : null;
            if (placer.HasValue && placer.Value.IsValid)
            {
                var placerKey = placer.Value;
                var placerIndex = -1;
                for (var i = 0; i < eligible.Count; i++)
                {
                    if (eligible[i].Owner.Owner.Equals(placerKey))
                    {
                        placerIndex = i;
                        break;
                    }
                }
                if (placerIndex >= 0)
                {
                    var entry = eligible[placerIndex];
                    accepted.Add(new BuildDispatchCandidate(entry.Owner, snapshot, kind, entry.Distance));
                    continue;
                }
            }

            eligible.Sort((left, right) =>
            {
                var leftScore = BuildCandidateScore.Calculate(
                    left.Owner.Owner.IsPlayer ? BuildOwnerKind.Player : BuildOwnerKind.Base,
                    left.Distance, left.Owner.Range);
                var rightScore = BuildCandidateScore.Calculate(
                    right.Owner.Owner.IsPlayer ? BuildOwnerKind.Player : BuildOwnerKind.Base,
                    right.Distance, right.Owner.Range);
                var byScore = rightScore.CompareTo(leftScore);
                if (byScore != 0) return byScore;
                var leftKey = left.Owner.Owner;
                var rightKey = right.Owner.Owner;
                return ConstructionOwnerKey.StableCompare(in leftKey, in rightKey);
            });
            foreach (var entry in eligible)
                accepted.Add(new BuildDispatchCandidate(entry.Owner, snapshot, kind, entry.Distance));
        }

        accepted.Sort((left, right) =>
        {
            var byScope = left.Target.Target.Scope.CompareTo(right.Target.Target.Scope);
            if (byScope != 0) return byScope;
            var byNative = left.Target.Target.NativeId.CompareTo(right.Target.Target.NativeId);
            if (byNative != 0) return byNative;
            var byGen = left.Target.Target.Generation.CompareTo(right.Target.Target.Generation);
            if (byGen != 0) return byGen;
            var leftKey = left.Owner.Owner;
            var rightKey = right.Owner.Owner;
            return ConstructionOwnerKey.StableCompare(in leftKey, in rightKey);
        });

        outCandidates.AddRange(accepted);
        return accepted.Count;
    }

    /// <summary>
    /// Evaluates and, when dispatchable, reserves one drone slot through the ledger.
    /// </summary>
    /// <returns>
    /// <see cref="ConstructionDispatchReason.None"/> with <see cref="ConstructionTaskError.None"/>
    /// on success. A failed evaluation returns its reason and touches no counter; a duplicate live
    /// task returns <see cref="ConstructionDispatchReason.DemandCovered"/> with
    /// <see cref="ConstructionTaskError.DuplicateTask"/> and the existing key.
    /// </returns>
    public ConstructionDispatchReason TryReserveBuild(in BuildOwnerSnapshot owner,
        in ObjectKey target, in BuildTargetSnapshot snapshot, ConstructionTaskKind kind,
        float distanceSquared, long hostTick, HostTransactionId transaction,
        out ConstructionTaskKey key, out ConstructionTaskError ledgerError)
    {
        key = default;
        ledgerError = ConstructionTaskError.None;
        var reason = EvaluateBuild(in owner, in target, in snapshot, kind, distanceSquared);
        if (reason != ConstructionDispatchReason.None) return reason;

        ledgerError = ledger.TryAddTask(owner.Owner, target, kind,
            hostTick, transaction, out key);
        switch (ledgerError)
        {
            case ConstructionTaskError.None:
                return ConstructionDispatchReason.None;
            case ConstructionTaskError.DuplicateTask:
                return ConstructionDispatchReason.DemandCovered;
            case ConstructionTaskError.NoIdleDrone:
                return ConstructionDispatchReason.NoIdleDrone;
            case ConstructionTaskError.WrongEpoch:
                return ConstructionDispatchReason.WrongGeneration;
            case ConstructionTaskError.KindTargetMismatch:
            case ConstructionTaskError.InvalidTarget:
                return ConstructionDispatchReason.TargetNotReady;
            case ConstructionTaskError.InvalidOwner:
                return ConstructionDispatchReason.Disabled;
            default:
                return ConstructionDispatchReason.TargetNotReady;
        }
    }
}
