using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A11: the host combat executor drains intents at the frame boundary, spends once, and answers
/// retries from the cache.
/// </summary>
[TestClass]
public class HostPlayerCombatExecutorTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA11E5E11A11E5E11, 0x1234567890ABCDEF);
    private static readonly ConnectionEpoch ConnA = new(41);
    private static readonly ConnectionEpoch ConnB = new(42);

    private sealed class AllowAllRules : IHostCombatRules
    {
        public PlayerCombatCosts Costs => PlayerCombatCosts.Default;

        public bool IsTargetKnown(in ObjectKey target, out string reason)
        {
            reason = null;
            return true;
        }

        public bool IsInRange(string persistentId, in ObjectKey target, PlayerWeaponKind weapon,
            out string reason)
        {
            reason = null;
            return true;
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Session = new AuthoritySession(new AuthoritySessionState());
            Session.BeginAuthorityWorld(Epoch, isHost: true);
            Session.HostCombatExecutor.Rules = new AllowAllRules();
            ConnectionA = Session.AssignConnectionEpoch(2);
            ConnectionB = Session.AssignConnectionEpoch(3);
            Session.HostPlayers.RegisterOrUpdate("p1", 2, HostPlayerRole.Remote, ConnectionA);
            Session.HostPlayers.RegisterOrUpdate("p2", 3, HostPlayerRole.Remote, ConnectionB);
            Session.HostCombat.EnsureOwner("p1", 1000);
            Session.HostCombat.EnsureOwner("p2", 1000);
        }

        public AuthoritySession Session { get; }

        public ConnectionEpoch ConnectionA { get; }

        public ConnectionEpoch ConnectionB { get; }

        public const long HostTick = 50000;

        public void SeedAmmo(string persistentId, int itemId, long count) =>
            Session.HostLedger.SeedLong(LedgerOwner.ForPlayer(persistentId),
                LedgerResourceKind.AmmoBullet, itemId, count);

        public void SeedEnergy(string persistentId, double amount) =>
            Session.HostLedger.SeedDouble(LedgerOwner.ForPlayer(persistentId),
                LedgerResourceKind.CoreEnergy, amount);

        public void SeedBombs(string persistentId, int protoId, long count) =>
            Session.HostLedger.SeedLong(LedgerOwner.ForPlayer(persistentId),
                LedgerResourceKind.BombStorageItem, protoId, count);

        public ObjectKey Target(int nativeId = 7) =>
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, 1);

        public long RevisionOf(string persistentId, LedgerResourceKind kind, int itemId = 0) =>
            Session.HostLedger.RevisionOf(
                new LedgerResourceKey(LedgerOwner.ForPlayer(persistentId), kind, itemId));

        public AuthorityCommandPacket FirePacket(ushort sessionId, ConnectionEpoch connection,
            long sequence, string persistentId, int ammoItemId, ObjectKey target)
        {
            var request = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire,
                PlayerWeaponKind.Gauss, ammoItemId, HostTick,
                RevisionOf(persistentId, LedgerResourceKind.AmmoBullet, ammoItemId), 0, 0);
            PlayerCombatCommand.TryEncode(request, out var payload);
            return AuthorityCommandPacket.Create(
                new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
                    connection, sequence, HostTick, sessionId, payload.Length),
                target, PlayerCombatCommand.Category, payload);
        }

        public void Dispose() => Session.Dispose();
    }

    [TestMethod]
    public void AHostWorldOpensCombatStateAndAnExecutor()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostCombat);
        TestAssert.IsNotNull(session.HostCombatExecutor);
        TestAssert.IsNotNull(session.HostExecutor);

        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(client.HostCombat);
        TestAssert.IsNull(client.HostCombatExecutor);
        session.Dispose();
        client.Dispose();
    }

    [TestMethod]
    public void AFireCommandSpendsOneRoundAndRecordsOneSkill()
    {
        var harness = new Harness();
        harness.SeedAmmo("p1", 7001, 10);
        var packet = harness.FirePacket(2, harness.ConnectionA, 1, "p1", 7001, harness.Target());
        var key = new CommandKey(Epoch, harness.ConnectionA, 1);

        var queue = harness.Session.Commands;
        TestAssert.IsTrue(queue.TryEnqueue(key, packet, 2, 99, Harness.HostTick));
        harness.Session.HostCombatExecutor.HostTick = Harness.HostTick;
        var outcomes = new List<CommandOutcome>();
        queue.Drain(harness.Session.HostCombatExecutor, (_, _, outcome) => outcomes.Add(outcome));

        TestAssert.HasCount(1, outcomes);
        TestAssert.AreEqual(CommandResultCode.Applied, outcomes[0].Code);
        TestAssert.IsTrue(outcomes[0].TransactionId > 0);
        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.AmmoBullet, 7001, out var balance, out _);
        TestAssert.AreEqual(9L, balance, "One trigger pull spends exactly one round.");
        TestAssert.HasCount(1, harness.Session.HostCombatExecutor.PendingSkills);

        // A retry of the same key is answered from the cache: no second spend, no second skill.
        TestAssert.IsTrue(queue.TryEnqueue(key, packet, 2, 99, Harness.HostTick + 5));
        var dispositions = new List<CommandDrainDisposition>();
        queue.Drain(harness.Session.HostCombatExecutor, (_, disposition, _) => dispositions.Add(disposition));
        TestAssert.AreEqual(CommandDrainDisposition.ReplayedDuplicate, dispositions[0]);
        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.AmmoBullet, 7001, out var kept, out _);
        TestAssert.AreEqual(9L, kept);
        TestAssert.HasCount(1, harness.Session.HostCombatExecutor.PendingSkills);
        harness.Dispose();
    }

    [TestMethod]
    public void EachWeaponSpendsItsOwnPool()
    {
        // The numeric对照: one submit per weapon family, each moving its own balance once.
        var harness = new Harness();
        harness.SeedAmmo("p1", 7001, 30);
        harness.SeedEnergy("p1", 100);
        harness.SeedBombs("p1", 8001, 5);
        var costs = PlayerCombatCosts.Default;
        var executor = harness.Session.HostCombatExecutor;
        executor.HostTick = Harness.HostTick;

        long sequence = 1;
        CommandOutcome Drain(PlayerCombatRequest request, ObjectKey target, bool hasTarget,
            ushort sessionId, ConnectionEpoch connection)
        {
            PlayerCombatCommand.TryEncode(request, out var payload);
            var packet = AuthorityCommandPacket.Create(
                new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
                    connection, sequence, Harness.HostTick, sessionId, payload.Length),
                target, PlayerCombatCommand.Category, payload);
            var key = new CommandKey(Epoch, connection, sequence);
            sequence++;
            harness.Session.Commands.TryEnqueue(key, packet, sessionId, 7, Harness.HostTick);
            CommandOutcome outcome = default;
            harness.Session.Commands.Drain(executor, (_, _, o) => outcome = o);
            executor.HostTick += costs.CooldownTicksPerShot;
            return outcome;
        }

        var ammoRev = harness.RevisionOf("p1", LedgerResourceKind.AmmoBullet, 7001);
        var gun = Drain(new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, executor.HostTick, ammoRev, 0, 0), harness.Target(11), true, 2, harness.ConnectionA);
        TestAssert.AreEqual(CommandResultCode.Applied, gun.Code);

        var energyRev = harness.RevisionOf("p1", LedgerResourceKind.CoreEnergy);
        var laser = Drain(new PlayerCombatRequest(PlayerCombatAction.LaserFire, PlayerWeaponKind.Laser,
            0, executor.HostTick, energyRev, 0, 0), harness.Target(12), true, 2, harness.ConnectionA);
        TestAssert.AreEqual(CommandResultCode.Applied, laser.Code);

        var bombRev = harness.RevisionOf("p1", LedgerResourceKind.BombStorageItem, 8001);
        var bomb = Drain(new PlayerCombatRequest(PlayerCombatAction.BombDrop, PlayerWeaponKind.Bomb,
            0, executor.HostTick, bombRev, 8001, 5), default, false, 2, harness.ConnectionA);
        TestAssert.AreEqual(CommandResultCode.Applied, bomb.Code);

        energyRev = harness.RevisionOf("p1", LedgerResourceKind.CoreEnergy);
        var burst = Drain(new PlayerCombatRequest(PlayerCombatAction.ShieldBurst, PlayerWeaponKind.Shield,
            0, executor.HostTick, energyRev, 0, 0), default, false, 2, harness.ConnectionA);
        TestAssert.AreEqual(CommandResultCode.Applied, burst.Code);

        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.AmmoBullet, 7001, out var ammo, out _);
        harness.Session.HostLedger.TryGetDouble(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.CoreEnergy, out var energy, out _);
        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.BombStorageItem, 8001, out var bombs, out _);
        TestAssert.AreEqual(29L, ammo, "30 - 1 gun round.");
        TestAssert.AreEqual(100 - costs.LaserEnergyPerShot - costs.ShieldEnergyPerBurst, energy, 1e-9);
        TestAssert.AreEqual(4L, bombs, "5 - 1 bomb.");
        TestAssert.AreEqual(4L, executor.SkillsRecordedTotal, "Four pulls, four skill records.");
        harness.Dispose();
    }

    [TestMethod]
    public void UnknownCategoriesStayNotReady()
    {
        var harness = new Harness();
        var executor = harness.Session.HostCombatExecutor;
        executor.HostTick = Harness.HostTick;
        var packet = AuthorityCommandPacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
                harness.ConnectionA, 1, Harness.HostTick, 2, 0),
            default, category: 0x21, payload: new byte[0]);
        var outcome = executor.Execute(new QueuedHostCommand(
            new CommandKey(Epoch, harness.ConnectionA, 1), packet, 2, 7, Harness.HostTick));
        TestAssert.AreEqual(CommandResultCode.RejectedNotReady, outcome.Code);
        harness.Dispose();
    }

    [TestMethod]
    public void TwoPlayersDrainWithoutCrosstalk()
    {
        var harness = new Harness();
        harness.SeedAmmo("p1", 7001, 10);
        harness.SeedAmmo("p2", 7001, 10);
        var executor = harness.Session.HostCombatExecutor;
        executor.HostTick = Harness.HostTick;

        var outcomes = new Dictionary<long, CommandOutcome>();
        var queue = harness.Session.Commands;
        queue.TryEnqueue(new CommandKey(Epoch, harness.ConnectionA, 1),
            harness.FirePacket(2, harness.ConnectionA, 1, "p1", 7001, harness.Target(21)), 2, 7, Harness.HostTick);
        queue.TryEnqueue(new CommandKey(Epoch, harness.ConnectionB, 1),
            harness.FirePacket(3, harness.ConnectionB, 1, "p2", 7001, harness.Target(22)), 3, 8, Harness.HostTick);
        queue.Drain(executor, (command, _, outcome) => outcomes[command.Key.Sequence * 10 + command.ConnectionPlayerId] = outcome);

        TestAssert.HasCount(2, outcomes);
        foreach (var outcome in outcomes.Values)
            TestAssert.AreEqual(CommandResultCode.Applied, outcome.Code);
        // Distinct ledger transactions group each owner's effects separately.
        TestAssert.AreNotEqual(outcomes[12].TransactionId, outcomes[13].TransactionId);
        harness.Dispose();
    }

    [TestMethod]
    public void ContinuousFireTicksOnTheHostClock()
    {
        var harness = new Harness();
        harness.SeedAmmo("p1", 7001, 10);
        var executor = harness.Session.HostCombatExecutor;
        var costs = PlayerCombatCosts.Default;
        executor.HostTick = Harness.HostTick;

        var rev = harness.RevisionOf("p1", LedgerResourceKind.AmmoBullet, 7001);
        var start = new PlayerCombatRequest(PlayerCombatAction.StartContinuous, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, rev, 0, 0);
        PlayerCombatCommand.TryEncode(start, out var payload);
        var packet = AuthorityCommandPacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
                harness.ConnectionA, 1, Harness.HostTick, 2, payload.Length),
            harness.Target(), PlayerCombatCommand.Category, payload);
        harness.Session.Commands.TryEnqueue(new CommandKey(Epoch, harness.ConnectionA, 1),
            packet, 2, 7, Harness.HostTick);
        harness.Session.Commands.Drain(executor, null);

        // The start spent the first round; ticks before the interval spend nothing.
        executor.HostTick = Harness.HostTick + 1;
        executor.TickContinuous();
        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.AmmoBullet, 7001, out var afterStart, out _);
        TestAssert.AreEqual(9L, afterStart);

        executor.HostTick = Harness.HostTick + costs.ContinuousTickInterval;
        executor.TickContinuous();
        harness.Session.HostLedger.TryGetLong(LedgerOwner.ForPlayer("p1"),
            LedgerResourceKind.AmmoBullet, 7001, out var afterTick, out _);
        TestAssert.AreEqual(8L, afterTick, "One host interval ticks one round.");

        // Disconnect stops the session without spending: no infinite lease on a gone client.
        harness.Session.ForgetPlayerConnection(2);
        executor.HostTick += costs.ContinuousTickInterval;
        executor.TickContinuous();
        TestAssert.IsFalse(harness.Session.HostCombat.EnsureOwner("p1", 1000).ContinuousFiring);
        harness.Dispose();
    }

    [TestMethod]
    public void TheCasterScopeRestoresPlayerIdEvenOnThrow()
    {
        var before = NebulaWorld.Combat.CombatManager.PlayerId;
        try
        {
            using (new HostCombatScope(5))
            {
                TestAssert.AreEqual(5, NebulaWorld.Combat.CombatManager.PlayerId);
                using (new HostCombatScope(9))
                {
                    TestAssert.AreEqual(9, NebulaWorld.Combat.CombatManager.PlayerId);
                    throw new System.InvalidOperationException("boom");
                }
            }
        }
        catch (System.InvalidOperationException)
        {
        }
        TestAssert.AreEqual(before, NebulaWorld.Combat.CombatManager.PlayerId,
            "Nested scopes unwind in reverse order even when the inner body throws.");
    }
}
