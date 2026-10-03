using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A15: construction tasks and the shared drone budget, pure model.
/// </summary>
/// <remarks>
/// TASKS.md A15 acceptance: build/repair/reconstruct share one total; reserved, active and
/// returning all occupy; one drone never serves two tasks; demolish/death/disconnect/retarget
/// each release once; replaying a task never raises repairerCount. Depends on A02 (identity),
/// A04 (frame-owned, non-thread-safe) and A06 (generation-scoped targets).
/// </remarks>
[TestClass]
public class ConstructionTaskTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA15A15A15A15A15A, 0xC0C0C0C0C0C0C0C0);
    private static readonly AuthorityEpoch OtherEpoch = new(0xDEADBEEFDEADBEEF, 0x0011223344556677);

    private static ConstructionOwnerKey Player(string persistent = "p1", ushort session = 2) =>
        ConstructionOwnerKey.ForPlayer(persistent, session);

    private static ConstructionOwnerKey Base(int nativeId = 5, long generation = 1) =>
        ConstructionOwnerKey.ForBase(ObjectKey.Create(Epoch, PoolKind.Base, 101, nativeId, generation));

    private static ObjectKey Entity(int nativeId = 11, long generation = 1, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.Entity, planet, nativeId, generation);

    private static ObjectKey Prebuild(int nativeId = 21, long generation = 1, int planet = 101) =>
        ObjectKey.Create(Epoch, PoolKind.Prebuild, planet, nativeId, generation);

    private static ConstructionTaskLedger NewLedger() => new(Epoch);

    private static ConstructionTaskLedger LedgerWithBudget(ConstructionOwnerKey owner, int total)
    {
        var ledger = NewLedger();
        ledger.EnsureBudget(owner, total);
        return ledger;
    }

    private static ConstructionTaskKey Add(ConstructionTaskLedger ledger, ConstructionOwnerKey owner,
        ObjectKey target, ConstructionTaskKind kind, long tick = 100)
    {
        var error = ledger.TryAddTask(owner, target, kind, tick, default, out var key);
        TestAssert.AreEqual(ConstructionTaskError.None, error, "Add " + kind + " on " + target + " should succeed.");
        TestAssert.IsTrue(key.IsValid);
        return key;
    }

    private static void Advance(ConstructionTaskLedger ledger, ConstructionTaskKey key,
        ConstructionTaskStage next, long tick)
    {
        TestAssert.AreEqual(ConstructionTaskError.None, ledger.TryAdvance(key, next, tick),
            "Advance to " + next + " should succeed.");
    }

    [TestMethod]
    public void OwnerKeysRejectBadIdentityAndOrderStably()
    {
        TestAssert.IsFalse(default(ConstructionOwnerKey).IsValid);
        TestAssert.IsFalse(ConstructionOwnerKey.ForPlayer(null, 2).IsValid);
        TestAssert.IsFalse(ConstructionOwnerKey.ForPlayer("p", 0).IsValid);
        TestAssert.IsFalse(ConstructionOwnerKey.ForBase(default).IsValid);
        TestAssert.IsFalse(ConstructionOwnerKey.ForBase(Entity()).IsValid, "A base owner must name a Base pool key.");

        var a = Player("p1", 2);
        var b = Player("p1", 2);
        TestAssert.AreEqual(a, b);
        TestAssert.AreEqual(a.GetHashCode(), b.GetHashCode());

        // Players sort before bases; same persistent sorts by session seat.
        TestAssert.IsTrue(ConstructionOwnerKey.StableCompare(Player("p1", 2), Base()) < 0);
        TestAssert.IsTrue(ConstructionOwnerKey.StableCompare(Player("p1", 2), Player("p1", 9)) < 0);
        TestAssert.IsTrue(ConstructionOwnerKey.StableCompare(Player("a", 2), Player("b", 2)) < 0);
        TestAssert.AreEqual(0, ConstructionOwnerKey.StableCompare(a, b));
    }

    [TestMethod]
    public void BudgetInvariantHoldsAcrossEveryBucketMove()
    {
        var budget = new DroneBudget(Player(), 3);
        TestAssert.IsTrue(budget.CheckInvariant());
        TestAssert.AreEqual(3, budget.Idle);

        string reason;
        TestAssert.IsTrue(budget.TryReserve(out reason));
        TestAssert.IsTrue(budget.TryReserve(out reason));
        TestAssert.AreEqual(1, budget.Idle);
        TestAssert.AreEqual(2, budget.Reserved);
        TestAssert.IsTrue(budget.CheckInvariant());

        TestAssert.IsTrue(budget.TryActivate(out reason));
        TestAssert.AreEqual(1, budget.Reserved);
        TestAssert.AreEqual(1, budget.Active);
        TestAssert.IsTrue(budget.TryBeginReturn(out reason));
        TestAssert.AreEqual(0, budget.Active);
        TestAssert.AreEqual(1, budget.Returning);
        TestAssert.AreEqual(1, budget.Idle, "Reserved, active and returning all occupy idle.");
        TestAssert.IsTrue(budget.CheckInvariant());

        TestAssert.IsTrue(budget.ReleaseReturning());
        TestAssert.IsFalse(budget.ReleaseReturning(), "Releasing an empty bucket moves nothing.");
        TestAssert.IsTrue(budget.CheckInvariant());
    }

    [TestMethod]
    public void BudgetRefusesOverReserveAndShrinkBelowOccupied()
    {
        var budget = new DroneBudget(Player(), 1);
        string reason;
        TestAssert.IsTrue(budget.TryReserve(out reason));
        TestAssert.IsFalse(budget.TryReserve(out reason), "No second drone exists.");
        TestAssert.AreEqual("NoIdleDrone", reason);

        TestAssert.IsFalse(budget.TrySetTotal(0, out reason), "Shrinking below occupied strands a reservation.");
        TestAssert.AreEqual("OccupiedSlots", reason);
        TestAssert.AreEqual(1, budget.Total);
        TestAssert.IsTrue(budget.TrySetTotal(2, out reason));
        TestAssert.AreEqual(2, budget.Total);
        TestAssert.IsTrue(budget.CheckInvariant());
    }

    [TestMethod]
    public void BuildRepairAndReconstructShareOneTotal()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 2);
        Add(ledger, owner, Prebuild(21), ConstructionTaskKind.Build);
        Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair);
        var third = ledger.TryAddTask(owner, Prebuild(22), ConstructionTaskKind.Reconstruct, 100,
            default, out _);
        TestAssert.AreEqual(ConstructionTaskError.NoIdleDrone, third,
            "The third kind shares the same total of 2.");
        TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(0, budget.Idle);
        TestAssert.AreEqual(2, budget.Occupied);
        TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out _));
    }

    [TestMethod]
    public void OneDroneNeverServesBuildAndRepairTogether()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 1);
        Add(ledger, owner, Prebuild(21), ConstructionTaskKind.Build);
        TestAssert.AreEqual(ConstructionTaskError.NoIdleDrone,
            ledger.TryAddTask(owner, Entity(11), ConstructionTaskKind.Repair, 100, default, out _));
        TestAssert.AreEqual(1, ledger.ActiveTaskCount);
        TestAssert.AreEqual(0, ledger.GetRepairerCount(Entity(11)));
    }

    [TestMethod]
    public void ARexervedSlotTravelsTheFullPathToCompleted()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 1);
        var key = Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair, 100);
        TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(0, budget.Idle, "Reserved occupies.");

        Advance(ledger, key, ConstructionTaskStage.Launching, 101);
        TestAssert.AreEqual(0, budget.Idle, "Launching occupies.");
        Advance(ledger, key, ConstructionTaskStage.Travelling, 102);
        Advance(ledger, key, ConstructionTaskStage.Working, 103);
        TestAssert.AreEqual(0, budget.Idle, "Working occupies.");
        TestAssert.AreEqual(1, ledger.GetRepairerCount(Entity(11)));

        Advance(ledger, key, ConstructionTaskStage.Returning, 104);
        TestAssert.AreEqual(0, budget.Idle, "Returning still occupies.");
        TestAssert.AreEqual(0, ledger.GetRepairerCount(Entity(11)),
            "A drone flying home no longer repairs.");
        Advance(ledger, key, ConstructionTaskStage.Completed, 105);
        TestAssert.AreEqual(1, budget.Idle, "Completion releases exactly once.");

        TestAssert.IsTrue(ledger.TryGetTask(key, out var task));
        TestAssert.AreEqual(ConstructionTaskStage.Completed, task.Stage);
        TestAssert.AreEqual(6L, task.Revision, "Create plus five advances.");
        TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out _));
    }

    [TestMethod]
    public void SkippedStagesTerminalMovesAndStaleTicksAreRefused()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 2);
        var key = Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair, 100);

        TestAssert.AreEqual(ConstructionTaskError.BadStageTransition,
            ledger.TryAdvance(key, ConstructionTaskStage.Working, 101), "Reserved cannot skip to working.");
        TestAssert.AreEqual(ConstructionTaskError.StaleTick,
            ledger.TryAdvance(key, ConstructionTaskStage.Launching, 99), "Ticks never move backwards.");
        Advance(ledger, key, ConstructionTaskStage.Launching, 100);
        TestAssert.IsTrue(ledger.TryGetTask(key, out var task));
        TestAssert.AreEqual(2L, task.Revision, "Refused moves bump nothing.");

        Advance(ledger, key, ConstructionTaskStage.Travelling, 101);
        Advance(ledger, key, ConstructionTaskStage.Working, 102);
        Advance(ledger, key, ConstructionTaskStage.Returning, 103);
        Advance(ledger, key, ConstructionTaskStage.Completed, 104);
        TestAssert.AreEqual(ConstructionTaskError.AlreadyTerminal,
            ledger.TryAdvance(key, ConstructionTaskStage.Cancelled, 105));
        TestAssert.AreEqual(ConstructionTaskError.AlreadyTerminal,
            ledger.TryCancel(key, 106, ConstructionCancelReason.TargetDestroyed));
    }

    [TestMethod]
    public void CancelFromAnyStageReleasesExactlyOnce()
    {
        var stages = new[]
        {
            ConstructionTaskStage.Reserved, ConstructionTaskStage.Launching,
            ConstructionTaskStage.Travelling, ConstructionTaskStage.Working,
            ConstructionTaskStage.Returning
        };
        foreach (var cancelAt in stages)
        {
            var owner = Player("cancel-" + cancelAt, 2);
            var ledger = LedgerWithBudget(owner, 1);
            var key = Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair, 100);
            var tick = 100L;
            foreach (var next in new[]
                     {
                         ConstructionTaskStage.Launching, ConstructionTaskStage.Travelling,
                         ConstructionTaskStage.Working, ConstructionTaskStage.Returning
                     })
            {
                if (cancelAt == ConstructionTaskStage.Reserved) break;
                Advance(ledger, key, next, ++tick);
                if (next == cancelAt) break;
            }
            TestAssert.AreEqual(ConstructionTaskError.None,
                ledger.TryCancel(key, tick + 1, ConstructionCancelReason.OwnerOffline),
                "Cancel at " + cancelAt + " should succeed.");
            TestAssert.AreEqual(ConstructionTaskError.AlreadyTerminal,
                ledger.TryCancel(key, tick + 2, ConstructionCancelReason.OwnerOffline),
                "A second cancel at " + cancelAt + " must mint nothing.");
            TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
            TestAssert.AreEqual(1, budget.Idle, "Cancel at " + cancelAt + " releases exactly once.");
            TestAssert.AreEqual(0, ledger.GetRepairerCount(Entity(11)));
            TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out _));
        }
    }

    [TestMethod]
    public void DemolishCancelMatchesExactGeneration()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 3);
        var oldTarget = Entity(11, 1);
        var other = Entity(12, 1);
        Add(ledger, owner, oldTarget, ConstructionTaskKind.Repair);
        Add(ledger, owner, other, ConstructionTaskKind.Repair);

        TestAssert.AreEqual(1, ledger.CancelByTarget(oldTarget, 200, ConstructionCancelReason.TargetDemolished));
        TestAssert.AreEqual(0, ledger.GetRepairerCount(oldTarget));
        TestAssert.AreEqual(1, ledger.GetRepairerCount(other));
        TestAssert.AreEqual(0, ledger.CancelByTarget(oldTarget, 201, ConstructionCancelReason.TargetDemolished),
            "Re-cancelling the same target releases nothing twice.");
        TestAssert.AreEqual(0, ledger.CancelByTarget(Entity(11, 2), 202, ConstructionCancelReason.TargetDemolished),
            "The next generation was never a task, so nothing cancels.");

        // The slot's next generation is a genuinely new task, not a silent retarget.
        Add(ledger, owner, Entity(11, 2), ConstructionTaskKind.Repair, 203);
        TestAssert.AreEqual(1, ledger.GetRepairerCount(Entity(11, 2)));
        TestAssert.AreEqual(0, ledger.GetRepairerCount(oldTarget));
    }

    [TestMethod]
    public void DisconnectCancelsEveryOwnerTaskOnce()
    {
        var owner = Player("p9", 7);
        var other = Player("p10", 8);
        var ledger = LedgerWithBudget(owner, 4);
        ledger.EnsureBudget(other, 4);
        Add(ledger, owner, Prebuild(21), ConstructionTaskKind.Build);
        Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair);
        Add(ledger, other, Entity(12), ConstructionTaskKind.Repair);

        TestAssert.AreEqual(2, ledger.CancelByOwner(owner, 300, ConstructionCancelReason.OwnerOffline));
        TestAssert.AreEqual(0, ledger.CancelByOwner(owner, 301, ConstructionCancelReason.OwnerOffline),
            "A second disconnect sweep mints nothing.");
        TestAssert.AreEqual(1, ledger.ActiveTaskCount, "The other owner's task survives.");
        TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(4, budget.Idle);
        TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out _));
        TestAssert.IsTrue(ledger.CheckOwnerInvariant(other, out _));
    }

    [TestMethod]
    public void RetargetMovesRepairOccupancyToTheNewTarget()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 1);
        var key = Add(ledger, owner, Entity(11), ConstructionTaskKind.Repair, 100);
        Advance(ledger, key, ConstructionTaskStage.Launching, 101);

        TestAssert.AreEqual(ConstructionTaskError.None, ledger.TryRetarget(key, Entity(12), 102));
        TestAssert.AreEqual(0, ledger.GetRepairerCount(Entity(11)), "The old target's occupancy is released.");
        TestAssert.AreEqual(1, ledger.GetRepairerCount(Entity(12)), "The new target holds it.");
        TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(0, budget.Idle, "Retarget never frees the drone mid-flight.");
        TestAssert.IsTrue(ledger.TryGetTask(key, out var task));
        TestAssert.AreEqual(Entity(12), task.Target);
        TestAssert.AreEqual(3L, task.Revision, "Create, launch, retarget.");

        TestAssert.AreEqual(ConstructionTaskError.KindTargetMismatch,
            ledger.TryRetarget(key, Prebuild(21), 103), "A repair task cannot slide onto a prebuild.");
        TestAssert.AreEqual(Entity(12), task.Target, "A refused retarget moves nothing.");
    }

    [TestMethod]
    public void ReplayingTheSameTaskNeverRaisesRepairerCount()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 2);
        var target = Entity(11);
        var first = Add(ledger, owner, target, ConstructionTaskKind.Repair);
        TestAssert.AreEqual(ConstructionTaskError.DuplicateTask,
            ledger.TryAddTask(owner, target, ConstructionTaskKind.Repair, 101, default, out var replay));
        TestAssert.AreEqual(first, replay, "The replay names the same task key.");
        TestAssert.AreEqual(1, ledger.GetRepairerCount(target));
        TestAssert.AreEqual(1, ledger.ActiveTaskCount);
        TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
        TestAssert.AreEqual(1, budget.Idle, "The replay reserves no second drone.");
    }

    [TestMethod]
    public void OnlyRepairTasksFeedRepairerCount()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 4);
        var repairTarget = Entity(11);
        var buildTarget = Prebuild(21);
        Add(ledger, owner, repairTarget, ConstructionTaskKind.Repair);
        Add(ledger, owner, buildTarget, ConstructionTaskKind.Build);
        Add(ledger, owner, Prebuild(22), ConstructionTaskKind.Reconstruct);
        TestAssert.AreEqual(1, ledger.GetRepairerCount(repairTarget));
        TestAssert.AreEqual(0, ledger.GetRepairerCount(buildTarget));
        TestAssert.AreEqual(3, ledger.ActiveTaskCount);
    }

    [TestMethod]
    public void KindTargetMismatchIsRefused()
    {
        var owner = Player();
        var ledger = LedgerWithBudget(owner, 2);
        TestAssert.AreEqual(ConstructionTaskError.KindTargetMismatch,
            ledger.TryAddTask(owner, Entity(11), ConstructionTaskKind.Build, 100, default, out _));
        TestAssert.AreEqual(ConstructionTaskError.KindTargetMismatch,
            ledger.TryAddTask(owner, Prebuild(21), ConstructionTaskKind.Repair, 100, default, out _));
        TestAssert.AreEqual(0, ledger.ActiveTaskCount);
        TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out _));
    }

    [TestMethod]
    public void ForeignEpochsHaveNoSideEffect()
    {
        var ledger = NewLedger();
        ledger.EnsureBudget(Player(), 2);
        var foreignTarget = ObjectKey.Create(OtherEpoch, PoolKind.Entity, 101, 11, 1);
        TestAssert.AreEqual(ConstructionTaskError.WrongEpoch,
            ledger.TryAddTask(Player(), foreignTarget, ConstructionTaskKind.Repair, 100, default, out _));
        var foreignTx = new HostTransactionId(OtherEpoch, 1);
        TestAssert.AreEqual(ConstructionTaskError.WrongEpoch,
            ledger.TryAddTask(Player(), Entity(), ConstructionTaskKind.Repair, 100, foreignTx, out _));
        TestAssert.AreEqual(0, ledger.ActiveTaskCount);
    }

    [TestMethod]
    public void PlacerFirstWinsAndTiesBreakByStableOwner()
    {
        var placer = Player("placer", 3);
        var other = Player("other", 4);
        TestAssert.IsTrue(ConstructionSelection.TryPickPlacerFirst(placer, true,
            new List<ConstructionOwnerKey> { other }, out var selected));
        TestAssert.AreEqual(placer, selected, "An available placer keeps its prebuild.");

        TestAssert.IsTrue(ConstructionSelection.TryPickPlacerFirst(placer, false,
            new List<ConstructionOwnerKey> { other, Base() }, out selected));
        TestAssert.AreEqual(other, selected, "Players sort before bases when the placer is away.");

        TestAssert.IsFalse(ConstructionSelection.TryPickPlacerFirst(default, false,
            new List<ConstructionOwnerKey>(), out _), "No candidate means no owner.");
    }

    [TestMethod]
    public void RepairOrderingPrefersMissingHpThenStableTarget()
    {
        var a = new ConstructionSelection.RepairCandidateInput(Entity(11, 1), 100, 1000, 2f, 0, 1f);
        var b = new ConstructionSelection.RepairCandidateInput(Entity(12, 1), 900, 1000, 5f, 0, 1f);
        TestAssert.IsTrue(ConstructionSelection.CompareRepairCandidates(in a, in b) < 0,
            "900 missing outranks 100 missing despite the lower damage rate.");

        var c = new ConstructionSelection.RepairCandidateInput(Entity(11, 1), 100, 1000, 1f, 2, 1f);
        var d = new ConstructionSelection.RepairCandidateInput(Entity(11, 1), 100, 1000, 1f, 0, 1f);
        TestAssert.IsTrue(ConstructionSelection.CompareRepairCandidates(in d, in c) < 0,
            "Fewer current repairers outranks more when damage is equal.");
    }

    [TestMethod]
    public void RandomTaskChurnNeverBreaksTheBudgetInvariant()
    {
        var random = new Random(0xA15);
        var ownerA = Player("fuzz-a", 2);
        var ownerB = Player("fuzz-b", 3);
        var ledger = NewLedger();
        ledger.EnsureBudget(ownerA, 4);
        ledger.EnsureBudget(ownerB, 4);
        var owners = new[] { ownerA, ownerB };
        var live = new List<ConstructionTaskKey>();

        for (var step = 0; step < 1500; step++)
        {
            var owner = owners[random.Next(owners.Length)];
            var roll = random.Next(100);
            if (roll < 45)
            {
                var kind = (ConstructionTaskKind)random.Next(1, 4);
                var target = kind == ConstructionTaskKind.Repair
                    ? Entity(random.Next(1, 6), random.Next(1, 3))
                    : Prebuild(random.Next(1, 6), random.Next(1, 3));
                var error = ledger.TryAddTask(owner, target, kind, step, default, out var key);
                if (error == ConstructionTaskError.None) live.Add(key);
            }
            else if (roll < 75 && live.Count > 0)
            {
                var key = live[random.Next(live.Count)];
                if (!ledger.TryGetTask(key, out var task) || task.IsTerminal)
                {
                    live.Remove(key);
                    continue;
                }
                var next = PickRandomNext(random, task.Stage);
                if (next == ConstructionTaskStage.Cancelled)
                    ledger.TryCancel(key, step, ConstructionCancelReason.DemandCovered);
                else ledger.TryAdvance(key, next, step);
                if (task.IsTerminal) live.Remove(key);
            }
            else if (roll < 85 && live.Count > 0)
            {
                var key = live[random.Next(live.Count)];
                if (ledger.TryGetTask(key, out var task) && task.IsTerminal) live.Remove(key);
                else if (task != null && task.Kind == ConstructionTaskKind.Repair)
                    ledger.TryRetarget(key, Entity(random.Next(1, 6), random.Next(1, 3)), step);
            }
            else
            {
                var victim = random.Next(2) == 0
                    ? (object)Entity(random.Next(1, 6), random.Next(1, 3))
                    : owners[random.Next(owners.Length)];
                if (victim is ObjectKey target)
                    ledger.CancelByTarget(target, step, ConstructionCancelReason.TargetDestroyed);
                else ledger.CancelByOwner((ConstructionOwnerKey)victim, step, ConstructionCancelReason.OwnerOffline);
                live.RemoveAll(k => !ledger.TryGetTask(k, out var t) || t.IsTerminal);
            }

            TestAssert.IsTrue(ledger.CheckOwnerInvariant(ownerA, out var detailA), "Step " + step + ": " + detailA);
            TestAssert.IsTrue(ledger.CheckOwnerInvariant(ownerB, out var detailB), "Step " + step + ": " + detailB);
        }

        foreach (var owner in owners)
        {
            TestAssert.IsTrue(ledger.CheckOwnerInvariant(owner, out var detail), detail);
            TestAssert.IsTrue(ledger.TryGetBudget(owner, out var budget));
            TestAssert.AreEqual(budget.Occupied, CountHeld(ledger, owner));
        }
    }

    private static ConstructionTaskStage PickRandomNext(Random random, ConstructionTaskStage stage)
    {
        var options = new List<ConstructionTaskStage>();
        switch (stage)
        {
            case ConstructionTaskStage.Reserved:
                options.Add(ConstructionTaskStage.Launching);
                break;
            case ConstructionTaskStage.Launching:
                options.Add(ConstructionTaskStage.Travelling);
                break;
            case ConstructionTaskStage.Travelling:
                options.Add(ConstructionTaskStage.Working);
                break;
            case ConstructionTaskStage.Working:
                options.Add(ConstructionTaskStage.Returning);
                break;
            case ConstructionTaskStage.Returning:
                options.Add(ConstructionTaskStage.Completed);
                break;
            default:
                return ConstructionTaskStage.Cancelled;
        }
        options.Add(ConstructionTaskStage.Cancelled);
        return options[random.Next(options.Count)];
    }

    private static int CountHeld(ConstructionTaskLedger ledger, ConstructionOwnerKey owner)
    {
        // Mirrors CheckOwnerInvariant's held-slot count through the public task surface.
        var held = 0;
        for (long sequence = 1; sequence < ledger.NextSequence; sequence++)
        {
            if (ledger.TryGetTask(new ConstructionTaskKey(ledger.Epoch, sequence), out var task) &&
                task.Owner.Equals(owner) && task.OccupiesBudget)
                held++;
        }
        return held;
    }

    [TestMethod]
    public void TheConstructionModelReferencesNoEngineTypes()
    {
        var assembly = typeof(ConstructionOwnerKey).Assembly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace != "NebulaModel.Authority") continue;
            if (type.Name != nameof(ConstructionOwnerKey) && type.Name != nameof(DroneBudget) &&
                type.Name != nameof(ConstructionTaskState) && type.Name != nameof(ConstructionTaskLedger) &&
                type.Name != nameof(ConstructionSelection) && type.Name != nameof(ConstructionTaskKey) &&
                type.Name.IndexOf("RepairCandidate", StringComparison.Ordinal) < 0 &&
                type.Name.IndexOf("DroneSlot", StringComparison.Ordinal) < 0 &&
                type.Name.IndexOf("ConstructionTask", StringComparison.Ordinal) < 0 &&
                type.Name.IndexOf("ConstructionOwner", StringComparison.Ordinal) < 0 &&
                type.Name.IndexOf("ConstructionCancel", StringComparison.Ordinal) < 0)
            {
                continue;
            }
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
        TestAssert.IsEmpty(offenders, "Engine references in the A15 model: " + string.Join("; ", offenders));
    }
}
