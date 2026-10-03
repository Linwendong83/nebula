#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// Which budget bucket a drone slot sits in. The ledger moves slots between buckets as tasks
/// advance; only <see cref="DroneBudget"/> may change the counts.
/// </summary>
public enum DroneSlotBucket : byte
{
    Unknown = 0,
    Idle = 1,
    Reserved = 2,
    Active = 3,
    Returning = 4
}

/// <summary>
/// One owner's shared build/repair drone quota (TASKS.md A15, pure model).
/// </summary>
/// <remarks>
/// <para>
/// DESIGN 8.1: <c>idle + reserved + active + returning = total</c> for the same owner, across
/// build, repair and reconstruct kinds. A reserved, in-flight or returning slot is not idle, so a
/// second task cannot reuse it — this is the executable form of "1架无人机不会同时建造和维修".
/// VALIDATION I05 pins the invariant (<c>值不为负</c>); every mutator preserves it or refuses.
/// </para>
/// <para>
/// The budget counts slots only. It never names a target, a task or a construct-stat id, so a
/// display packet cannot inflate it: only <see cref="ConstructionTaskLedger"/> calls these
/// mutators, and repair occupancy shown to the game is derived from that ledger's valid tasks
/// (which is what later replaces the vanilla <c>repairerCount</c> writer at A17).
/// </para>
/// <para>
/// Not thread-safe: only the frame boundary touches it, like <see cref="CommandWindow"/>.
/// </para>
/// </remarks>
public sealed class DroneBudget
{
    /// <summary>Highest total any owner may hold. Matches the mod's builder-capability clamp (256).</summary>
    public const int MaxTotal = 256;

    private readonly ConstructionOwnerKey owner;
    private int total;
    private int reserved;
    private int active;
    private int returning;

    private long reservesTotal;
    private long releasesTotal;

    public DroneBudget(ConstructionOwnerKey owner, int total)
    {
        if (!owner.IsValid) throw new ArgumentException("A drone budget needs a valid owner.", nameof(owner));
        if (total < 0 || total > MaxTotal) throw new ArgumentOutOfRangeException(nameof(total));
        this.owner = owner;
        this.total = total;
    }

    public ConstructionOwnerKey Owner => owner;

    public int Total => total;

    public int Reserved => reserved;

    public int Active => active;

    public int Returning => returning;

    /// <summary>Slots free for a new task: <c>total - reserved - active - returning</c>.</summary>
    public int Idle => total - reserved - active - returning;

    /// <summary>Slots held by live tasks: <c>reserved + active + returning</c>.</summary>
    public int Occupied => reserved + active + returning;

    public long ReservesTotal => reservesTotal;

    public long ReleasesTotal => releasesTotal;

    /// <summary>
    /// True exactly when <c>idle + reserved + active + returning == total</c> and no bucket is
    /// negative. Called after every mutation in tests and by the ledger's audit path.
    /// </summary>
    public bool CheckInvariant() =>
        total >= 0 && total <= MaxTotal && reserved >= 0 && active >= 0 && returning >= 0 &&
        reserved + active + returning <= total && Idle >= 0 &&
        Idle + reserved + active + returning == total;

    /// <summary>
    /// Changes the owner's drone capacity (tech unlock, drone count change, R10). Refuses to shrink
    /// below the currently occupied slots: callers cancel tasks first, so capacity can never strand
    /// a live reservation.
    /// </summary>
    public bool TrySetTotal(int newTotal, out string reason)
    {
        reason = null;
        if (newTotal < 0 || newTotal > MaxTotal)
        {
            reason = "TotalOutOfRange";
            return false;
        }
        if (newTotal < Occupied)
        {
            reason = "OccupiedSlots";
            return false;
        }
        total = newTotal;
        return true;
    }

    /// <summary>Takes one idle slot into reserved for a new task.</summary>
    public bool TryReserve(out string reason)
    {
        reason = null;
        if (Idle <= 0)
        {
            reason = "NoIdleDrone";
            return false;
        }
        reserved++;
        reservesTotal++;
        return true;
    }

    /// <summary>Moves one reserved slot into active (launching).</summary>
    public bool TryActivate(out string reason)
    {
        reason = null;
        if (reserved <= 0)
        {
            reason = "NothingReserved";
            return false;
        }
        reserved--;
        active++;
        return true;
    }

    /// <summary>Moves one active slot into returning (work done, flying home).</summary>
    public bool TryBeginReturn(out string reason)
    {
        reason = null;
        if (active <= 0)
        {
            reason = "NothingActive";
            return false;
        }
        active--;
        returning++;
        return true;
    }

    /// <summary>Releases one reserved slot back to idle (cancel before launch).</summary>
    public bool ReleaseReserved()
    {
        if (reserved <= 0) return false;
        reserved--;
        releasesTotal++;
        return true;
    }

    /// <summary>Releases one active slot back to idle (cancel while flying/working).</summary>
    public bool ReleaseActive()
    {
        if (active <= 0) return false;
        active--;
        releasesTotal++;
        return true;
    }

    /// <summary>Releases one returning slot back to idle (cancel on the way home, or arrival).</summary>
    public bool ReleaseReturning()
    {
        if (returning <= 0) return false;
        returning--;
        releasesTotal++;
        return true;
    }

    /// <summary>
    /// Releases the bucket a task stage holds. Reserved releases reserved; launching, travelling
    /// and working release active; returning releases returning. Terminal stages hold nothing.
    /// </summary>
    public bool ReleaseForStage(ConstructionTaskStage stage)
    {
        switch (stage)
        {
            case ConstructionTaskStage.Reserved:
                return ReleaseReserved();
            case ConstructionTaskStage.Launching:
            case ConstructionTaskStage.Travelling:
            case ConstructionTaskStage.Working:
                return ReleaseActive();
            case ConstructionTaskStage.Returning:
                return ReleaseReturning();
            default:
                return false;
        }
    }

    public override string ToString() =>
        owner + "|total=" + total + "|idle=" + Idle + "|reserved=" + reserved + "|active=" + active +
        "|returning=" + returning;
}
