using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A17: host repair energy, lifecycle and single-writer count, pure model.
/// </summary>
/// <remarks>
/// TASKS.md A17 acceptance (pure half): two repairers sum to their legal ratios, an observer
/// adds nothing, low energy scales instead of faking full efficiency, target/owner loss releases
/// once, and repairerCount derives from the ledger instead of the vanilla idle reset. The caller
/// supplies every vanilla number (available energy, repair cost, base repair, scale); this file
/// never copies a balance formula, so a game update cannot silently diverge here.
/// </remarks>
[TestClass]
public class HostRepairExecutionTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA17A17A17A17A17A, 0xB17B17B17B17B17B);
    private static readonly AuthorityEpoch OtherEpoch = new(0xDEADBEEFDEADBEEF, 0x0011223344556677);

    private static ConstructionOwnerKey Player(string persistent = "p1", ushort session = 2) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

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

    [TestMethod]
    public void MechaRatiosFollowAvailableOverCost()
    {
        TestAssert.AreEqual(1f, HostRepairEnergy.ComputeMechaRatio(10.0, 5.0));
        TestAssert.AreEqual(1f, HostRepairEnergy.ComputeMechaRatio(5.0, 5.0));
        TestAssert.AreEqual(0.5f, HostRepairEnergy.ComputeMechaRatio(5.0, 10.0), 1e-6);
        TestAssert.AreEqual(0f, HostRepairEnergy.ComputeMechaRatio(0.0, 10.0));
        TestAssert.AreEqual(0f, HostRepairEnergy.ComputeMechaRatio(-3.0, 10.0));
        TestAssert.AreEqual(1f, HostRepairEnergy.ComputeMechaRatio(10.0, 0.0), "Zero cost is free.");
        TestAssert.AreEqual(0f, HostRepairEnergy.ComputeMechaRatio(double.NaN, 10.0));
    }

    [TestMethod]
    public void BaseRatiosCoverBothStores()
    {
        TestAssert.AreEqual(1f, HostRepairEnergy.ComputeBaseRatio(10.0, 5.0));
        TestAssert.AreEqual(0.2f, HostRepairEnergy.ComputeBaseRatio(2.0, 10.0), 1e-6);
        TestAssert.AreEqual(1f, HostRepairEnergy.ComputeBaseRatio(10L, 5.0));
        TestAssert.AreEqual(0.2f, HostRepairEnergy.ComputeBaseRatio(2L, 10.0), 1e-6);
        TestAssert.AreEqual(0f, HostRepairEnergy.ComputeBaseRatio(0L, 10.0));
    }

    [TestMethod]
    public void RepairDeltasMatchTheVanillaLine()
    {
        TestAssert.AreEqual(10, HostRepairEnergy.ComputeRepairDelta(10f, 1f, 1f));
        TestAssert.AreEqual(5, HostRepairEnergy.ComputeRepairDelta(10f, 0.5f, 1f));
        TestAssert.AreEqual(0, HostRepairEnergy.ComputeRepairDelta(10f, 0f, 1f));
        TestAssert.AreEqual(0, HostRepairEnergy.ComputeRepairDelta(0f, 1f, 1f));
        TestAssert.AreEqual(20, HostRepairEnergy.ComputeRepairDelta(10f, 1f, 2f));
    }

    [TestMethod]
    public void TwoRatiosSumAndObserversAddNothing()
    {
        var total = HostRepairEnergy.SumRepairDeltas(10f, 1f, new List<float> { 1f, 1f });
        TestAssert.AreEqual(20, total, "Two full repairers heal twice one.");
        var mixed = HostRepairEnergy.SumRepairDeltas(10f, 1f, new List<float> { 1f, 0.2f });
        TestAssert.AreEqual(12, mixed, "Low energy scales, never fakes full efficiency.");
        TestAssert.AreEqual(0, HostRepairEnergy.SumRepairDeltas(10f, 1f, new List<float>()));
        TestAssert.AreEqual(0, HostRepairEnergy.SumRepairDeltas(10f, 1f, null));
    }

    [TestMethod]
    public void OwnerLossCancelsForOfflineSwitchAndPlanet()
    {
        var target = Entity();
        var damage = Damage(target);
        var offline = OwnerSnap(Player(), available: false);
        TestAssert.AreEqual(ConstructionCancelReason.OwnerOffline,
            HostDroneLifecycle.OwnerCancelReason(offline, 101));
        var switchedOff = OwnerSnap(Player(), repair: false);
        TestAssert.AreEqual(ConstructionCancelReason.SwitchDisabled,
            HostDroneLifecycle.OwnerCancelReason(switchedOff, 101));
        var elsewhere = OwnerSnap(Player(), planet: 102);
        TestAssert.AreEqual(ConstructionCancelReason.OwnerLeftPlanet,
            HostDroneLifecycle.OwnerCancelReason(elsewhere, 101));
        var staying = OwnerSnap(Player(), planet: 101);
        TestAssert.IsNull(HostDroneLifecycle.OwnerCancelReason(staying, 101));
    }

    [TestMethod]
    public void LiveTasksSurviveLowEnergyAndSpeed()
    {
        // Energy gates new dispatches (A16 NoEnergy); live drones pause with ratio zero.
        var dry = OwnerSnap(Player(), planet: 101, energy: false);
        TestAssert.IsNull(HostDroneLifecycle.OwnerCancelReason(dry, 101),
            "Low energy pauses live work, it never cancels it.");
        var fast = OwnerSnap(Player(), planet: 101, speed: 99f);
        TestAssert.IsNull(HostDroneLifecycle.OwnerCancelReason(fast, 101),
            "Speed gates launches only; in-flight drones fly home on their own.");
    }

    [TestMethod]
    public void TargetFateCompletesCancelsOrKeeps()
    {
        var target = Entity(11, 1);
        var full = Damage(target, hp: 1000, hpMax: 1000);
        var (fateFull, _) = HostDroneLifecycle.TargetFate(target, full);
        TestAssert.AreEqual(HostRepairTaskFate.CompleteRepair, fateFull);

        var missing = Damage(target, combat: false);
        var (fateMissing, cancelMissing) = HostDroneLifecycle.TargetFate(target, missing);
        TestAssert.AreEqual(HostRepairTaskFate.Cancel, fateMissing);
        TestAssert.AreEqual(ConstructionCancelReason.TargetDestroyed, cancelMissing);

        var newGen = Damage(Entity(11, 2));
        var (fateStale, cancelStale) = HostDroneLifecycle.TargetFate(target, newGen);
        TestAssert.AreEqual(HostRepairTaskFate.Cancel, fateStale);
        TestAssert.AreEqual(ConstructionCancelReason.TargetRecycled, cancelStale,
            "The next generation is another object, never an implicit retarget.");

        var foreign = Damage(ObjectKey.Create(OtherEpoch, PoolKind.Entity, 101, 11, 1));
        var (fateForeign, _) = HostDroneLifecycle.TargetFate(target, foreign);
        TestAssert.AreEqual(HostRepairTaskFate.Cancel, fateForeign);

        var open = Damage(target, hp: 500, hpMax: 1000);
        var (fateKeep, _) = HostDroneLifecycle.TargetFate(target, open);
        TestAssert.AreEqual(HostRepairTaskFate.Keep, fateKeep);
    }

    [TestMethod]
    public void RepairerCountDerivesFromTheLedger()
    {
        TestAssert.AreEqual(2, HostRepairerCountPolicy.Reconcile(2, 999),
            "The ledger wins over any vanilla value.");
        TestAssert.AreEqual(0, HostRepairerCountPolicy.Reconcile(0, 3));
        TestAssert.IsFalse(HostRepairerCountPolicy.MayClearOnIdle(true, 2, 2),
            "Live ledger work is never zeroed as idle.");
        TestAssert.IsFalse(HostRepairerCountPolicy.MayClearOnIdle(false, 0, 2));
        TestAssert.IsTrue(HostRepairerCountPolicy.MayClearOnIdle(true, 0, 2),
            "Only a stale vanilla count with no ledger work may clear.");
        TestAssert.IsFalse(HostRepairerCountPolicy.MayClearOnIdle(true, 0, 0));
    }

    [TestMethod]
    public void DisplayStatesCarryIdentityOnly()
    {
        var owner = Player();
        var target = Entity();
        var key = new ConstructionTaskKey(Epoch, 7);
        var first = new HostDroneDisplayState(key, owner, target,
            ConstructionTaskStage.Working, 4);
        var same = new HostDroneDisplayState(key, owner, target,
            ConstructionTaskStage.Working, 4);
        var otherStage = new HostDroneDisplayState(key, owner, target,
            ConstructionTaskStage.Returning, 5);
        TestAssert.AreEqual(first, same);
        TestAssert.AreNotEqual(first, otherStage);
    }

    [TestMethod]
    public void TheRepairModelReferencesNoEngineTypes()
    {
        var assembly = typeof(HostRepairEnergy).Assembly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace != "NebulaModel.Authority") continue;
            if (type.Name != nameof(HostRepairEnergy) && type.Name != nameof(HostDroneLifecycle) &&
                type.Name != nameof(HostRepairerCountPolicy) && type.Name != nameof(HostDroneDisplayState) &&
                type.Name != nameof(HostRepairTaskFate))
                continue;
            foreach (var member in type.GetMembers())
            {
                if (member is System.Reflection.FieldInfo field)
                {
                    var name = field.FieldType.FullName ?? string.Empty;
                    if (name.Contains("UnityEngine") || name.Contains("GameMain") ||
                        name.Contains("NebulaWorld") || name.Contains("NebulaNetwork"))
                        offenders.Add(type.Name + "." + member.Name + ": " + name);
                }
                else if (member is System.Reflection.PropertyInfo property)
                {
                    var name = property.PropertyType.FullName ?? string.Empty;
                    if (name.Contains("UnityEngine") || name.Contains("GameMain") ||
                        name.Contains("NebulaWorld") || name.Contains("NebulaNetwork"))
                        offenders.Add(type.Name + "." + member.Name + ": " + name);
                }
                else if (member is System.Reflection.MethodInfo method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var full = parameter.ParameterType.FullName ?? "";
                        if (full.Contains("UnityEngine") || full.Contains("GameMain") ||
                            full.Contains("NebulaWorld") || full.Contains("NebulaNetwork"))
                            offenders.Add(type.Name + "." + method.Name + "(" + full + ")");
                    }
                }
            }
        }
        TestAssert.IsEmpty(offenders, "Engine references in the A17 model: " + string.Join("; ", offenders));
    }
}
