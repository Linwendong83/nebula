#region

using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 (part 1): deterministic fault injection at the replication/apply boundary, VALIDATION §4's
/// N2–N6 semantics — delay, duplication, logical reordering, an application pause, explicit loss of
/// one apply message, delayed old-generation tails, and baseline corruption.
/// </summary>
/// <remarks>
/// Every scenario is end to end at the model level: a host replicator scans a fake world, hands its
/// packets to a <see cref="FaultyAuthorityLink"/>, and the client replica applies what arrives. The
/// assertions are the recovery contract, not just the failure: a suspended scope converges back to
/// Live, the mirror ends up equal to the host's canonical facts (I10), a dead generation stays dead
/// under a delayed tail (I03), and absolute HP self-heals a lost update without accumulating
/// anything. No game type is involved.
/// </remarks>
[TestClass]
public class AuthorityFaultInjectionTest
{
    private static readonly AuthorityEpoch Epoch = new(0x1616161616161616, 0x1616161616161616);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);
    private static readonly ScopeKey OtherScope = new(PoolKind.GroundEnemy, 102);

    private const ushort Subscriber = 1;

    private sealed class FakeWorld : IHostWorldView
    {
        public readonly Dictionary<ScopeKey, List<ObjectKey>> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public ObjectKey Add(ScopeKey scope, int nativeId, long generation, byte[] state)
        {
            var key = ObjectKey.Create(Epoch, scope.Kind, scope.Scope, nativeId, generation);
            if (!Members.TryGetValue(scope, out var list))
            {
                list = [];
                Members[scope] = list;
            }
            list.Add(key);
            if (state != null) States[key] = state;
            return key;
        }

        public void Remove(ScopeKey scope, int nativeId, long generation) =>
            Members[scope].Remove(ObjectKey.Create(Epoch, scope.Kind, scope.Scope, nativeId, generation));

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            if (Members.TryGetValue(scope, out var list)) members.AddRange(list);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    /// <summary>The client inbox: applies what the link releases, counting refusals instead of asserting.</summary>
    private sealed class ReplicaInbox : IReplicationSink
    {
        public readonly ClientWorldReplica Replica;
        public readonly List<AuthorityEnvelopePacket> Forwarded = [];
        public long RefusedApplies;

        public ReplicaInbox(ClientWorldReplica replica) => Replica = replica;

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
        {
            Forwarded.Add(packet);
            if (!ApplyToReplica(Replica, packet)) RefusedApplies++;
        }
    }

    private sealed class RelayAck : ISnapshotAckSink
    {
        private readonly HostWorldReplicator host;
        private readonly ushort subscriber;

        public RelayAck(HostWorldReplicator host, ushort subscriber)
        {
            this.host = host;
            this.subscriber = subscriber;
        }

        public void Send(AuthoritySnapshotAckPacket packet) =>
            host.OnSnapshotAck(subscriber, packet.BaselineId, packet.LastAppliedSequence, packet.Accepted);
    }

    private sealed class RecordingControlSink : IScopeControlSink
    {
        public readonly List<(ScopeControlOp Op, ScopeKey Scope, ScopeRecoveryReason Reason)> Sent = [];

        public void Send(ScopeControlOp op, ScopeKey scope, ScopeRecoveryReason reason, bool digestOnly,
            long subscriptionEpoch, long lastAppliedSequence) => Sent.Add((op, scope, reason));
    }

    private static readonly AuthoritySessionContext SessionContext =
        new(AuthorityMode.HostAuthority, AuthoritySchema.V1, Epoch, default, 0, isHost: false);

    /// <summary>Applies one host packet the way the frame-boundary applier does; false means refused.</summary>
    private static bool ApplyToReplica(ClientWorldReplica replica, AuthorityEnvelopePacket packet)
    {
        switch (packet)
        {
            case AuthoritySnapshotBeginPacket begin:
                return begin.TryGetScopeKey(out var beginScope) &&
                       replica.BeginSnapshot(beginScope, begin.SubscriptionEpoch, begin.BaselineId,
                           begin.ChunkCount, begin.TotalBytes, begin.SnapshotHash);
            case AuthoritySnapshotChunkPacket chunk:
                return chunk.TryGetScopeKey(out var chunkScope) &&
                       replica.ReceiveSnapshotChunk(chunkScope, chunk.SubscriptionEpoch, chunk.BaselineId,
                           chunk.ChunkIndex, chunk.Data);
            case AuthoritySnapshotCommitPacket commit:
                return commit.TryGetScopeKey(out var commitScope) &&
                       replica.CommitSnapshot(commitScope, commit.SubscriptionEpoch, commit.BaselineId,
                           commit.ChunkCount, commit.SnapshotHash);
            case AuthorityLifecyclePacket lifecycle:
                return lifecycle.TryGetScopeKey(out var lifecycleScope) &&
                       AuthorityLifecycleCodec.TryDecode(lifecycle.Data, 0, lifecycle.Data.Length,
                           lifecycle.RecordCount, SessionContext, out var lifecycleRecords, out _) &&
                       replica.ApplyLifecycle(lifecycleScope, lifecycle.SubscriptionEpoch, lifecycle.Sequence,
                           lifecycleRecords);
            case AuthorityWorldStatePacket worldState:
                return worldState.TryGetScopeKey(out var stateScope) &&
                       AuthorityWorldStateCodec.TryDecode(worldState.Data, 0, worldState.Data.Length,
                           worldState.RecordCount, SessionContext, out var stateRecords, out _) &&
                       replica.ApplyWorldState(stateScope, worldState.SubscriptionEpoch, worldState.Sequence,
                           worldState.DeclaredBaselineId, stateRecords);
            case AuthorityScopeDigestPacket digest:
                return digest.TryGetScopeKey(out var digestScope) &&
                       replica.ReceiveScopeDigest(digestScope, digest.SubscriptionEpoch, digest.HostTick,
                           digest.DeclaredStreamSequence, digest.BaselineId, digest.MemberCount, digest.Digest);
            default:
                return false;
        }
    }

    /// <summary>One scenario: host world, faulty link, replica, and the ack/control conversations.</summary>
    private sealed class Rig
    {
        public readonly FakeWorld World = new();
        public readonly FaultInjectionRules Rules = new();
        public readonly ReplicaInbox Inbox;
        public readonly FaultyAuthorityLink Link;
        public readonly ClientWorldReplica Replica;
        public readonly HostWorldReplicator Host;
        public readonly RecordingControlSink Control = new();

        public Rig()
        {
            Replica = new ClientWorldReplica(Epoch);
            Inbox = new ReplicaInbox(Replica);
            Link = new FaultyAuthorityLink(Rules, Inbox);
            Host = new HostWorldReplicator(Epoch, World, Link);
            Replica.SnapshotAckSink = new RelayAck(Host, Subscriber);
            Replica.ScopeControlSink = Control;
        }

        /// <summary>Runs one clean subscription conversation and leaves the scope Live.</summary>
        public void GoLive(ScopeKey scope)
        {
            Replica.Subscribe(scope);
            Host.Subscribe(Subscriber, scope);
            Pump(2);
            TestAssert.AreEqual(SubscriptionPhase.Live, Phase(scope), "The subscription ended Live.");
        }

        /// <summary>
        /// Recovers a scope the way the real peers would: the replica asks for a baseline (a no-op
        /// while one is already pending), the host answers with one, and the conversation is pumped
        /// through the link to install and resume.
        /// </summary>
        public void RecoverViaResync(ScopeKey scope)
        {
            Replica.RequestResync(scope, ScopeRecoveryReason.SequenceEvicted);
            TestAssert.IsTrue(Host.RequestResync(Subscriber, scope));
            Pump(3);
        }

        /// <summary>Publishes a digest, delivers it, and asserts the scope verified equal (I10).</summary>
        public void AssertDigestConverges(long hostTick, ScopeKey scope)
        {
            var matchedBefore = Replica.DigestsMatchedTotal;
            var mismatchedBefore = Replica.DigestsMismatchedTotal;
            Host.PublishDigests(hostTick, intervalTicks: 1);
            Pump(2);
            TestAssert.IsTrue(Replica.DigestsMatchedTotal > matchedBefore,
                $"The digest must verify equal once the scope converged ({scope}).");
            TestAssert.AreEqual(mismatchedBefore, Replica.DigestsMismatchedTotal,
                "A converged scope must not raise a new digest mismatch.");
        }

        public void Pump(int times)
        {
            for (var i = 0; i < times; i++) Link.Pump();
        }

        public SubscriptionPhase Phase(ScopeKey scope) =>
            Replica.Versions.TryGetScope(scope, out var state)
                ? state.Phase
                : SubscriptionPhase.Unsubscribed;
    }

    [TestMethod]
    public void DuplicatedPacketsApplyOnceAndStayQuiet()
    {
        // N3 duplication from the first byte: the whole subscription conversation and the live
        // stream arrive as three copies each. The replica installs one baseline, applies one
        // record, and never flags the scope.
        var rig = new Rig();
        rig.Rules.ExtraCopies = 2;
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        TestAssert.AreEqual(1L, rig.Replica.SnapshotsInstalledTotal,
            "Three Begin copies must still be one baseline.");
        TestAssert.AreEqual(2L, rig.Replica.Snapshots.DuplicateChunksTotal,
            "The duplicated chunks are counted transport duplicates, not corruption.");
        TestAssert.IsTrue(rig.Link.DuplicateDeliveries > 0);

        rig.World.States[key] = [70];
        rig.Host.CaptureFrame(1000);
        rig.Pump(2);
        CollectionAssert.AreEquivalent(new byte[] { 70 }, rig.Replica.Mirrors[key].CanonicalState,
            "The duplicated state batch applies its facts exactly once.");
        TestAssert.IsTrue(rig.Replica.DuplicateTotal > 0);
        TestAssert.IsEmpty(rig.Replica.ScopesNeedingResync);
        rig.AssertDigestConverges(1300, Scope);
    }

    [TestMethod]
    public void ReorderedStreamPacketsRefuseAtTheGapAndRecoverThroughABaseline()
    {
        // N2/N3 reorder: two stream packets of one scope are released inverted. The replica must
        // see the missing sequence as a gap, suspend the scope, ask for a baseline, and converge
        // after the host re-baselines it — not skip ahead over the loss.
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        rig.Rules.ReorderDepth = 1;
        rig.World.States[key] = [80];
        rig.Host.CaptureFrame(1000);
        rig.World.States[key] = [60];
        rig.Host.CaptureFrame(1001);
        rig.Pump(2);

        TestAssert.AreEqual(SubscriptionPhase.Resyncing, rig.Phase(Scope));
        TestAssert.IsTrue(rig.Replica.GapTotal >= 1, "An inverted delivery is a real gap.");
        TestAssert.Contains((ScopeControlOp.Resync, Scope, ScopeRecoveryReason.StreamGap), rig.Control.Sent);
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "Nothing after the gap was applied; the mirror keeps the last applied absolute value.");

        // The link heals (transient fault), the host answers the request, and the scope converges.
        rig.Rules.ReorderDepth = 0;
        rig.RecoverViaResync(Scope);
        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(Scope));
        CollectionAssert.AreEquivalent(new byte[] { 60 }, rig.Replica.Mirrors[key].CanonicalState,
            "The rebuilt mirror holds the host's current absolute state.");
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void ADroppedSpawnIsDiscoveredByTheGapAndRebuiltFromABaseline()
    {
        // N4: exactly one Spawn lifecycle batch is lost. The next packet of the scope exposes the
        // missing sequence, the batch it carried is refused, the scope suspends, and the baseline
        // rebuild restores the membership deterministically.
        var rig = new Rig();
        rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        rig.Rules.DropNextLifecycle = true;
        var spawned = rig.World.Add(Scope, 2, 1, [90]);
        rig.Host.CaptureFrame(1000);
        rig.Pump(2);

        TestAssert.IsTrue(rig.Link.DroppedTotal >= 1, "The spawn batch was consumed by the link.");
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(spawned), "No identity, no mirror.");
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, rig.Phase(Scope));
        TestAssert.Contains((ScopeControlOp.Resync, Scope, ScopeRecoveryReason.StreamGap), rig.Control.Sent);

        rig.Rules.DropNextLifecycle = false;
        rig.RecoverViaResync(Scope);
        TestAssert.IsTrue(rig.Replica.Mirrors.ContainsKey(spawned), "The baseline restores the lost identity.");
        CollectionAssert.AreEquivalent(new byte[] { 90 }, rig.Replica.Mirrors[spawned].CanonicalState);
        TestAssert.AreEqual(2, rig.Replica.Mirrors.Count, "The membership equals the host's.");
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void ADroppedDeathIsDiscoveredByTheNextPacketAndTheGenerationStaysDead()
    {
        // N4: the Despawn batch is lost, so the replica keeps holding a dead object and cannot
        // notice locally. The next stream packet exposes the missing sequence and suspends the
        // scope; the rebuild removes the mirror, and no late fact for the dead key may resurrect
        // it (I03).
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        var witness = rig.World.Add(Scope, 2, 1, [90]);
        rig.GoLive(Scope);

        rig.Rules.DropNextLifecycle = true;
        rig.World.Remove(Scope, 1, 1);
        rig.Host.CaptureFrame(1000);
        rig.Pump(2);
        TestAssert.IsTrue(rig.Replica.Mirrors.ContainsKey(key), "Without the despawn the mirror stays.");
        TestAssert.IsEmpty(rig.Replica.ScopesNeedingResync, "A quiet stream cannot know it lost a death.");

        rig.Rules.DropNextLifecycle = false;
        rig.World.States[witness] = [80];
        rig.Host.CaptureFrame(1001);
        rig.Pump(2);
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, rig.Phase(Scope),
            "The packet behind the lost death is a real gap.");
        TestAssert.Contains((ScopeControlOp.Resync, Scope, ScopeRecoveryReason.StreamGap), rig.Control.Sent);

        rig.RecoverViaResync(Scope);
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(key), "The rebuilt membership no longer holds the dead.");
        rig.AssertDigestConverges(1600, Scope);

        // A late fact for the dead key changes nothing.
        TestAssert.IsTrue(rig.Replica.Versions.TryGetScope(Scope, out var version));
        TestAssert.IsFalse(rig.Replica.ApplyWorldState(Scope, version.SubscriptionEpoch,
            version.LastAppliedSequence + 1, version.BaselineId,
            new List<AuthorityWorldStateRecord> { new(key, 99, [55]) }));
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(key), "The dead generation stays dead (I03).");
    }

    [TestMethod]
    public void ADroppedHitpointBatchBreaksTheStreamAndTheBaselineRestoresTheAbsoluteValue()
    {
        // N4 on an HP fact: the lost batch is a sequence gap, so the next packet suspends the
        // scope and the rebuild ships the host's current absolute value. Nothing is accumulated
        // on the way — the mirror jumps to the host's truth, never to truth ± lost deltas.
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        rig.Rules.DropNextWorldState = true;
        rig.World.States[key] = [70];
        rig.Host.CaptureFrame(1000);
        rig.Pump(2);
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "The lost batch left the mirror at the last applied absolute value.");

        rig.Rules.DropNextWorldState = false;
        rig.World.States[key] = [40];
        rig.Host.CaptureFrame(1001);
        rig.Pump(2);
        TestAssert.AreEqual(SubscriptionPhase.Resyncing, rig.Phase(Scope));
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "The suspended scope applied nothing behind the gap.");

        rig.RecoverViaResync(Scope);
        CollectionAssert.AreEquivalent(new byte[] { 40 }, rig.Replica.Mirrors[key].CanonicalState,
            "The baseline restores the host's absolute value; no lost delta was accumulated.");
        TestAssert.AreEqual(0L, rig.Replica.StaleTotal);
        rig.AssertDigestConverges(1300, Scope);
    }

    [TestMethod]
    public void ABaselineMissingAChunkIsRefusedAndLeavesTheLiveWorldIntact()
    {
        // N5: a snapshot conversation loses one chunk. Staging refuses at commit, the buffer is
        // discarded, the active mirror keeps the old baseline's facts, and a clean re-baseline
        // converges.
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);
        rig.World.States[key] = [30];

        rig.Rules.DropChunkIndex = 0;
        rig.RecoverViaResync(Scope);

        TestAssert.AreEqual(SubscriptionPhase.Snapshotting, rig.Phase(Scope),
            "The refused commit leaves the conversation waiting for a usable baseline.");
        TestAssert.Contains(Scope, rig.Replica.ScopesNeedingResync);
        TestAssert.AreEqual(0, rig.Replica.Snapshots.Count, "The staging buffer was discarded.");
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "The live mirror was not touched by the broken baseline.");

        rig.Rules.DropChunkIndex = -1;
        rig.RecoverViaResync(Scope);
        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(Scope));
        CollectionAssert.AreEquivalent(new byte[] { 30 }, rig.Replica.Mirrors[key].CanonicalState);
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void CorruptedBaselineImagesNeverReachTheActiveWorld()
    {
        // N5, two conversations: a flipped chunk byte breaks the image hash against Commit, and a
        // flipped Commit hash contradicts Begin and the assembled image. Both are refused before
        // installation; the mirror survives both intact.
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);
        rig.World.States[key] = [30];

        rig.Rules.CorruptChunkBytes = true;
        rig.RecoverViaResync(Scope);
        TestAssert.AreEqual(SubscriptionPhase.Snapshotting, rig.Phase(Scope));
        TestAssert.Contains(Scope, rig.Replica.ScopesNeedingResync);
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "The hash-mismatched image was discarded, not installed.");

        rig.World.States[key] = [20];
        rig.Rules.CorruptCommitHash = true;
        rig.RecoverViaResync(Scope);
        TestAssert.AreEqual(SubscriptionPhase.Snapshotting, rig.Phase(Scope));
        CollectionAssert.AreEquivalent(new byte[] { 100 }, rig.Replica.Mirrors[key].CanonicalState,
            "The contradictory Commit never reached the mirror.");

        rig.RecoverViaResync(Scope);
        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(Scope));
        CollectionAssert.AreEquivalent(new byte[] { 20 }, rig.Replica.Mirrors[key].CanonicalState);
        rig.AssertDigestConverges(1500, Scope);
    }

    [TestMethod]
    public void ADuplicateChunkIsBenignAndInstallsTheSameImage()
    {
        // N5: a repeated chunk is a transport duplicate the staging area is built to absorb — the
        // baseline must still install exactly once and hash-verify.
        var rig = new Rig();
        var key = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);
        rig.World.States[key] = [45];

        rig.Rules.DuplicateChunkIndex = 0;
        rig.RecoverViaResync(Scope);

        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(Scope));
        TestAssert.IsTrue(rig.Replica.Snapshots.DuplicateChunksTotal >= 1);
        CollectionAssert.AreEquivalent(new byte[] { 45 }, rig.Replica.Mirrors[key].CanonicalState,
            "The duplicate chunk did not perturb the assembled image.");
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void AFaultConfinedToOneScopeLeavesTheOtherScopeLive()
    {
        // N4 with the scope filter: one scope loses a state batch; the other scope's stream on the
        // same faulty link is untouched. Recovery is local (DESIGN 9.3), never a world rebuild.
        var rig = new Rig();
        var keyA = rig.World.Add(Scope, 1, 1, [100]);
        var keyB = rig.World.Add(OtherScope, 1, 1, [100]);
        rig.GoLive(Scope);
        rig.GoLive(OtherScope);

        rig.Rules.DropNextWorldState = true;
        rig.Rules.DropOnlyScope = Scope;
        rig.World.States[keyA] = [80];
        rig.World.States[keyB] = [80];
        rig.Host.CaptureFrame(1000);
        rig.World.States[keyA] = [60];
        rig.World.States[keyB] = [60];
        rig.Host.CaptureFrame(1001);
        rig.Pump(2);

        TestAssert.AreEqual(SubscriptionPhase.Resyncing, rig.Phase(Scope), "The damaged scope suspends.");
        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(OtherScope),
            "The spared scope never noticed the fault.");
        TestAssert.IsFalse(rig.Replica.ScopesNeedingResync.Contains(OtherScope));
        CollectionAssert.AreEquivalent(new byte[] { 60 }, rig.Replica.Mirrors[keyB].CanonicalState,
            "The spared scope kept applying its own stream.");

        rig.Rules.DropNextWorldState = false;
        rig.Rules.DropOnlyScope = null;
        rig.RecoverViaResync(Scope);
        CollectionAssert.AreEquivalent(new byte[] { 60 }, rig.Replica.Mirrors[keyA].CanonicalState);
        rig.AssertDigestConverges(1400, Scope);
        rig.AssertDigestConverges(1401, OtherScope);
    }

    [TestMethod]
    public void APausedClientAppliesTheWholeBacklogInOrderWhenResumed()
    {
        // N6: the application stops for several host frames while the world keeps moving — HP
        // changes, a death, the slot recycled into a new generation. Resuming drains the backlog
        // in send order, so the final mirror equals the host's facts with no resync at all.
        var rig = new Rig();
        var old = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        rig.Rules.Paused = true;
        rig.World.States[old] = [70];
        rig.Host.CaptureFrame(1000);
        rig.World.Remove(Scope, 1, 1);
        rig.Host.CaptureFrame(1001);
        var fresh = rig.World.Add(Scope, 1, 2, [50]);
        rig.Host.CaptureFrame(1002);
        rig.Pump(5);
        TestAssert.IsTrue(rig.Link.HeldCount > 0, "The pause is holding the backlog.");
        TestAssert.AreEqual(1, rig.Replica.Mirrors.Count, "Nothing was applied while paused.");

        rig.Rules.Paused = false;
        rig.Link.Flush();
        TestAssert.AreEqual(0, rig.Link.HeldCount);
        TestAssert.IsEmpty(rig.Replica.ScopesNeedingResync,
            "An ordered backlog after a pause is not a break; no resync may fire.");
        TestAssert.AreEqual(0L, rig.Replica.TombstoneRefusedTotal,
            "In-order delivery means no late tail ever met the tombstone.");
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(old));
        TestAssert.IsTrue(rig.Replica.Mirrors.ContainsKey(fresh));
        CollectionAssert.AreEquivalent(new byte[] { 50 }, rig.Replica.Mirrors[fresh].CanonicalState);
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void ADelayedDuplicateOfTheOldGenerationCannotCorruptTheNewOne()
    {
        // N3's "旧 generation 尾包延迟": a copy of the old object's last state travels behind the
        // death and the recycled slot's next generation. The sequence gate refuses it — no
        // revival, no perturbation of the new object, no resync.
        var rig = new Rig();
        var old = rig.World.Add(Scope, 1, 1, [100]);
        rig.GoLive(Scope);

        rig.Rules.DelayedDuplicatePumps = 2;
        rig.Rules.DelayDuplicateNextPacket = true;
        rig.World.States[old] = [70];
        rig.Host.CaptureFrame(1000);
        rig.World.Remove(Scope, 1, 1);
        rig.Host.CaptureFrame(1001);
        var fresh = rig.World.Add(Scope, 1, 2, [88]);
        rig.Host.CaptureFrame(1002);
        rig.Pump(3);

        TestAssert.AreEqual(1L, rig.Link.RequeuedDuplicates, "Exactly the armed packet travels late.");
        TestAssert.IsTrue(rig.Replica.DuplicateTotal >= 1, "The late tail is refused by its sequence.");
        TestAssert.AreEqual(0L, rig.Replica.TombstoneRefusedTotal,
            "The late tail never even reached a record decision.");
        TestAssert.IsEmpty(rig.Replica.ScopesNeedingResync);
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(old), "The old generation stays dead.");
        CollectionAssert.AreEquivalent(new byte[] { 88 }, rig.Replica.Mirrors[fresh].CanonicalState,
            "The new generation is untouched by the old packet.");
        rig.AssertDigestConverges(1400, Scope);
    }

    [TestMethod]
    public void ASnapshotDuringCombatDeliversTheCompleteBacklogAfterTheAck()
    {
        // L01 at model level: a subscriber joins mid-fight. Between the baseline freeze and its
        // ack the world keeps fighting — state, spawn, death. The host sends nothing ahead of the
        // baseline; the ack releases the backlog; the mirror ends equal to the world.
        var rig = new Rig();
        var first = rig.World.Add(Scope, 1, 1, [100]);

        rig.Replica.Subscribe(Scope);
        rig.Host.Subscribe(Subscriber, Scope);

        rig.World.States[first] = [80];
        rig.Host.CaptureFrame(1000);
        var second = rig.World.Add(Scope, 2, 1, [90]);
        rig.Host.CaptureFrame(1001);
        rig.World.Remove(Scope, 1, 1);
        rig.Host.CaptureFrame(1002);

        TestAssert.AreEqual(0, rig.Inbox.Forwarded.Count,
            "Nothing may reach the replica while its baseline is pending.");

        rig.Pump(3); // baseline arrives, installs, acks; backlog + marker are released into the link
        rig.Pump(3);
        TestAssert.AreEqual(1L, rig.Replica.SnapshotsInstalledTotal);
        TestAssert.AreEqual(SubscriptionPhase.Live, rig.Phase(Scope));
        TestAssert.IsFalse(rig.Replica.Mirrors.ContainsKey(first), "The death in the backlog stands.");
        TestAssert.IsTrue(rig.Replica.Mirrors.ContainsKey(second));
        CollectionAssert.AreEquivalent(new byte[] { 90 }, rig.Replica.Mirrors[second].CanonicalState);
        TestAssert.IsEmpty(rig.Replica.ScopesNeedingResync);
        rig.AssertDigestConverges(1400, Scope);
    }
}
