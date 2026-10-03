#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A13 end-to-end: host space enemies and craft flow through the standard replicator and the
/// client's replica into bindings that rebuild display-only shells. Covers the task card's
/// acceptance at the model level: full lifecycle, cross-astro key stability, slot reuse, scope
/// rebuild after losing the hive core, multi-owner craft isolation — and never through a Kill or
/// destruction path (the fake pools have neither to call).
/// </summary>
[TestClass]
public class SpaceCraftReplicationTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A13080808080808, 0x0D13080808080808);
    private static readonly ScopeKey SpaceScope = new(PoolKind.SpaceEnemy, AuthorityScope.Sector);
    private static readonly ScopeKey SpaceCraftScope = new(PoolKind.SpaceCraft, AuthorityScope.Sector);
    private static readonly ScopeKey GroundCraftScope = new(PoolKind.GroundCraft, 101);

    private static ObjectKey SpaceKey(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.SpaceEnemy, AuthorityScope.Sector, nativeId, generation);

    private static ObjectKey SpaceCraftKey(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.SpaceCraft, AuthorityScope.Sector, nativeId, generation);

    private static SpaceEnemyState HiveCore() => new(
        SpaceEnemyKind.SpaceCore, true, false, 8100, 40, 1, 0, 0,
        1000101, 1000101, 0, 5, 3, 0.0, 0.0, 0.0, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 5000, 8000, 0, 0);

    private static SpaceEnemyState HiveUnit(int hp = 500) => new(
        SpaceEnemyKind.SpaceUnit, true, false, 8113, 45, 2, 3, 0,
        1000101, 1000101, 0, 0, 0, 4900.0, -350.0, 1200.0, 0f, 0f, 0f, 1f, 0f, 0f, 0f, hp, 900, 2, 0);

    private static CraftState SpaceFighter(int owner, int hp = 700) => new(
        true, true, true, 5201, 61, 2, 7, 0, 1000101, owner, 9,
        4900.0, -350.0, 1200.0, 0f, 0f, 0f, 1f, 10f, 0f, 0f, hp, 1100, 0, 0);

    private static byte[] Encoded(SpaceEnemyState state)
    {
        TestAssert.IsTrue(SpaceEnemyStateCodec.TryEncode(state, out var data));
        return data;
    }

    private static byte[] Encoded(CraftState state)
    {
        TestAssert.IsTrue(CraftStateCodec.TryEncode(state, out var data));
        return data;
    }

    private sealed class FakeHostDomain : IHostWorldView
    {
        public readonly List<ObjectKey> Members = [];
        public readonly Dictionary<ObjectKey, byte[]> States = [];

        public bool TryReadMembers(ScopeKey scope, List<ObjectKey> members)
        {
            var mine = new List<ObjectKey>();
            foreach (var key in Members)
            {
                if (key.Kind == scope.Kind && key.Scope == scope.Scope) mine.Add(key);
            }
            if (mine.Count == 0 && Members.Count > 0) return false;
            if (Members.Count == 0) return true;
            members.AddRange(mine);
            return true;
        }

        public bool TryReadState(ObjectKey key, out byte[] state) => States.TryGetValue(key, out state!);
    }

    private sealed class FakeSpacePools : ISpaceEnemyPools
    {
        public readonly Dictionary<int, SpaceEnemyState> Shells = [];
        public int PureRemovals;

        public bool EnemyExists(int enemyId) => Shells.ContainsKey(enemyId);
        public void CreateEnemyShell(int enemyId, in SpaceEnemyState state) => Shells[enemyId] = state;
        public void WriteEnemyState(int enemyId, in SpaceEnemyState state) => Shells[enemyId] = state;

        public void RemoveEnemyShell(int enemyId)
        {
            TestAssert.IsTrue(Shells.Remove(enemyId), "Pure removal releases a shell that exists; no Kill, no drops.");
            PureRemovals++;
        }
    }

    private sealed class FakeCraftPools : ICraftPools
    {
        public readonly Dictionary<int, CraftState> Shells = [];
        public int PureRemovals;

        public bool CraftExists(int craftId) => Shells.ContainsKey(craftId);
        public void CreateCraftShell(int craftId, in CraftState state) => Shells[craftId] = state;
        public void WriteCraftState(int craftId, in CraftState state) => Shells[craftId] = state;

        public void RemoveCraftShell(int craftId)
        {
            TestAssert.IsTrue(Shells.Remove(craftId), "Pure removal releases a shell that exists; no destruction rule.");
            PureRemovals++;
        }
    }

    private sealed class TestSpaceObserver : IReplicaMirrorObserver
    {
        public readonly FakeSpacePools Pools = new();
        public readonly SpaceEnemyBinding Core;
        public int BaselinesInstalled;

        public TestSpaceObserver() => Core = new SpaceEnemyBinding(Pools);

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
        {
            if (scope.Kind == PoolKind.SpaceEnemy) Core.ApplyState(key, state);
        }

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            if (scope.Kind != PoolKind.SpaceEnemy) return;
            BaselinesInstalled++;
            Core.ReconcileBaseline(members);
        }

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
        {
            if (scope.Kind == PoolKind.SpaceEnemy) Core.RemoveMember(key);
        }
    }

    private sealed class TestCraftObserver : IReplicaMirrorObserver
    {
        public readonly FakeCraftPools Pools = new();
        public readonly CraftBinding Core;
        public int BaselinesInstalled;

        public TestCraftObserver() => Core = new CraftBinding(PoolKind.SpaceCraft, AuthorityScope.Sector, Pools);

        public void OnStateApplied(ScopeKey scope, in ObjectKey key, long revision, byte[] state)
        {
            if (scope.Kind == PoolKind.SpaceCraft) Core.ApplyState(key, state);
        }

        public void OnBaselineInstalled(ScopeKey scope, IReadOnlyList<SnapshotMemberRecord> members)
        {
            if (scope.Kind != PoolKind.SpaceCraft) return;
            BaselinesInstalled++;
            Core.ReconcileBaseline(members);
        }

        public void OnMemberRemoved(ScopeKey scope, in ObjectKey key)
        {
            if (scope.Kind == PoolKind.SpaceCraft) Core.RemoveMember(key);
        }
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
    public void ASpaceLifecycleSurvivesTheFullConversation()
    {
        var enemies = new FakeHostDomain();
        var core = SpaceKey(4, 1);
        var unitA = SpaceKey(9, 1);
        enemies.Members.AddRange([core, unitA]);
        enemies.States[core] = Encoded(HiveCore());
        enemies.States[unitA] = Encoded(HiveUnit());

        var hostSession = new AuthoritySession(new AuthoritySessionState());
        hostSession.BeginAuthorityWorld(Epoch, isHost: true);
        var clientSession = new AuthoritySession(new AuthoritySessionState());
        clientSession.BeginAuthorityWorld(Epoch, isHost: false);
        hostSession.RegisterHostReplication(enemies, new SinkToClientInbox(clientSession));

        var observer = new TestSpaceObserver();
        clientSession.WorldReplica.MirrorObserver = observer;
        clientSession.WorldReplica.SnapshotAckSink = new AckToHost(hostSession);

        clientSession.WorldReplica.Subscribe(SpaceScope);
        hostSession.HostReplicator.Subscribe(1, SpaceScope);
        hostSession.OnFrameBoundary(100);
        clientSession.OnFrameBoundary(100);
        hostSession.OnFrameBoundary(110);
        clientSession.OnFrameBoundary(110);
        TestAssert.IsTrue(clientSession.WorldReplica.Versions.TryGetScope(SpaceScope, out var clientScope));
        TestAssert.AreEqual(SubscriptionPhase.Live, clientScope.Phase);
        TestAssert.AreEqual(1, observer.BaselinesInstalled);
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(500, observer.Pools.Shells[9].Hp);

        var rebuilt = SpaceKey(9, 2);
        enemies.Members.Remove(unitA);
        enemies.Members.Add(rebuilt);
        enemies.States[rebuilt] = Encoded(HiveUnit(hp: 880));
        hostSession.Capture.Capture(120);
        hostSession.OnFrameBoundary(120);
        clientSession.OnFrameBoundary(120);

        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(880, observer.Pools.Shells[9].Hp, "Same slot, new generation: fresh shell, never the dead one's HP.");
        TestAssert.AreEqual(0, observer.Pools.PureRemovals, "Slot reuse transfers ownership: the trailing despawn of the old generation must not delete the new shell.");
    }

    [TestMethod]
    public void LosingTheHiveCoreRebuildsTheScope()
    {
        var observer = new TestSpaceObserver();
        var core = SpaceKey(4, 1);
        var unit = SpaceKey(9, 1);
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>
        {
            new(core, 1, Encoded(HiveCore())),
            new(unit, 2, Encoded(HiveUnit()))
        });
        TestAssert.IsTrue(observer.Pools.EnemyExists(4));
        TestAssert.IsTrue(observer.Pools.EnemyExists(9));
        observer.Core.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.IsFalse(observer.Pools.EnemyExists(4));
        TestAssert.IsFalse(observer.Pools.EnemyExists(9));
        TestAssert.AreEqual(2, observer.Pools.PureRemovals);
    }

    [TestMethod]
    public void TwoPlayersCraftNeverOccupyEachOther()
    {
        var observer = new TestCraftObserver();
        var first = SpaceCraftKey(7, 1);
        var second = SpaceCraftKey(8, 1);
        observer.Core.ApplyState(first, Encoded(SpaceFighter(owner: 5, hp: 700)));
        observer.Core.ApplyState(second, Encoded(SpaceFighter(owner: 9, hp: 600)));
        TestAssert.AreEqual(5, observer.Pools.Shells[7].Owner);
        TestAssert.AreEqual(9, observer.Pools.Shells[8].Owner);
        observer.Core.ApplyState(first, Encoded(SpaceFighter(owner: 5, hp: 300)));
        TestAssert.AreEqual(300, observer.Pools.Shells[7].Hp);
        TestAssert.AreEqual(600, observer.Pools.Shells[8].Hp);
        observer.Core.RemoveMember(first);
        TestAssert.IsFalse(observer.Pools.CraftExists(7));
        TestAssert.IsTrue(observer.Pools.CraftExists(8), "Removing one owner's craft never touches the other's.");
    }

    [TestMethod]
    public void TheCompositeFansOutAcrossAllFourDomains()
    {
        var applied = new List<string>();
        var composite = new CompositeMirrorObserver(
            new RecordingObserver("entity", applied),
            new RecordingObserver("ground", applied),
            new RecordingObserver("space", applied),
            new RecordingObserver("craft", applied));
        var spaceKey = SpaceKey(1, 1);
        composite.OnStateApplied(SpaceScope, spaceKey, 1, Encoded(HiveUnit()));
        var craftKey = SpaceCraftKey(2, 1);
        composite.OnStateApplied(SpaceCraftScope, craftKey, 1, Encoded(SpaceFighter(owner: 5)));
        TestAssert.AreEqual(8, applied.Count, "Each of the two states fans out to all four observers (they filter by scope internally).");
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
