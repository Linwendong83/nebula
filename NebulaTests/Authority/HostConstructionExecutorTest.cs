using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A17: host repair execution over the task ledger with per-owner real energy.
/// </summary>
/// <remarks>
/// TASKS.md A17 acceptance: two repairers heal the sum of their legal ratios, an observer adds
/// nothing, low energy scales, and target/owner loss in every stage releases exactly once. The
/// executor takes damages, owners, distances, demands and costs as arguments — the production
/// adapter reads them from the real pools (vanilla GetRepairValue/Demand), tests inject canned
/// numbers — and spends energy through the host ledger, so a game balance update cannot diverge
/// here. Depends on A09 (registry/ledger), A10 (energy truth), A15 (budgets/tasks) and A16
/// (candidate gates).
/// </remarks>
[TestClass]
public class HostConstructionExecutorTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA17E17E17E17E17E, 0xC17C17C17C17C17C);

    private sealed class FakeWriter : IHostRepairWriter
    {
        public readonly List<(ObjectKey Target, float Ratio)> Calls = new();
        public Func<ObjectKey, bool> ShouldFinish;

        public bool TryRepair(in ObjectKey target, int nativeEntityId, float ratio, long hostTick,
            out bool finished)
        {
            Calls.Add((target, ratio));
            finished = ShouldFinish != null && ShouldFinish(target);
            return true;
        }
    }

    private static ConstructionOwnerKey Player(string persistent, ushort session) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

    private static ObjectKey Entity(int nativeId, long generation, int planet) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, planet, nativeId, generation);

    private static (HostPlayerRegistry Registry, ConstructionTaskLedger Tasks,
        HostConstructionService Service, HostResourceLedger Resources, HostConstructionExecutor Executor)
        NewExecutor()
    {
        var registry = new HostPlayerRegistry();
        var tasks = new ConstructionTaskLedger(Epoch);
        var service = new HostConstructionService(Epoch, registry, tasks);
        var resources = new HostResourceLedger(Epoch);
        return (registry, tasks, service, resources,
            new HostConstructionExecutor(Epoch, registry, tasks, service, resources));
    }

    private static ConnectionEpoch Conn(ulong value) => new(value);

    private static void Register(HostPlayerRegistry registry, string persistent, ushort session,
        int planet, bool repair = true)
    {
        registry.RegisterOrUpdate(persistent, session,
            session == 1 ? HostPlayerRole.LocalHost : HostPlayerRole.Remote, Conn(100UL + session));
        TestAssert.IsTrue(registry.UpdatePresence(session, planet, 10,
            new HostPlayerPose(0f, 0f, 0f, 0f), true, repair, true, 4));
    }

    private static void SeedCore(HostResourceLedger resources, string persistent, double balance)
    {
        resources.SeedDouble(LedgerOwner.ForPlayer(persistent), LedgerResourceKind.CoreEnergy, balance);
    }

    private static RepairDamageSnapshot Damage(ObjectKey target, int planet, int hp = 500,
        int hpMax = 1000) =>
        new(target, planet, hp, hpMax, 300f, 0, true, true);

    private static List<RepairOwnerSnapshot> Owners(HostConstructionService service,
        params ConstructionOwnerKey[] keys)
    {
        var list = new List<RepairOwnerSnapshot>();
        foreach (var key in keys)
        {
            TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(key, 300f, true, true, out var snap),
                "Owner snapshot for " + key + " should resolve.");
            list.Add(snap);
        }
        return list;
    }

    private static HostRepairTickSummary TickToWorking(
        HostConstructionExecutor executor, long tick,
        IReadOnlyList<RepairDamageSnapshot> damages, IReadOnlyList<RepairOwnerSnapshot> owners,
        FakeWriter writer, double cost = 5.0)
    {
        HostRepairTickSummary summary = default;
        for (var i = 0; i < 4; i++)
            summary = executor.Tick(tick + i, damages, owners, (_, _) => 100f, (_, _) => 2f,
                _ => 1f, _ => cost, 10f, 1f, writer);
        return summary;
    }

    [TestMethod]
    public void TwoRepairersHealTheSumOfTheirLegalRatios()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        Register(registry, "b", 3, 101);
        var ownerA = Player("a", 2);
        var ownerB = Player("b", 3);
        tasks.EnsureBudget(ownerA, 2);
        tasks.EnsureBudget(ownerB, 2);
        SeedCore(resources, "a", 100.0);
        SeedCore(resources, "b", 100.0);

        var target = Entity(11, 1, 101);
        var damages = new List<RepairDamageSnapshot> { Damage(target, 101) };
        var owners = Owners(service, ownerA, ownerB);
        var writer = new FakeWriter();
        TickToWorking(executor, 100, damages, owners, writer);

        TestAssert.AreEqual(2, writer.Calls.Count, "Both working drones repair this tick.");
        TestAssert.AreEqual(1f, writer.Calls[0].Ratio, 1e-6);
        TestAssert.AreEqual(1f, writer.Calls[1].Ratio, 1e-6);
        TestAssert.AreEqual(2, tasks.GetRepairerCount(target));
        TestAssert.AreEqual(2, HostRepairerCountPolicy.Reconcile(tasks.GetRepairerCount(target), 999));
    }

    [TestMethod]
    public void AnObserverDoesNotChangeTheSpeed()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        Register(registry, "b", 3, 101);
        Register(registry, "watcher", 4, 101);
        var ownerA = Player("a", 2);
        var ownerB = Player("b", 3);
        var watcher = Player("watcher", 4);
        tasks.EnsureBudget(ownerA, 2);
        tasks.EnsureBudget(ownerB, 2);
        tasks.EnsureBudget(watcher, 2);
        SeedCore(resources, "a", 100.0);
        SeedCore(resources, "b", 100.0);
        SeedCore(resources, "watcher", 100.0);

        var target = Entity(11, 1, 101);
        var damages = new List<RepairDamageSnapshot> { Damage(target, 101) };
        // The watcher is out of range: present in the owners list, never dispatched.
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(watcher, 300f, true, true, out var watchSnap));
        var owners = Owners(service, ownerA, ownerB);
        owners.Add(new RepairOwnerSnapshot(watcher, 102, 0f, 0f, 0f, 0f, true, true, true, 300f, true));
        var writer = new FakeWriter();
        TickToWorking(executor, 100, damages, owners, writer);

        TestAssert.AreEqual(2, writer.Calls.Count);
        TestAssert.AreEqual(2, tasks.GetRepairerCount(target));
        TestAssert.AreEqual(0, tasks.GetRepairerCount(Entity(99, 1, 101)),
            "The observer holds no repairer slot anywhere.");
    }

    [TestMethod]
    public void LowEnergyScalesInsteadOfFakingFullEfficiency()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "full", 2, 101);
        Register(registry, "low", 3, 101);
        var full = Player("full", 2);
        var low = Player("low", 3);
        tasks.EnsureBudget(full, 2);
        tasks.EnsureBudget(low, 2);
        SeedCore(resources, "full", 100.0);
        SeedCore(resources, "low", 2.0);

        var target = Entity(11, 1, 101);
        var damages = new List<RepairDamageSnapshot> { Damage(target, 101) };
        var owners = Owners(service, full, low);
        var writer = new FakeWriter();
        var summary = TickToWorking(executor, 100, damages, owners, writer, cost: 10.0);

        TestAssert.AreEqual(2, writer.Calls.Count);
        var ratios = new List<float> { writer.Calls[0].Ratio, writer.Calls[1].Ratio };
        ratios.Sort();
        TestAssert.AreEqual(0.2f, ratios[0], 1e-6);
        TestAssert.AreEqual(1f, ratios[1], 1e-6);
        TestAssert.AreEqual(12, summary.TotalDelta, "10 at full plus 2 at one fifth.");
    }

    [TestMethod]
    public void NoEnergyPausesWithoutCancelling()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "dry", 2, 101);
        var owner = Player("dry", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "dry", 0.0);

        var target = Entity(11, 1, 101);
        var damages = new List<RepairDamageSnapshot> { Damage(target, 101) };
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        TickToWorking(executor, 100, damages, owners, writer, cost: 10.0);

        TestAssert.AreEqual(1, writer.Calls.Count);
        TestAssert.AreEqual(0f, writer.Calls[0].Ratio, 1e-6);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount, "A dry drone stays Working, it never cancels.");
        TestAssert.AreEqual(1, tasks.GetRepairerCount(target));
    }

    [TestMethod]
    public void TargetLossCancelsOnceAndReleases()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Entity(11, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        executor.Tick(100, new List<RepairDamageSnapshot> { Damage(target, 101) }, owners,
            (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        var summary = executor.Tick(101, new List<RepairDamageSnapshot>(), owners,
            (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, summary.Cancelled);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        TestAssert.AreEqual(0, tasks.GetRepairerCount(target));
        TestAssert.IsTrue(tasks.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(1, budget.Idle, "The slot returns exactly once.");

        var again = executor.Tick(102, new List<RepairDamageSnapshot>(), owners,
            (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(0, again.Cancelled, "A second sweep mints nothing.");
    }

    [TestMethod]
    public void OwnerLeavingThePlanetCancelsWithReason()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Entity(11, 1, 101);
        var writer = new FakeWriter();
        executor.Tick(100, new List<RepairDamageSnapshot> { Damage(target, 101) },
            Owners(service, owner), (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        Register(registry, "a", 2, 102);
        var summary = executor.Tick(101, new List<RepairDamageSnapshot> { Damage(target, 101) },
            Owners(service, owner), (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, summary.Cancelled);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && state.Stage == ConstructionTaskStage.Cancelled)
                TestAssert.AreEqual(ConstructionCancelReason.OwnerLeftPlanet, state.CancelReason);
        }
    }

    [TestMethod]
    public void FullHpCompletesThroughReturning()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Entity(11, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        TickToWorking(executor, 100, new List<RepairDamageSnapshot> { Damage(target, 101) },
            owners, writer);
        TestAssert.AreEqual(1, tasks.GetRepairerCount(target));

        var healed = new List<RepairDamageSnapshot> { Damage(target, 101, hp: 1000, hpMax: 1000) };
        var first = executor.Tick(200, healed, owners, (_, _) => 100f, (_, _) => 2f,
            _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, first.Completed, "Working moves to Returning.");
        TestAssert.AreEqual(0, tasks.GetRepairerCount(target),
            "A drone flying home no longer repairs.");
        var second = executor.Tick(201, healed, owners, (_, _) => 100f, (_, _) => 2f,
            _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, second.Completed, "Returning moves to Completed.");
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
    }

    [TestMethod]
    public void StaleGenerationsNeverRepairTheNewObject()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 2);
        SeedCore(resources, "a", 100.0);

        var oldTarget = Entity(11, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        executor.Tick(100, new List<RepairDamageSnapshot> { Damage(oldTarget, 101) }, owners,
            (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        var newTarget = Entity(11, 2, 101);
        var summary = executor.Tick(101, new List<RepairDamageSnapshot> { Damage(newTarget, 101) },
            owners, (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, writer);
        TestAssert.AreEqual(1, summary.Cancelled, "The old task cancels as recycled.");
        TestAssert.AreEqual(0, tasks.GetRepairerCount(oldTarget));
        TestAssert.AreEqual(1, tasks.ActiveTaskCount, "The new generation dispatches fresh.");
        TestAssert.AreEqual(1, tasks.GetRepairerCount(newTarget));
    }

    [TestMethod]
    public void ConstructionPolicySuppressesOnlyInAuthorityMode()
    {
        TestAssert.IsTrue(HostConstructionPolicy.ShouldSuppressVanillaDroneLoop(true));
        TestAssert.IsFalse(HostConstructionPolicy.ShouldSuppressVanillaDroneLoop(false));
        TestAssert.IsTrue(HostConstructionPolicy.ShouldSuppressDetermineLaunch(true));
        TestAssert.IsFalse(HostConstructionPolicy.ShouldSuppressDetermineLaunch(false));
        TestAssert.IsTrue(HostConstructionPolicy.ShouldSuppressIdleReset(true));
        TestAssert.IsFalse(HostConstructionPolicy.ShouldSuppressIdleReset(false));
        TestAssert.IsTrue(HostConstructionPolicy.MustUseOwnerPlanetForRecycle(true));
        TestAssert.IsFalse(HostConstructionPolicy.MustUseOwnerPlanetForRecycle(false));
        TestAssert.IsTrue(HostConstructionPolicy.MustPresentDronesFromTasks(true));
        TestAssert.IsFalse(HostConstructionPolicy.MustPresentDronesFromTasks(false));
        TestAssert.IsNotEmpty(HostConstructionPolicy.SuppressionReason("UpdateDrones"));
    }

    [TestMethod]
    public void SessionHoldsTheExecutorFailClosed()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostConstructionExecutor);
        TestAssert.IsNotNull(session.HostConstruction);
        TestAssert.IsNotNull(session.HostConstructionLedger);
        session.OnFrameBoundary(100);
        TestAssert.AreEqual(100L, session.LastFrameTick);
        TestAssert.AreEqual(0, session.HostConstructionLedger.ActiveTaskCount,
            "With no damage view the frame tick dispatches nothing.");
        session.Reset();
        TestAssert.IsNull(session.HostConstructionExecutor);
        session.Dispose();

        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(client.HostConstructionExecutor);
        client.Dispose();
    }

    [TestMethod]
    public void DisplayStatesMirrorLiveTasksWithoutProtectedNumbers()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Entity(11, 1, 101);
        var owners = Owners(service, owner);
        executor.Tick(100, new List<RepairDamageSnapshot> { Damage(target, 101) }, owners,
            (_, _) => 100f, (_, _) => 2f, _ => 1f, _ => 5.0, 10f, 1f, new FakeWriter());
        var states = executor.CollectDisplayStates();
        TestAssert.AreEqual(1, states.Count);
        TestAssert.AreEqual(target, states[0].Target);
        TestAssert.AreEqual(owner, states[0].Owner);
        var names = new HashSet<string>();
        foreach (var property in typeof(HostDroneDisplayState).GetProperties())
            names.Add(property.Name);
        TestAssert.IsFalse(names.Contains("Hp"));
        TestAssert.IsFalse(names.Contains("Energy"));
        TestAssert.IsFalse(names.Contains("RepairerCount"));
        TestAssert.IsFalse(names.Contains("Demand"));
    }

    private static IEnumerable<ConstructionTaskKey> AllKeys(ConstructionTaskLedger ledger)
    {
        for (long sequence = 1; sequence < ledger.NextSequence; sequence++)
        {
            var key = new ConstructionTaskKey(ledger.Epoch, sequence);
            if (ledger.TryGetTask(key, out _)) yield return key;
        }
    }
}
