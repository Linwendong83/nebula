#region

using System;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// How a family competes for send budget.
/// </summary>
/// <remarks>
/// DESIGN 10 orders the send queue as command results and lifecycle first, then values, then visual
/// traffic, and requires a large snapshot to be interleaved by chunk rather than allowed to block
/// the reliable stream. This classification is that ordering, made explicit: a family is either
/// control traffic that must always go out, or bulk traffic that yields when the frame's budget is
/// spent.
/// </remarks>
public enum AuthorityFamilyClass : byte
{
    None = 0,

    /// <summary>Always delivered in the frame it was produced (identity, death, results, control).</summary>
    Critical = 1,

    /// <summary>Absolute state refreshes. Postponable: a later revision supersedes an earlier one.</summary>
    Bulk = 2
}

/// <summary>
/// The send- and apply-side budgets of the authority session (TASKS.md A23, VALIDATION §9).
/// </summary>
/// <remarks>
/// <para>
/// Every number here is a starting value from the design's initial budget, not a measured result.
/// A23 exists to replace them with measurements; changing any of them is a deliberate act that
/// re-runs the back-pressure validation, which is why they live in one immutable object rather than
/// as constants at each call site.
/// </para>
/// <para>
/// <see cref="Unbounded"/> disables every budget. It exists for the legacy-comparison runs and for
/// tests that assert on exact delivery; the production new-mode path uses <see cref="Default"/>.
/// </para>
/// </remarks>
public sealed class AuthorityBackpressurePolicy
{
    /// <summary>Bulk state bytes one subscriber may receive per host frame before the rest waits.</summary>
    public const long DefaultBulkBytesPerSubscriberPerFrame = 32 * 1024;

    /// <summary>Snapshot chunks one subscriber may receive per host frame.</summary>
    public const int DefaultSnapshotChunksPerSubscriberPerFrame = 64;

    /// <summary>Replica messages one client may apply per frame before the rest waits.</summary>
    public const int DefaultApplyMessagesPerFrame = 4096;

    /// <summary>
    /// Messages a client frame always applies if they are queued, even when the time budget is spent.
    /// </summary>
    /// <remarks>
    /// The time budget exists to stop a frame from spending an unbounded amount of work, not to
    /// throttle a burst of small messages down to one per frame. Without a floor, a single cold call
    /// (JIT, a first allocation) could exhaust the milliseconds and leave a snapshot install crawling
    /// one message per frame; with it, the clock can only end an apply pass that is already doing
    /// real work.
    /// </remarks>
    public const int DefaultMinApplyMessagesPerFrame = 64;

    /// <summary>Apply milliseconds one client frame may spend before the rest waits.</summary>
    public const double DefaultApplyBudgetMs = 3.0;

    /// <summary>A policy that postpones nothing. Legacy-comparison runs and exact-delivery tests use it.</summary>
    public static AuthorityBackpressurePolicy Unbounded { get; } =
        new(long.MaxValue, int.MaxValue, int.MaxValue, DefaultMinApplyMessagesPerFrame,
            double.PositiveInfinity, coalesceStatePerKey: false, "unbounded");

    /// <summary>The initial new-mode budget (VALIDATION §9), pending A23's measurements.</summary>
    public static AuthorityBackpressurePolicy Default { get; } = new(
        DefaultBulkBytesPerSubscriberPerFrame, DefaultSnapshotChunksPerSubscriberPerFrame,
        DefaultApplyMessagesPerFrame, DefaultMinApplyMessagesPerFrame, DefaultApplyBudgetMs,
        coalesceStatePerKey: true, "default");

    public AuthorityBackpressurePolicy(long bulkBytesPerSubscriberPerFrame,
        int snapshotChunksPerSubscriberPerFrame, int applyMessagesPerFrame, int minApplyMessagesPerFrame,
        double applyBudgetMs, bool coalesceStatePerKey, string name)
    {
        if (bulkBytesPerSubscriberPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(bulkBytesPerSubscriberPerFrame));
        if (snapshotChunksPerSubscriberPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(snapshotChunksPerSubscriberPerFrame));
        if (applyMessagesPerFrame <= 0) throw new ArgumentOutOfRangeException(nameof(applyMessagesPerFrame));
        if (minApplyMessagesPerFrame <= 0 || minApplyMessagesPerFrame > applyMessagesPerFrame)
        {
            throw new ArgumentOutOfRangeException(nameof(minApplyMessagesPerFrame));
        }
        if (applyBudgetMs <= 0) throw new ArgumentOutOfRangeException(nameof(applyBudgetMs));
        BulkBytesPerSubscriberPerFrame = bulkBytesPerSubscriberPerFrame;
        SnapshotChunksPerSubscriberPerFrame = snapshotChunksPerSubscriberPerFrame;
        ApplyMessagesPerFrame = applyMessagesPerFrame;
        MinApplyMessagesPerFrame = minApplyMessagesPerFrame;
        ApplyBudgetMs = applyBudgetMs;
        CoalesceStatePerKey = coalesceStatePerKey;
        Name = name ?? "custom";
    }

    /// <summary>Name used in the perf report, so a run states which budget produced it.</summary>
    public string Name { get; }

    public long BulkBytesPerSubscriberPerFrame { get; }

    public int SnapshotChunksPerSubscriberPerFrame { get; }

    public int ApplyMessagesPerFrame { get; }

    /// <summary>Messages a frame applies before the time budget may end the pass.</summary>
    public int MinApplyMessagesPerFrame { get; }

    public double ApplyBudgetMs { get; }

    /// <summary>
    /// True when a delivered batch keeps only the newest state record per key.
    /// </summary>
    /// <remarks>
    /// Coalescing is safe because a state record is an absolute value: dropping a superseded record
    /// cannot change what the replica ends up holding, and DESIGN 10 explicitly sanctions merging to
    /// the latest sample before sending. Lifecycle records are never coalesced — identity and death
    /// are not supersedable.
    /// </remarks>
    public bool CoalesceStatePerKey { get; }

    /// <summary>True when the bulk byte budget actually postpones anything.</summary>
    public bool BudgetsBulk => BulkBytesPerSubscriberPerFrame != long.MaxValue;

    /// <summary>Classifies one family.</summary>
    public static AuthorityFamilyClass Classify(AuthorityFamily family)
    {
        switch (family)
        {
            // Bulk: absolute state refreshes, the only family whose payload scales with object count
            // and whose content a later revision replaces. A future visual/effect batch belongs here
            // too, which is why the classification is a function rather than a flag on each packet.
            case AuthorityFamily.WorldState:
                return AuthorityFamilyClass.Bulk;
            default:
                return AuthorityFamilyClass.Critical;
        }
    }

    public static bool IsCritical(AuthorityFamily family) => Classify(family) == AuthorityFamilyClass.Critical;

    public static bool IsBulk(AuthorityFamily family) => Classify(family) == AuthorityFamilyClass.Bulk;
}

/// <summary>
/// One subscriber's backpressure bookkeeping for the current host frame.
/// </summary>
/// <remarks>
/// The counters are per subscriber, not per scope, because VALIDATION §9's fairness question is
/// asked per subscriber ("关键生命不能持续饿死"): a subscriber that accumulates deferrals across many
/// scopes is exactly the case a per-scope counter would hide.
/// </remarks>
public sealed class AuthorityDeferralState
{
    /// <summary>Bulk bytes already sent to this subscriber in the current frame.</summary>
    public long BulkBytesThisFrame { get; private set; }

    /// <summary>Bulk bytes the current frame postponed for this subscriber.</summary>
    public long BulkBytesDeferredThisFrame { get; private set; }

    /// <summary>Snapshot chunks the current frame postponed for this subscriber.</summary>
    public long SnapshotChunksDeferredThisFrame { get; private set; }

    /// <summary>Consecutive frames in which something was postponed for this subscriber.</summary>
    public int ConsecutiveDeferredFrames { get; private set; }

    /// <summary>Longest such run ever observed (VALIDATION §9's longest wait).</summary>
    public int LongestDeferralFrames { get; private set; }

    /// <summary>Snapshot chunks postponed to later frames, cumulative.</summary>
    public long DeferredSnapshotChunks { get; private set; }

    /// <summary>Baselines still being paced out to this subscriber.</summary>
    public int SnapshotsInFlight { get; set; }

    public void BeginFrame()
    {
        BulkBytesThisFrame = 0;
        BulkBytesDeferredThisFrame = 0;
        SnapshotChunksDeferredThisFrame = 0;
    }

    /// <summary>True when another <paramref name="bytes"/> of bulk traffic fits in this frame.</summary>
    public bool TryTakeBulk(long bytes, long budget)
    {
        if (BulkBytesThisFrame + bytes > budget) return false;
        BulkBytesThisFrame += bytes;
        return true;
    }

    /// <summary>
    /// Charges bulk bytes past the budget.
    /// </summary>
    /// <remarks>
    /// Used for the one guaranteed-progress record per scope per frame: without it a single record
    /// larger than the whole budget would be postponed every frame and the subscription would never
    /// advance. Charging it keeps the next frame's accounting honest instead of letting the overshoot
    /// disappear from the byte counters.
    /// </remarks>
    public void ForceTakeBulk(long bytes) => BulkBytesThisFrame += bytes;

    /// <summary>Records that the frame postponed this many bulk bytes for the subscriber.</summary>
    public void NoteDeferredBulk(long bytes)
    {
        if (bytes > 0) BulkBytesDeferredThisFrame += bytes;
    }

    /// <summary>Records that the frame postponed this many snapshot chunks for the subscriber.</summary>
    public void NoteDeferredSnapshotChunks(long chunks)
    {
        if (chunks <= 0) return;
        SnapshotChunksDeferredThisFrame += chunks;
        DeferredSnapshotChunks += chunks;
    }

    /// <summary>
    /// Closes the frame: a frame that postponed anything extends the deferral run, a frame that
    /// postponed nothing ends it. One place, so "consecutive" cannot be reset by whichever scope
    /// happened to deliver cleanly first.
    /// </summary>
    public void EndOfFrame()
    {
        if (BulkBytesDeferredThisFrame > 0 || SnapshotChunksDeferredThisFrame > 0)
        {
            ConsecutiveDeferredFrames++;
            if (ConsecutiveDeferredFrames > LongestDeferralFrames)
            {
                LongestDeferralFrames = ConsecutiveDeferredFrames;
            }
        }
        else
        {
            ConsecutiveDeferredFrames = 0;
        }
    }
}
