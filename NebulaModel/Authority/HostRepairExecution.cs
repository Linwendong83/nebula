#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Per-drone repair energy and amount math (TASKS.md A17, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Vanilla charges repair energy inside <c>DroneComponent.InternalUpdate</c> stage 3 and heals
/// inside <c>ConstructionSystem.Repair</c> as
/// <c>hp += droneRepairHpPerTick * ratio * globalHPScale</c>. This type carries that arithmetic
/// without copying any balance number: the caller supplies the available energy, the per-tick
/// repair cost and the base repair/scale from the vanilla configs/history, tests inject canned
/// numbers, and the production adapter (A17 game seam) reads the real pools. That is the same
/// "no hand-copied multiplier" contract A16 keeps for demand.
/// </para>
/// <para>
/// Ratio follows the vanilla stage-3 rule: full cost when the owner can pay, otherwise the
/// remaining fraction, otherwise zero. A fake infinite value (the E05
/// <c>float.MaxValue</c> dummy) is never accepted here: availability comes from the host ledger
/// in production and from explicit test balances here, so an observer with no task contributes
/// no ratio and no repair.
/// </para>
/// </remarks>
public static class HostRepairEnergy
{
    /// <summary>
    /// Repair ratio for a mecha owner from its host-ledger core energy (double).
    /// </summary>
    public static float ComputeMechaRatio(double available, double cost)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0.0)
            return 0f;
        if (double.IsNaN(cost) || double.IsInfinity(cost) || cost <= 0.0)
            return 1f;
        if (available >= cost)
            return 1f;
        var ratio = available / cost;
        if (ratio < 0.0) return 0f;
        if (ratio > 1.0) return 1f;
        return (float)ratio;
    }

    /// <summary>
    /// Repair ratio for a battle-base owner from its host-ledger energy (double view).
    /// </summary>
    public static float ComputeBaseRatio(double available, double cost) =>
        ComputeMechaRatio(available, cost);

    /// <summary>
    /// Repair ratio for a battle-base owner from its vanilla long energy store.
    /// </summary>
    public static float ComputeBaseRatio(long available, double cost)
    {
        if (available <= 0L)
            return 0f;
        if (double.IsNaN(cost) || double.IsInfinity(cost) || cost <= 0.0)
            return 1f;
        var asDouble = (double)available;
        if (asDouble >= cost)
            return 1f;
        var ratio = asDouble / cost;
        if (ratio < 0.0) return 0f;
        if (ratio > 1.0) return 1f;
        return (float)ratio;
    }

    /// <summary>
    /// How much to reserve from the ledger for one working tick. Never negative.
    /// </summary>
    public static double AmountToReserve(double available, double cost)
    {
        if (double.IsNaN(available) || double.IsInfinity(available) || available <= 0.0)
            return 0.0;
        if (double.IsNaN(cost) || double.IsInfinity(cost) || cost <= 0.0)
            return 0.0;
        return Math.Min(available, cost);
    }

    /// <summary>
    /// One drone's heal for one working tick, matching the vanilla Repair line.
    /// </summary>
    public static int ComputeRepairDelta(float baseRepairPerTick, float ratio, float globalScale)
    {
        if (float.IsNaN(baseRepairPerTick) || float.IsInfinity(baseRepairPerTick) || baseRepairPerTick <= 0f)
            return 0;
        if (float.IsNaN(ratio) || float.IsInfinity(ratio) || ratio <= 0f)
            return 0;
        if (float.IsNaN(globalScale) || float.IsInfinity(globalScale) || globalScale <= 0f)
            return 0;
        var clamped = ratio > 1f ? 1f : ratio;
        return (int)((baseRepairPerTick * clamped) * globalScale + 0.1f);
    }

    /// <summary>
    /// Total heal for one target from its working drones. Observers hold no task and add nothing.
    /// </summary>
    public static int SumRepairDeltas(float baseRepairPerTick, float globalScale,
        System.Collections.Generic.IReadOnlyList<float> ratios)
    {
        if (ratios == null || ratios.Count == 0)
            return 0;
        var total = 0;
        for (var i = 0; i < ratios.Count; i++)
            total += ComputeRepairDelta(baseRepairPerTick, ratios[i], globalScale);
        return total;
    }
}

/// <summary>
/// What one live repair task should do this tick (TASKS.md A17, pure decision).
/// </summary>
public enum HostRepairTaskFate : byte
{
    Keep = 0,
    CompleteRepair = 1,
    Cancel = 2
}

/// <summary>
/// Owner-lifecycle and target-lifecycle gates for live repair tasks (TASKS.md A17, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Vanilla recycles a mecha drone when the host's own factory is not the locally loaded one
/// (<c>localLoadedPlanetFactory != factory</c>). That condition names the host's camera, not the
/// task's owner, so a remote client alone on the damage planet loses its drones whenever the
/// host looks away. The new rule names the candidate owner's planet instead: a task survives
/// while its owner stays on the damage planet, online, alive, switched on and funded, and while
/// its target still names the same generation with an open damage record. There is no host-planet
/// field on any input, so "不读主机本地 planet" is structural.
/// </para>
/// <para>
/// Speed never cancels a live task: vanilla only gates new launches above 20 m/s. A fast owner
/// simply stops receiving new reservations (A16 <c>SpeedTooHigh</c>) while its in-flight drone
/// flies home.
/// </para>
/// </remarks>
public static class HostDroneLifecycle
{
    /// <summary>
    /// Owner-side cancel reason for a live task, or null when the owner may keep working.
    /// </summary>
    /// <remarks>
    /// Energy never cancels a live task: a dry drone pauses with ratio zero (vanilla stage-3
    /// rule) while new dispatches stay gated by <c>NoEnergy</c> (A16). Cancelling live work on
    /// every brownout would churn budgets and hide the "low energy means slow, not gone"
    /// acceptance.
    /// </remarks>
    public static ConstructionCancelReason? OwnerCancelReason(in RepairOwnerSnapshot owner,
        int damagePlanet)
    {
        if (!owner.Owner.IsValid || !owner.IsAvailable)
            return ConstructionCancelReason.OwnerOffline;
        if (!owner.RepairEnabled || !owner.DroneEnabled)
            return ConstructionCancelReason.SwitchDisabled;
        if (owner.PlanetId != damagePlanet)
            return ConstructionCancelReason.OwnerLeftPlanet;
        return null;
    }

    /// <summary>
    /// Target-side fate for a live task. Full HP completes; a lost record or a generation
    /// mismatch cancels; otherwise the task keeps working.
    /// </summary>
    public static (HostRepairTaskFate Fate, ConstructionCancelReason Cancel) TargetFate(
        in ObjectKey taskTarget, in RepairDamageSnapshot damage)
    {
        if (!damage.Target.IsValid || damage.Target.Kind != PoolKind.Entity)
            return (HostRepairTaskFate.Cancel, ConstructionCancelReason.TargetDestroyed);
        if (!taskTarget.IsValid || taskTarget.Kind != PoolKind.Entity)
            return (HostRepairTaskFate.Cancel, ConstructionCancelReason.TargetDestroyed);
        if (!taskTarget.Epoch.Equals(damage.Target.Epoch) ||
            taskTarget.Scope != damage.Target.Scope ||
            taskTarget.NativeId != damage.Target.NativeId ||
            taskTarget.Generation != damage.Target.Generation)
            return (HostRepairTaskFate.Cancel, ConstructionCancelReason.TargetRecycled);
        if (!damage.HasCombatStat || !damage.HasConstructStat)
            return (HostRepairTaskFate.Cancel, ConstructionCancelReason.TargetDestroyed);
        if (damage.HpMax <= 0)
            return (HostRepairTaskFate.Cancel, ConstructionCancelReason.TargetDestroyed);
        if (damage.Hp >= damage.HpMax)
            return (HostRepairTaskFate.CompleteRepair, ConstructionCancelReason.None);
        return (HostRepairTaskFate.Keep, ConstructionCancelReason.None);
    }
}

/// <summary>
/// Single-writer rule for <c>repairerCount</c> (TASKS.md A17, pure model).
/// </summary>
/// <remarks>
/// <para>
/// Vanilla zeroes every <c>repairerCount</c> when all drones look idle
/// (<c>ConstructionSystem.UpdateModules</c>). That self-heal hides the mod's private drone pool
/// (E05): the private pool is not in <c>drones</c>, so vanilla sees "all idle" while remote
/// drones still claim to repair. The new rule derives the count from the task ledger
/// (<see cref="ConstructionTaskLedger.GetRepairerCount"/>) exactly once per target; the idle
/// reset may only clear a count the ledger also calls empty.
/// </para>
/// </remarks>
public static class HostRepairerCountPolicy
{
    /// <summary>
    /// The count the adapter must write: always the ledger-derived occupancy.
    /// </summary>
    public static int Reconcile(int ledgerCount, int vanillaCount)
    {
        if (ledgerCount < 0) ledgerCount = 0;
        return ledgerCount;
    }

    /// <summary>
    /// Whether the vanilla "all idle so clear" reset may run. False while the ledger holds work.
    /// </summary>
    public static bool MayClearOnIdle(bool allIdle, int ledgerActiveTasks, int vanillaCount)
    {
        if (!allIdle)
            return false;
        if (ledgerActiveTasks > 0)
            return false;
        return vanillaCount > 0;
    }
}

/// <summary>
/// Client-visible repair drone state: task identity, owner, target and stage only (A17 display).
/// </summary>
/// <remarks>
/// <para>
/// The client's own mecha drones are also TaskBatch display in the new mode: the client never
/// runs <c>InternalUpdate</c> with a real energy reference and never calls <c>Repair</c> or
/// <c>FindNextRepair</c>. This struct is the whole display contract until A20 puts it on the
/// wire. It carries no HP, energy, demand or repairerCount, so rendering cannot move a
/// protected number by construction.
/// </para>
/// </remarks>
public readonly struct HostDroneDisplayState : IEquatable<HostDroneDisplayState>
{
    public HostDroneDisplayState(ConstructionTaskKey task, ConstructionOwnerKey owner,
        ObjectKey target, ConstructionTaskStage stage, long revision)
    {
        Task = task;
        Owner = owner;
        Target = target;
        Stage = stage;
        Revision = revision;
    }

    public ConstructionTaskKey Task { get; }

    public ConstructionOwnerKey Owner { get; }

    public ObjectKey Target { get; }

    public ConstructionTaskStage Stage { get; }

    public long Revision { get; }

    public bool Equals(HostDroneDisplayState other) =>
        Task.Equals(other.Task) && Owner.Equals(other.Owner) &&
        Target.Equals(other.Target) && Stage == other.Stage && Revision == other.Revision;

    public override bool Equals(object obj) => obj is HostDroneDisplayState other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Task.GetHashCode();
            hash = (hash * 397) ^ Owner.GetHashCode();
            hash = (hash * 397) ^ Target.GetHashCode();
            hash = (hash * 397) ^ (int)Stage;
            hash = (hash * 397) ^ Revision.GetHashCode();
            return hash;
        }
    }

    public override string ToString() => Task + "|owner=" + Owner + "|target=" + Target + "|stage=" + Stage;
}
