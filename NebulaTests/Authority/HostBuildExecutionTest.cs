using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A18: host build execution over the task ledger with per-owner real energy and single-shot materials.
/// </summary>
/// <remarks>
/// TASKS.md A18 acceptance: the placer-first rule survives (the placer serves its own prebuild when
/// available, a stable fallback otherwise); one builder per prebuild (no double task from two
/// owners); an observer adds nothing; build and repair share one budget; materials are spent once
/// when the writer reports the site finished and a shortfall cancels with InsufficientStock; low
/// energy scales instead of faking full efficiency. Snapshots, distances, recipes and costs arrive
/// as arguments — the production adapter reads them from the real pools, tests inject canned
/// numbers. Depends on A09 (registry/ledger), A10 (energy/material truth), A15 (budgets/tasks)
/// and A16 (candidate gates); the repair executor (A17) is untouched.
/// </remarks>
[TestClass]
public class HostBuildExecutionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA18B18A18B18A18, 0xC18C18C18C18C1);

    private sealed class FakeWriter : IHostBuildWriter
    {
        public readonly List<(ObjectKey Target, float Ratio)> Calls = new();
        public Func<ObjectKey, bool> ShouldFinish;

        public bool TryBuild(in ObjectKey target, int nativePrebuildId, float ratio, long hostTick,
            out bool finished)
        {
            Calls.Add((target, ratio));
            finished = ShouldFinish != null && ShouldFinish(target);
            return true;
        }
    }

    private sealed class FakeBuildBases : IHostBuildBaseSource
    {
        public readonly Dictionary<ConstructionOwnerKey, BuildOwnerSnapshot> Snapshots = new();

        public bool TryGetBuildBaseSnapshot(ConstructionOwnerKey baseOwner, out BuildOwnerSnapshot snapshot) =>
            Snapshots.TryGetValue(baseOwner, out snapshot);
    }

    private static ConstructionOwnerKey Player(string persistent, ushort session) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

    private static ConstructionOwnerKey Base(int nativeId, long generation, int planet) =>
        ConstructionOwnerKey.ForBase(ObjectKey.Create(Epoch, PoolKind.Base, planet, nativeId, generation));

    private static ObjectKey Prebuild(int nativeId, long generation, int planet) =>
        ObjectKey.Create(Epoch, PoolKind.Prebuild, planet, nativeId, generation);

    private static (HostPlayerRegistry Registry, ConstructionTaskLedger Tasks,
        HostConstructionService Service, HostResourceLedger Resources, HostConstructionExecutor Executor)
        NewExecutor(IHostBuildBaseSource buildBases = null)
    {
        var registry = new HostPlayerRegistry();
        var tasks = new ConstructionTaskLedger(Epoch);
        var service = new HostConstructionService(Epoch, registry, tasks, null, buildBases);
        var resources = new HostResourceLedger(Epoch);
        return (registry, tasks, service, resources,
            new HostConstructionExecutor(Epoch, registry, tasks, service, resources));
    }

    private static ConnectionEpoch Conn(ulong value) => new(value);

    private static void Register(HostPlayerRegistry registry, string persistent, ushort session,
        int planet, float speed = 0f, bool build = true)
    {
        registry.RegisterOrUpdate(persistent, session,
            session == 1 ? HostPlayerRole.LocalHost : HostPlayerRole.Remote, Conn(100UL + session));
        TestAssert.IsTrue(registry.UpdatePresence(session, planet, 10,
            new HostPlayerPose(0f, 0f, 0f, speed), true, true, build, 4));
    }

    private static void SeedCore(HostResourceLedger resources, string persistent, double balance)
    {
        resources.SeedDouble(LedgerOwner.ForPlayer(persistent), LedgerResourceKind.CoreEnergy, balance);
    }

    private static BuildTargetSnapshot Site(ObjectKey target, int planet, int need = 0,
        bool destroyed = false, int builders = 0) =>
        new(target, planet, need, destroyed, builders);

    private static List<BuildOwnerSnapshot> Owners(HostConstructionService service,
        params ConstructionOwnerKey[] keys)
    {
        var list = new List<BuildOwnerSnapshot>();
        foreach (var key in keys)
        {
            TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(key, 300f, true, true, out var snap),
                "Build owner snapshot for " + key + " should resolve.");
            list.Add(snap);
        }
        return list;
    }

    private static HostBuildTickSummary TickBuildToWorking(
        HostConstructionExecutor executor, long tick,
        IReadOnlyList<BuildTargetSnapshot> targets, IReadOnlyList<BuildOwnerSnapshot> owners,
        FakeWriter writer, double cost = 5.0,
        BuildMaterialRequirement material = default)
    {
        HostBuildTickSummary summary = default;
        for (var i = 0; i < 4; i++)
            summary = executor.TickBuild(tick + i, targets, owners, (_, _) => 100f, _ => null,
                (_, _) => material, _ => cost, writer);
        return summary;
    }

    [TestMethod]
    public void PlacerFirstWinsWhenAvailable()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "placer", 2, 101);
        Register(registry, "other", 3, 101);
        var placer = Player("placer", 2);
        var other = Player("other", 3);
        tasks.EnsureBudget(placer, 2);
        tasks.EnsureBudget(other, 2);
        SeedCore(resources, "placer", 100.0);
        SeedCore(resources, "other", 100.0);

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        var owners = Owners(service, placer, other);
        var writer = new FakeWriter();
        var first = executor.TickBuild(100, targets, owners, (_, _) => 100f,
            _ => (ConstructionOwnerKey?)placer, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0,
            writer);
        TestAssert.AreEqual(1, first.Dispatched, "Only the placer dispatches for its prebuild.");
        TickBuildToWorking(executor, 101, targets, owners, writer);

        TestAssert.AreEqual(1, tasks.ActiveTaskCount);
        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(target));
    }

    [TestMethod]
    public void FallbackToStableOwnerWhenPlacerIsAway()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "placer", 2, 102);
        Register(registry, "a", 3, 101);
        Register(registry, "b", 4, 101);
        var placer = Player("placer", 2);
        var ownerA = Player("a", 3);
        var ownerB = Player("b", 4);
        tasks.EnsureBudget(placer, 2);
        tasks.EnsureBudget(ownerA, 2);
        tasks.EnsureBudget(ownerB, 2);
        SeedCore(resources, "a", 100.0);
        SeedCore(resources, "b", 100.0);

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        // The placer is off-planet, so it is not dispatchable; the fallback is the stable order.
        var owners = Owners(service, ownerB, ownerA);
        var writer = new FakeWriter();
        executor.TickBuild(100, targets, owners, (_, _) => 100f,
            _ => placer, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);

        TestAssert.AreEqual(1, tasks.ActiveTaskCount);
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && !state.IsTerminal)
                TestAssert.AreEqual(ownerA, state.Owner, "Same distance: stable owner order wins.");
        }
    }

    [TestMethod]
    public void OneBuilderPerPrebuild()
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

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        var owners = Owners(service, ownerA, ownerB);
        var writer = new FakeWriter();
        TickBuildToWorking(executor, 100, targets, owners, writer);

        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(target),
            "The second owner sees DemandCovered, not a second task.");
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);
    }

    [TestMethod]
    public void AnObserverDoesNotChangeTheBuild()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        Register(registry, "watcher", 4, 101);
        var owner = Player("a", 2);
        var watcher = Player("watcher", 4);
        tasks.EnsureBudget(owner, 2);
        tasks.EnsureBudget(watcher, 2);
        SeedCore(resources, "a", 100.0);
        SeedCore(resources, "watcher", 100.0);

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        var owners = Owners(service, owner);
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(watcher, 300f, true, true, out _));
        owners.Add(new BuildOwnerSnapshot(watcher, 102, 0f, 0f, 0f, 0f, true, true, true, 300f, true));
        var writer = new FakeWriter();
        TickBuildToWorking(executor, 100, targets, owners, writer);

        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(target));
        TestAssert.AreEqual(0, tasks.GetBuildTaskCount(Prebuild(99, 1, 101)),
            "The observer holds no builder slot anywhere.");
    }

    [TestMethod]
    public void BuildAndRepairShareOneBudget()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var prebuild = Prebuild(21, 1, 101);
        var owners = Owners(service, owner);
        executor.TickBuild(100, new List<BuildTargetSnapshot> { Site(prebuild, 101) }, owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0,
            new FakeWriter());
        TestAssert.AreEqual(1, tasks.ActiveTaskCount, "The single drone slot is building.");

        var entity = ObjectKey.Create(Epoch, PoolKind.Entity, 101, 11, 1);
        var damage = new RepairDamageSnapshot(entity, 101, 500, 1000, 300f, 0, true, true);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(owner, 300f, true, true, out var repairSnap));
        var reason = service.TryReserveRepair(repairSnap, entity, damage, 100f, 2f, 100, default,
            out _, out _);
        TestAssert.AreEqual(ConstructionDispatchReason.NoIdleDrone, reason,
            "One drone cannot build and repair together.");
    }

    [TestMethod]
    public void StaleGenerationsNeverBuildTheNewObject()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 2);
        SeedCore(resources, "a", 100.0);

        var oldTarget = Prebuild(21, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        executor.TickBuild(100, new List<BuildTargetSnapshot> { Site(oldTarget, 101) }, owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        var newTarget = Prebuild(21, 2, 101);
        var summary = executor.TickBuild(101, new List<BuildTargetSnapshot> { Site(newTarget, 101) },
            owners, (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0,
            writer);
        TestAssert.AreEqual(1, summary.Cancelled, "The old task cancels exactly once.");
        TestAssert.AreEqual(0, tasks.GetBuildTaskCount(oldTarget));
        TestAssert.AreEqual(1, tasks.ActiveTaskCount, "The new generation dispatches fresh.");
        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(newTarget));
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && state.Stage == ConstructionTaskStage.Cancelled)
                TestAssert.AreEqual(ConstructionCancelReason.TargetDemolished, state.CancelReason,
                    "The vanished record cancels the pre-work task as demolished.");
        }
    }

    [TestMethod]
    public void UnsuppliedSiteIsNotReady()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Prebuild(21, 1, 101);
        var owners = Owners(service, owner);
        var summary = executor.TickBuild(100,
            new List<BuildTargetSnapshot> { Site(target, 101, need: 2) }, owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0,
            new FakeWriter());
        TestAssert.AreEqual(0, summary.Dispatched);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(owner, 300f, true, true, out var snap));
        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            service.EvaluateBuild(snap, target, Site(target, 101, need: 2),
                ConstructionTaskKind.Build, 100f));
    }

    [TestMethod]
    public void RuinStateMustMatchTheTaskKind()
    {
        var (registry, tasks, service, _, _) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(owner, 300f, true, true, out var snap));
        var green = Prebuild(21, 1, 101);
        var ruin = Prebuild(22, 1, 101);
        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            service.EvaluateBuild(snap, green, Site(green, 101, destroyed: true),
                ConstructionTaskKind.Build, 100f));
        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            service.EvaluateBuild(snap, ruin, Site(ruin, 101, destroyed: false),
                ConstructionTaskKind.Reconstruct, 100f));
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            service.EvaluateBuild(snap, ruin, Site(ruin, 101, destroyed: true),
                ConstructionTaskKind.Reconstruct, 100f));
    }

    [TestMethod]
    public void ReconstructCompletesWhenAlreadyWhole()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var ruin = Prebuild(22, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        TickBuildToWorking(executor, 100,
            new List<BuildTargetSnapshot> { Site(ruin, 101, destroyed: true) }, owners, writer);
        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(ruin));

        var healed = new List<BuildTargetSnapshot> { Site(ruin, 101, destroyed: false) };
        var first = executor.TickBuild(200, healed, owners, (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, first.Completed, "Working moves to Returning.");
        var second = executor.TickBuild(201, healed, owners, (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, second.Completed, "Returning moves to Completed.");
        var liveReconstructs = 0;
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && !state.IsTerminal &&
                state.Kind == ConstructionTaskKind.Reconstruct)
                liveReconstructs++;
        }
        TestAssert.AreEqual(0, liveReconstructs, "No reconstruct task survives its whole site.");
        TestAssert.AreEqual(1, tasks.GetBuildTaskCount(ruin),
            "The now-green site correctly takes a fresh Build task.");
    }

    [TestMethod]
    public void MaterialsAreSpentOnceAtFinish()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);
        resources.SeedLong(LedgerOwner.ForPlayer("a"), LedgerResourceKind.InventoryItem, 42, 10);

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        var owners = Owners(service, owner);
        var writer = new FakeWriter { ShouldFinish = _ => true };
        TickBuildToWorking(executor, 100, targets, owners, writer,
            cost: 5.0, material: new BuildMaterialRequirement(42, 3));
        // The fourth tick reaches Working and finishes in the same tick: materials move once.
        TestAssert.IsTrue(resources.TryGetLong(LedgerOwner.ForPlayer("a"),
            LedgerResourceKind.InventoryItem, 42, out var balance, out _));
        TestAssert.AreEqual(10 - 3, balance, "One build spends its recipe exactly once.");
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && !state.IsTerminal)
                TestAssert.AreEqual(ConstructionTaskStage.Returning, state.Stage);
        }

        var second = executor.TickBuild(104, targets, owners, (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(42, 3), _ => 5.0, writer);
        TestAssert.AreEqual(1, second.Advanced, "Returning advances to Completed.");
        TestAssert.AreEqual(0, tasks.ActiveTaskCount, "The finished task leaves the ledger.");
        TestAssert.IsTrue(tasks.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(1, budget.Idle, "The slot returns exactly once.");
        TestAssert.IsTrue(resources.TryGetLong(LedgerOwner.ForPlayer("a"),
            LedgerResourceKind.InventoryItem, 42, out var after, out _));
        TestAssert.AreEqual(10 - 3, after, "Flying home spends nothing more.");
    }

    [TestMethod]
    public void InsufficientMaterialsCancelWithReason()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Prebuild(21, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(target, 101) };
        var owners = Owners(service, owner);
        var writer = new FakeWriter { ShouldFinish = _ => true };
        var summary = TickBuildToWorking(executor, 100, targets, owners, writer,
            cost: 5.0, material: new BuildMaterialRequirement(42, 3));
        TestAssert.AreEqual(1, summary.Cancelled, "No stock: the task cancels instead of minting.");
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        TestAssert.IsTrue(tasks.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(1, budget.Idle, "The slot returns exactly once.");
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && state.Stage == ConstructionTaskStage.Cancelled)
                TestAssert.AreEqual(ConstructionCancelReason.InsufficientStock, state.CancelReason);
        }
    }

    [TestMethod]
    public void LowEnergyScalesBuildRatioInsteadOfFakingFullEfficiency()
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

        // Two prebuilds so both owners dispatch without covering each other: each site
        // names its own placer.
        var first = Prebuild(21, 1, 101);
        var second = Prebuild(22, 1, 101);
        var targets = new List<BuildTargetSnapshot> { Site(first, 101), Site(second, 101) };
        var owners = Owners(service, full, low);
        var writer = new FakeWriter();
        ConstructionOwnerKey? PlacerFor(BuildTargetSnapshot site) =>
            site.Target.NativeId == 21 ? full : low;
        for (var i = 0; i < 4; i++)
            executor.TickBuild(100 + i, targets, owners, (_, _) => 100f, PlacerFor,
                (_, _) => new BuildMaterialRequirement(0, 0), _ => 10.0, writer);

        TestAssert.AreEqual(2, writer.Calls.Count);
        var ratios = new List<float> { writer.Calls[0].Ratio, writer.Calls[1].Ratio };
        ratios.Sort();
        TestAssert.AreEqual(0.2f, ratios[0], 1e-6);
        TestAssert.AreEqual(1f, ratios[1], 1e-6);
    }

    [TestMethod]
    public void NoEnergyPausesBuildWithoutCancelling()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "dry", 2, 101);
        var owner = Player("dry", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "dry", 0.0);

        var target = Prebuild(21, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        TickBuildToWorking(executor, 100,
            new List<BuildTargetSnapshot> { Site(target, 101) }, owners, writer, cost: 10.0);

        TestAssert.AreEqual(1, writer.Calls.Count);
        TestAssert.AreEqual(0f, writer.Calls[0].Ratio, 1e-6);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount, "A dry drone stays Working, it never cancels.");
    }

    [TestMethod]
    public void TargetDemolishedCancelsOnceAndReleases()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Prebuild(21, 1, 101);
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        executor.TickBuild(100, new List<BuildTargetSnapshot> { Site(target, 101) }, owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        var summary = executor.TickBuild(101, new List<BuildTargetSnapshot>(), owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, summary.Cancelled);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        TestAssert.AreEqual(0, tasks.GetBuildTaskCount(target));
        TestAssert.IsTrue(tasks.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(1, budget.Idle, "The slot returns exactly once.");

        var again = executor.TickBuild(102, new List<BuildTargetSnapshot>(), owners,
            (_, _) => 100f, _ => null, (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(0, again.Cancelled, "A second sweep mints nothing.");
    }

    [TestMethod]
    public void OwnerLeavingThePlanetCancelsBuildWithReason()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Prebuild(21, 1, 101);
        var writer = new FakeWriter();
        executor.TickBuild(100, new List<BuildTargetSnapshot> { Site(target, 101) },
            Owners(service, owner), (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);

        Register(registry, "a", 2, 102);
        var summary = executor.TickBuild(101, new List<BuildTargetSnapshot> { Site(target, 101) },
            Owners(service, owner), (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, writer);
        TestAssert.AreEqual(1, summary.Cancelled);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount);
        foreach (var key in AllKeys(tasks))
        {
            if (tasks.TryGetTask(key, out var state) && state.Stage == ConstructionTaskStage.Cancelled)
                TestAssert.AreEqual(ConstructionCancelReason.OwnerLeftPlanet, state.CancelReason);
        }
    }

    [TestMethod]
    public void NoEnergyBlocksNewDispatchButNeverCancelsLiveWork()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "dry", 2, 101);
        var owner = Player("dry", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "dry", 0.0);

        var target = Prebuild(21, 1, 101);
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(owner, 300f, true, false, out var snap));
        TestAssert.AreEqual(ConstructionDispatchReason.NoEnergy,
            service.EvaluateBuild(snap, target, Site(target, 101),
                ConstructionTaskKind.Build, 100f));

        // ...but a live task with the same dry owner pauses with ratio zero instead of cancelling.
        var owners = Owners(service, owner);
        var writer = new FakeWriter();
        TickBuildToWorking(executor, 100,
            new List<BuildTargetSnapshot> { Site(target, 101) }, owners, writer, cost: 10.0);
        TestAssert.AreEqual(1, tasks.ActiveTaskCount);
    }

    [TestMethod]
    public void BuildTargetFateHonorsGeneration()
    {
        var oldTarget = Prebuild(21, 1, 101);
        var newTarget = Prebuild(21, 2, 101);
        var owner = Player("a", 2);
        var ownerSnap = new BuildOwnerSnapshot(owner, 101, 0f, 0f, 0f, 0f, true, true, true, 300f, true);

        // Same generation keeps working.
        var same = HostBuildLifecycle.TargetFate(oldTarget, ConstructionTaskKind.Build,
            ConstructionTaskStage.Working, true, Site(oldTarget, 101));
        TestAssert.AreEqual(HostBuildTaskFate.Keep, same.Fate);

        // Another generation of the same slot never slides onto the live task.
        var recycled = HostBuildLifecycle.TargetFate(oldTarget, ConstructionTaskKind.Build,
            ConstructionTaskStage.Working, true, Site(newTarget, 101));
        TestAssert.AreEqual(HostBuildTaskFate.Cancel, recycled.Fate);
        TestAssert.AreEqual(ConstructionCancelReason.TargetRecycled, recycled.Cancel);

        // A missing record demolishes pre-work but completes a drone already flying home.
        var goneEarly = HostBuildLifecycle.TargetFate(oldTarget, ConstructionTaskKind.Build,
            ConstructionTaskStage.Working, false, default);
        TestAssert.AreEqual(HostBuildTaskFate.Cancel, goneEarly.Fate);
        TestAssert.AreEqual(ConstructionCancelReason.TargetDemolished, goneEarly.Cancel);
        var goneHome = HostBuildLifecycle.TargetFate(oldTarget, ConstructionTaskKind.Build,
            ConstructionTaskStage.Returning, false, default);
        TestAssert.AreEqual(HostBuildTaskFate.CompleteBuild, goneHome.Fate);

        // Owner lifecycle never reads a host planet: only the task owner's planet matters.
        TestAssert.IsNull(HostBuildLifecycle.OwnerCancelReason(ownerSnap, 101));
        TestAssert.AreEqual(ConstructionCancelReason.OwnerLeftPlanet,
            HostBuildLifecycle.OwnerCancelReason(ownerSnap, 102));
    }

    [TestMethod]
    public void SpeedGateAppliesToMechaButNeverToBases()
    {
        var bases = new FakeBuildBases();
        var (registry, tasks, service, _, _) = NewExecutor(bases);
        Register(registry, "fast", 2, 101, speed: 21f);
        var fast = Player("fast", 2);
        var baseOwner = Base(5, 1, 101);
        tasks.EnsureBudget(fast, 1);
        tasks.EnsureBudget(baseOwner, 1);
        bases.Snapshots[baseOwner] = new BuildOwnerSnapshot(baseOwner, 101, 0f, 0f, 0f, 99f,
            true, true, true, 300f, true);
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(fast, 300f, true, true, out var fastSnap));
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshotAny(baseOwner, 300f, true, true, out var baseSnap));

        var target = Prebuild(21, 1, 101);
        var site = Site(target, 101);
        TestAssert.AreEqual(ConstructionDispatchReason.SpeedTooHigh,
            service.EvaluateBuild(fastSnap, target, site, ConstructionTaskKind.Build, 100f));
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            service.EvaluateBuild(baseSnap, target, site, ConstructionTaskKind.Build, 100f));
    }

    [TestMethod]
    public void BuildSwitchOffIsDisabledNotNoEnergy()
    {
        var (registry, tasks, service, _, _) = NewExecutor();
        Register(registry, "a", 2, 101, build: false);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        // The snapshot still resolves (presence is known); the closed build switch reports
        // Disabled even when the owner is also dry.
        TestAssert.IsTrue(service.TryGetBuildOwnerSnapshot(owner, 300f, true, false, out var snap));
        TestAssert.IsFalse(snap.ConstructEnabled);
        var target = Prebuild(21, 1, 101);
        TestAssert.AreEqual(ConstructionDispatchReason.Disabled,
            service.EvaluateBuild(snap, target, Site(target, 101),
                ConstructionTaskKind.Build, 100f));
    }

    [TestMethod]
    public void BuildPolicyRetiresOnlyInAuthorityMode()
    {
        TestAssert.IsTrue(HostConstructionPolicy.MustUseHostTaskForBuildDispatch(true));
        TestAssert.IsFalse(HostConstructionPolicy.MustUseHostTaskForBuildDispatch(false));
        TestAssert.IsTrue(HostConstructionPolicy.ShouldRefuseLegacyBuildLaunch(true));
        TestAssert.IsFalse(HostConstructionPolicy.ShouldRefuseLegacyBuildLaunch(false));
        TestAssert.IsTrue(HostConstructionPolicy.ShouldRefuseClientMaterialClaim(true));
        TestAssert.IsFalse(HostConstructionPolicy.ShouldRefuseClientMaterialClaim(false));
        TestAssert.IsNotEmpty(HostConstructionPolicy.BuildSuppressionReason("BuildDroneLaunch"));
    }

    [TestMethod]
    public void SessionBuildTickIsFailClosed()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(session.HostConstructionExecutor);
        session.OnFrameBoundary(100);
        TestAssert.AreEqual(100L, session.LastFrameTick);
        TestAssert.AreEqual(0, session.HostConstructionLedger.ActiveTaskCount,
            "With no prebuild view the frame tick dispatches nothing.");
        session.Reset();
        TestAssert.IsNull(session.HostConstructionExecutor);
        session.Dispose();

        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(client.HostConstructionExecutor);
        client.Dispose();
    }

    [TestMethod]
    public void BuildDisplayCarriesIdentityOnly()
    {
        var (registry, tasks, service, resources, executor) = NewExecutor();
        Register(registry, "a", 2, 101);
        var owner = Player("a", 2);
        tasks.EnsureBudget(owner, 1);
        SeedCore(resources, "a", 100.0);

        var target = Prebuild(21, 1, 101);
        executor.TickBuild(100, new List<BuildTargetSnapshot> { Site(target, 101) },
            Owners(service, owner), (_, _) => 100f, _ => null,
            (_, _) => new BuildMaterialRequirement(0, 0), _ => 5.0, new FakeWriter());
        var states = executor.CollectDisplayStates();
        TestAssert.AreEqual(1, states.Count);
        TestAssert.AreEqual(target, states[0].Target);
        TestAssert.AreEqual(owner, states[0].Owner);
        var names = new HashSet<string>();
        foreach (var property in typeof(HostDroneDisplayState).GetProperties())
            names.Add(property.Name);
        TestAssert.IsFalse(names.Contains("Hp"));
        TestAssert.IsFalse(names.Contains("Energy"));
        TestAssert.IsFalse(names.Contains("Material"));
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
