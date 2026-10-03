using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A19: one death transaction per object generation, binding statistics/loot/task release once,
/// with tombstones that no same-generation Alive/FullHp can overturn.
/// </summary>
/// <remarks>
/// TASKS.md A19 acceptance: a target killed simultaneously by several weapons produces one death
/// transaction, one drop and one statistics entry; a stale Alive or FullHp cannot revive or heal;
/// the host's own respawn is the only revival, and only for players. Depends on A09 (registry,
/// for the owner key), A15 (task ledger, for the release binding) and A11 (the executor's lethal
/// damage path).
/// </remarks>
[TestClass]
public class HostDeathTransactionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA19DEA7A19DEA7A, 0x19C19C19C19C19);

    private sealed class CountingBinding : IHostDeathBinding
    {
        public int Statistics;
        public int Loot;
        public int TaskReleaseCalls;
        public Func<ObjectKey, int> TaskReleaseOverride;

        public void RecordStatistics(in HostDeathReceipt receipt) => Statistics++;

        public void GrantLoot(in HostDeathReceipt receipt) => Loot++;

        public int ReleaseTasks(in HostDeathReceipt receipt)
        {
            TaskReleaseCalls++;
            return TaskReleaseOverride != null ? TaskReleaseOverride(receipt.Target) : 0;
        }
    }

    private static ObjectKey Entity(int nativeId, long generation, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, planet, nativeId, generation);

    private static ObjectKey GroundEnemy(int nativeId, long generation, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, planet, nativeId, generation);

    private static ConstructionOwnerKey Player(string persistent, ushort session) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

    [TestMethod]
    public void MultiWeaponKillOpensExactlyOneTransaction()
    {
        var binding = new CountingBinding();
        var ledger = new HostDeathLedger(Epoch, binding);
        var target = GroundEnemy(7, 3);

        var first = ledger.OpenDeath(target, 1000, 42);
        var second = ledger.OpenDeath(target, 1000, 42);
        var third = ledger.OpenDeath(target, 1001, 43);

        TestAssert.IsTrue(first.IsFirst, "the first report opens the transaction.");
        TestAssert.IsFalse(second.IsFirst, "a second killing blow in the same frame is a cached no-op.");
        TestAssert.IsFalse(third.IsFirst, "a later replay is a cached no-op too.");
        TestAssert.AreEqual(first.TransactionId, second.TransactionId);
        TestAssert.AreEqual(first.DeathTick, second.DeathTick);
        TestAssert.AreEqual(1, ledger.DeathCount);
        TestAssert.AreEqual(1, binding.Statistics, "one statistics entry per death.");
        TestAssert.AreEqual(1, binding.Loot, "one drop per death.");
        TestAssert.AreEqual(0, binding.TaskReleaseCalls,
            "an enemy death releases no construction tasks; its tombstone is the removal.");
        TestAssert.AreEqual(first.TasksReleased, second.TasksReleased);
    }

    [TestMethod]
    public void SameGenerationAliveAndHealAreRefusedAfterDeath()
    {
        var ledger = new HostDeathLedger(Epoch);
        var target = GroundEnemy(9, 5);
        TestAssert.IsTrue(ledger.CanAcceptAliveState(target), "before death, alive state is fine.");
        TestAssert.IsTrue(ledger.CanAcceptHealing(target));

        ledger.OpenDeath(target, 2000, 10);

        TestAssert.IsTrue(ledger.IsDead(target));
        TestAssert.IsFalse(ledger.CanAcceptAliveState(target), "no same-generation Alive may revive.");
        TestAssert.IsFalse(ledger.CanAcceptHealing(target), "no same-generation FullHp may heal back.");
        // A recycled slot is a different key: its own alive/heal state is independent.
        var nextGeneration = GroundEnemy(9, 6);
        TestAssert.IsFalse(ledger.IsDead(nextGeneration));
        TestAssert.IsTrue(ledger.CanAcceptAliveState(nextGeneration));
        TestAssert.IsTrue(ledger.CanAcceptHealing(nextGeneration));
    }

    [TestMethod]
    public void WrongEpochAndNonDeathPoolAreRefused()
    {
        var binding = new CountingBinding();
        var ledger = new HostDeathLedger(Epoch, binding);
        var otherEpoch = new AuthorityEpoch(0x123, 0x456);
        var foreign = ObjectKey.Create(otherEpoch, PoolKind.GroundEnemy, 101, 1, 1);

        TestAssert.IsTrue(foreign.IsValid, "sanity: the foreign key itself is well formed.");
        TestAssert.IsFalse(ledger.OpenDeath(foreign, 1, 1).HasTombstone, "wrong epoch is refused.");
        var prebuild = ObjectKey.Create(Epoch, PoolKind.Prebuild, 101, 4, 1);
        TestAssert.IsFalse(ledger.OpenDeath(prebuild, 1, 1).HasTombstone,
            "prebuilds are build-domain records, not deaths.");
        TestAssert.AreEqual(0, binding.Statistics, "a refused death runs no side effect.");
        TestAssert.AreEqual(0, ledger.DeathCount);
    }

    [TestMethod]
    public void KindMustMatchPoolKind()
    {
        var ledger = new HostDeathLedger(Epoch);
        // The kind is derived from the pool, so a hostile or buggy caller cannot mislabel an
        // entity death as an enemy death to mint a statistics entry.
        var building = Entity(3, 2);
        var receipt = ledger.OpenDeath(building, 100, 1);
        TestAssert.IsTrue(receipt.IsFirst);
        TestAssert.AreEqual(HostDeathObjectKind.Entity, receipt.Kind);
    }

    [TestMethod]
    public void EntityDeathReleasesConstructionTasksExactlyOnce()
    {
        var tasks = new ConstructionTaskLedger(Epoch);
        var ledger = new HostDeathLedger(Epoch, new ConstructionTaskDeathBinding(tasks));
        var owner = Player("worker", 2);
        TestAssert.IsNotNull(tasks.EnsureBudget(owner, 4), "budget sanity.");
        var target = Entity(11, 8);
        TestAssert.AreEqual(ConstructionTaskError.None,
            tasks.TryAddTask(owner, target, ConstructionTaskKind.Repair, 100,
                new HostTransactionId(Epoch, 1), out var key));
        TestAssert.AreEqual(ConstructionTaskError.None,
            tasks.TryAdvance(key, ConstructionTaskStage.Launching, 101));
        TestAssert.AreEqual(ConstructionTaskError.None,
            tasks.TryAdvance(key, ConstructionTaskStage.Travelling, 102));
        TestAssert.AreEqual(ConstructionTaskError.None,
            tasks.TryAdvance(key, ConstructionTaskStage.Working, 103));
        TestAssert.AreEqual(1, tasks.GetRepairerCount(target), "one repairer before death.");

        var receipt = ledger.OpenDeath(target, 500, 7);
        TestAssert.IsTrue(receipt.IsFirst);
        TestAssert.AreEqual(1, receipt.TasksReleased, "exactly one task released.");

        var repeat = ledger.OpenDeath(target, 501, 7);
        TestAssert.IsFalse(repeat.IsFirst);
        TestAssert.AreEqual(receipt.TasksReleased, repeat.TasksReleased, "cached receipt, no re-cancel.");
        TestAssert.AreEqual(0, tasks.GetRepairerCount(target), "no ghost occupancy.");
        TestAssert.IsTrue(tasks.TryGetTask(key, out var task) && task.IsTerminal &&
            task.CancelReason == ConstructionCancelReason.TargetDestroyed);
    }

    [TestMethod]
    public void NewGenerationOfSameSlotIsNotCancelledByOldDeath()
    {
        var tasks = new ConstructionTaskLedger(Epoch);
        var ledger = new HostDeathLedger(Epoch, new ConstructionTaskDeathBinding(tasks));
        var owner = Player("worker", 2);
        tasks.EnsureBudget(owner, 4);
        var oldBuilding = Entity(12, 1);
        var newBuilding = Entity(12, 2);
        ledger.OpenDeath(oldBuilding, 100, 1);

        TestAssert.AreEqual(ConstructionTaskError.None,
            tasks.TryAddTask(owner, newBuilding, ConstructionTaskKind.Repair, 200,
                new HostTransactionId(Epoch, 2), out _),
            "a task on the rebuilt (next generation) structure is untouched by the old tombstone.");
    }

    [TestMethod]
    public void PlayerDeathIsOnceAndRespawnIsHostOnly()
    {
        var binding = new CountingBinding();
        var ledger = new HostDeathLedger(Epoch, binding);
        var owner = Player("hero", 3);

        var first = ledger.OpenPlayerDeath("hero", owner, 900);
        var second = ledger.OpenPlayerDeath("hero", owner, 950);

        TestAssert.IsTrue(first.IsFirst);
        TestAssert.IsFalse(second.IsFirst);
        TestAssert.AreEqual(first.TransactionId, second.TransactionId);
        TestAssert.AreEqual(1, binding.Loot, "player death drops the inventory once.");
        TestAssert.AreEqual(0, binding.Statistics, "a player is not a kill-statistics entry.");
        TestAssert.IsTrue(ledger.IsPlayerDead("hero"));

        TestAssert.IsFalse(ledger.ApplyHostRespawn("someone-else"), "no tombstone to clear.");
        TestAssert.IsTrue(ledger.ApplyHostRespawn("hero"), "the host respawn clears the tombstone.");
        TestAssert.IsFalse(ledger.IsPlayerDead("hero"));
        var again = ledger.OpenPlayerDeath("hero", owner, 1200);
        TestAssert.IsTrue(again.IsFirst, "a second life may end once too.");
        TestAssert.AreNotEqual(first.TransactionId, again.TransactionId, "a new death transaction.");
    }

    [TestMethod]
    public void TombstonesRecoverOnlyAtAStrictlyNewerBaseline()
    {
        var ledger = new HostDeathLedger(Epoch);
        ledger.OpenDeath(GroundEnemy(2, 1), 10, 1);
        ledger.OpenPlayerDeath("p", Player("p", 1), 10);

        TestAssert.IsFalse(ledger.TryRecoverForNewBaseline(0), "a zero id is never a baseline.");
        TestAssert.IsTrue(ledger.IsDead(GroundEnemy(2, 1)), "refused recovery keeps tombstones.");
        TestAssert.IsTrue(ledger.TryRecoverForNewBaseline(7), "a confirmed new baseline recovers.");
        TestAssert.IsFalse(ledger.IsDead(GroundEnemy(2, 1)));
        TestAssert.IsFalse(ledger.IsPlayerDead("p"));
        TestAssert.IsFalse(ledger.TryRecoverForNewBaseline(7), "replaying the same id recovers nothing.");
        TestAssert.IsFalse(ledger.TryRecoverForNewBaseline(6), "an older id recovers nothing.");
        TestAssert.AreEqual(7, ledger.RecoveryBaseline);
    }

    [TestMethod]
    public void SaturatedLedgerRefusesFailClosedInsteadOfEvicting()
    {
        var binding = new CountingBinding();
        var ledger = new HostDeathLedger(Epoch, binding, capacity: 1);
        var first = ledger.OpenDeath(GroundEnemy(1, 1), 10, 1);
        var second = ledger.OpenDeath(GroundEnemy(2, 1), 10, 1);

        TestAssert.IsTrue(first.IsFirst);
        TestAssert.IsTrue(second.Saturated, "the second death is refused, not tracked over the first.");
        TestAssert.IsFalse(second.IsFirst);
        TestAssert.IsFalse(second.HasTombstone);
        TestAssert.AreEqual(0, binding.TaskReleaseCalls, "a refused death runs no side effect.");
        TestAssert.IsTrue(ledger.IsSaturated);
        TestAssert.IsTrue(ledger.IsDead(first.Target), "the tracked death keeps its tombstone — no eviction.");
    }

    [TestMethod]
    public void BindingFailureDoesNotDoubleExecuteOnRetry()
    {
        var binding = new CountingBinding();
        var boom = false;
        binding.TaskReleaseOverride = _ =>
        {
            if (boom) throw new InvalidOperationException("binding failed mid-death");
            return 0;
        };
        var ledger = new HostDeathLedger(Epoch, binding);
        // An entity owes task release, so the binding's failing step is reached.
        var target = Entity(4, 2);

        ledger.OpenDeath(target, 100, 1);
        TestAssert.AreEqual(1, binding.TaskReleaseCalls, "the task release ran and threw.");

        // The binding is healthy again, but a retry must observe the tombstone, not re-run it.
        var retry = ledger.OpenDeath(target, 100, 1);
        TestAssert.IsFalse(retry.IsFirst);
        TestAssert.AreEqual(1, binding.TaskReleaseCalls, "no second binding run after a failed first attempt.");
        TestAssert.AreEqual(1, ledger.DeathCount);
    }

    [TestMethod]
    public void ClassificationCoversEveryDamageTargetClass()
    {
        // Vanilla EObjectType mapping, with the enemy pool split.
        TestAssert.AreEqual(HostDeathObjectKind.Entity, HostDeathKinds.FromVanillaObjectType(0, false));
        TestAssert.AreEqual(HostDeathObjectKind.Vegetable, HostDeathKinds.FromVanillaObjectType(1, false));
        TestAssert.AreEqual(HostDeathObjectKind.Vein, HostDeathKinds.FromVanillaObjectType(2, false));
        TestAssert.AreEqual(HostDeathObjectKind.GroundEnemy, HostDeathKinds.FromVanillaObjectType(4, false));
        TestAssert.AreEqual(HostDeathObjectKind.SpaceEnemy, HostDeathKinds.FromVanillaObjectType(4, true));
        TestAssert.AreEqual(HostDeathObjectKind.Craft, HostDeathKinds.FromVanillaObjectType(6, false));
        TestAssert.AreEqual(HostDeathObjectKind.Unknown, HostDeathKinds.FromVanillaObjectType(3, false),
            "prebuilds are not deaths.");
        TestAssert.AreEqual(HostDeathObjectKind.Unknown, HostDeathKinds.FromVanillaObjectType(5, false),
            "ruins are build-domain records.");
        TestAssert.AreEqual(HostDeathObjectKind.Unknown, HostDeathKinds.FromVanillaObjectType(99, true),
            "an unknown vanilla value cannot invent a classification.");

        // Side-effect policy per kind (the A19 card: every class has an explicit policy).
        TestAssert.IsTrue(HostDeathKinds.ProducesLoot(HostDeathObjectKind.GroundEnemy));
        TestAssert.IsTrue(HostDeathKinds.ProducesLoot(HostDeathObjectKind.SpaceEnemy));
        TestAssert.IsTrue(HostDeathKinds.ProducesLoot(HostDeathObjectKind.Vegetable));
        TestAssert.IsTrue(HostDeathKinds.ProducesLoot(HostDeathObjectKind.Player));
        TestAssert.IsFalse(HostDeathKinds.ProducesLoot(HostDeathObjectKind.Entity));
        TestAssert.IsFalse(HostDeathKinds.ProducesLoot(HostDeathObjectKind.Vein));

        TestAssert.IsTrue(HostDeathKinds.CountsKillStatistics(HostDeathObjectKind.GroundEnemy));
        TestAssert.IsTrue(HostDeathKinds.CountsKillStatistics(HostDeathObjectKind.SpaceEnemy));
        TestAssert.IsFalse(HostDeathKinds.CountsKillStatistics(HostDeathObjectKind.Craft));
        TestAssert.IsFalse(HostDeathKinds.CountsKillStatistics(HostDeathObjectKind.Player));

        TestAssert.IsTrue(HostDeathKinds.ReleasesTasks(HostDeathObjectKind.Entity));
        TestAssert.IsTrue(HostDeathKinds.ReleasesTasks(HostDeathObjectKind.Player));
        TestAssert.IsTrue(HostDeathKinds.ReleasesTasks(HostDeathObjectKind.Base));
        TestAssert.IsFalse(HostDeathKinds.ReleasesTasks(HostDeathObjectKind.GroundEnemy));
    }

    [TestMethod]
    public void PolicyRetiresTheLegacyChainOnlyInHostAuthority()
    {
        TestAssert.IsFalse(HostDeathPolicy.ShouldRefuseLegacyDamageFact(isHostAuthority: false));
        TestAssert.IsFalse(HostDeathPolicy.ShouldRefuseLegacyFullHpFact(isHostAuthority: false));
        TestAssert.IsFalse(HostDeathPolicy.ShouldRefuseLegacyKillReplay(isHostAuthority: false));
        TestAssert.IsFalse(HostDeathPolicy.MustSuppressLegacyDeathBroadcast(isHostAuthority: false));
        TestAssert.IsFalse(HostDeathPolicy.MustNotStageOneHpQuery(isHostAuthority: false));
        TestAssert.IsFalse(HostDeathPolicy.MustNotSelfHealOnClient(isHostAuthority: false),
            "legacy rooms keep every old path byte for byte.");

        TestAssert.IsTrue(HostDeathPolicy.ShouldRefuseLegacyDamageFact(isHostAuthority: true));
        TestAssert.IsTrue(HostDeathPolicy.ShouldRefuseLegacyFullHpFact(isHostAuthority: true));
        TestAssert.IsTrue(HostDeathPolicy.ShouldRefuseLegacyKillReplay(isHostAuthority: true));
        TestAssert.IsTrue(HostDeathPolicy.MustSuppressLegacyDeathBroadcast(isHostAuthority: true));
        TestAssert.IsTrue(HostDeathPolicy.MustNotStageOneHpQuery(isHostAuthority: true));
        TestAssert.IsTrue(HostDeathPolicy.MustNotSelfHealOnClient(isHostAuthority: true));
        StringAssert.Contains(HostDeathPolicy.RefusalReason("CombatStatDamagePacket"), "A19");

        // Display-only policy: only presentation fields may pass; everything else defaults to
        // protected, and a rule may never read a listed field.
        TestAssert.IsTrue(HostDeathPolicy.IsDisplayOnlyCombatField("localPos"));
        TestAssert.IsTrue(HostDeathPolicy.IsDisplayOnlyCombatField("size"));
        TestAssert.IsTrue(HostDeathPolicy.IsDisplayOnlyCombatField("lastImpact"));
        TestAssert.IsTrue(HostDeathPolicy.IsDisplayOnlyCombatField("modelId"));
        TestAssert.IsTrue(HostDeathPolicy.IsDisplayOnlyCombatField("colliderId"));
        TestAssert.IsFalse(HostDeathPolicy.IsDisplayOnlyCombatField("hp"));
        TestAssert.IsFalse(HostDeathPolicy.IsDisplayOnlyCombatField("hpMax"));
        TestAssert.IsFalse(HostDeathPolicy.IsDisplayOnlyCombatField("hpIncoming"));
        TestAssert.IsFalse(HostDeathPolicy.IsDisplayOnlyCombatField("hpRecover"));
        TestAssert.IsFalse(HostDeathPolicy.IsDisplayOnlyCombatField(null));
    }

    [TestMethod]
    public void SessionWiresDeathLedgerToTasksAndExecutor()
    {
        using var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);

        TestAssert.IsNotNull(session.HostDeaths, "a host world owns its death ledger.");
        TestAssert.IsNotNull(session.HostCombatExecutor.DeathLedger,
            "the combat executor reports lethal damage into it.");

        var connection = session.AssignConnectionEpoch(2);
        session.HostPlayers.RegisterOrUpdate("hero", 2, HostPlayerRole.Remote, connection);
        session.HostCombat.EnsureOwner("hero", 1000);
        var owner = Player("hero", 2);
        TestAssert.IsNotNull(session.HostConstructionLedger.EnsureBudget(owner, 4));
        var target = Entity(20, 1, planet: 101);
        TestAssert.AreEqual(ConstructionTaskError.None,
            session.HostConstructionLedger.TryAddTask(owner, target, ConstructionTaskKind.Repair, 10,
                new HostTransactionId(Epoch, 1), out var taskKey));

        var result = session.HostCombatExecutor.ApplyHostDamage("hero", 5000);
        TestAssert.IsTrue(result.Died, "lethal host damage.");
        TestAssert.AreEqual(1, session.HostCombatExecutor.DeathsRecorded);
        TestAssert.IsTrue(session.HostDeaths.IsPlayerDead("hero"));

        // The same death reported again (a duplicate hazard tick) moves nothing.
        session.HostCombatExecutor.ApplyHostDamage("hero", 5000);
        TestAssert.AreEqual(1, session.HostCombatExecutor.DeathsRecorded);
        TestAssert.AreEqual(1, session.HostDeaths.DeathCount);

        TestAssert.AreEqual(0, session.HostConstructionLedger.GetRepairerCount(target),
            "the owner's tasks released once with the death.");
        TestAssert.IsTrue(session.HostConstructionLedger.TryGetTask(taskKey, out var task) &&
            task.IsTerminal && task.CancelReason == ConstructionCancelReason.OwnerDead);

        session.Reset();
        TestAssert.IsNull(session.HostDeaths, "leaving the world clears the ledger.");
        TestAssert.IsNull(session.HostCombatExecutor?.DeathLedger);
    }
}
