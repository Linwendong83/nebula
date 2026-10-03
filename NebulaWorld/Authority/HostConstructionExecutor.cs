#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;

#endregion

namespace NebulaWorld.Authority;

/// <summary>
/// Narrow repair writer: one validated task tick performs one vanilla repair (A17 seam).
/// </summary>
/// <remarks>
/// <para>
/// Tests inject a fake that records ratios; the production adapter validates the full
/// <see cref="ObjectKey"/> (epoch, scope, slot, generation) against the live damage record
/// before calling the vanilla <c>Repair</c>, then reports whether the target is done. The
/// writer never invents a damage record, never slides onto the next generation and never
/// touches <c>repairerCount</c>: the ledger owns the count.
/// </para>
/// </remarks>
public interface IHostRepairWriter
{
    /// <summary>
    /// Performs one working tick for <paramref name="target"/> with the owner's legal ratio.
    /// </summary>
    /// <param name="target">Full entity key including generation.</param>
    /// <param name="nativeEntityId">Host entity slot, for the vanilla call.</param>
    /// <param name="ratio">Owner-real energy ratio for this tick (0..1).</param>
    /// <param name="hostTick">Host tick of the execution.</param>
    /// <param name="finished">True when the target needs no further working ticks.</param>
    /// <returns>False when the repair could not be performed (target already gone).</returns>
    bool TryRepair(in ObjectKey target, int nativeEntityId, float ratio, long hostTick,
        out bool finished);
}

/// <summary>
/// Narrow build writer: one validated task tick performs one vanilla build step (A18 seam).
/// </summary>
/// <remarks>
/// <para>
/// Tests inject a fake that records ratios; the production adapter validates the full
/// <see cref="ObjectKey"/> (epoch, scope, slot, generation) against the live prebuild record
/// before stepping the vanilla build, then reports whether the site needs no further working
/// ticks. The writer never invents a prebuild record, never slides onto the next generation and
/// never consumes materials: the ledger spends those once at finish.
/// </para>
/// </remarks>
public interface IHostBuildWriter
{
    /// <summary>
    /// Performs one working tick for <paramref name="target"/> with the owner's legal ratio.
    /// </summary>
    /// <param name="target">Full prebuild key including generation.</param>
    /// <param name="nativePrebuildId">Host prebuild slot, for the vanilla call.</param>
    /// <param name="ratio">Owner-real energy ratio for this tick (0..1).</param>
    /// <param name="hostTick">Host tick of the execution.</param>
    /// <param name="finished">True when the target needs no further working ticks.</param>
    /// <returns>False when the build could not be performed (target already gone).</returns>
    bool TryBuild(in ObjectKey target, int nativePrebuildId, float ratio, long hostTick,
        out bool finished);
}

/// <summary>
/// One host build tick's observable outcome, for tests and logs (A18).
/// </summary>
public readonly struct HostBuildTickSummary
{
    public HostBuildTickSummary(int dispatched, int advanced, int builtTasks, long materialsSpent,
        int completed, int cancelled)
    {
        Dispatched = dispatched;
        Advanced = advanced;
        BuiltTasks = builtTasks;
        MaterialsSpent = materialsSpent;
        Completed = completed;
        Cancelled = cancelled;
    }

    public int Dispatched { get; }

    public int Advanced { get; }

    public int BuiltTasks { get; }

    public long MaterialsSpent { get; }

    public int Completed { get; }

    public int Cancelled { get; }

    public override string ToString() =>
        "dispatch=" + Dispatched + "|advance=" + Advanced + "|built=" + BuiltTasks +
        "|materials=" + MaterialsSpent + "|done=" + Completed + "|cancel=" + Cancelled;
}

/// <summary>
/// One host repair tick's observable outcome, for tests and logs (A17).
/// </summary>
public readonly struct HostRepairTickSummary
{
    public HostRepairTickSummary(int dispatched, int advanced, int repairedTasks, int totalDelta,
        int completed, int cancelled)
    {
        Dispatched = dispatched;
        Advanced = advanced;
        RepairedTasks = repairedTasks;
        TotalDelta = totalDelta;
        Completed = completed;
        Cancelled = cancelled;
    }

    public int Dispatched { get; }

    public int Advanced { get; }

    public int RepairedTasks { get; }

    public int TotalDelta { get; }

    public int Completed { get; }

    public int Cancelled { get; }

    public override string ToString() =>
        "dispatch=" + Dispatched + "|advance=" + Advanced + "|repaired=" + RepairedTasks +
        "|delta=" + TotalDelta + "|done=" + Completed + "|cancel=" + Cancelled;
}

/// <summary>
/// The host's repair and build execution runtime: ledger tasks plus per-owner real energy (A17/A18).
/// </summary>
/// <remarks>
/// <para>
/// Owns the DESIGN 8.2 steps 1-6 for repairs and for builds: candidate filtering comes from
/// <see cref="HostConstructionService"/> (A16 repairs, A18 builds), budgeting and lifecycle from
/// <see cref="ConstructionTaskLedger"/> (A15), energy truth from
/// <see cref="HostResourceLedger"/> (A09/A10), the single vanilla <c>Repair</c> call per
/// working repair drone goes through <see cref="IHostRepairWriter"/> with the task's exact key,
/// and the single vanilla build step per working build drone goes through
/// <see cref="IHostBuildWriter"/> the same way. Build materials are spent once through the
/// ledger when the writer reports the site finished; a shortfall cancels with
/// <see cref="ConstructionCancelReason.InsufficientStock"/> instead of completing.
/// The vanilla <c>DetermineLaunch</c>, <c>FindNextRepair</c> self-assignment, dummy-energy remote
/// pool, client build-launch facts and all-idle <c>repairerCount</c> reset never run in the new
/// mode (<see cref="HostConstructionPolicy"/>); this executor is the only dispatcher.
/// </para>
/// <para>
/// Damage/build snapshots, owner snapshots, distances, demands, recipes and build costs arrive as
/// arguments — the production adapter computes them from the real pools, tests supply canned
/// numbers — so this file never copies a vanilla multiplier and never reads <c>GameMain</c>.
/// Frame-thread only, like the ledger.
/// </para>
/// </remarks>
public sealed class HostConstructionExecutor
{
    private readonly AuthorityEpoch epoch;
    private readonly HostPlayerRegistry registry;
    private readonly ConstructionTaskLedger tasks;
    private readonly HostConstructionService service;
    private readonly HostResourceLedger resources;

    public HostConstructionExecutor(AuthorityEpoch epoch, HostPlayerRegistry registry,
        ConstructionTaskLedger tasks, HostConstructionService service, HostResourceLedger resources)
    {
        if (!epoch.IsValid) throw new ArgumentException("The executor needs a world epoch.", nameof(epoch));
        this.epoch = epoch;
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        this.service = service ?? throw new ArgumentNullException(nameof(service));
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public AuthorityEpoch Epoch => epoch;

    public long TicksTotal { get; private set; }

    public long DispatchedTotal { get; private set; }

    public long RepairTicksTotal { get; private set; }

    public long TotalRepairDelta { get; private set; }

    public long BuiltTasksTotal { get; private set; }

    public long MaterialsSpentTotal { get; private set; }

    /// <summary>
    /// Runs one host repair tick: lifecycle, dispatch, advance, working repairs.
    /// </summary>
    public HostRepairTickSummary Tick(long hostTick,
        IReadOnlyList<RepairDamageSnapshot> damages,
        IReadOnlyList<RepairOwnerSnapshot> owners,
        Func<RepairOwnerSnapshot, RepairDamageSnapshot, float> distanceProvider,
        Func<RepairOwnerSnapshot, RepairDamageSnapshot, float> demandProvider,
        Func<RepairDamageSnapshot, float> techProvider,
        Func<ConstructionOwnerKey, double> repairCostProvider,
        float baseRepairPerTick, float globalScale,
        IHostRepairWriter writer)
    {
        if (hostTick < 0) throw new ArgumentOutOfRangeException(nameof(hostTick));
        TicksTotal++;

        var damageByTarget = new Dictionary<ObjectKey, RepairDamageSnapshot>();
        if (damages != null)
        {
            foreach (var damage in damages)
            {
                if (damage.Target.IsValid) damageByTarget[damage.Target] = damage;
            }
        }
        var ownerByKey = new Dictionary<ConstructionOwnerKey, RepairOwnerSnapshot>();
        if (owners != null)
        {
            foreach (var owner in owners)
            {
                if (owner.Owner.IsValid) ownerByKey[owner.Owner] = owner;
            }
        }

        var cancelled = 0;
        var completed = 0;

        // 1. Lifecycle for live repair tasks: owner loss cancels, target loss cancels,
        // full HP moves Working toward Returning (or cancels pre-work with DemandCovered).
        foreach (var task in tasks.CollectLiveTasks())
        {
            if (task.Kind != ConstructionTaskKind.Repair)
                continue;
            if (!damageByTarget.TryGetValue(task.Target, out var damage))
            {
                if (tasks.TryCancel(task.Key, hostTick, ConstructionCancelReason.TargetDestroyed) ==
                    ConstructionTaskError.None)
                    cancelled++;
                continue;
            }
            var (fate, cancel) = HostDroneLifecycle.TargetFate(task.Target, damage);
            if (fate == HostRepairTaskFate.Cancel)
            {
                if (tasks.TryCancel(task.Key, hostTick, cancel) == ConstructionTaskError.None)
                    cancelled++;
                continue;
            }
            if (fate == HostRepairTaskFate.CompleteRepair)
            {
                if (task.Stage == ConstructionTaskStage.Working)
                {
                    if (tasks.TryAdvance(task.Key, ConstructionTaskStage.Returning, hostTick) ==
                        ConstructionTaskError.None)
                        completed++;
                }
                else if (task.Stage == ConstructionTaskStage.Returning)
                {
                    if (tasks.TryAdvance(task.Key, ConstructionTaskStage.Completed, hostTick) ==
                        ConstructionTaskError.None)
                        completed++;
                }
                else
                {
                    if (tasks.TryCancel(task.Key, hostTick, ConstructionCancelReason.DemandCovered) ==
                        ConstructionTaskError.None)
                        cancelled++;
                }
                continue;
            }
            if (!ownerByKey.TryGetValue(task.Owner, out var ownerSnap))
            {
                if (tasks.TryCancel(task.Key, hostTick, ConstructionCancelReason.OwnerOffline) ==
                    ConstructionTaskError.None)
                    cancelled++;
                continue;
            }
            var ownerCancel = HostDroneLifecycle.OwnerCancelReason(ownerSnap, damage.PlanetId);
            if (ownerCancel.HasValue)
            {
                if (tasks.TryCancel(task.Key, hostTick, ownerCancel.Value) ==
                    ConstructionTaskError.None)
                    cancelled++;
            }
        }

        // 2. Dispatch: refresh repairer counts from the ledger so the second drone for the
        // same target sees the first one's reservation (no over-dispatch beyond demand).
        var dispatched = 0;
        if (damages != null && owners != null && distanceProvider != null && demandProvider != null &&
            writer != null)
        {
            var refreshed = RefreshDamageCounts(damages);
            var candidates = new List<RepairDispatchCandidate>();
            service.CollectDispatchableRepairs(refreshed, owners, distanceProvider,
                demandProvider, techProvider, candidates);
            foreach (var candidate in candidates)
            {
                var freshDamage = candidate.Damage;
                if (tasks.GetRepairerCount(freshDamage.Target) != freshDamage.CurrentRepairers)
                {
                    freshDamage = new RepairDamageSnapshot(freshDamage.Target, freshDamage.PlanetId,
                        freshDamage.Hp, freshDamage.HpMax, freshDamage.DamageRate,
                        tasks.GetRepairerCount(freshDamage.Target),
                        freshDamage.HasCombatStat, freshDamage.HasConstructStat);
                }
                var reason = service.TryReserveRepair(candidate.Owner, freshDamage.Target,
                    freshDamage, candidate.DistanceSquared, candidate.Demand, hostTick, default,
                    out _, out _);
                if (reason == ConstructionDispatchReason.None)
                {
                    dispatched++;
                    DispatchedTotal++;
                }
                else if (reason == ConstructionDispatchReason.DemandCovered ||
                         reason == ConstructionDispatchReason.NoIdleDrone)
                {
                    // Covered or fully booked: later candidates for the same target in this
                    // tick would report the same, so keep scanning other targets.
                    continue;
                }
            }
        }

        // 3. Advance one stage per tick (Reserved->Launching->Travelling->Working, then
        // Returning->Completed). At most one transition per task per tick: tasks the lifecycle
        // or dispatch just touched (LastHostTick == hostTick) wait for the next tick, which is
        // what keeps Reserved from reaching Working in the same tick it was created and what
        // keeps Working->Returning from completing in the same tick.
        var advanced = 0;
        foreach (var task in tasks.CollectLiveTasks())
        {
            if (task.Kind != ConstructionTaskKind.Repair || task.IsTerminal)
                continue;
            if (task.LastHostTick == hostTick)
                continue;
            ConstructionTaskStage? next = task.Stage switch
            {
                ConstructionTaskStage.Reserved => ConstructionTaskStage.Launching,
                ConstructionTaskStage.Launching => ConstructionTaskStage.Travelling,
                ConstructionTaskStage.Travelling => ConstructionTaskStage.Working,
                ConstructionTaskStage.Returning => ConstructionTaskStage.Completed,
                _ => null
            };
            if (next.HasValue &&
                tasks.TryAdvance(task.Key, next.Value, hostTick) == ConstructionTaskError.None)
                advanced++;
        }

        // 4. Working repairs: one ledger-spent tick per drone, one writer call per drone.
        var repairedTasks = 0;
        var totalDelta = 0;
        if (writer != null && baseRepairPerTick > 0f && globalScale > 0f)
        {
            foreach (var task in tasks.CollectLiveTasks())
            {
                if (task.Kind != ConstructionTaskKind.Repair ||
                    task.Stage != ConstructionTaskStage.Working)
                    continue;
                if (!damageByTarget.TryGetValue(task.Target, out _))
                    continue;
                var cost = repairCostProvider != null ? repairCostProvider(task.Owner) : 0.0;
                var costIsFree = double.IsNaN(cost) || double.IsInfinity(cost) || cost <= 0.0;
                var ratio = costIsFree ? 1f : RatioFor(task.Owner, cost);
                var toReserve = costIsFree ? 0.0 : HostRepairEnergy.AmountToReserve(AvailableFor(task.Owner), cost);
                HostTransactionId tx = default;
                var committed = false;
                if (toReserve > 0.0)
                {
                    tx = resources.BeginHostTransaction();
                    var key = LedgerKeyFor(task.Owner, out var kind);
                    var revision = key.IsValid ? resources.RevisionOf(key) : 0;
                    var reserve = kind == LedgerResourceKind.CoreEnergy ||
                                  kind == LedgerResourceKind.BaseEnergy
                        ? resources.TryReserveDouble(tx, key.Owner, kind, toReserve, revision,
                            out _, out _)
                        : LedgerReserveCode.WrongKind;
                    if (reserve == LedgerReserveCode.Ok)
                        committed = resources.CommitHostTransaction(tx, CommandResultCode.Applied,
                            hostTick, out _);
                    else
                        resources.AbortHostTransaction(tx);
                    if (!committed)
                    {
                        // Lost a race inside the same tick: pause with ratio zero, retry next tick.
                        ratio = 0f;
                    }
                }
                else if (!costIsFree && AvailableFor(task.Owner) <= 0.0)
                {
                    ratio = 0f;
                }
                var delta = HostRepairEnergy.ComputeRepairDelta(baseRepairPerTick, ratio, globalScale);
                bool finished;
                if (!writer.TryRepair(task.Target, task.Target.NativeId, ratio, hostTick,
                        out finished))
                    continue;
                repairedTasks++;
                RepairTicksTotal++;
                totalDelta += delta;
                TotalRepairDelta += delta;
                if (finished &&
                    tasks.TryAdvance(task.Key, ConstructionTaskStage.Returning, hostTick) ==
                    ConstructionTaskError.None)
                    completed++;
            }
        }

        return new HostRepairTickSummary(dispatched, advanced, repairedTasks, totalDelta,
            completed, cancelled);
    }

    /// <summary>
    /// Runs one host build tick: lifecycle, dispatch, advance, working builds.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="Tick"/> for the build side (TASKS.md A18): one builder per prebuild
    /// (the ledger form of the legacy claim exclusivity), placer-first dispatch, per-tick flight
    /// energy through the ledger, one <see cref="IHostBuildWriter"/> call per working drone, and
    /// a single material spend when the writer reports the site finished. A material shortfall
    /// cancels with <see cref="ConstructionCancelReason.InsufficientStock"/> (R11) instead of
    /// completing, so a dry owner never mints a building.
    /// </remarks>
    public HostBuildTickSummary TickBuild(long hostTick,
        IReadOnlyList<BuildTargetSnapshot> targets,
        IReadOnlyList<BuildOwnerSnapshot> owners,
        Func<BuildOwnerSnapshot, BuildTargetSnapshot, float> distanceProvider,
        Func<BuildTargetSnapshot, ConstructionOwnerKey?> placerProvider,
        Func<BuildOwnerSnapshot, BuildTargetSnapshot, BuildMaterialRequirement> materialProvider,
        Func<ConstructionOwnerKey, double> buildCostProvider,
        IHostBuildWriter writer)
    {
        if (hostTick < 0) throw new ArgumentOutOfRangeException(nameof(hostTick));
        TicksTotal++;

        var snapshotByTarget = new Dictionary<ObjectKey, BuildTargetSnapshot>();
        if (targets != null)
        {
            foreach (var snapshot in targets)
            {
                if (snapshot.Target.IsValid) snapshotByTarget[snapshot.Target] = snapshot;
            }
        }
        var ownerByKey = new Dictionary<ConstructionOwnerKey, BuildOwnerSnapshot>();
        if (owners != null)
        {
            foreach (var owner in owners)
            {
                if (owner.Owner.IsValid) ownerByKey[owner.Owner] = owner;
            }
        }

        var cancelled = 0;
        var completed = 0;

        // 1. Lifecycle for live build/reconstruct tasks: owner loss cancels, target loss or
        // generation change cancels, an already-whole ruin completes.
        foreach (var task in tasks.CollectLiveTasks())
        {
            if (task.Kind != ConstructionTaskKind.Build &&
                task.Kind != ConstructionTaskKind.Reconstruct)
                continue;
            if (!snapshotByTarget.TryGetValue(task.Target, out var snapshot))
            {
                var (missingFate, missingCancel) = HostBuildLifecycle.TargetFate(
                    task.Target, task.Kind, task.Stage, false, default);
                if (missingFate == HostBuildTaskFate.Cancel)
                {
                    if (tasks.TryCancel(task.Key, hostTick, missingCancel) ==
                        ConstructionTaskError.None)
                        cancelled++;
                }
                else
                {
                    completed += CompleteBuildTask(task, hostTick);
                }
                continue;
            }
            var (fate, cancel) = HostBuildLifecycle.TargetFate(
                task.Target, task.Kind, task.Stage, true, snapshot);
            if (fate == HostBuildTaskFate.Cancel)
            {
                if (tasks.TryCancel(task.Key, hostTick, cancel) == ConstructionTaskError.None)
                    cancelled++;
                continue;
            }
            if (fate == HostBuildTaskFate.CompleteBuild)
            {
                completed += CompleteBuildTask(task, hostTick);
                continue;
            }
            if (!ownerByKey.TryGetValue(task.Owner, out var ownerSnap))
            {
                if (tasks.TryCancel(task.Key, hostTick, ConstructionCancelReason.OwnerOffline) ==
                    ConstructionTaskError.None)
                    cancelled++;
                continue;
            }
            var ownerCancel = HostBuildLifecycle.OwnerCancelReason(ownerSnap, snapshot.PlanetId);
            if (ownerCancel.HasValue)
            {
                if (tasks.TryCancel(task.Key, hostTick, ownerCancel.Value) ==
                    ConstructionTaskError.None)
                    cancelled++;
            }
        }

        // 2. Dispatch: refresh builder counts from the ledger so the second owner for the same
        // prebuild sees the first one's reservation (one builder per prebuild, like the claim).
        var dispatched = 0;
        if (targets != null && owners != null && distanceProvider != null && writer != null)
        {
            var refreshed = RefreshBuildCounts(targets);
            var candidates = new List<BuildDispatchCandidate>();
            service.CollectDispatchableBuilds(refreshed, owners, distanceProvider,
                placerProvider, candidates);
            foreach (var candidate in candidates)
            {
                var freshTarget = candidate.Target;
                var liveBuilders = tasks.GetBuildTaskCount(freshTarget.Target);
                if (liveBuilders != freshTarget.CurrentBuilders)
                {
                    freshTarget = new BuildTargetSnapshot(freshTarget.Target, freshTarget.PlanetId,
                        freshTarget.ItemRequired, freshTarget.IsDestroyed, liveBuilders);
                }
                var reason = service.TryReserveBuild(candidate.Owner, freshTarget.Target,
                    freshTarget, candidate.Kind, candidate.DistanceSquared, hostTick, default,
                    out _, out _);
                if (reason == ConstructionDispatchReason.None)
                {
                    dispatched++;
                    DispatchedTotal++;
                }
                else if (reason == ConstructionDispatchReason.DemandCovered ||
                          reason == ConstructionDispatchReason.NoIdleDrone)
                {
                    // Covered or fully booked: later candidates for the same prebuild in this
                    // tick would report the same, so keep scanning other prebuilds.
                    continue;
                }
            }
        }

        // 3. Advance one stage per tick, same single-step guard as repairs: tasks the lifecycle
        // or dispatch just touched (LastHostTick == hostTick) wait for the next tick.
        var advanced = 0;
        foreach (var task in tasks.CollectLiveTasks())
        {
            if ((task.Kind != ConstructionTaskKind.Build &&
                 task.Kind != ConstructionTaskKind.Reconstruct) || task.IsTerminal)
                continue;
            if (task.LastHostTick == hostTick)
                continue;
            ConstructionTaskStage? next = task.Stage switch
            {
                ConstructionTaskStage.Reserved => ConstructionTaskStage.Launching,
                ConstructionTaskStage.Launching => ConstructionTaskStage.Travelling,
                ConstructionTaskStage.Travelling => ConstructionTaskStage.Working,
                ConstructionTaskStage.Returning => ConstructionTaskStage.Completed,
                _ => null
            };
            if (next.HasValue &&
                tasks.TryAdvance(task.Key, next.Value, hostTick) == ConstructionTaskError.None)
                advanced++;
        }

        // 4. Working builds: one ledger-spent energy tick per drone, one writer call per drone,
        // and a single material spend when the writer reports the site finished.
        var builtTasks = 0;
        long materialsSpent = 0;
        if (writer != null)
        {
            foreach (var task in tasks.CollectLiveTasks())
            {
                if ((task.Kind != ConstructionTaskKind.Build &&
                     task.Kind != ConstructionTaskKind.Reconstruct) ||
                    task.Stage != ConstructionTaskStage.Working)
                    continue;
                if (!snapshotByTarget.TryGetValue(task.Target, out var snapshot))
                    continue;
                if (!ownerByKey.TryGetValue(task.Owner, out var ownerSnap))
                    continue;
                var cost = buildCostProvider != null ? buildCostProvider(task.Owner) : 0.0;
                var costIsFree = double.IsNaN(cost) || double.IsInfinity(cost) || cost <= 0.0;
                var ratio = costIsFree ? 1f : RatioFor(task.Owner, cost);
                var toReserve = costIsFree ? 0.0 : HostRepairEnergy.AmountToReserve(AvailableFor(task.Owner), cost);
                var committed = false;
                if (toReserve > 0.0)
                {
                    var tx = resources.BeginHostTransaction();
                    var key = LedgerKeyFor(task.Owner, out var kind);
                    var revision = key.IsValid ? resources.RevisionOf(key) : 0;
                    var reserve = kind == LedgerResourceKind.CoreEnergy ||
                                  kind == LedgerResourceKind.BaseEnergy
                        ? resources.TryReserveDouble(tx, key.Owner, kind, toReserve, revision,
                            out _, out _)
                        : LedgerReserveCode.WrongKind;
                    if (reserve == LedgerReserveCode.Ok)
                        committed = resources.CommitHostTransaction(tx, CommandResultCode.Applied,
                            hostTick, out _);
                    else
                        resources.AbortHostTransaction(tx);
                    if (!committed)
                    {
                        // Lost a race inside the same tick: pause with ratio zero, retry next tick.
                        ratio = 0f;
                    }
                }
                else if (!costIsFree && AvailableFor(task.Owner) <= 0.0)
                {
                    ratio = 0f;
                }
                bool finished;
                if (!writer.TryBuild(task.Target, task.Target.NativeId, ratio, hostTick,
                        out finished))
                    continue;
                builtTasks++;
                BuiltTasksTotal++;
                if (!finished)
                    continue;
                if (SpendBuildMaterials(task.Owner, snapshot, ownerSnap, materialProvider,
                        hostTick, out var spent))
                {
                    materialsSpent += spent;
                    MaterialsSpentTotal += spent;
                    if (tasks.TryAdvance(task.Key, ConstructionTaskStage.Returning, hostTick) ==
                        ConstructionTaskError.None)
                        completed++;
                }
                else
                {
                    if (tasks.TryCancel(task.Key, hostTick,
                            ConstructionCancelReason.InsufficientStock) ==
                        ConstructionTaskError.None)
                        cancelled++;
                }
            }
        }

        return new HostBuildTickSummary(dispatched, advanced, builtTasks, materialsSpent,
            completed, cancelled);
    }

    /// <summary>
    /// Display states for the TaskBatch presentation: every live build and repair task, no
    /// protected numbers. The target key kind (Prebuild vs Entity) tells builds from repairs.
    /// </summary>
    public List<HostDroneDisplayState> CollectDisplayStates()
    {
        var states = new List<HostDroneDisplayState>();
        foreach (var task in tasks.CollectLiveTasks())
        {
            if (task.Kind != ConstructionTaskKind.Repair &&
                task.Kind != ConstructionTaskKind.Build &&
                task.Kind != ConstructionTaskKind.Reconstruct)
                continue;
            states.Add(new HostDroneDisplayState(task.Key, task.Owner, task.Target, task.Stage,
                task.Revision));
        }
        states.Sort((left, right) => left.Task.Sequence.CompareTo(right.Task.Sequence));
        return states;
    }

    private int CompleteBuildTask(ConstructionTaskState task, long hostTick)
    {
        if (task.Stage == ConstructionTaskStage.Working)
        {
            return tasks.TryAdvance(task.Key, ConstructionTaskStage.Returning, hostTick) ==
                ConstructionTaskError.None ? 1 : 0;
        }
        if (task.Stage == ConstructionTaskStage.Returning)
        {
            return tasks.TryAdvance(task.Key, ConstructionTaskStage.Completed, hostTick) ==
                ConstructionTaskError.None ? 1 : 0;
        }
        return tasks.TryCancel(task.Key, hostTick, ConstructionCancelReason.DemandCovered) ==
            ConstructionTaskError.None ? 1 : 0;
    }

    private bool SpendBuildMaterials(ConstructionOwnerKey owner, BuildTargetSnapshot snapshot,
        BuildOwnerSnapshot ownerSnap, Func<BuildOwnerSnapshot, BuildTargetSnapshot, BuildMaterialRequirement> materialProvider,
        long hostTick, out long spent)
    {
        spent = 0;
        var requirement = materialProvider != null
            ? materialProvider(ownerSnap, snapshot)
            : new BuildMaterialRequirement(0, 0);
        if (requirement.IsFree) return true;
        var key = MaterialKeyFor(owner, requirement.ItemId);
        if (!key.IsValid) return false;
        var tx = resources.BeginHostTransaction();
        var revision = resources.RevisionOf(key);
        var reserve = resources.TryReserveLong(tx, key.Owner, key.Kind, requirement.ItemId,
            requirement.Count, revision, out _, out _);
        if (reserve != LedgerReserveCode.Ok)
        {
            resources.AbortHostTransaction(tx);
            return false;
        }
        if (!resources.CommitHostTransaction(tx, CommandResultCode.Applied, hostTick, out _))
        {
            resources.AbortHostTransaction(tx);
            return false;
        }
        spent = requirement.Count;
        return true;
    }

    private LedgerResourceKey MaterialKeyFor(ConstructionOwnerKey owner, int itemId)
    {
        if (!owner.IsValid || itemId <= 0) return default;
        if (owner.IsPlayer)
            return new LedgerResourceKey(LedgerOwner.ForPlayer(owner.PersistentId),
                LedgerResourceKind.InventoryItem, itemId);
        return new LedgerResourceKey(LedgerOwner.ForBase(owner.BaseKey),
            LedgerResourceKind.BaseStockItem, itemId);
    }

    private List<BuildTargetSnapshot> RefreshBuildCounts(
        IReadOnlyList<BuildTargetSnapshot> targets)
    {
        var refreshed = new List<BuildTargetSnapshot>(targets.Count);
        foreach (var snapshot in targets)
            refreshed.Add(new BuildTargetSnapshot(snapshot.Target, snapshot.PlanetId,
                snapshot.ItemRequired, snapshot.IsDestroyed,
                tasks.GetBuildTaskCount(snapshot.Target)));
        return refreshed;
    }

    private List<RepairDamageSnapshot> RefreshDamageCounts(
        IReadOnlyList<RepairDamageSnapshot> damages)
    {
        var refreshed = new List<RepairDamageSnapshot>(damages.Count);
        foreach (var damage in damages)
            refreshed.Add(new RepairDamageSnapshot(damage.Target, damage.PlanetId, damage.Hp,
                damage.HpMax, damage.DamageRate, tasks.GetRepairerCount(damage.Target),
                damage.HasCombatStat, damage.HasConstructStat));
        return refreshed;
    }

    private double AvailableFor(ConstructionOwnerKey owner)
    {
        var key = LedgerKeyFor(owner, out _);
        if (!key.IsValid)
            return 0.0;
        if (LedgerResourceKey.IsDoubleKind(key.Kind) &&
            resources.TryGetDouble(key.Owner, key.Kind, out var balance, out _))
            return balance;
        return 0.0;
    }

    private float RatioFor(ConstructionOwnerKey owner, double cost)
    {
        var available = AvailableFor(owner);
        return owner.IsBase
            ? HostRepairEnergy.ComputeBaseRatio(available, cost)
            : HostRepairEnergy.ComputeMechaRatio(available, cost);
    }

    private LedgerResourceKey LedgerKeyFor(ConstructionOwnerKey owner,
        out LedgerResourceKind kind)
    {
        kind = LedgerResourceKind.Unknown;
        if (!owner.IsValid)
            return default;
        if (owner.IsPlayer)
        {
            kind = LedgerResourceKind.CoreEnergy;
            return new LedgerResourceKey(LedgerOwner.ForPlayer(owner.PersistentId), kind, 0);
        }
        kind = LedgerResourceKind.BaseEnergy;
        return new LedgerResourceKey(LedgerOwner.ForBase(owner.BaseKey), kind, 0);
    }
}

/// <summary>
/// No-op build writer for the session's fail-closed tick (A18).
/// </summary>
/// <remarks>
/// The session has no prebuild world view yet, so its frame tick must not invent builds.
/// This writer refuses every build, which keeps the empty-input tick a pure lifecycle probe.
/// Unit tests inject recording fakes instead.
/// </remarks>
public sealed class NullBuildWriter : IHostBuildWriter
{
    public static readonly NullBuildWriter Instance = new();

    public bool TryBuild(in ObjectKey target, int nativePrebuildId, float ratio, long hostTick,
        out bool finished)
    {
        finished = false;
        return false;
    }
}
/// <summary>
/// No-op repair writer for the session's fail-closed tick (A17).
/// </summary>
/// <remarks>
/// The session has no damage/owner world view yet, so its frame tick must not invent repairs.
/// This writer refuses every repair, which keeps the empty-input tick a pure lifecycle probe.
/// Unit tests inject recording fakes instead.
/// </remarks>
public sealed class NullRepairWriter : IHostRepairWriter
{
    public static readonly NullRepairWriter Instance = new();

    public bool TryRepair(in ObjectKey target, int nativeEntityId, float ratio, long hostTick,
        out bool finished)
    {
        finished = false;
        return false;
    }
}
