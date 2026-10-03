#region

using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A12's pure-model surface: the canonical ground-enemy state codec, the kind derivation that
/// replaces the old snapshot's string table, and the client binding that rebuilds display-only
/// shells from host facts. Everything here runs without a game process.
/// </summary>
[TestClass]
public class GroundEnemyStateTest
{
    private static readonly AuthorityEpoch Epoch = new(0x0A12080808080808, 0x0B12080808080808);
    private const int Planet = 101;

    private static ObjectKey Key(int nativeId, long generation) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, Planet, nativeId, generation);

    private static GroundEnemyState Unit(int baseId, int hp = 500) => new(
        GroundEnemyKind.GroundUnit, true, false,
        protoId: 8130, modelIndex: 45, owner: 2, port: 3, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: baseId, builderIndex: 0, level: 1,
        posX: 10f, posY: 0f, posZ: 20f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: hp, hpMax: 900, hpRecover: 2, hpIncoming: 0);

    private static GroundEnemyState BaseCore(int baseId) => new(
        GroundEnemyKind.GroundBase, true, false,
        protoId: 8100, modelIndex: 40, owner: 1, port: 0, stateFlags: 0,
        astroId: Planet, originAstroId: Planet, baseId: baseId, builderIndex: 5, level: 3,
        posX: 0f, posY: 0f, posZ: 0f, rotX: 0f, rotY: 0f, rotZ: 0f, rotW: 1f,
        velX: 0f, velY: 0f, velZ: 0f, hp: 5000, hpMax: 8000, hpRecover: 0, hpIncoming: 0);

    private static byte[] Encoded(GroundEnemyState state)
    {
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var data), "A defined kind must encode.");
        return data;
    }

    private sealed class FakePools : IGroundEnemyPools
    {
        public readonly Dictionary<int, GroundEnemyState> Shells = [];
        public readonly List<int> Removed = [];

        public bool EnemyExists(int enemyId) => Shells.ContainsKey(enemyId);
        public void CreateEnemyShell(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;
        public void WriteEnemyState(int enemyId, in GroundEnemyState state) => Shells[enemyId] = state;
        public void RemoveEnemyShell(int enemyId)
        {
            TestAssert.IsTrue(Shells.Remove(enemyId), "A removed shell must have existed.");
            Removed.Add(enemyId);
        }
    }

    [TestMethod]
    public void AStateRoundTripsEveryField()
    {
        var state = Unit(baseId: 7, hp: 640);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(state, out var data));
        TestAssert.IsTrue(GroundEnemyStateCodec.TryDecode(data, 0, data.Length, out var decoded, out var reject), reject.ToString());
        TestAssert.AreEqual(GroundEnemyKind.GroundUnit, decoded.Kind);
        TestAssert.IsTrue(decoded.HasCombatStat);
        TestAssert.AreEqual((short)8130, decoded.ProtoId);
        TestAssert.AreEqual(7, decoded.BaseId);
        TestAssert.AreEqual(10f, decoded.PosX, 1e-6f);
        TestAssert.AreEqual(1f, decoded.RotW, 1e-6f);
        TestAssert.AreEqual(640, decoded.Hp);
        TestAssert.AreEqual(900, decoded.HpMax);
    }

    [TestMethod]
    public void TheEncodedLayoutIsFixedAndBounded()
    {
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(Unit(1), out var data));
        TestAssert.AreEqual(GroundEnemyStateCodec.FixedSize, data.Length);
        TestAssert.IsTrue(data.Length <= AuthorityLimits.StateRecordMaxBytes);
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(Unit(1), out var again));
        CollectionAssert.AreEqual(data, again, "Encoding is deterministic: the dirt comparison depends on it.");
    }

    [TestMethod]
    public void ACorruptBlobIsRefused()
    {
        TestAssert.IsTrue(GroundEnemyStateCodec.TryEncode(Unit(1), out var data));
        var unknownKind = (byte[])data.Clone();
        unknownKind[1] = 0;
        TestAssert.IsFalse(GroundEnemyStateCodec.TryDecode(unknownKind, 0, unknownKind.Length, out _, out var kindReject));
        TestAssert.AreEqual(AuthorityRejectCode.MalformedEnvelope, kindReject.Code);
        var badFlags = (byte[])data.Clone();
        badFlags[2] |= 0x80;
        TestAssert.IsFalse(GroundEnemyStateCodec.TryDecode(badFlags, 0, badFlags.Length, out _, out _));
        var truncated = new byte[data.Length - 4];
        System.Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);
        TestAssert.IsFalse(GroundEnemyStateCodec.TryDecode(truncated, 0, truncated.Length, out _, out _));
        var padded = new byte[data.Length + 1];
        System.Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        TestAssert.IsFalse(GroundEnemyStateCodec.TryDecode(padded, 0, padded.Length, out _, out _));
        TestAssert.IsFalse(GroundEnemyStateCodec.TryEncode(new GroundEnemyState(), out _), "Unknown kind never encodes.");
    }

    [TestMethod]
    public void KindDerivationPrefersBaseOverUnitOverBuilder()
    {
        TestAssert.AreEqual(GroundEnemyKind.GroundBase,
            GroundEnemyKinds.FromComponentIds(dfGBaseId: 3, unitId: 4, builderId: 5, dfGTurretId: 6, dfGShieldId: 7, dfGConnectorId: 8, dfGReplicatorId: 9));
        TestAssert.AreEqual(GroundEnemyKind.GroundUnit,
            GroundEnemyKinds.FromComponentIds(0, unitId: 4, builderId: 5, 0, 0, 0, 0));
        TestAssert.AreEqual(GroundEnemyKind.GroundTurret,
            GroundEnemyKinds.FromComponentIds(0, 0, 0, dfGTurretId: 6, 0, 0, 0));
        TestAssert.AreEqual(GroundEnemyKind.Unknown,
            GroundEnemyKinds.FromComponentIds(0, 0, 0, 0, 0, 0, 0));
    }

    [TestMethod]
    public void AHostEnemyIsCreatedAsADisplayShell()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(Unit(baseId: 0))));
        TestAssert.IsTrue(pools.EnemyExists(7));
        TestAssert.AreEqual(GroundEnemyKind.GroundUnit, pools.Shells[7].Kind);
        TestAssert.AreEqual(1, binding.BoundCount);
    }

    [TestMethod]
    public void UpdatingABoundEnemyWritesAbsoluteValuesWithoutReallocating()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(Unit(baseId: 0, hp: 500))));
        TestAssert.IsTrue(binding.ApplyState(Key(7, 1), Encoded(Unit(baseId: 0, hp: 300))));
        TestAssert.AreEqual(300, pools.Shells[7].Hp);
        TestAssert.AreEqual(1L, binding.ShellsCreated);
        TestAssert.AreEqual(1, binding.BoundCount);
    }

    [TestMethod]
    public void AChildBeforeItsBaseIsDeferredThenConverges()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        var child = Key(9, 1);
        TestAssert.IsFalse(binding.ApplyState(child, Encoded(Unit(baseId: 2))));
        TestAssert.AreEqual(1L, binding.StatesDeferred);
        TestAssert.IsFalse(pools.EnemyExists(9), "A deferred child creates nothing until its base is known.");
        TestAssert.IsTrue(binding.ApplyState(Key(4, 1), Encoded(BaseCore(baseId: 2))));
        TestAssert.IsTrue(pools.EnemyExists(9), "The base arrival retries the deferred child.");
        TestAssert.AreEqual(500, pools.Shells[9].Hp);
        TestAssert.AreEqual(0, binding.DeferredCount);
    }

    [TestMethod]
    public void ARebuiltSlotIsANewKeyThatNeverInheritsTheOldShell()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        var first = Key(7, 1);
        TestAssert.IsTrue(binding.ApplyState(first, Encoded(new GroundEnemyState(GroundEnemyKind.GroundBuilder, false, false,
            8101, 41, 1, 0, 0, Planet, Planet, 0, 2, 0, 1f, 0f, 2f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0, 0, 0, 0))));
        binding.RemoveMember(first);
        TestAssert.IsFalse(pools.EnemyExists(7));
        TestAssert.AreEqual(1, pools.Removed.Count);
        var second = Key(7, 2);
        TestAssert.IsTrue(binding.ApplyState(second, Encoded(Unit(baseId: 0, hp: 880))));
        TestAssert.IsTrue(pools.EnemyExists(7));
        TestAssert.AreEqual(880, pools.Shells[7].Hp);
    }

    [TestMethod]
    public void ABaselineReconcilesTheWholeScopeAtomically()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        TestAssert.IsTrue(binding.ApplyState(Key(4, 1), Encoded(BaseCore(baseId: 2))));
        TestAssert.IsTrue(binding.ApplyState(Key(9, 1), Encoded(Unit(baseId: 2))));
        // The baseline keeps the base core and the child (now at 100 HP): both survive, the child
        // converges to the new absolute HP. A baseline that kept the child without its base would
        // leave the child deferred by design, so the base stays named here.
        var members = new List<SnapshotMemberRecord>
        {
            new(Key(4, 1), 7, Encoded(BaseCore(baseId: 2))),
            new(Key(9, 1), 6, Encoded(Unit(baseId: 2, hp: 100)))
        };
        binding.ReconcileBaseline(members);
        TestAssert.IsTrue(pools.EnemyExists(4));
        TestAssert.IsTrue(pools.EnemyExists(9));
        TestAssert.AreEqual(100, pools.Shells[9].Hp);
        TestAssert.AreEqual(2, binding.BoundCount);
        binding.ReconcileBaseline(new List<SnapshotMemberRecord>());
        TestAssert.AreEqual(0, binding.BoundCount);
        TestAssert.AreEqual(2L, binding.MembersRemoved);
    }

    [TestMethod]
    public void ABaselineWithChildBeforeParentStillConverges()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        var members = new List<SnapshotMemberRecord>
        {
            new(Key(9, 1), 2, Encoded(Unit(baseId: 2))),
            new(Key(4, 1), 1, Encoded(BaseCore(baseId: 2)))
        };
        binding.ReconcileBaseline(members);
        TestAssert.IsTrue(pools.EnemyExists(4));
        TestAssert.IsTrue(pools.EnemyExists(9), "The second baseline pass retries the deferred child.");
    }

    [TestMethod]
    public void AKeyOutsideTheBindingScopeIsRefused()
    {
        var pools = new FakePools();
        var binding = new GroundEnemyBinding(Planet, pools);
        TestAssert.IsFalse(binding.ApplyState(Key(1, 1), null));
        TestAssert.AreEqual(1L, binding.StatesRefused);
        var otherPlanet = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 102, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(otherPlanet, Encoded(Unit(baseId: 0))));
        TestAssert.AreEqual(2L, binding.StatesRefused);
        var wrongKind = ObjectKey.Create(Epoch, PoolKind.Entity, Planet, 1, 1);
        TestAssert.IsFalse(binding.ApplyState(wrongKind, Encoded(Unit(baseId: 0))));
        TestAssert.AreEqual(3L, binding.StatesRefused);
    }
}
