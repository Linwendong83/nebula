using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A16: multiplayer repair candidates and explainable dispatch, pure model.
/// </summary>
/// <remarks>
/// TASKS.md A16 acceptance: a remote client alone on the damage planet is still a candidate
/// (the service never reads the host's local planet); enough repairers already serving blocks
/// over-dispatch; switch-off, over-speed and no-energy each report their own reason. Depends on
/// A06 (generation-scoped targets), A09 (registry presence) and A15 (ledger budgets and the
/// repair ordering inputs). The vanilla GetRepairValue/GetRepairDroneDemand numbers arrive as
/// arguments — this file never copies the formula — so the tests pin the gates, not the balance.
/// </remarks>
[TestClass]
public class HostConstructionDispatchTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA16A16A16A16A16A, 0xB16B16B16B16B16B);
    private static readonly AuthorityEpoch OtherEpoch = new(0xDEADBEEFDEADBEEF, 0x0011223344556677);

    private static ConnectionEpoch Conn(ulong value) => new(value);

    private static ConstructionOwnerKey Player(string persistent = "p1", ushort session = 2) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

    private static ConstructionOwnerKey Base(int nativeId = 5, long generation = 1, int planet = 101) =>
        ConstructionOwnerKey.ForBase(ObjectKey.Create(Epoch, PoolKind.Base, planet, nativeId, generation));

    private static ObjectKey Entity(int nativeId = 11, long generation = 1, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, planet, nativeId, generation);

    private static RepairOwnerSnapshot OwnerSnap(ConstructionOwnerKey owner, int planet = 101,
        float speed = 0f, bool available = true, bool repair = true, bool drone = true,
        float range = 300f, bool energy = true) =>
        new(owner, planet, 0f, 0f, 0f, speed, available, repair, drone, range, energy);

    private static RepairDamageSnapshot Damage(ObjectKey target, int planet = 101,
        int hp = 500, int hpMax = 1000, float rate = 300f, int repairers = 0,
        bool combat = true, bool construct = true) =>
        new(target, planet, hp, hpMax, rate, repairers, combat, construct);

    private static (HostPlayerRegistry Registry, ConstructionTaskLedger Ledger, HostConstructionService Service)
        NewService(IHostConstructionBaseSource? bases = null)
    {
        var registry = new HostPlayerRegistry();
        var ledger = new ConstructionTaskLedger(Epoch);
        return (registry, ledger, new HostConstructionService(Epoch, registry, ledger, bases));
    }

    private static void RegisterPlayer(HostPlayerRegistry registry, string persistent, ushort session,
        int planet, float speed = 0f, bool repair = true)
    {
        registry.RegisterOrUpdate(persistent, session,
            session == 1 ? HostPlayerRole.LocalHost : HostPlayerRole.Remote, Conn(100UL + session));
        TestAssert.IsTrue(registry.UpdatePresence(session, planet, 10,
            new HostPlayerPose(0f, 0f, 0f, speed), true, repair, true, 4));
    }

    private sealed class FakeBases : IHostConstructionBaseSource
    {
        public readonly Dictionary<ConstructionOwnerKey, RepairOwnerSnapshot> Snapshots = new();

        public bool TryGetBaseSnapshot(ConstructionOwnerKey baseOwner, out RepairOwnerSnapshot snapshot) =>
            Snapshots.TryGetValue(baseOwner, out snapshot);
    }

    [TestMethod]
    public void ARemoteAloneOnTheDamagePlanetIsStillACandidate()
    {
        // Host sits on planet 103; the damage and the only candidate sit on 101. The dispatch
        // types carry no host-planet field at all, so the remote cannot be skipped for it.
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player("remote", 2), planet: 101);
        var damage = Damage(target, planet: 101);
        var reason = HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage,
            distanceSquared: 100f, demand: 2f, idleDrones: 2);
        TestAssert.AreEqual(ConstructionDispatchReason.None, reason);
        TestAssert.IsTrue(HostConstructionDispatch.IsDispatchable(reason));
    }

    [TestMethod]
    public void ServicePicksTheOwnerOnTheDamagePlanetNotTheHostPlanet()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "host", 1, planet: 103);
        RegisterPlayer(registry, "remote", 2, planet: 101);
        var hostOwner = Player("host", 1);
        var remoteOwner = Player("remote", 2);
        ledger.EnsureBudget(hostOwner, 4);
        ledger.EnsureBudget(remoteOwner, 4);

        var target = Entity(11, 1, 101);
        var damage = Damage(target, planet: 101);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(hostOwner, 300f, true, true, out var hostSnap));
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(remoteOwner, 300f, true, true, out var remoteSnap));
        TestAssert.AreEqual(ConstructionDispatchReason.OutOfRange,
            service.EvaluateRepair(in hostSnap, in target, in damage, 100f, 2f));
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            service.EvaluateRepair(in remoteSnap, in target, in damage, 100f, 2f));
    }

    [TestMethod]
    public void RepairSwitchOffIsDisabledNotNoEnergy()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101, repair: false, energy: false);
        var damage = Damage(target);
        TestAssert.AreEqual(ConstructionDispatchReason.Disabled,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage, 100f, 2f, 2));
    }

    [TestMethod]
    public void MasterDroneSwitchOffIsDisabled()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101, drone: false);
        var damage = Damage(target);
        TestAssert.AreEqual(ConstructionDispatchReason.Disabled,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage, 100f, 2f, 2));
    }

    [TestMethod]
    public void OfflineOwnerIsDisabled()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101, available: false);
        var damage = Damage(target);
        TestAssert.AreEqual(ConstructionDispatchReason.Disabled,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage, 100f, 2f, 2));
    }

    [TestMethod]
    public void StaleSessionSeatIsDisabledThroughTheService()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "p1", 2, planet: 101);
        ledger.EnsureBudget(Player("p1", 2), 2);
        // The registry rebound seat 2 to another player (L09): the old key must not dispatch.
        registry.RegisterOrUpdate("p9", 2, HostPlayerRole.Remote, Conn(999));
        TestAssert.IsFalse(service.TryGetPlayerOwnerSnapshot(Player("p1", 2), 300f, true, true, out _));
    }

    [TestMethod]
    public void SpeedAbove20BlocksMechaButNeverABase()
    {
        var target = Entity(11, 1, 101);
        var damage = Damage(target);
        var fastMecha = OwnerSnap(Player(), planet: 101, speed: 21f);
        TestAssert.AreEqual(ConstructionDispatchReason.SpeedTooHigh,
            HostConstructionDispatch.EvaluateRepair(in fastMecha, in target, in damage, 100f, 2f, 2));

        var atLimit = OwnerSnap(Player(), planet: 101, speed: 20f);
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            HostConstructionDispatch.EvaluateRepair(in atLimit, in target, in damage, 100f, 2f, 2));

        var fastBase = OwnerSnap(Base(), planet: 101, speed: 99f);
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            HostConstructionDispatch.EvaluateRepair(in fastBase, in target, in damage, 100f, 2f, 2));
    }

    [TestMethod]
    public void WrongPlanetAndTooFarAreBothOutOfRange()
    {
        var target = Entity(11, 1, 101);
        var damage = Damage(target, planet: 101);
        var elsewhere = OwnerSnap(Player(), planet: 102);
        TestAssert.AreEqual(ConstructionDispatchReason.OutOfRange,
            HostConstructionDispatch.EvaluateRepair(in elsewhere, in target, in damage, 0f, 2f, 2));

        var tooFar = OwnerSnap(Player(), planet: 101, range: 300f);
        TestAssert.AreEqual(ConstructionDispatchReason.OutOfRange,
            HostConstructionDispatch.EvaluateRepair(in tooFar, in target, in damage, 300f * 300f + 1f, 2f, 2));

        var edge = OwnerSnap(Player(), planet: 101, range: 300f);
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            HostConstructionDispatch.EvaluateRepair(in edge, in target, in damage, 300f * 300f, 2f, 2));
    }

    [TestMethod]
    public void NoEnergyIsDistinctFromDisabledAndOutOfRange()
    {
        var target = Entity(11, 1, 101);
        var damage = Damage(target);
        var dry = OwnerSnap(Player(), planet: 101, energy: false);
        TestAssert.AreEqual(ConstructionDispatchReason.NoEnergy,
            HostConstructionDispatch.EvaluateRepair(in dry, in target, in damage, 100f, 2f, 2));
    }

    [TestMethod]
    public void CoveredDemandBlocksOverDispatchButFirstDroneStillGoes()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101);
        var covered = Damage(target, repairers: 1);
        TestAssert.AreEqual(ConstructionDispatchReason.DemandCovered,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in covered, 100f, 0f, 4));
        TestAssert.AreEqual(ConstructionDispatchReason.DemandCovered,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in covered, 100f, -5f, 4));

        var first = Damage(target, repairers: 0);
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in first, 100f, 0f, 4),
            "DetermineLaunch still sends one drone when nobody serves yet.");
    }

    [TestMethod]
    public void FullHpIsCoveredAndMissingStatIsNoRecord()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101);
        var full = Damage(target, hp: 1000, hpMax: 1000, repairers: 0);
        TestAssert.AreEqual(ConstructionDispatchReason.DemandCovered,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in full, 100f, 2f, 4));

        var noCombat = Damage(target, combat: false);
        TestAssert.AreEqual(ConstructionDispatchReason.NoDamageRecord,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in noCombat, 100f, 2f, 4));

        var noConstruct = Damage(target, construct: false);
        TestAssert.AreEqual(ConstructionDispatchReason.NoDamageRecord,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in noConstruct, 100f, 2f, 4));
    }

    [TestMethod]
    public void StaleGenerationNeverSlidesOntoTheNewObject()
    {
        var oldTarget = Entity(11, 1, 101);
        var newTarget = Entity(11, 2, 101);
        var owner = OwnerSnap(Player(), planet: 101);
        var newRecord = Damage(newTarget);
        TestAssert.AreEqual(ConstructionDispatchReason.WrongGeneration,
            HostConstructionDispatch.EvaluateRepair(in owner, in oldTarget, in newRecord, 100f, 2f, 2));

        var foreignEpoch = ObjectKey.Create(OtherEpoch, PoolKind.Entity, 101, 11, 1);
        TestAssert.AreEqual(ConstructionDispatchReason.WrongGeneration,
            HostConstructionDispatch.EvaluateRepair(in owner, in foreignEpoch, in newRecord, 100f, 2f, 2));
    }

    [TestMethod]
    public void BadTargetShapesAreNotReady()
    {
        var target = Entity(11, 1, 101);
        var owner = OwnerSnap(Player(), planet: 101);
        var damage = Damage(target);

        var prebuild = ObjectKey.Create(Epoch, PoolKind.Prebuild, 101, 21, 1);
        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            HostConstructionDispatch.EvaluateRepair(in owner, in prebuild, in damage, 100f, 2f, 2));

        var badRecord = Damage(target, hpMax: 0);
        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in badRecord, 100f, 2f, 2));

        TestAssert.AreEqual(ConstructionDispatchReason.TargetNotReady,
            HostConstructionDispatch.EvaluateRepair(in owner, in target, in damage, 100f, float.NaN, 2));
    }

    [TestMethod]
    public void EmptyBudgetReportsNoIdleDroneWithoutMovingCounters()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "p1", 2, planet: 101);
        var owner = Player("p1", 2);
        ledger.EnsureBudget(owner, 1);
        var target = Entity(11, 1, 101);
        var damage = Damage(target);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(owner, 300f, true, true, out var snap));

        var first = service.TryReserveRepair(in snap, in target, in damage, 100f, 2f, 100L, default,
            out var key, out var ledgerError);
        TestAssert.AreEqual(ConstructionDispatchReason.None, first);
        TestAssert.AreEqual(ConstructionTaskError.None, ledgerError);
        TestAssert.IsTrue(key.IsValid);
        TestAssert.AreEqual(1, ledger.GetRepairerCount(target));

        var secondTarget = Entity(12, 1, 101);
        var secondDamage = Damage(secondTarget);
        var blocked = service.TryReserveRepair(in snap, in secondTarget, in secondDamage, 100f, 2f, 101L,
            default, out _, out _);
        TestAssert.AreEqual(ConstructionDispatchReason.NoIdleDrone, blocked);
        TestAssert.AreEqual(0, ledger.GetRepairerCount(secondTarget));
    }

    [TestMethod]
    public void ReplayingTheSameRepairReportsCoveredWithoutRaisingRepairerCount()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "p1", 2, planet: 101);
        var owner = Player("p1", 2);
        ledger.EnsureBudget(owner, 4);
        var target = Entity(11, 1, 101);
        var damage = Damage(target);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(owner, 300f, true, true, out var snap));

        TestAssert.AreEqual(ConstructionDispatchReason.None,
            service.TryReserveRepair(in snap, in target, in damage, 100f, 2f, 100L, default,
                out var first, out var firstError));
        TestAssert.AreEqual(ConstructionTaskError.None, firstError);
        TestAssert.AreEqual(ConstructionDispatchReason.DemandCovered,
            service.TryReserveRepair(in snap, in target, in damage, 100f, 2f, 101L, default,
                out var second, out var secondError));
        TestAssert.AreEqual(ConstructionTaskError.DuplicateTask, secondError);
        TestAssert.AreEqual(first, second);
        TestAssert.AreEqual(1, ledger.GetRepairerCount(target));
    }

    [TestMethod]
    public void BasesUseTheSameGatesThroughOneInterface()
    {
        var bases = new FakeBases();
        var baseOwner = Base();
        bases.Snapshots[baseOwner] = OwnerSnap(baseOwner, planet: 101, speed: 99f);
        var (registry, ledger, service) = NewService(bases);
        ledger.EnsureBudget(baseOwner, 2);

        var target = Entity(11, 1, 101);
        var damage = Damage(target);
        TestAssert.IsTrue(service.TryGetOwnerSnapshot(baseOwner, 300f, true, true, out var snap));
        TestAssert.AreEqual(ConstructionDispatchReason.None,
            service.EvaluateRepair(in snap, in target, in damage, 100f, 2f));

        var unknownBase = Base(nativeId: 9);
        TestAssert.IsFalse(service.TryGetOwnerSnapshot(unknownBase, 300f, true, true, out _),
            "Without a base source entry the base stays fail-closed.");
    }

    [TestMethod]
    public void CollectionSortsByDamageThenStableOwner()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "b", 3, planet: 101);
        RegisterPlayer(registry, "a", 2, planet: 101);
        var ownerA = Player("a", 2);
        var ownerB = Player("b", 3);
        ledger.EnsureBudget(ownerA, 4);
        ledger.EnsureBudget(ownerB, 4);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(ownerA, 300f, true, true, out var snapA));
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(ownerB, 300f, true, true, out var snapB));

        var small = Damage(Entity(11, 1, 101), hp: 900, hpMax: 1000);
        var big = Damage(Entity(12, 1, 101), hp: 100, hpMax: 1000);
        var outCandidates = new List<RepairDispatchCandidate>();
        var found = service.CollectDispatchableRepairs(
            new[] { small, big }, new[] { snapB, snapA },
            (_, _) => 100f, (_, _) => 2f, _ => 1f, outCandidates);
        TestAssert.AreEqual(4, found);
        TestAssert.AreEqual(big.Target, outCandidates[0].Damage.Target, "Missing HP sorts first.");
        TestAssert.AreEqual(big.Target, outCandidates[1].Damage.Target);
        TestAssert.AreEqual(ownerA, outCandidates[0].Owner.Owner, "Ties break by stable owner.");
        TestAssert.AreEqual(ownerA, outCandidates[2].Owner.Owner);
    }

    [TestMethod]
    public void CollectionSkipsBlockedPairsButKeepsTheScopeLive()
    {
        var (registry, ledger, service) = NewService();
        RegisterPlayer(registry, "near", 2, planet: 101);
        RegisterPlayer(registry, "far", 3, planet: 102);
        var near = Player("near", 2);
        var far = Player("far", 3);
        ledger.EnsureBudget(near, 2);
        ledger.EnsureBudget(far, 2);
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(near, 300f, true, true, out var nearSnap));
        TestAssert.IsTrue(service.TryGetPlayerOwnerSnapshot(far, 300f, true, true, out var farSnap));

        var target = Entity(11, 1, 101);
        var damage = Damage(target, planet: 101);
        var outCandidates = new List<RepairDispatchCandidate>();
        var found = service.CollectDispatchableRepairs(
            new[] { damage }, new[] { nearSnap, farSnap },
            (_, _) => 100f, (_, _) => 2f, _ => 1f, outCandidates);
        TestAssert.AreEqual(1, found);
        TestAssert.AreEqual(near, outCandidates[0].Owner.Owner);
    }

    [TestMethod]
    public void DistanceHelperMatchesTheRangeGate()
    {
        TestAssert.AreEqual(0f, HostConstructionDispatch.DistanceSquared(1f, 2f, 3f, 1f, 2f, 3f));
        TestAssert.AreEqual(25f, HostConstructionDispatch.DistanceSquared(0f, 0f, 0f, 3f, 4f, 0f));
    }
}
