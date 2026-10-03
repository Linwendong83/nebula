#region

using System;
using System.Collections.Generic;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// What a construction drone task does (DESIGN 8.1).
/// </summary>
public enum ConstructionTaskKind : byte
{
    Unknown = 0,
    Build = 1,
    Repair = 2,
    Reconstruct = 3
}

/// <summary>
/// Where a construction drone task stands (DESIGN 8.1).
/// </summary>
/// <remarks>
/// Linear path: <c>Reserved -&gt; Launching -&gt; Travelling -&gt; Working -&gt; Returning -&gt;
/// Completed</c>. Any non-terminal stage may enter <see cref="Cancelled"/> instead, which releases
/// its budget slot exactly once. <see cref="Completed"/> and <see cref="Cancelled"/> are terminal.
/// </remarks>
public enum ConstructionTaskStage : byte
{
    Unknown = 0,
    Reserved = 1,
    Launching = 2,
    Travelling = 3,
    Working = 4,
    Returning = 5,
    Completed = 6,
    Cancelled = 7
}

/// <summary>
/// Why a task stopped before completing. Stored on the cancelled task for R08 diagnostics.
/// </summary>
public enum ConstructionCancelReason : byte
{
    None = 0,
    TargetDemolished = 1,
    TargetDestroyed = 2,
    TargetRecycled = 3,
    OwnerOffline = 4,
    OwnerDead = 5,
    OwnerLeftPlanet = 6,
    SwitchDisabled = 7,
    BaseUnpowered = 8,
    InsufficientStock = 9,
    DemandCovered = 10,
    Retargeted = 11,
    Superseded = 12,
    WorldUnloaded = 13
}

/// <summary>
/// Why a ledger operation was refused. Success is <see cref="None"/>.
/// </summary>
public enum ConstructionTaskError : byte
{
    None = 0,
    InvalidOwner = 1,
    InvalidTarget = 2,
    KindTargetMismatch = 3,
    DuplicateTask = 4,
    NoIdleDrone = 5,
    UnknownTask = 6,
    BadStageTransition = 7,
    AlreadyTerminal = 8,
    StaleTick = 9,
    WrongEpoch = 10,
    BudgetCorrupt = 11
}

/// <summary>
/// Identity of one host construction task: <c>AuthorityEpoch + HostTaskSequence</c> (DESIGN 8.1).
/// </summary>
public readonly struct ConstructionTaskKey : IEquatable<ConstructionTaskKey>
{
    private readonly AuthorityEpoch epoch;
    private readonly long sequence;

    public ConstructionTaskKey(AuthorityEpoch epoch, long sequence)
    {
        this.epoch = epoch;
        this.sequence = sequence;
    }

    public AuthorityEpoch Epoch => epoch;

    public long Sequence => sequence;

    public bool IsValid => epoch.IsValid && sequence > 0;

    public bool Equals(ConstructionTaskKey other) => epoch.Equals(other.epoch) && sequence == other.sequence;

    public override bool Equals(object obj) => obj is ConstructionTaskKey other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            return (epoch.GetHashCode() * 397) ^ sequence.GetHashCode();
        }
    }

    public static bool operator ==(ConstructionTaskKey left, ConstructionTaskKey right) => left.Equals(right);

    public static bool operator !=(ConstructionTaskKey left, ConstructionTaskKey right) => !left.Equals(right);

    public override string ToString() => "epoch=" + epoch + "|task=" + sequence;
}

/// <summary>
/// One host-owned construction drone task (TASKS.md A15, pure model).
/// </summary>
/// <remarks>
/// <para>
/// The task carries the full DESIGN 8.1 tuple — owner, target, kind, stage, revision, resource
/// transaction and host tick — in primitives only, so it runs in a plain test process. The target
/// is a full <see cref="ObjectKey"/> (kind plus generation), never a construct-stat id: build and
/// reconstruct address <see cref="PoolKind.Prebuild"/>, repair addresses
/// <see cref="PoolKind.Entity"/>. A task that loses its object never slides onto the next
/// generation of the same slot; the caller retargets explicitly with the new key or the ledger
/// cancels by exact target.
/// </para>
/// <para>
/// Only <see cref="ConstructionTaskLedger"/> mutates tasks and budgets, so "same replay never
/// raises repairerCount" and "every release happens once" are structural: a duplicate add returns
/// the existing key without touching any counter, and a second cancel of a terminal task moves
/// nothing.
/// </para>
/// </remarks>
public sealed class ConstructionTaskState
{
    internal ConstructionTaskState(ConstructionTaskKey key, ConstructionOwnerKey owner, ObjectKey target,
        ConstructionTaskKind kind, long hostTick, HostTransactionId transaction)
    {
        Key = key;
        Owner = owner;
        Target = target;
        Kind = kind;
        Stage = ConstructionTaskStage.Reserved;
        Revision = 1;
        LastHostTick = hostTick;
        Transaction = transaction;
        CancelReason = ConstructionCancelReason.None;
    }

    public ConstructionTaskKey Key { get; }

    public ConstructionOwnerKey Owner { get; }

    public ObjectKey Target { get; internal set; }

    public ConstructionTaskKind Kind { get; }

    public ConstructionTaskStage Stage { get; internal set; }

    /// <summary>Incremented on creation and on every stage, retarget or cancel.</summary>
    public long Revision { get; internal set; }

    /// <summary>Host tick of the last transition. Every transfer carries it (DESIGN 8.1).</summary>
    public long LastHostTick { get; internal set; }

    /// <summary>Ledger transaction that reserved this task's resources. May be default for host work.</summary>
    public HostTransactionId Transaction { get; }

    /// <summary>Set when the task enters <see cref="ConstructionTaskStage.Cancelled"/>.</summary>
    public ConstructionCancelReason CancelReason { get; internal set; }

    public bool IsTerminal =>
        Stage == ConstructionTaskStage.Completed || Stage == ConstructionTaskStage.Cancelled;

    /// <summary>
    /// True while the task holds a budget slot: every stage from reserved through returning.
    /// </summary>
    public bool OccupiesBudget =>
        Stage == ConstructionTaskStage.Reserved || Stage == ConstructionTaskStage.Launching ||
        Stage == ConstructionTaskStage.Travelling || Stage == ConstructionTaskStage.Working ||
        Stage == ConstructionTaskStage.Returning;

    /// <summary>
    /// True while the task holds a repairer slot on its target. Only repair tasks in reserved
    /// through working count: a returning drone is flying home, not repairing, and build tasks
    /// never touch <c>repairerCount</c>. The A17 adapter sums this per target to derive the single
    /// vanilla count exactly once.
    /// </summary>
    public bool ContributesRepairer =>
        Kind == ConstructionTaskKind.Repair &&
        (Stage == ConstructionTaskStage.Reserved || Stage == ConstructionTaskStage.Launching ||
         Stage == ConstructionTaskStage.Travelling || Stage == ConstructionTaskStage.Working);

    /// <summary>Whether <paramref name="from"/> may move to <paramref name="to"/> directly.</summary>
    public static bool CanTransition(ConstructionTaskStage from, ConstructionTaskStage to)
    {
        switch (from)
        {
            case ConstructionTaskStage.Reserved:
                return to == ConstructionTaskStage.Launching || to == ConstructionTaskStage.Cancelled;
            case ConstructionTaskStage.Launching:
                return to == ConstructionTaskStage.Travelling || to == ConstructionTaskStage.Cancelled;
            case ConstructionTaskStage.Travelling:
                return to == ConstructionTaskStage.Working || to == ConstructionTaskStage.Cancelled;
            case ConstructionTaskStage.Working:
                return to == ConstructionTaskStage.Returning || to == ConstructionTaskStage.Cancelled;
            case ConstructionTaskStage.Returning:
                return to == ConstructionTaskStage.Completed || to == ConstructionTaskStage.Cancelled;
            default:
                return false;
        }
    }

    /// <summary>Whether <paramref name="kind"/> may address <paramref name="target"/>.</summary>
    public static bool KindMatchesTarget(ConstructionTaskKind kind, in ObjectKey target)
    {
        switch (kind)
        {
            case ConstructionTaskKind.Build:
            case ConstructionTaskKind.Reconstruct:
                return target.Kind == PoolKind.Prebuild;
            case ConstructionTaskKind.Repair:
                return target.Kind == PoolKind.Entity;
            default:
                return false;
        }
    }

    public override string ToString() =>
        Key + "|owner=" + Owner + "|target=" + Target + "|kind=" + Kind + "|stage=" + Stage +
        "|rev=" + Revision;
}

/// <summary>
/// The host's construction task truth: per-owner budgets plus the task table (TASKS.md A15).
/// </summary>
/// <remarks>
/// <para>
/// Flow per task: <c>TryAddTask (reserve)</c> → <c>TryAdvance</c> through the linear stages →
/// <c>Returning → Completed</c> (release), or any stage → <c>TryCancel</c> (release once).
/// Target loss (<see cref="CancelByTarget"/>), owner loss (<see cref="CancelByOwner"/>) and relay
/// retarget (<see cref="TryRetarget"/>) all funnel through the same single-release path, which is
/// what makes "拆除/死亡/断线/更换目标各只释放一次" structural rather than conventional.
/// </para>
/// <para>
/// Duplicate protection is by full (owner, target, kind) with the target's generation included:
/// re-adding a live task returns <see cref="ConstructionTaskError.DuplicateTask"/> with the
/// existing key and moves no counter, so "相同任务重放不增 repairerCount". A new generation of
/// the same slot is a different target and therefore a different task — it is never merged
/// implicitly.
/// </para>
/// <para>
/// Not thread-safe: only the frame boundary touches it.
/// </para>
/// </remarks>
public sealed class ConstructionTaskLedger
{
    private readonly AuthorityEpoch epoch;
    private readonly Dictionary<ConstructionTaskKey, ConstructionTaskState> tasks = new();
    private readonly Dictionary<ConstructionOwnerKey, DroneBudget> budgets = new();
    private readonly Dictionary<ActiveTaskIndex, ConstructionTaskKey> activeIndex = new();

    private long nextSequence;

    public ConstructionTaskLedger(AuthorityEpoch epoch)
    {
        if (!epoch.IsValid) throw new ArgumentException("A task ledger needs a world epoch.", nameof(epoch));
        this.epoch = epoch;
    }

    public AuthorityEpoch Epoch => epoch;

    public int TaskCount => tasks.Count;

    public long NextSequence => nextSequence + 1;

    private readonly struct ActiveTaskIndex : IEquatable<ActiveTaskIndex>
    {
        public ActiveTaskIndex(ConstructionOwnerKey owner, ObjectKey target, ConstructionTaskKind kind)
        {
            Owner = owner;
            Target = target;
            Kind = kind;
        }

        public ConstructionOwnerKey Owner { get; }

        public ObjectKey Target { get; }

        public ConstructionTaskKind Kind { get; }

        public bool Equals(ActiveTaskIndex other) =>
            Owner.Equals(other.Owner) && Target.Equals(other.Target) && Kind == other.Kind;

        public override bool Equals(object obj) => obj is ActiveTaskIndex other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Owner.GetHashCode();
                hash = (hash * 397) ^ Target.GetHashCode();
                hash = (hash * 397) ^ (int)Kind;
                return hash;
            }
        }
    }

    /// <summary>Creates or returns the owner's budget. Host-only seed path.</summary>
    public DroneBudget EnsureBudget(ConstructionOwnerKey owner, int total)
    {
        if (!owner.IsValid) throw new ArgumentException("A budget needs a valid owner.", nameof(owner));
        if (budgets.TryGetValue(owner, out var existing)) return existing;
        var budget = new DroneBudget(owner, total);
        budgets[owner] = budget;
        return budget;
    }

    public bool TryGetBudget(ConstructionOwnerKey owner, out DroneBudget budget)
    {
        budget = null;
        return owner.IsValid && budgets.TryGetValue(owner, out budget);
    }

    /// <summary>Changes an owner's capacity. Refuses to shrink below occupied slots.</summary>
    public bool TrySetOwnerTotal(ConstructionOwnerKey owner, int newTotal, out string reason)
    {
        reason = null;
        if (!TryGetBudget(owner, out var budget))
        {
            reason = "UnknownOwner";
            return false;
        }
        return budget.TrySetTotal(newTotal, out reason);
    }

    public bool TryGetTask(ConstructionTaskKey key, out ConstructionTaskState task)
    {
        task = null;
        return key.IsValid && tasks.TryGetValue(key, out task);
    }

    /// <summary>
    /// Reserves one drone slot for a new task. A live duplicate returns its existing key with
    /// <see cref="ConstructionTaskError.DuplicateTask"/> and moves nothing.
    /// </summary>
    public ConstructionTaskError TryAddTask(ConstructionOwnerKey owner, ObjectKey target,
        ConstructionTaskKind kind, long hostTick, HostTransactionId transaction,
        out ConstructionTaskKey key)
    {
        key = default;
        if (!owner.IsValid) return ConstructionTaskError.InvalidOwner;
        if (!target.IsValid || !target.Epoch.Equals(epoch))
        {
            if (target.IsValid && !target.Epoch.Equals(epoch)) return ConstructionTaskError.WrongEpoch;
            return ConstructionTaskError.InvalidTarget;
        }
        if (owner.IsBase && !owner.BaseKey.Epoch.Equals(epoch)) return ConstructionTaskError.WrongEpoch;
        if (kind == ConstructionTaskKind.Unknown) return ConstructionTaskError.KindTargetMismatch;
        if (!ConstructionTaskState.KindMatchesTarget(kind, in target))
            return ConstructionTaskError.KindTargetMismatch;
        if (hostTick < 0) return ConstructionTaskError.StaleTick;
        if (transaction.IsValid && !transaction.Epoch.Equals(epoch)) return ConstructionTaskError.WrongEpoch;

        var index = new ActiveTaskIndex(owner, target, kind);
        if (activeIndex.TryGetValue(index, out var existingKey) &&
            tasks.TryGetValue(existingKey, out var existing) && !existing.IsTerminal)
        {
            key = existingKey;
            return ConstructionTaskError.DuplicateTask;
        }
        // A stale index entry for a task that has since terminated can never be reused as work:
        // drop it so the slot's next generation starts clean.
        if (activeIndex.ContainsKey(index)) activeIndex.Remove(index);

        if (!budgets.TryGetValue(owner, out var budget)) return ConstructionTaskError.NoIdleDrone;
        string reason;
        if (!budget.TryReserve(out reason)) return ConstructionTaskError.NoIdleDrone;

        nextSequence++;
        key = new ConstructionTaskKey(epoch, nextSequence);
        var task = new ConstructionTaskState(key, owner, target, kind, hostTick, transaction);
        tasks[key] = task;
        activeIndex[index] = key;
        return ConstructionTaskError.None;
    }

    /// <summary>Moves one task to its next stage, moving its budget slot with it.</summary>
    public ConstructionTaskError TryAdvance(ConstructionTaskKey key, ConstructionTaskStage next,
        long hostTick)
    {
        if (!tasks.TryGetValue(key, out var task)) return ConstructionTaskError.UnknownTask;
        if (task.IsTerminal) return ConstructionTaskError.AlreadyTerminal;
        if (next == ConstructionTaskStage.Unknown) return ConstructionTaskError.BadStageTransition;
        if (!ConstructionTaskState.CanTransition(task.Stage, next))
            return ConstructionTaskError.BadStageTransition;
        if (hostTick < task.LastHostTick) return ConstructionTaskError.StaleTick;
        if (!budgets.TryGetValue(task.Owner, out var budget)) return ConstructionTaskError.BudgetCorrupt;

        var from = task.Stage;
        if (!MoveBudgetForTransition(budget, from, next)) return ConstructionTaskError.BudgetCorrupt;

        task.Stage = next;
        task.Revision++;
        task.LastHostTick = hostTick;
        if (next == ConstructionTaskStage.Completed || next == ConstructionTaskStage.Cancelled)
            activeIndex.Remove(new ActiveTaskIndex(task.Owner, task.Target, task.Kind));
        return ConstructionTaskError.None;
    }

    /// <summary>Cancels one task from any live stage, releasing its slot exactly once.</summary>
    public ConstructionTaskError TryCancel(ConstructionTaskKey key, long hostTick,
        ConstructionCancelReason reason)
    {
        if (!tasks.TryGetValue(key, out var task)) return ConstructionTaskError.UnknownTask;
        if (task.IsTerminal) return ConstructionTaskError.AlreadyTerminal;
        if (reason == ConstructionCancelReason.None) return ConstructionTaskError.BadStageTransition;
        if (!budgets.TryGetValue(task.Owner, out var budget)) return ConstructionTaskError.BudgetCorrupt;
        if (!budget.ReleaseForStage(task.Stage)) return ConstructionTaskError.BudgetCorrupt;

        task.Stage = ConstructionTaskStage.Cancelled;
        task.CancelReason = reason;
        task.Revision++;
        if (hostTick > task.LastHostTick) task.LastHostTick = hostTick;
        activeIndex.Remove(new ActiveTaskIndex(task.Owner, task.Target, task.Kind));
        return ConstructionTaskError.None;
    }

    /// <summary>
    /// Cancels every live task on an exact target (demolish, death, despawn). Matches the full
    /// key including generation: the next generation of the same slot is a different object and
    /// is never cancelled implicitly. Each task releases once; returns the cancelled count.
    /// </summary>
    public int CancelByTarget(ObjectKey target, long hostTick, ConstructionCancelReason reason)
    {
        if (!target.IsValid || reason == ConstructionCancelReason.None) return 0;
        var cancelled = 0;
        foreach (var task in new List<ConstructionTaskState>(tasks.Values))
        {
            if (task.IsTerminal || !task.Target.Equals(target)) continue;
            if (TryCancel(task.Key, hostTick, reason) == ConstructionTaskError.None) cancelled++;
        }
        return cancelled;
    }

    /// <summary>
    /// Cancels every live task of one owner (disconnect, death, leaving the planet, switch off).
    /// Each task releases once; returns the cancelled count.
    /// </summary>
    public int CancelByOwner(ConstructionOwnerKey owner, long hostTick, ConstructionCancelReason reason)
    {
        if (!owner.IsValid || reason == ConstructionCancelReason.None) return 0;
        var cancelled = 0;
        foreach (var task in new List<ConstructionTaskState>(tasks.Values))
        {
            if (task.IsTerminal || !task.Owner.Equals(owner)) continue;
            if (TryCancel(task.Key, hostTick, reason) == ConstructionTaskError.None) cancelled++;
        }
        return cancelled;
    }

    /// <summary>
    /// Retargets one live task (relay conversion): releases the old target's repair occupancy and
    /// reserves the new one under the same task key, bumping the revision. Allowed from reserved
    /// through working; returning tasks fly home first.
    /// </summary>
    public ConstructionTaskError TryRetarget(ConstructionTaskKey key, ObjectKey newTarget, long hostTick)
    {
        if (!tasks.TryGetValue(key, out var task)) return ConstructionTaskError.UnknownTask;
        if (task.IsTerminal) return ConstructionTaskError.AlreadyTerminal;
        if (task.Stage == ConstructionTaskStage.Returning) return ConstructionTaskError.BadStageTransition;
        if (!newTarget.IsValid || !newTarget.Epoch.Equals(epoch))
        {
            if (newTarget.IsValid && !newTarget.Epoch.Equals(epoch)) return ConstructionTaskError.WrongEpoch;
            return ConstructionTaskError.InvalidTarget;
        }
        if (!ConstructionTaskState.KindMatchesTarget(task.Kind, in newTarget))
            return ConstructionTaskError.KindTargetMismatch;
        if (task.Target.Equals(newTarget)) return ConstructionTaskError.DuplicateTask;
        if (hostTick < task.LastHostTick) return ConstructionTaskError.StaleTick;

        var nextIndex = new ActiveTaskIndex(task.Owner, newTarget, task.Kind);
        if (activeIndex.TryGetValue(nextIndex, out var holder) && !holder.Equals(key) &&
            tasks.TryGetValue(holder, out var blocking) && !blocking.IsTerminal)
            return ConstructionTaskError.DuplicateTask;

        activeIndex.Remove(new ActiveTaskIndex(task.Owner, task.Target, task.Kind));
        task.Target = newTarget;
        task.Revision++;
        task.LastHostTick = hostTick;
        activeIndex[nextIndex] = key;
        return ConstructionTaskError.None;
    }

    /// <summary>
    /// The ledger-derived repairer count for one target: live repair tasks holding a repairer slot
    /// on exactly this key (including generation). The A17 adapter writes this value once; no
    /// display path may increment it.
    /// </summary>
    public int GetRepairerCount(ObjectKey target)
    {
        if (!target.IsValid) return 0;
        var count = 0;
        foreach (var task in tasks.Values)
        {
            if (task.Target.Equals(target) && task.ContributesRepairer) count++;
        }
        return count;
    }

    /// <summary>
    /// The ledger-derived builder count for one prebuild: live build/reconstruct tasks holding a
    /// budget slot on exactly this key (including generation). The A18 adapter derives the single
    /// "one builder per prebuild" coverage from this value; no display path may claim it.
    /// </summary>
    public int GetBuildTaskCount(ObjectKey target)
    {
        if (!target.IsValid) return 0;
        var count = 0;
        foreach (var task in tasks.Values)
        {
            if (!task.Target.Equals(target) || task.IsTerminal) continue;
            if (task.Kind == ConstructionTaskKind.Build ||
                task.Kind == ConstructionTaskKind.Reconstruct)
                count++;
        }
        return count;
    }

    /// <summary>Live (non-terminal) tasks currently held.</summary>
    public int ActiveTaskCount
    {
        get
        {
            var count = 0;
            foreach (var task in tasks.Values)
            {
                if (!task.IsTerminal) count++;
            }
            return count;
        }
    }

    /// <summary>
    /// Live tasks for the frame executor (A17). Snapshot semantics: the caller copies before
    /// mutating, so advancing or cancelling during the tick never invalidates the walk.
    /// </summary>
    public List<ConstructionTaskState> CollectLiveTasks()
    {
        var live = new List<ConstructionTaskState>(ActiveTaskCount);
        foreach (var task in tasks.Values)
        {
            if (!task.IsTerminal) live.Add(task);
        }
        return live;
    }

    /// <summary>Audit helper for I05/I06: the owner's budget invariant plus its task-held slots.</summary>
    public bool CheckOwnerInvariant(ConstructionOwnerKey owner, out string detail)
    {
        detail = null;
        if (!budgets.TryGetValue(owner, out var budget))
        {
            detail = "UnknownOwner";
            return false;
        }
        if (!budget.CheckInvariant())
        {
            detail = "BudgetInvariant:" + budget;
            return false;
        }
        var held = 0;
        foreach (var task in tasks.Values)
        {
            if (!task.Owner.Equals(owner) || !task.OccupiesBudget) continue;
            held++;
        }
        if (held != budget.Occupied)
        {
            detail = "HeldMismatch:tasks=" + held + "|budget=" + budget;
            return false;
        }
        return true;
    }

    /// <summary>Drops everything. Used when the world or session ends.</summary>
    public void Clear()
    {
        tasks.Clear();
        budgets.Clear();
        activeIndex.Clear();
        nextSequence = 0;
    }

    /// <summary>
    /// Captures every budget's capacity into the save-sidecar record form (TASKS.md A21).
    /// </summary>
    /// <remarks>
    /// Only the total is captured. The bucket split is a function of live tasks, and live tasks
    /// are reclaimed on load (DESIGN 11), so buckets are not persisted — a restored budget is all
    /// idle and no <c>repairerCount</c> ghost survives the reload.
    /// </remarks>
    public List<AuthoritySaveBudget> CaptureBudgetsForSave()
    {
        var budgets = new List<AuthoritySaveBudget>(this.budgets.Count);
        foreach (var pair in this.budgets)
        {
            budgets.Add(new AuthoritySaveBudget
            {
                OwnerKind = pair.Key.Kind,
                PersistentId = pair.Key.PersistentId,
                BaseScope = pair.Key.BaseKey.Scope,
                BaseNativeId = pair.Key.BaseKey.NativeId,
                BaseGeneration = pair.Key.BaseKey.Generation,
                Total = pair.Value.Total
            });
        }
        return budgets;
    }

    /// <summary>
    /// Captures the live task table into the save-sidecar record form (TASKS.md A21).
    /// </summary>
    /// <remarks>
    /// The records are provenance for the load transaction's one-time reclaim: they are counted
    /// and reported, never re-inserted into a new world's ledger. Persisting them is what makes
    /// the reclaim auditable ("写明材料/名额处理") instead of a silent drop.
    /// </remarks>
    public List<AuthoritySaveTask> CaptureLiveTasksForSave()
    {
        var records = new List<AuthoritySaveTask>(ActiveTaskCount);
        foreach (var task in tasks.Values)
        {
            if (task.IsTerminal) continue;
            records.Add(new AuthoritySaveTask
            {
                Sequence = task.Key.Sequence,
                OwnerKind = task.Owner.Kind,
                PersistentId = task.Owner.PersistentId,
                BaseScope = task.Owner.BaseKey.Scope,
                BaseNativeId = task.Owner.BaseKey.NativeId,
                BaseGeneration = task.Owner.BaseKey.Generation,
                TargetKind = task.Target.Kind,
                TargetScope = task.Target.Scope,
                TargetNativeId = task.Target.NativeId,
                TargetGeneration = task.Target.Generation,
                TaskKind = task.Kind,
                Stage = task.Stage,
                Revision = task.Revision,
                LastHostTick = task.LastHostTick,
                CancelReason = task.CancelReason
            });
        }
        return records;
    }

    private static bool MoveBudgetForTransition(DroneBudget budget, ConstructionTaskStage from,
        ConstructionTaskStage to)
    {
        string reason;
        switch (to)
        {
            case ConstructionTaskStage.Launching:
                return from == ConstructionTaskStage.Reserved && budget.TryActivate(out reason);
            case ConstructionTaskStage.Travelling:
            case ConstructionTaskStage.Working:
                return true;
            case ConstructionTaskStage.Returning:
                return from == ConstructionTaskStage.Working && budget.TryBeginReturn(out reason);
            case ConstructionTaskStage.Completed:
                return from == ConstructionTaskStage.Returning && budget.ReleaseReturning();
            case ConstructionTaskStage.Cancelled:
                return budget.ReleaseForStage(from);
            default:
                return false;
        }
    }
}

/// <summary>
/// Preserved selection rules the A16 service builds on (TASKS.md A15, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Build dispatch keeps the shipped "placer available时优先" rule
/// (<c>BuildDispatchManager.TrySelectPlacer</c>): when the player who placed a prebuild can serve
/// it, that player owns it; only otherwise does the host fall back to the scored candidates. This
/// helper encodes the priority without reading any game pool, so tests pin the rule while the A16
/// adapter supplies availability (planet, range, speed, switches, capability).
/// </para>
/// <para>
/// Repair scoring calls the vanilla <c>GetRepairValue</c>/<c>GetRepairDroneDemand</c> in the A16
/// adapter with the same owner candidate filter (planet, switches, range, speed, energy). The pure
/// model only fixes the inputs that ordering must consider and the stable tie-break; it never
/// reimplements the vanilla multiplier, so a game update cannot silently diverge from a copied
/// formula here.
/// </para>
/// </remarks>
public static class ConstructionSelection
{
    /// <summary>
    /// Placer-first pick: the placer wins when available, otherwise the smallest fallback by
    /// stable owner order. Availability filtering lives in the caller (A16); this fixes priority.
    /// </summary>
    public static bool TryPickPlacerFirst(ConstructionOwnerKey placer, bool placerAvailable,
        IList<ConstructionOwnerKey> fallbacks, out ConstructionOwnerKey selected)
    {
        selected = default;
        if (placer.IsValid && placerAvailable)
        {
            selected = placer;
            return true;
        }
        if (fallbacks == null || fallbacks.Count == 0) return false;
        var found = false;
        foreach (var candidate in fallbacks)
        {
            if (!candidate.IsValid) continue;
            if (!found || ConstructionOwnerKey.StableCompare(in candidate, in selected) < 0)
            {
                selected = candidate;
                found = true;
            }
        }
        return found;
    }

    /// <summary>
    /// Inputs the repair ordering must consider (E04): damage rate, missing HP, current repairers
    /// and the tech multiplier the vanilla scorer applies. The A16 adapter feeds these from the
    /// real <c>combatStat/constructStat</c> records.
    /// </summary>
    public readonly struct RepairCandidateInput
    {
        public RepairCandidateInput(ObjectKey target, int hp, int hpMax, float damageRate,
            int currentRepairers, float techMultiplier)
        {
            Target = target;
            Hp = hp;
            HpMax = hpMax;
            DamageRate = damageRate;
            CurrentRepairers = currentRepairers;
            TechMultiplier = techMultiplier;
        }

        public ObjectKey Target { get; }

        public int Hp { get; }

        public int HpMax { get; }

        public float DamageRate { get; }

        public int CurrentRepairers { get; }

        public float TechMultiplier { get; }

        public int MissingHp => HpMax - Hp;
    }

    /// <summary>
    /// Deterministic repair ordering over the candidate inputs: larger missing HP first, then
    /// higher damage rate, then fewer current repairers, then stable target order (generation
    /// included). The final launch decision additionally applies the vanilla demand value and the
    /// owner's eligibility; this ordering only guarantees two hosts rank the same inputs the same
    /// way. Returns negative when <paramref name="left"/> sorts first.
    /// </summary>
    public static int CompareRepairCandidates(in RepairCandidateInput left, in RepairCandidateInput right)
    {
        var byMissing = right.MissingHp.CompareTo(left.MissingHp);
        if (byMissing != 0) return byMissing;
        var byRate = right.DamageRate.CompareTo(left.DamageRate);
        if (byRate != 0) return byRate;
        var byRepairers = left.CurrentRepairers.CompareTo(right.CurrentRepairers);
        if (byRepairers != 0) return byRepairers;
        var byTech = right.TechMultiplier.CompareTo(left.TechMultiplier);
        if (byTech != 0) return byTech;
        var byScope = left.Target.Scope.CompareTo(right.Target.Scope);
        if (byScope != 0) return byScope;
        var byNative = left.Target.NativeId.CompareTo(right.Target.NativeId);
        if (byNative != 0) return byNative;
        return left.Target.Generation.CompareTo(right.Target.Generation);
    }
}
