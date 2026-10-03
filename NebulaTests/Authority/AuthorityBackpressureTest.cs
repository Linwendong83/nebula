#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A23: the send budget postpones bulk state without ever postponing identity, death or control
/// traffic, never reorders what it does send, and always makes progress.
/// </summary>
/// <remarks>
/// Every case here is the model-level half of VALIDATION §9's budget: the deferral must be a prefix
/// of the stream (so a batch boundary stays a transaction boundary), the postponed events must
/// arrive in a later frame in the same order, and a baseline bigger than one frame's chunk allowance
/// must be paced out with the conversation still ordered Begin → chunks → Commit. The real-run half
/// is the `perf` report the harness writes.
/// </remarks>
[TestClass]
public class AuthorityBackpressureTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0B0B0B0B0B0B0B0B, 0x0B0B0B0B0B0B0B0B);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey OtherScope = new(PoolKind.GroundEnemy, 102);

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly Dictionary<ScopeKey, List<ObjectKey>> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];
        public bool MembersReadable = true;

        public ObjectKey Add(ScopeKey scope, int nativeId, byte[] state, long generation = 1)
        {
            var key = ObjectKey.Create(Epoch, scope.Kind, scope.Scope, nativeId, generation);
            if (!Members.TryGetValue(scope, out var list))
            {
                list = [];
                Members[scope] = list;
            }
            if (!list.Contains(key)) list.Add(key);
            if (state != null) States[key] = state;
            return key;
        }

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            if (!MembersReadable) return false;
            if (Members.TryGetValue(scope, out var list)) members.AddRange(list);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class RecordingSink : IReplicationSink
    {
        public readonly List<(ushort Subscriber, AuthorityEnvelopePacket Packet)> Sent = [];

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet) =>
            Sent.Add((subscriberId, packet));

        public List<AuthorityEnvelopePacket> OfFamily(AuthorityFamily family)
        {
            var packets = new List<AuthorityEnvelopePacket>();
            foreach (var entry in Sent)
            {
                if (entry.Packet.Family == family) packets.Add(entry.Packet);
            }
            return packets;
        }

        public List<AuthorityEnvelopePacket> Since(int index, AuthorityFamily family)
        {
            var packets = new List<AuthorityEnvelopePacket>();
            for (var i = index; i < Sent.Count; i++)
            {
                if (Sent[i].Packet.Family == family) packets.Add(Sent[i].Packet);
            }
            return packets;
        }

        public void Clear() => Sent.Clear();
    }

    private static AuthorityBackpressurePolicy Policy(long bulkBytes, int chunks = 1024,
        bool coalesce = true) =>
        new(bulkBytes, chunks, applyMessagesPerFrame: 4096, minApplyMessagesPerFrame: 64, applyBudgetMs: 3.0,
            coalesceStatePerKey: coalesce, name: "test");

    /// <summary>Subscribes and completes the baseline so later frames deliver increments only.</summary>
    private static void Establish(HostWorldReplicator replicator, RecordingSink sink, ScopeKey scope,
        ushort subscriber = 1)
    {
        var before = sink.Sent.Count;
        replicator.Subscribe(subscriber, scope);
        var begin = (AuthoritySnapshotBeginPacket)Find(sink, before, AuthorityFamily.SnapshotBegin);
        var commit = (AuthoritySnapshotCommitPacket)Find(sink, before, AuthorityFamily.SnapshotCommit);
        replicator.OnSnapshotAck(subscriber, begin.BaselineId, commit.ChunkCount, accepted: true);
    }

    private static AuthorityEnvelopePacket Find(RecordingSink sink, int from, AuthorityFamily family)
    {
        for (var i = from; i < sink.Sent.Count; i++)
        {
            if (sink.Sent[i].Packet.Family == family) return sink.Sent[i].Packet;
        }
        throw new System.InvalidOperationException("No " + family + " packet was sent.");
    }

    private static AuthoritySessionContext Context() =>
        new(AuthorityMode.HostAuthority, AuthoritySchema.V1, Epoch, connection: default,
            connectedPlayerId: 1, isHost: true);

    private static List<AuthorityWorldStateRecord> Decode(AuthorityWorldStatePacket packet)
    {
        TestAssert.IsTrue(AuthorityWorldStateCodec.TryDecode(packet.Data, 0, packet.Data.Length,
                packet.RecordCount, Context(), out var records, out var reject),
            "The delivered batch must decode: " + reject);
        return records;
    }

    [TestMethod]
    public void IdentityIsDeliveredEvenWhenTheBulkBudgetIsOneByte()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 1));
        world.Add(Scope, 1, [7, 7]);
        Establish(replicator, sink, Scope);
        sink.Clear();

        // Two new objects: their spawns are critical, their states are bulk.
        world.Add(Scope, 2, [1, 1]);
        world.Add(Scope, 3, [2, 2]);
        replicator.CaptureFrame(10);

        var lifecycle = sink.OfFamily(AuthorityFamily.Lifecycle);
        TestAssert.HasCount(1, lifecycle, "The batch the budget never postpones goes out first.");
        // Both spawns precede every state in the log (capture appends identity before HP), so the
        // strict prefix defers only state here — which is exactly why identity cannot starve.
        TestAssert.AreEqual(2, ((AuthorityLifecyclePacket)lifecycle[0]).RecordCount);
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.WorldState),
            "A one-byte budget cannot fit a state record, so none goes out ahead of the spawns.");
        TestAssert.IsTrue(replicator.Metrics.DeferredEvents > 0, "The postponement is reported.");

        // The next frame delivers the states that waited, in order, with nothing lost.
        sink.Clear();
        replicator.CaptureFrame(11);
        var first = sink.OfFamily(AuthorityFamily.WorldState);
        TestAssert.HasCount(1, first);
        var records = Decode((AuthorityWorldStatePacket)first[0]);
        TestAssert.HasCount(1, records, "The forced-progress record is the front of the backlog.");
        CollectionAssert.AreEqual(new byte[] { 1, 1 }, records[0].State);
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.Lifecycle),
            "Nothing else was pending, so this frame is bulk only.");

        sink.Clear();
        replicator.CaptureFrame(12);
        var second = sink.OfFamily(AuthorityFamily.WorldState);
        TestAssert.HasCount(1, second);
        CollectionAssert.AreEqual(new byte[] { 2, 2 },
            Decode((AuthorityWorldStatePacket)second[0])[0].State, "The backlog drains in order.");
    }

    [TestMethod]
    public void ARecordLargerThanTheWholeBudgetStillMakesProgress()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 1));
        world.Add(Scope, 1, [1]);
        Establish(replicator, sink, Scope);

        // One member changes to a state far larger than the per-frame allowance. Without the
        // forced-progress rule it would be postponed every frame forever.
        world.Add(Scope, 2, new byte[256]);
        sink.Clear();
        replicator.CaptureFrame(10);
        sink.Clear();
        replicator.CaptureFrame(11);

        var delivered = sink.OfFamily(AuthorityFamily.WorldState);
        TestAssert.HasCount(1, delivered, "The oversized record is delivered, not livelocked.");
        TestAssert.AreEqual(256, Decode((AuthorityWorldStatePacket)delivered[0])[0].State.Length);
        TestAssert.IsTrue(replicator.Metrics.LongestDeferralFrames <= 2, "No unbounded deferral run.");
    }

    [TestMethod]
    public void CoalescingKeepsOnlyTheNewestStatePerKey()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 4096));
        var key = world.Add(Scope, 1, [1, 1]);
        // A baseline is started while the member keeps changing: both changes land in the backlog
        // that the ack releases, which is exactly the case coalescing exists for.
        replicator.Subscribe(1, Scope);
        world.Add(Scope, 1, [2, 2]);
        replicator.CaptureFrame(10);
        world.Add(Scope, 1, [3, 3]);
        replicator.CaptureFrame(11);

        var begin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        var commit = (AuthoritySnapshotCommitPacket)Find(sink, 0, AuthorityFamily.SnapshotCommit);
        sink.Clear();
        replicator.OnSnapshotAck(1, begin.BaselineId, commit.ChunkCount, accepted: true);

        var batches = sink.OfFamily(AuthorityFamily.WorldState);
        var coalesced = new List<AuthorityWorldStateRecord>();
        foreach (AuthorityWorldStatePacket packet in batches)
        {
            coalesced.AddRange(Decode(packet));
        }
        TestAssert.HasCount(1, coalesced, "One key, one record: the superseded revision is dropped.");
        TestAssert.AreEqual(key, coalesced[0].Key);
        CollectionAssert.AreEqual(new byte[] { 3, 3 }, coalesced[0].State, "The newest state survives.");
    }

    [TestMethod]
    public void WithoutCoalescingBothRevisionsAreDelivered()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var policy = new AuthorityBackpressurePolicy(long.MaxValue, 1024, 4096, 64, 3.0,
            coalesceStatePerKey: false, "no-coalesce");
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, policy);
        world.Add(Scope, 1, [1, 1]);
        replicator.Subscribe(1, Scope);
        world.Add(Scope, 1, [2, 2]);
        replicator.CaptureFrame(10);
        world.Add(Scope, 1, [3, 3]);
        replicator.CaptureFrame(11);

        var begin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        var commit = (AuthoritySnapshotCommitPacket)Find(sink, 0, AuthorityFamily.SnapshotCommit);
        sink.Clear();
        replicator.OnSnapshotAck(1, begin.BaselineId, commit.ChunkCount, accepted: true);

        var delivered = new List<AuthorityWorldStateRecord>();
        foreach (AuthorityWorldStatePacket packet in sink.OfFamily(AuthorityFamily.WorldState))
        {
            delivered.AddRange(Decode(packet));
        }
        TestAssert.HasCount(2, delivered, "The unbounded shape keeps every revision.");
        TestAssert.IsTrue(delivered[1].Revision > delivered[0].Revision);
    }

    [TestMethod]
    public void ASubscriberThatKeepsUpIsNeverDeferred()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 4096));
        world.Add(Scope, 1, [1]);
        Establish(replicator, sink, Scope);

        for (var frame = 0; frame < 5; frame++)
        {
            world.Add(Scope, 1, new byte[] { (byte)(10 + frame) });
            sink.Clear();
            replicator.CaptureFrame(10 + frame);
            TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.WorldState),
                "One changed member, one batch, sent in the frame it changed.");
        }
        TestAssert.AreEqual(0L, replicator.Metrics.DeferredEvents);
        TestAssert.AreEqual(0, replicator.Metrics.LongestDeferralFrames);
    }

    [TestMethod]
    public void ABigBaselineIsPacedOutWithTheConversationStillOrdered()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        // One chunk per frame, so a multi-chunk baseline must be spread over several frames.
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 64 * 1024, chunks: 1));

        // A second scope is already live, so the fairness claim can be checked: the paced baseline
        // must not stop the other scope's lifecycle traffic from going out in the meantime.
        world.Add(OtherScope, 1, [1]);
        Establish(replicator, sink, OtherScope);

        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        sink.Clear();
        replicator.Subscribe(1, Scope);

        var begin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        var expectedChunks = begin.ChunkCount;
        TestAssert.IsTrue(expectedChunks >= 2,
            "The test needs a baseline larger than one frame's chunk allowance.");
        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.SnapshotBegin));
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotChunk),
            "The Begin goes out at once; the bulk middle is paced over the frames that follow.");
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotCommit));

        for (var index = 0; index < expectedChunks; index++)
        {
            if (index == 0)
            {
                // The other scope changes while the baseline is mid-flight.
                world.Add(OtherScope, 2, [2]);
            }
            sink.Clear();
            replicator.CaptureFrame(11 + index);
            AssertOrderedChunkFrame(sink, index, expectBegin: false, expectCommit: index + 1 == expectedChunks);
            if (index == 0)
            {
                TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.Lifecycle),
                    "The other scope's spawn is delivered while the baseline is still being paced out; " +
                    "its lifecycle batch is never held behind the snapshot.");
            }
        }
    }

    /// <summary>
    /// Asserts one frame's slice of a paced baseline conversation: at most one chunk, the expected
    /// index (so the pace resumes instead of restarting), the Begin only on the first frame, and the
    /// Commit only on the frame that completes the last chunk.
    /// </summary>
    private static void AssertOrderedChunkFrame(RecordingSink sink, int expectedChunkIndex, bool expectBegin,
        bool expectCommit)
    {
        var chunks = sink.OfFamily(AuthorityFamily.SnapshotChunk);
        TestAssert.HasCount(1, chunks, "One chunk per frame is the configured pace.");
        TestAssert.AreEqual(expectedChunkIndex, ((AuthoritySnapshotChunkPacket)chunks[0]).ChunkIndex,
            "The chunks stay in order and resume where the previous frame stopped.");
        TestAssert.HasCount(expectBegin ? 1 : 0, sink.OfFamily(AuthorityFamily.SnapshotBegin));
        TestAssert.HasCount(expectCommit ? 1 : 0, sink.OfFamily(AuthorityFamily.SnapshotCommit));
    }

    [TestMethod]
    public void RemovingASubscriberDropsItsPacedBaseline()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 64 * 1024, chunks: 1));
        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        replicator.Subscribe(1, Scope);
        sink.Clear();

        replicator.RemoveSubscriber(1);
        replicator.CaptureFrame(10);

        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotChunk),
            "A departed subscriber's remaining chunks are dropped, not shipped to nobody.");
        TestAssert.HasCount(0, sink.OfFamily(AuthorityFamily.SnapshotCommit));
    }

    [TestMethod]
    public void AResyncDuringAPacedBaselineStillRebaselines()
    {
        // Regression: the client only acks a baseline it installed, so a paced (or broken)
        // conversation leaves the host's cursor waiting forever. A resync must discard that
        // in-flight baseline instead of being refused as "already subscribing", or the scope would
        // stay in Snapshotting for the rest of the session.
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096, Policy(bulkBytes: 64 * 1024, chunks: 1));
        for (var i = 0; i < 300; i++) world.Add(Scope, 100 + i, new byte[256]);
        replicator.Subscribe(1, Scope);

        var firstBegin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        sink.Clear();
        TestAssert.IsTrue(replicator.RequestResync(1, Scope), "A live subscription can be resynced.");

        var secondBegin = (AuthoritySnapshotBeginPacket)Find(sink, 0, AuthorityFamily.SnapshotBegin);
        TestAssert.AreNotEqual(firstBegin.BaselineId, secondBegin.BaselineId,
            "A resync mints a new baseline; replaying the old one would refuse the old stream's tail.");

        // The replacement conversation still runs to completion, chunk by chunk.
        var expectedChunks = secondBegin.ChunkCount;
        for (var index = 0; index < expectedChunks; index++)
        {
            sink.Clear();
            replicator.CaptureFrame(20 + index);
            AssertOrderedChunkFrame(sink, index, expectBegin: false, expectCommit: index + 1 == expectedChunks);
        }
    }

    [TestMethod]
    public void DeferralRunsAreMeasuredPerFrameRun()
    {
        var state = new AuthorityDeferralState();

        state.BeginFrame();
        state.NoteDeferredBulk(100);
        state.EndOfFrame();
        TestAssert.AreEqual(1, state.ConsecutiveDeferredFrames);

        state.BeginFrame();
        state.NoteDeferredSnapshotChunks(3);
        state.EndOfFrame();
        TestAssert.AreEqual(2, state.ConsecutiveDeferredFrames, "A chunk postponement is a deferral too.");

        state.BeginFrame();
        state.EndOfFrame();
        TestAssert.AreEqual(0, state.ConsecutiveDeferredFrames, "A clean frame ends the run.");
        TestAssert.AreEqual(2, state.LongestDeferralFrames, "The high-water mark survives the reset.");
        TestAssert.AreEqual(3, state.DeferredSnapshotChunks);

        // The bulk budget is per frame: the counter is refilled, not carried over.
        state.BeginFrame();
        TestAssert.IsTrue(state.TryTakeBulk(64, 64));
        TestAssert.IsFalse(state.TryTakeBulk(1, 64));
        state.BeginFrame();
        TestAssert.IsTrue(state.TryTakeBulk(64, 64), "A new frame gets a fresh allowance.");
    }

    [TestMethod]
    public void UnboundedDeliversEverythingInTheFrameItChanged()
    {
        var world = new FakeWorld();
        var sink = new RecordingSink();
        var replicator = new HostWorldReplicator(Epoch, world, sink, 4096,
            AuthorityBackpressurePolicy.Unbounded);
        world.Add(Scope, 1, [1]);
        Establish(replicator, sink, Scope);

        for (var i = 0; i < 500; i++) world.Add(Scope, 1000 + i, new byte[256]);
        sink.Clear();
        replicator.CaptureFrame(10);

        TestAssert.HasCount(1, sink.OfFamily(AuthorityFamily.WorldState));
        TestAssert.AreEqual(500, ((AuthorityWorldStatePacket)sink.OfFamily(AuthorityFamily.WorldState)[0]).RecordCount,
            "With no budget there is nothing to postpone.");
        TestAssert.AreEqual(0L, replicator.Metrics.DeferredEvents);
    }
}
