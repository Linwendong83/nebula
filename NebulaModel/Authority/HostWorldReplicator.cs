#region

using System;
using System.Collections.Generic;
using System.Diagnostics;
using NebulaModel.Packets.Authority;

#endregion

namespace NebulaModel.Authority;

/// <summary>
/// The host's read-only view of the canonical world, as the replicator sees it.
/// </summary>
/// <remarks>
/// <para>
/// A06 develops the whole replication path against a fake implementation of this interface. The
/// real adapter that reads the vanilla pools arrives with the domain cards (A08 and later); nothing
/// in the replicator may reach into game types directly, which is what keeps the scan-then-publish
/// loop testable without a game process.
/// </para>
/// <para>
/// The two reads have deliberately different failure meanings. <see cref="TryReadMembers"/>
/// returning false means "this scope cannot be read right now" and the frame skips it: treating it
/// as an empty scope would publish a wave of fake deaths. Returning true with an empty list is how
/// a scope that really lost all its members is expressed. <see cref="TryReadState"/> returning
/// false means this member has no canonical state to publish (yet), not that it is gone.
/// </para>
/// <para>
/// State blobs must not exceed <see cref="AuthorityLimits.StateRecordMaxBytes"/>; the adapter owns
/// the canonical field selection that keeps them bounded (A01's member tables are the reference).
/// </para>
/// </remarks>
public interface IHostWorldView
{
    /// <summary>Reads the current member keys of a scope into <paramref name="members"/>.</summary>
    bool TryReadMembers(ScopeKey scope, List<ObjectKey> members);

    /// <summary>Reads the canonical state of one member, or false when it has none.</summary>
    bool TryReadState(ObjectKey key, out byte[] state);
}

/// <summary>
/// Receives the packets the replicator produces for one subscriber.
/// </summary>
/// <remarks>
/// A06 keeps this a seam: the tests record into a list, the network layer (later cards) forwards to
/// the subscriber's connection. The replicator never touches a socket itself.
/// </remarks>
public interface IReplicationSink
{
    void Send(ushort subscriberId, AuthorityEnvelopePacket packet);
}

/// <summary>
/// The host's standard replicator (TASKS.md A06): a frame-end canonical scan that publishes
/// membership and absolute state as deterministic, revisioned events.
/// </summary>
/// <remarks>
/// <para>
/// The initial correctness path is a frame-end scan of the canonical fields, exactly as TASKS.md
/// allows: every subscribed scope's membership is diffed against the world view and every member's
/// canonical state is compared with the last published bytes. Nothing depends on a patch having
/// reported the change, so a writer that is not hooked yet cannot silently escape replication; the
/// scan cost is the price of that guarantee until A23 replaces it with measured, still-complete
/// capture.
/// </para>
/// <para>
/// Ordering rules the method maintains: within one frame, all spawns are published before any
/// state, and states before despawns, so a subscriber never sees HP for an identity it has not
/// been given. An object that both spawns and dies inside one frame is published as spawn then
/// despawn — the subscriber's tombstone lands on the key that actually died. A recycled pool slot
/// is a different key (new generation) and is never merged with the old object's history.
/// </para>
/// <para>
/// Everything here runs at the frame boundary A01 proved quiescent — capture, subscription and
/// delivery alike — so the state needs no locking and the log order is the delivery order.
/// </para>
/// </remarks>
public sealed class HostWorldReplicator
{
    private readonly AuthorityEpoch epoch;
    private readonly IHostWorldView worldView;
    private readonly IReplicationSink sink;
    private readonly int logCapacity;
    private readonly AuthorityBackpressurePolicy policy;
    private readonly Dictionary<ScopeKey, ScopeReplicationState> scopes = [];
    private readonly Dictionary<ushort, AuthorityDeferralState> deferrals = [];
    private readonly Dictionary<(ushort Subscriber, ScopeKey Scope), PendingSnapshotSend> pendingSnapshots = [];
    private readonly List<(ushort Subscriber, ScopeKey Scope)> finishedSnapshots = [];
    private readonly List<ObjectKey> memberScratch = [];
    private readonly HashSet<ObjectKey> memberSetScratch = [];
    private readonly List<ScopeEvent> pendingScratch = [];
    private readonly List<ObjectKey> despawnScratch = [];
    private readonly List<ScopeDigestMember> digestScratch = [];

    /// <summary>
    /// One baseline whose chunks are being paced out over several frames (A23).
    /// </summary>
    /// <remarks>
    /// A snapshot larger than one frame's chunk allowance is queued here after its Begin is sent.
    /// The Begin/Commit pair stays on the critical path, so the conversation is still ordered
    /// Begin → chunks → Commit; only the bulk middle is spread, which is what keeps other scopes'
    /// lifecycle traffic flowing during a large baseline (DESIGN 10, VALIDATION §9 fairness).
    /// </remarks>
    private sealed class PendingSnapshotSend
    {
        public ScopeKey Scope;
        public long BaselineId;
        public long SubscriptionEpoch;
        public byte[] Image;
        public int ChunkCount;
        public int NextChunkIndex;
        public ulong Hash;
    }

    private long framesCaptured;
    private long lastCaptureTick;
    private long eventsAppended;
    private long scopesSkipped;
    private long oversizedStatesRefused;
    private long packetsSent;
    private long baselineCounter;
    private long subscriptionEpochCounter;
    private long snapshotsSent;
    private long snapshotRefusedTotal;
    private long acksAccepted;
    private long acksRejected;
    private long acksUnknown;
    private long eventsTrimmed;
    private long resyncsServed;
    private long digestsPublished;
    private long lastDigestTick;

    /// <param name="epoch">World epoch every published key and envelope carries.</param>
    /// <param name="worldView">The canonical world source.</param>
    /// <param name="sink">Where produced packets are handed for delivery.</param>
    /// <param name="logCapacity">Retained events per scope before a lagging subscriber needs a baseline.</param>
    /// <param name="policy">Send budget (A23). Defaults to the measured-pending initial budget.</param>
    /// <param name="meter">Performance meter to feed (A23). A private one is created when omitted.</param>
    public HostWorldReplicator(AuthorityEpoch epoch, IHostWorldView worldView, IReplicationSink sink,
        int logCapacity = 4096, AuthorityBackpressurePolicy policy = null, AuthorityPerfMeter meter = null)
    {
        if (!epoch.IsValid) throw new ArgumentException("HostWorldReplicator needs a valid epoch.", nameof(epoch));
        this.epoch = epoch;
        this.worldView = worldView ?? throw new ArgumentNullException(nameof(worldView));
        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.logCapacity = logCapacity;
        this.policy = policy ?? AuthorityBackpressurePolicy.Default;
        Metrics = meter ?? new AuthorityPerfMeter("host");
        Revisions = new ObjectRevisionTracker();
    }

    /// <summary>Revision minting for this world. One source, per key with its generation.</summary>
    public ObjectRevisionTracker Revisions { get; }

    /// <summary>Capture/apply/traffic counters for this world's replication (A23).</summary>
    public AuthorityPerfMeter Metrics { get; }

    /// <summary>The send budget in force. Read-only: budgets change by a deliberate run parameter.</summary>
    public AuthorityBackpressurePolicy Policy => policy;

    public int ScopeCount => scopes.Count;

    public long FramesCaptured => framesCaptured;

    public long EventsAppended => eventsAppended;

    /// <summary>Host tick of the most recent capture, or 0 before the first frame.</summary>
    public long LastCaptureTick => lastCaptureTick;

    /// <summary>Frames a scope could not be read. Persistent growth means the adapter is broken.</summary>
    public long ScopesSkipped => scopesSkipped;

    /// <summary>Frames refused because a member's canonical state exceeded the record ceiling.</summary>
    public long OversizedStatesRefused => oversizedStatesRefused;

    public long PacketsSent => packetsSent;

    /// <summary>Baselines frozen and sent to a subscriber.</summary>
    public long SnapshotsSent => snapshotsSent;

    /// <summary>Subscriptions refused a baseline (unreadable scope, oversized snapshot).</summary>
    public long SnapshotRefusedTotal => snapshotRefusedTotal;

    public long AcksAccepted => acksAccepted;
    public long AcksRejected => acksRejected;

    /// <summary>Acks for no pending baseline. Either a replay or a bookkeeping bug.</summary>
    public long AcksUnknown => acksUnknown;

    /// <summary>Log events reclaimed because every subscriber was covered by a baseline or delivery.</summary>
    public long EventsTrimmed => eventsTrimmed;

    /// <summary>Resync requests answered with a fresh baseline (A20).</summary>
    public long ResyncsServed => resyncsServed;

    /// <summary>Digest messages handed to subscribers (A20).</summary>
    public long DigestsPublished => digestsPublished;

    /// <summary>
    /// Starts one subscriber's subscription of a scope with a frozen baseline (DESIGN 9.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A new or broken subscription never receives a join burst: the host freezes the scope's
    /// membership and canonical state at the current log position, sends it as
    /// Begin/Chunk/Commit, and holds every stream packet until the subscriber's ack reports the
    /// baseline installed. The backlog of events that happened while the client was installing is
    /// delivered after the ack, in log order, so no event is either lost or applied early.
    /// </para>
    /// <para>
    /// Re-subscribing an active, healthy subscriber resumes its stream instead: no new baseline,
    /// no sequence reset. A subscriber whose stream broke recovers only through a fresh baseline —
    /// the next call replaces its cursor with a new subscription epoch, which is what makes the
    /// old stream's tail refusable.
    /// </para>
    /// </remarks>
    public void Subscribe(ushort subscriberId, ScopeKey scope, bool digestOnly = false)
    {
        var state = GetOrCreateScope(scope);

        // A digest-only observation holds no baseline and no stream: its cursor exists only so the
        // periodic digest knows where to send the summary. Re-issuing it is a no-op; upgrading to a
        // full subscription replaces the cursor through the normal path below.
        if (digestOnly)
        {
            if (!state.TryGetSubscriber(subscriberId, out var existing) || !existing.DigestOnly)
            {
                state.AddSubscriber(subscriberId, 0, digestOnly: true);
            }
            return;
        }

        if (CountScopesFor(subscriberId) >= AuthorityLimits.ScopesPerSubscriberMax &&
            !(state.TryGetSubscriber(subscriberId, out var held) && held.Active))
        {
            // A subscriber cannot grow its delivery surface without bound; the request is refused
            // visibly instead of silently inflating every later capture.
            snapshotRefusedTotal++;
            return;
        }

        // A baseline already on its way is the answer to a repeat request. With chunks paced across
        // frames (A23) a second StartSnapshotSubscription would race the first, and the client
        // refuses a different baseline while one is staging — fail-closed, but a stall until the
        // next resync. Waiting for the in-flight ack is the correct answer to a duplicate request.
        if (state.TryGetSubscriber(subscriberId, out var inFlight) && inFlight.PendingBaselineId != 0)
        {
            return;
        }

        if (state.TryGetSubscriber(subscriberId, out var cursor) && cursor.Active &&
            !cursor.NeedsResync && cursor.PendingBaselineId == 0)
        {
            DeliverScope(state);
            TrimCoveredLog(state);
            return;
        }

        if (!ReadMembers(state, out var current))
        {
            // The scope cannot be read; refuse to serve it rather than present an empty world.
            state.AddSubscriber(subscriberId, 0);
            state.TryGetSubscriber(subscriberId, out cursor);
            cursor.NeedsResync = true;
            return;
        }

        StartSnapshotSubscription(state, subscriberId, current);
    }

    /// <summary>Counts the scopes a subscriber holds an active cursor for.</summary>
    private int CountScopesFor(ushort subscriberId)
    {
        var count = 0;
        foreach (var state in scopes.Values)
        {
            if (state.TryGetSubscriber(subscriberId, out var cursor) && cursor.Active) count++;
        }
        return count;
    }

    /// <summary>
    /// Answers a replica's resync request (A20): the subscription is marked broken and immediately
    /// re-baselined with a new subscription epoch, so the old stream's tail stays refusable.
    /// </summary>
    /// <remarks>
    /// A baseline still in flight is discarded first, not awaited. The client only acks a baseline it
    /// installed, so a broken conversation leaves <c>PendingBaselineId</c> set forever; a resync is
    /// exactly the signal that the conversation is dead, and without clearing the marker the host
    /// would refuse (or, before A23, half-serve) every later recovery. The paced chunks of the
    /// discarded baseline are dropped by the pump, which checks the baseline id against the cursor.
    /// </remarks>
    public bool RequestResync(ushort subscriberId, ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope, out var state) ||
            !state.TryGetSubscriber(subscriberId, out var cursor) ||
            !cursor.Active)
        {
            return false;
        }
        cursor.NeedsResync = true;
        cursor.PendingBaselineId = 0;
        resyncsServed++;
        Subscribe(subscriberId, scope);
        return true;
    }

    /// <summary>
    /// Publishes every scope's canonical digest to its subscribers when the interval is due (A20).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The digest is computed once per scope from the member table — the same canonical records the
    /// stream publishes — and stamped per subscriber with that subscriber's own stream position and
    /// baseline, because a scope's subscribers can sit on different baselines. A subscriber still
    /// installing its baseline receives none: the replica drops digests outside Live anyway, so
    /// sending one would only be dropped twice.
    /// </para>
    /// <para>
    /// The interval is a duty cycle, not a deadline: the check runs at the same post-frame safe
    /// point as capture, and a quiet period simply skips. Scopes with no active subscriber are not
    /// digested at all — a digest nobody reads is pure cost.
    /// </para>
    /// </remarks>
    public void PublishDigests(long hostTick, long intervalTicks)
    {
        if (intervalTicks <= 0) return;
        if (lastDigestTick != 0 && hostTick - lastDigestTick < intervalTicks) return;
        lastDigestTick = hostTick;

        foreach (var state in scopes.Values)
        {
            var hasListener = false;
            foreach (var pair in state.Subscribers)
            {
                if (pair.Value.Active && pair.Value.PendingBaselineId == 0 && !pair.Value.NeedsResync)
                {
                    hasListener = true;
                    break;
                }
            }
            if (!hasListener) continue;

            digestScratch.Clear();
            foreach (var pair in state.Members)
            {
                digestScratch.Add(new ScopeDigestMember(pair.Key, pair.Value.Revision, pair.Value.State));
            }
            var memberCount = digestScratch.Count;
            var digest = ScopeDigestComputer.Compute(digestScratch);

            foreach (var pair in state.Subscribers)
            {
                var cursor = pair.Value;
                if (!cursor.Active || cursor.PendingBaselineId != 0 || cursor.NeedsResync) continue;
                Send(pair.Key, AuthorityScopeDigestPacket.Create(
                    ControlHeader(AuthorityFamily.ScopeDigest, hostTick), state.Scope,
                    cursor.SubscriptionEpoch, cursor.BaselineId, cursor.NextStreamSequence - 1,
                    memberCount, digest));
                digestsPublished++;
            }
        }
    }

    /// <summary>Stops one subscriber's stream of a scope, keeping the cursor for continuity.</summary>
    public void Unsubscribe(ushort subscriberId, ScopeKey scope)
    {
        if (scopes.TryGetValue(scope, out var state) &&
            state.TryGetSubscriber(subscriberId, out var cursor))
        {
            cursor.Active = false;
            // The old baseline may never have been ACKed. Leaving the scope abandons that
            // conversation; a later subscription must snapshot the world as it is then.
            cursor.PendingBaselineId = 0;
            cursor.NeedsResync = true;
            pendingSnapshots.Remove((subscriberId, scope));
        }
    }

    /// <summary>Removes a subscriber from every scope. Used when its connection is gone.</summary>
    public void RemoveSubscriber(ushort subscriberId)
    {
        foreach (var state in scopes.Values)
        {
            state.RemoveSubscriber(subscriberId);
        }
        // A paced baseline for a departed connection has nobody to reach; dropping it here keeps the
        // queue bounded by live subscribers rather than by everyone who ever subscribed.
        foreach (var key in PendingSnapshotKeysFor(subscriberId))
        {
            pendingSnapshots.Remove(key);
        }
        deferrals.Remove(subscriberId);
    }

    private List<(ushort Subscriber, ScopeKey Scope)> PendingSnapshotKeysFor(ushort subscriberId)
    {
        List<(ushort, ScopeKey)> keys = null;
        foreach (var key in pendingSnapshots.Keys)
        {
            if (key.Subscriber != subscriberId) continue;
            keys ??= [];
            keys.Add(key);
        }
        return keys ?? [];
    }

    /// <summary>True when the subscriber's stream of this scope is waiting for a baseline.</summary>
    public bool NeedsResync(ushort subscriberId, ScopeKey scope) =>
        scopes.TryGetValue(scope, out var state) &&
        state.TryGetSubscriber(subscriberId, out var cursor) &&
        cursor.NeedsResync;

    /// <summary>
    /// A22 matrix diagnostic: one line per scope with its members and each subscriber's cursor.
    /// Read-only.
    /// </summary>
    public List<string> DescribeScopes()
    {
        var lines = new List<string>();
        foreach (var pair in scopes)
        {
            var state = pair.Value;
            lines.Add("host-scope " + state.Scope + " members=" + state.Members.Count +
                      " subscribers=" + state.SubscriberCount);
            foreach (var s in state.Subscribers)
            {
                lines.Add("  sub=" + s.Key + " active=" + s.Value.Active +
                          " pendingBaseline=" + s.Value.PendingBaselineId +
                          " needsResync=" + s.Value.NeedsResync +
                          " baseline=" + s.Value.BaselineId +
                          " subEpoch=" + s.Value.SubscriptionEpoch +
                          " nextStream=" + s.Value.NextStreamSequence +
                          " lastLog=" + s.Value.LastLogSequence);
            }
        }
        foreach (var pair in deferrals)
        {
            lines.Add("host-deferral sub=" + pair.Key + " bulkBytesThisFrame=" + pair.Value.BulkBytesThisFrame +
                      " consecutiveDeferredFrames=" + pair.Value.ConsecutiveDeferredFrames +
                      " longestDeferralFrames=" + pair.Value.LongestDeferralFrames +
                      " deferredSnapshotChunks=" + pair.Value.DeferredSnapshotChunks +
                      " snapshotsInFlight=" + pair.Value.SnapshotsInFlight);
        }
        return lines;
    }

    /// <summary>A23 diagnostic: the replication performance summary, one line per fact.</summary>
    public List<string> DescribePerf()
    {
        var lines = Metrics.Describe();
        lines.Add("perf budget=" + policy.Name +
                  " bulkBytesPerSubscriberPerFrame=" + policy.BulkBytesPerSubscriberPerFrame +
                  " snapshotChunksPerSubscriberPerFrame=" + policy.SnapshotChunksPerSubscriberPerFrame +
                  " coalesceStatePerKey=" + policy.CoalesceStatePerKey);
        return lines;
    }

    /// <summary>
    /// One frame-end canonical scan: diff every subscribed scope against the world, then deliver.
    /// </summary>
    /// <remarks>
    /// The frame also owns the two A23 budgets. Per-subscriber bulk bytes and per-subscriber snapshot
    /// chunks are both refilled here, so a frame's cost is bounded by the policy rather than by how
    /// much changed; whatever did not fit stays pending in the scope log (or in the paced snapshot
    /// queue) and is delivered by a later frame, in the same order. Timing wraps the whole pass so
    /// the reported capture cost is what VALIDATION §9 budgets, not a subset of it.
    /// </remarks>
    public void CaptureFrame(long hostTick)
    {
        var started = Stopwatch.GetTimestamp();
        framesCaptured++;
        lastCaptureTick = hostTick;
        foreach (var deferral in deferrals.Values)
        {
            deferral.BeginFrame();
        }

        var packetsBefore = packetsSent;
        var bytesBefore = Metrics.BytesSent;
        var longestDeferral = 0;
        long deferredBulkBytes = 0;

        foreach (var pair in scopes)
        {
            // Keep inactive cursors for reconnect continuity, but do not scan worlds nobody reads.
            if (!HasActiveSubscriber(pair.Value)) continue;
            CaptureScope(pair.Value, hostTick);
            DeliverScope(pair.Value);
            TrimCoveredLog(pair.Value);
        }

        // Snapshot chunks go out after the scopes so a paced baseline never delays this frame's
        // lifecycle or state delivery — the ordering DESIGN 10 asks for.
        PumpSnapshots();

        foreach (var deferral in deferrals.Values)
        {
            deferral.EndOfFrame();
            if (deferral.ConsecutiveDeferredFrames > longestDeferral)
            {
                longestDeferral = deferral.ConsecutiveDeferredFrames;
            }
            deferredBulkBytes += deferral.BulkBytesDeferredThisFrame;
        }

        Metrics.RecordHostCapture(hostTick, ElapsedMs(started), (int)(packetsSent - packetsBefore),
            Metrics.BytesSent - bytesBefore, RetainedEventCount(), deferredBulkBytes, longestDeferral);
    }

    /// <summary>Events still retained in the logs: the queue depth a lagging subscriber would face.</summary>
    private int RetainedEventCount()
    {
        var total = 0;
        foreach (var state in scopes.Values)
        {
            total += state.LogCount;
        }
        return total;
    }

    /// <summary>
    /// Sends the next slice of every paced baseline (A23), finishing the conversation when done.
    /// </summary>
    /// <remarks>
    /// A pending snapshot whose subscriber left, unsubscribed or re-baselined is dropped rather than
    /// shipped to a cursor that no longer wants it; the client's own staging refusal is the backstop,
    /// not the plan.
    /// </remarks>
    private void PumpSnapshots()
    {
        if (pendingSnapshots.Count == 0) return;
        finishedSnapshots.Clear();
        foreach (var entry in pendingSnapshots)
        {
            var (subscriberId, scope) = entry.Key;
            var pending = entry.Value;
            if (!scopes.TryGetValue(scope, out var state) ||
                !state.TryGetSubscriber(subscriberId, out var cursor) ||
                !cursor.Active || cursor.PendingBaselineId != pending.BaselineId)
            {
                // The subscription this baseline belonged to is gone. Its chunks are meaningless.
                finishedSnapshots.Add(entry.Key);
                continue;
            }

            var budget = policy.SnapshotChunksPerSubscriberPerFrame;
            var sentThisFrame = 0;
            while (pending.NextChunkIndex < pending.ChunkCount && sentThisFrame < budget)
            {
                SendChunk(subscriberId, scope, pending.Image, pending.BaselineId, pending.SubscriptionEpoch,
                    pending.NextChunkIndex);
                pending.NextChunkIndex++;
                sentThisFrame++;
            }

            if (pending.NextChunkIndex < pending.ChunkCount)
            {
                var postponement = pending.ChunkCount - pending.NextChunkIndex;
                DeferralFor(subscriberId).NoteDeferredSnapshotChunks(postponement);
                continue;
            }

            Send(subscriberId, AuthoritySnapshotCommitPacket.Create(
                ControlHeader(AuthorityFamily.SnapshotCommit, lastCaptureTick), scope,
                pending.BaselineId, pending.SubscriptionEpoch, pending.ChunkCount, pending.Hash));
            snapshotsSent++;
            finishedSnapshots.Add(entry.Key);
            DeferralFor(subscriberId).SnapshotsInFlight--;
        }

        foreach (var key in finishedSnapshots)
        {
            pendingSnapshots.Remove(key);
        }
    }

    private void SendChunk(ushort subscriberId, ScopeKey scope, byte[] image, long baselineId,
        long subscriptionEpoch, int index)
    {
        var offset = (long)index * AuthorityLimits.ChunkMaxBytes;
        var length = (int)Math.Min(AuthorityLimits.ChunkMaxBytes, image.Length - offset);
        var chunk = new byte[length];
        Buffer.BlockCopy(image, (int)offset, chunk, 0, length);
        Send(subscriberId, AuthoritySnapshotChunkPacket.Create(
            ControlHeader(AuthorityFamily.SnapshotChunk, lastCaptureTick), scope,
            baselineId, subscriptionEpoch, index, chunk));
    }

    private AuthorityDeferralState DeferralFor(ushort subscriberId)
    {
        if (!deferrals.TryGetValue(subscriberId, out var state))
        {
            state = new AuthorityDeferralState();
            deferrals[subscriberId] = state;
        }
        return state;
    }

    private static double ElapsedMs(long startedTimestamp) =>
        (Stopwatch.GetTimestamp() - startedTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Applies a subscriber's snapshot acknowledgement: the baseline was installed (or refused) and
    /// the stream may resume with the backlog the subscriber has not seen.
    /// </summary>
    /// <remarks>
    /// An accepted ack delivers the backlog first, then a zero-record state batch that tells the
    /// replica "everything before this position is applied" — the marker the replica leaves
    /// CatchingUp on. A rejected ack stops the subscription (fail-closed): the recovery is a new
    /// subscription with a new baseline, not a retry over the same bytes.
    /// </remarks>
    public void OnSnapshotAck(ushort subscriberId, long baselineId, long lastAppliedSequence, bool accepted)
    {
        foreach (var state in scopes.Values)
        {
            if (!state.TryGetSubscriber(subscriberId, out var cursor) ||
                cursor.PendingBaselineId != baselineId)
            {
                continue;
            }

            cursor.PendingBaselineId = 0;
            cursor.ClientAppliedSequence = lastAppliedSequence;
            if (!accepted)
            {
                cursor.NeedsResync = true;
                acksRejected++;
                return;
            }

            acksAccepted++;
            DeliverScope(state);
            SendResumeMarker(state, subscriberId, cursor);
            TrimCoveredLog(state);
            return;
        }
        acksUnknown++;
    }

    /// <summary>
    /// Freezes the scope at the current log position and sends it as Begin/Chunk/Commit.
    /// </summary>
    private void StartSnapshotSubscription(ScopeReplicationState scope, ushort subscriberId,
        List<ObjectKey> current)
    {
        var startLogSequence = scope.NextLogSequence - 1;
        scope.AddSubscriber(subscriberId, startLogSequence);
        if (!scope.TryGetSubscriber(subscriberId, out var cursor))
        {
            // AddSubscriber just created this cursor; reaching here would be a defect, and
            // proceeding without a cursor would strand the subscription unacked.
            snapshotRefusedTotal++;
            return;
        }

        var baselineId = ++baselineCounter;
        cursor.SubscriptionEpoch = ++subscriptionEpochCounter;
        cursor.StartLogSequence = startLogSequence;
        cursor.LastLogSequence = startLogSequence;
        cursor.BaselineId = baselineId;
        cursor.PendingBaselineId = baselineId;
        scope.BaselineId = baselineId;

        // Freeze: the member table is completed from the world view at this safe point, so the
        // baseline is a full statement — identity and current state for every current member. The
        // frozen state becomes the member's last published record, which keeps the post-install
        // scan quiet until something really changes.
        var members = new List<SnapshotMemberRecord>(current.Count);
        foreach (var key in current)
        {
            if (!scope.Members.TryGetValue(key, out var record))
            {
                record = new HostMemberRecord(Revisions.Next(key), null);
                scope.Members[key] = record;
            }
            worldView.TryReadState(key, out var canonical);
            if (canonical != null) record.State = canonical;
            members.Add(new SnapshotMemberRecord(key, record.Revision, canonical));
        }

        var frozen = new FrozenScopeSnapshot(scope.Scope, baselineId, cursor.SubscriptionEpoch,
            startLogSequence, lastCaptureTick, members);
        if (!AuthoritySnapshotCodec.TryEncode(frozen, out var image))
        {
            // The adapter handed out state that cannot be shipped as one baseline. The
            // subscription stops here rather than sending a partial world (fail-closed).
            scope.RemoveSubscriber(subscriberId);
            snapshotRefusedTotal++;
            return;
        }

        var hash = AuthoritySnapshotCodec.Hash(image);
        var chunkCount = AuthoritySnapshotCodec.ChunkCountFor(image.Length);

        Send(subscriberId, AuthoritySnapshotBeginPacket.Create(
            ControlHeader(AuthorityFamily.SnapshotBegin, lastCaptureTick), scope.Scope, baselineId,
            cursor.SubscriptionEpoch, startLogSequence, image.Length, chunkCount, hash));

        if (chunkCount <= policy.SnapshotChunksPerSubscriberPerFrame)
        {
            // The whole baseline fits one frame's chunk allowance: send it and finish, exactly as
            // before the budget existed.
            for (var index = 0; index < chunkCount; index++)
            {
                SendChunk(subscriberId, scope.Scope, image, baselineId, cursor.SubscriptionEpoch, index);
            }
            Send(subscriberId, AuthoritySnapshotCommitPacket.Create(
                ControlHeader(AuthorityFamily.SnapshotCommit, lastCaptureTick), scope.Scope,
                baselineId, cursor.SubscriptionEpoch, chunkCount, hash));
            snapshotsSent++;
            return;
        }

        // Larger than one frame's allowance: the Begin is out, the middle is paced across the frames
        // that follow, and the Commit is sent by the pump when the last chunk has gone. The
        // conversation stays ordered; it just no longer monopolises a frame.
        var pendingKey = (Subscriber: subscriberId, Scope: scope.Scope);
        var replacing = pendingSnapshots.ContainsKey(pendingKey);
        pendingSnapshots[pendingKey] = new PendingSnapshotSend
        {
            Scope = scope.Scope,
            BaselineId = baselineId,
            SubscriptionEpoch = cursor.SubscriptionEpoch,
            Image = image,
            ChunkCount = chunkCount,
            Hash = hash
        };
        if (!replacing)
        {
            // A superseded baseline replaces its predecessor's slot, so the in-flight count stays a
            // count of live conversations rather than of everything ever started.
            DeferralFor(subscriberId).SnapshotsInFlight++;
        }
    }

    /// <summary>
    /// A zero-record state batch that ends the backlog: applying it is how the replica's scope
    /// leaves CatchingUp for Live (DESIGN 9.2's "接续到 B 后切到 Live", with B = the marker).
    /// </summary>
    private void SendResumeMarker(ScopeReplicationState scope, ushort subscriberId, SubscriberCursor cursor)
    {
        if (!AuthorityWorldStateCodec.TryEncode([], out var data))
        {
            throw new InvalidOperationException("An empty state batch cannot fail its own codec.");
        }
        Send(subscriberId, Stamped(AuthorityWorldStatePacket.Create(
            Header(AuthorityFamily.WorldState, cursor, lastCaptureTick, data.Length),
            scope.Scope, cursor.BaselineId, recordCount: 0, data), cursor));
    }

    /// <summary>Stamps the subscription epoch onto a stream packet before delivery.</summary>
    private static AuthorityEnvelopePacket Stamped<TPacket>(TPacket packet, SubscriberCursor cursor)
        where TPacket : AuthorityEnvelopePacket
    {
        packet.SubscriptionEpoch = cursor.SubscriptionEpoch;
        return packet;
    }

    /// <summary>
    /// Header for the snapshot control families. They are not part of the per-scope stream
    /// sequence space: the conversation is Begin → chunks → Commit → Ack, and each message is
    /// complete on its own, so no stream position is consumed.
    /// </summary>
    private AuthorityEnvelopeHeader ControlHeader(AuthorityFamily family, long hostTick) =>
        new(AuthoritySchema.V1, family, epoch, connection: default, sequence: 0, hostTick,
            claimedPlayerId: 0, payloadLength: 0);

    /// <summary>
    /// Reclaims log events every subscriber is done with: delivered, or covered by the baseline
    /// they are currently installing. This is the deterministic form of tombstone/log reclamation —
    /// bound to the baseline cutoff and to delivery, never to a timer.
    /// </summary>
    private void TrimCoveredLog(ScopeReplicationState scope)
    {
        // With nobody listening the log is dead weight; a later subscriber always arrives through
        // a fresh baseline, never through the log's history.
        var firstRetained = scope.NextLogSequence;
        foreach (var pair in scope.Subscribers)
        {
            var cursor = pair.Value;
            // A digest-only subscriber holds no log position, so it must never pin reclamation.
            if (!cursor.Active || cursor.NeedsResync || cursor.DigestOnly) continue;
            var coveredThrough = cursor.PendingBaselineId != 0 ? cursor.StartLogSequence : cursor.LastLogSequence;
            firstRetained = Math.Min(firstRetained, coveredThrough + 1);
        }
        eventsTrimmed += scope.TrimEventsBefore(firstRetained);
    }

    private void CaptureScope(ScopeReplicationState scope, long hostTick)
    {
        if (!ReadMembers(scope, out var current)) return;
        memberSetScratch.Clear();
        foreach (var key in current) memberSetScratch.Add(key);

        // 1. Spawns first: identity precedes every HP this frame will publish.
        foreach (var key in current)
        {
            if (scope.Members.ContainsKey(key)) continue;
            var revision = Revisions.Next(key);
            scope.Members[key] = new HostMemberRecord(revision, null);
            Append(scope, new ScopeEvent(scope.NextLogSequence, ScopeEventKind.Spawn, key, revision, hostTick,
                null, scope.BaselineId));
        }

        // 2. Absolute state for members whose canonical bytes changed. The comparison is against
        //    the last published bytes, not against a hook's claim, so nothing escapes by being
        //    unwired.
        foreach (var key in current)
        {
            if (!scope.Members.TryGetValue(key, out var record)) continue;
            if (!worldView.TryReadState(key, out var canonical)) continue;
            if (canonical.Length > AuthorityLimits.StateRecordMaxBytes)
            {
                // Refuse the whole frame for this scope: publishing the other members would let a
                // silently unpublishable field diverge.
                oversizedStatesRefused++;
                return;
            }
            if (SameBytes(record.State, canonical)) continue;
            var revision = Revisions.Next(key);
            record.Revision = revision;
            record.State = canonical;
            Append(scope, new ScopeEvent(scope.NextLogSequence, ScopeEventKind.State, key, revision, hostTick,
                canonical, scope.BaselineId));
        }

        // 3. Despawns last, with a final revision, so a death can never be overwritten by an older
        //    state still in flight.
        despawnScratch.Clear();
        foreach (var key in scope.Members.Keys)
        {
            if (!memberSetScratch.Contains(key)) despawnScratch.Add(key);
        }
        foreach (var key in despawnScratch)
        {
            var revision = Revisions.Next(key);
            scope.Members.Remove(key);
            Append(scope, new ScopeEvent(scope.NextLogSequence, ScopeEventKind.Despawn, key, revision, hostTick,
                null, scope.BaselineId));
            // The generation is over on the host; the client's tombstone is what refuses any late
            // packet for this key from here on.
            Revisions.Forget(key);
        }
    }

    private static bool HasActiveSubscriber(ScopeReplicationState scope)
    {
        foreach (var subscriber in scope.Subscribers)
            if (subscriber.Value.Active) return true;
        return false;
    }

    private void DeliverScope(ScopeReplicationState scope)
    {
        foreach (var pair in scope.Subscribers)
        {
            // A subscriber still installing its baseline receives nothing yet: its backlog is
            // delivered after the ack, so no event is applied ahead of the identity baseline.
            if (pair.Value.PendingBaselineId != 0) continue;
            if (!scope.TryTakePending(pair.Value, pendingScratch)) continue;
            DeliverEvents(scope, pair.Key, pair.Value, pendingScratch, DeferralFor(pair.Key));
        }
    }

    /// <summary>
    /// Turns one subscriber's pending events into lifecycle and state batches, within the frame's
    /// bulk budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The budget is applied as a prefix, never as a filter: events are consumed from the front of
    /// the log and everything from the first event that does not fit stays pending for a later frame.
    /// That is what preserves order across the deferral — a lifecycle batch and the states after it
    /// are never reordered — and it makes the visible transaction atomic, because a batch boundary is
    /// exactly where delivery stops (VALIDATION §9's "超预算分批但保持可见事务原子性").
    /// </para>
    /// <para>
    /// The first state event of a scope's delivery is charged even past the budget. Without that,
    /// one record larger than the entire budget would be postponed every frame and the subscription
    /// would never advance; with it, progress is guaranteed at one scope's worth of events per frame.
    /// </para>
    /// <para>
    /// When <see cref="AuthorityBackpressurePolicy.CoalesceStatePerKey"/> is set, a key that appears
    /// more than once in the batch keeps only its newest record. A state record is absolute, so the
    /// superseded one cannot change what the replica ends up holding.
    /// </para>
    /// </remarks>
    private void DeliverEvents(ScopeReplicationState scope, ushort subscriberId, SubscriberCursor cursor,
        List<ScopeEvent> pending, AuthorityDeferralState deferral)
    {
        if (pending.Count == 0) return;
        var lifecycle = new List<AuthorityLifecycleRecord>();
        var states = new List<AuthorityWorldStateRecord>();
        var stateSlotByKey = policy.CoalesceStatePerKey ? new Dictionary<ObjectKey, int>() : null;
        var packetTick = pending[0].HostTick;
        var lastConsumed = -1;
        var deferredEvents = 0;
        long deferredBytes = 0;

        for (var index = 0; index < pending.Count; index++)
        {
            var @event = pending[index];
            if (@event.Kind == ScopeEventKind.State && policy.BudgetsBulk)
            {
                var bytes = AuthorityWireSize.ForFamily(AuthorityFamily.WorldState, @event.State?.Length ?? 0);
                if (deferral.TryTakeBulk(bytes, policy.BulkBytesPerSubscriberPerFrame))
                {
                    // Fits: charged by TryTakeBulk.
                }
                else if (index == 0)
                {
                    deferral.ForceTakeBulk(bytes);
                }
                else
                {
                    deferredEvents = pending.Count - index;
                    for (var remaining = index; remaining < pending.Count; remaining++)
                    {
                        deferredBytes += AuthorityWireSize.ForFamily(AuthorityFamily.WorldState,
                            pending[remaining].State?.Length ?? 0);
                    }
                    break;
                }
            }

            switch (@event.Kind)
            {
                case ScopeEventKind.Spawn:
                case ScopeEventKind.Despawn:
                    FlushStates();
                    lifecycle.Add(new AuthorityLifecycleRecord(
                        @event.Kind == ScopeEventKind.Spawn ? AuthorityLifecycleOp.Spawn : AuthorityLifecycleOp.Despawn,
                        @event.Key, @event.Revision));
                    if (lifecycle.Count >= AuthorityLimits.LifecycleRecordCountMax) FlushLifecycle();
                    break;
                case ScopeEventKind.State:
                    FlushLifecycle();
                    var record = new AuthorityWorldStateRecord(@event.Key, @event.Revision, @event.State);
                    if (stateSlotByKey != null && stateSlotByKey.TryGetValue(@event.Key, out var slot))
                    {
                        states[slot] = record;
                    }
                    else
                    {
                        stateSlotByKey?.Add(@event.Key, states.Count);
                        states.Add(record);
                    }
                    if (states.Count >= AuthorityLimits.StateRecordCountMax) FlushStates();
                    break;
            }
            lastConsumed = index;
        }

        if (lastConsumed < 0)
        {
            // No event could be consumed at all, which no budget path can produce (index 0 is always
            // charged). Stopping here would strand the cursor, so it is a defect rather than a state.
            throw new InvalidOperationException("A pending delivery consumed no events.");
        }

        FlushLifecycle();
        FlushStates();
        cursor.LastLogSequence = pending[lastConsumed].LogSequence;
        if (deferredEvents > 0)
        {
            deferral.NoteDeferredBulk(deferredBytes);
            Metrics.RecordDeferral(deferredEvents, deferredBytes);
        }
        return;

        void FlushLifecycle()
        {
            if (lifecycle.Count == 0) return;
            if (!AuthorityLifecycleCodec.TryEncode(lifecycle, out var data))
            {
                // Encoding a fixed-size record batch cannot fail for counts the split enforces;
                // reaching here is a defect, not a delivery condition.
                throw new InvalidOperationException("Lifecycle batch refused by its own codec.");
            }
            Send(subscriberId, Stamped(AuthorityLifecyclePacket.Create(
                Header(AuthorityFamily.Lifecycle, cursor, packetTick, data.Length),
                scope.Scope, lifecycle.Count, data), cursor));
            lifecycle.Clear();
        }

        void FlushStates()
        {
            if (states.Count == 0) return;
            if (!AuthorityWorldStateCodec.TryEncode(states, out var data))
            {
                throw new InvalidOperationException("World state batch refused by its own codec.");
            }
            Send(subscriberId, Stamped(AuthorityWorldStatePacket.Create(
                Header(AuthorityFamily.WorldState, cursor, packetTick, data.Length),
                scope.Scope, cursor.BaselineId, states.Count, data), cursor));
            states.Clear();
            stateSlotByKey?.Clear();
        }
    }

    private AuthorityEnvelopeHeader Header(AuthorityFamily family, SubscriberCursor cursor, long hostTick,
        int payloadLength) =>
        new(AuthoritySchema.V1, family, epoch, connection: default, cursor.NextStreamSequence, hostTick,
            claimedPlayerId: 0, payloadLength);

    private void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        // The stream sequence lives in the header and was assigned there; sending consumes it. The
        // wire estimate is taken here, at the one place every packet passes through, so the traffic
        // counters cannot drift from what was actually handed to the transport.
        packetsSent++;
        Metrics.RecordPacketSent(packet.Family, AuthorityWireSize.Of(packet));
        sink.Send(subscriberId, packet);
        AdvanceStreamSequence(subscriberId, packet);
    }

    /// <summary>
    /// Advances the subscriber's stream sequence after a packet was handed to the sink.
    /// </summary>
    /// <remarks>
    /// The cursor is found through the packet's scope so both packet families share one per-subscriber,
    /// per-scope sequence space, which is what lets the replica's contiguous-sequence gate see the
    /// lifecycle and state packets of one frame as one ordered stream.
    /// </remarks>
    private void AdvanceStreamSequence(ushort subscriberId, AuthorityEnvelopePacket packet)
    {
        var scopeKey = packet switch
        {
            AuthorityLifecyclePacket lifecycle => new ScopeKey((PoolKind)lifecycle.ScopeKind, lifecycle.Scope),
            AuthorityWorldStatePacket worldState => new ScopeKey((PoolKind)worldState.ScopeKind, worldState.Scope),
            _ => default
        };
        if (!scopeKey.IsValid) return;
        if (scopes.TryGetValue(scopeKey, out var state) &&
            state.TryGetSubscriber(subscriberId, out var cursor))
        {
            cursor.NextStreamSequence++;
        }
    }

    private bool ReadMembers(ScopeReplicationState scope, out List<ObjectKey> current)
    {
        memberScratch.Clear();
        if (!worldView.TryReadMembers(scope.Scope, memberScratch))
        {
            scopesSkipped++;
            current = null;
            return false;
        }
        current = memberScratch;
        return true;
    }

    private void Append(ScopeReplicationState scope, in ScopeEvent @event)
    {
        scope.Append(@event);
        eventsAppended++;
    }

    private ScopeReplicationState GetOrCreateScope(ScopeKey scope)
    {
        if (!scopes.TryGetValue(scope, out var state))
        {
            state = new ScopeReplicationState(scope, epoch, logCapacity);
            scopes.Add(scope, state);
        }
        return state;
    }

    private static bool SameBytes(byte[] left, byte[] right)
    {
        if (left == null || right == null) return ReferenceEquals(left, right);
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i]) return false;
        }
        return true;
    }
}
