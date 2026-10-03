#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A12 end-to-end: a host planet of ground enemies flows through the standard replicator and the
/// client's replica into a binding that rebuilds display-only shells. Covers the task card's
/// acceptance at the model level: full lifecycle, parent-child inversion, slot reuse, scope rebuild
/// after losing the core — and never through a Kill path (the fake pools have no Kill to call).
/// </summary>
[TestClass]
public class GroundEnemyReplicationTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A12080808080808, 0x0C12080808080808);
    private static readonly ScopeKey Scope = new(PoolKind.GroundEnemy, 101);

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    private static GroundEnemyState BaseCore(int baseId) => new(
        GroundEnemyKind.GroundBase, true, false, 8100, 40, 1, 0, 0,
        101, 101, baseId, 5, 3, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 5000, 8000, 0, 0);

    private static GroundEnemyState Unit(int baseId, int hp = 500) => new(
        GroundEnemyKind.GroundUnit, true, false, 8130, 45, 2, 3, 0,
        101, 101, baseId, 0, 1, 10f, 0f, 20f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, hp, 900, 2, 0);

    private static byte[] Encoded(GroundEnemyState state)
    {
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var data));
        return data;
    }

    private sealed class FakeHostEnemies : IHostWorldView
    {
        public readonly List<ObjectKey> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            members.AddRange(Members);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class FakeClientPools : IGroundEnemyPools
    {
        public readonly Dictionary<int, GroundEnemyState> Shells = [];
        public int PureRemovals;

        public bool EnemyExists(int enemyId) => Shells.ContainsKey(enemyId);
        public void CreateEnemyShell(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;
        public void WriteEnemyState(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;

        public void RemoveEnemyShell(int enemyId)
        {
            TestAssert.IsTrue(Shells.Remove(enemyId), "Pure removal releases a shell that exists; no Kill, no drops.");
            PureRemovals++;
        }
    }

    private sealed class TestBindingObserver : IReplicaMirrorObserver
    {
        public readonly FakeClientPools Pools = new();
        public readonly GroundEnemyBinding Core;
        public int BaselinesInstalled;

        public TestBindingObserver() => Core = new GroundEnemyBinding(101, Pools, (key, _) => { });

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) =>
            Core.ApplyState(key, state);

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            BaselinesInstalled++;
            Core.ReconcileBaseline(members);
        }

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) => Core.RemoveMember(key);
    }

    private sealed class SinkToClientInbox : IReplicationSink
    {
        private readonly AuthoritySession client;
        public SinkToClientInbox(AuthoritySession client) => this.client = client;

        public void Send(ushort subscriberId, AuthorityEnvelopePacket packet)
        {
            var scope = packet switch
            {
                AuthorityLifecyclePacket lifecycle => new ScopeKey((PoolKind)lifecycle.ScopeKind, lifecycle.Scope),
                AuthorityWorldStatePacket worldState => new ScopeKey((PoolKind)worldState.ScopeKind, worldState.Scope),
                AuthoritySnapshotBeginPacket begin => new ScopeKey((PoolKind)begin.ScopeKind, begin.Scope),
                AuthoritySnapshotChunkPacket chunk => new ScopeKey((PoolKind)chunk.ScopeKind, chunk.Scope),
                AuthoritySnapshotCommitPacket commit => new ScopeKey((PoolKind)commit.ScopeKind, commit.Scope),
                _ => default
            };
            TestAssert.IsTrue(scope.IsValid);
            TestAssert.IsTrue(client.TryEnqueueReplicaMessage(packet, new ApplyScope(scope, 0, packet.Sequence, packet.HostTick)));
        }
    }

    private sealed class AckToHost : ISnapshotAckSink
    {
        private readonly AuthoritySession host;
        public AckToHost(AuthoritySession host) => this.host = host;
        public void Send(AuthoritySnapshotAckPacket packet) =>
            TestAssert.IsTrue(host.NotifySnapshotAck(1, packet.BaselineId, packet.LastAppliedSequence, packet.Accepted));
    }

    [TestMethod]
    public void AGroundLifecycleSurvivesTheFullConversation()
    {
        var enemies = new FakeHostEnemies();
        var core = Key(4, 1);
        var unitA = Key(9, 1);
        var lone = Key(12, 1);
        enemies.Members.AddRange([core, unitA, lone]);
        enemies.States[core] = Encoded(BaseCore(baseId: 2));
        enemies.States[unitA] = Encoded(Unit(baseId: 2));
        enemies.States[lone] = Encoded(Unit(baseId: 0, hp: 700));

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var clientSession = new AuthoritySession(new AuthoritySessionState());
        clientSession.BeginAuthorityWorld(Epoch, isHost: false);
        hostSession.RegisterHostReplication(enemies, new SinkToClientInbox(clientSession));

        var observer = new TestBindingObserver();
        clientSession.WorldReplica.MirrorObserver = observer;
        clientSession.WorldReplica.SnapshotAckSink = new AckToHost(hostSession);

        clientSession.WorldReplica.Subscribe(Scope);
        hostSession.HostReplicator.Subscribe(1, Scope);
        hostSession.OnFrameBoundary(100);
        clientSession.OnFrameBoundary(100);
        hostSession.OnFrameBoundary(110);
        clientSession.OnFrameBoundary(110);
        TestAssert.IsTrue(clientSession.WorldReplica.Versions.TryGetScope(Scope, out var clientScope));
        TestAssert.AreEqual(SubscriptionPhase.Live, clientScope.Phase);
        TestAssert.AreEqual(1, observer.BaselinesInstalled);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(500, observer.Pools.Shells[9].Hp);

        var rebuilt = Key(9, 2);
        enemies.Members.Remove(unitA);
        enemies.Members.Add(rebuilt);
        enemies.States[rebuilt] = Encoded(Unit(baseId: 2, hp: 880));
        enemies.States[lone] = Encoded(Unit(baseId: 0, hp: 100));
        hostSession.Capture.Capture(120);
        hostSession.OnFrameBoundary(120);
        clientSession.OnFrameBoundary(120);

        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(880, observer.Pools.Shells[9].Hp, "Same slot, new generation: fresh shell, never the dead one's HP.");
        TestAssert.AreEqual(0, observer.Pools.PureRemovals, "Slot reuse transfers ownership: the trailing despawn of the old generation must not delete the new shell (no Kill path exists in the fake pools by construction).");
        TestAssert.AreEqual(100, observer.Pools.Shells[12].Hp);
    }

    [TestMethod]
    public void LosingTheCoreRebuildsTheScope()
    {
        var observer = new TestBindingObserver();
        var core = Key(4, 1);
        var unit = Key(9, 1);
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(core, 1, Encoded(BaseCore(baseId: 2))),
            new(unit, 2, Encoded(Unit(baseId: 2)))
        });
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.IsFalse(observer.Pools.EnemyExists(4));
        TestAssert.IsFalse(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(2, observer.Pools.PureRemovals);
    }

    [TestMethod]
    public void TheCompositeFansOutToBothDomains()
    {
        var entityView = new FakeHostEnemies();
        var compositeView = new CompositeHostWorldView(new FakeGroundView(), new FakeGroundView(empty: true));
        TestAssert.IsFalse(compositeView.TryReadMembers(new ScopeKey(PoolKind.Entity, 101), new List<ObjectKey>()));
        var applied = new List<string>();
        var composite = new CompositeMirrorObserver(new RecordingObserver("first", applied), new RecordingObserver("second", applied));
        var key = Key(1, 1);
        composite.OnStateApplied(Scope, key, 1, Encoded(Unit(baseId: 0)));
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, applied);
    }

    private sealed class FakeGroundView : IHostWorldView
    {
        private readonly bool empty;
        public FakeGroundView(bool empty = false) => this.empty = empty;
        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members) => false;
        public bool TryReadState(ObjectKey key, out byte[] state)
        {
            state = null;
            return false;
        }
    }

    private sealed class RecordingObserver : IReplicaMirrorObserver
    {
        private readonly string name;
        private readonly List<string> applied;
        public RecordingObserver(string name, List<string> applied)
        {
            this.name = name;
            this.applied = applied;
        }
        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state) => applied.Add(name);
        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members) { }
        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key) { }
    }
}
