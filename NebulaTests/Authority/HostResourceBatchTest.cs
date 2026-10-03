using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A10: concurrent spends, anti-rollback, deterministic frame order and conservation.
/// </summary>
/// <remarks>
/// <para>
/// TASKS.md A10 acceptance, all against the A09 ledger through the A10 frame plan:
/// repair/fire/flight spending the same energy never overdraws; an old snapshot never restores
/// spent items (stale revision, not a second spend); factory production and UI transfers in one
/// frame commit in one order no matter which packet arrived first; totals conserve.
/// </para>
/// <para>
/// The tests drive <see cref="HostResourceLedger"/> directly because that is the host truth the
/// refused packets can no longer touch. Game wiring (A08 pattern) is a thin refuse branch with
/// no direct unit test; legacy behaviour is pinned by the full suite having no regression.
/// </para>
/// </remarks>
[TestClass]
public class HostResourceBatchTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA10A10A10A10A10A, 0xBEEFBEEFBEEFBEEF);
    private static readonly ConnectionEpoch ConnA = new(21);
    private static readonly ConnectionEpoch ConnB = new(22);
    private static readonly ConnectionEpoch ConnC = new(23);

    private static CommandKey Key(ConnectionEpoch connection, long sequence) =>
        new(Epoch, connection, sequence);

    private static LedgerOwner Player(string persistentId) => LedgerOwner.ForPlayer(persistentId);

    private static HostTransactionId Begin(HostResourceLedger ledger, ConnectionEpoch connection, long sequence)
    {
        var result = ledger.BeginTransaction(Key(connection, sequence), out var tx, out _);
        TestAssert.AreEqual(LedgerBeginResult.Begun, result, "seq " + sequence + " should begin.");
        return tx;
    }

    private static void Commit(HostResourceLedger ledger, HostTransactionId tx)
    {
        TestAssert.IsTrue(ledger.CommitTransaction(tx, CommandResultCode.Applied, 77, out var outcome));
        TestAssert.AreEqual(CommandResultCode.Applied, outcome.Code);
    }

    [TestMethod]
    public void RepairFireAndFlightNeverOverdrawTheSameEnergy()
    {
        // Three consumers share one CoreEnergy balance with room for two spends, not three.
        var ledger = new HostResourceLedger(Epoch);
        var owner = Player("pilot");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 100);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev0);

        // Repair and fire read the same revision: the second is stale, not a second spend.
        var repair = Begin(ledger, ConnA, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(repair, owner, LedgerResourceKind.CoreEnergy, 40, rev0, out _, out _));
        var fire = Begin(ledger, ConnB, 1);
        TestAssert.AreEqual(LedgerReserveCode.StaleRevision,
            ledger.TryReserveDouble(fire, owner, LedgerResourceKind.CoreEnergy, 40, rev0, out _, out _));
        ledger.AbortTransaction(fire);

        // Flight reads fresh and fits exactly into what is left.
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev1);
        var flight = Begin(ledger, ConnC, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(flight, owner, LedgerResourceKind.CoreEnergy, 40, rev1, out _, out _));

        // A fourth spend with a fresh read no longer fits: insufficient, not overdrawn.
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev2);
        var extra = Begin(ledger, ConnA, 2);
        TestAssert.AreEqual(LedgerReserveCode.Insufficient,
            ledger.TryReserveDouble(extra, owner, LedgerResourceKind.CoreEnergy, 40, rev2, out _, out _));
        ledger.AbortTransaction(extra);

        Commit(ledger, repair);
        Commit(ledger, flight);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var balance, out _);
        TestAssert.AreEqual(20, balance, 1e-9, "100 - 40 - 40 exactly once; the losers moved nothing.");
    }

    [TestMethod]
    public void AnOldSnapshotNeverRestoresSpentItems()
    {
        var ledger = new HostResourceLedger(Epoch);
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 1001, 10);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out _, out var rev0);

        var spend = Begin(ledger, ConnA, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(spend, owner, LedgerResourceKind.InventoryItem, 1001, 6, rev0, out _, out _));
        Commit(ledger, spend);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out var afterSpend, out _);
        TestAssert.AreEqual(4L, afterSpend);

        // The old packet still names rev0 with its stale "10". The ledger refuses it as stale:
        // there is no path that writes the client's claimed balance over host truth.
        var stale = Begin(ledger, ConnB, 1);
        TestAssert.AreEqual(LedgerReserveCode.StaleRevision,
            ledger.TryReserveLong(stale, owner, LedgerResourceKind.InventoryItem, 1001, 6, rev0, out _, out _));
        ledger.AbortTransaction(stale);

        // Even a fresh read cannot conjure the spent items back.
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out _, out var rev1);
        var refill = Begin(ledger, ConnB, 2);
        TestAssert.AreEqual(LedgerReserveCode.Insufficient,
            ledger.TryReserveLong(refill, owner, LedgerResourceKind.InventoryItem, 1001, 10, rev1, out _, out _));
        ledger.AbortTransaction(refill);

        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out var kept, out _);
        TestAssert.AreEqual(4L, kept, "Spent items stay spent; no client balance is ever applied.");
    }

    [TestMethod]
    public void FrameOrderIsDeterministicRegardlessOfArrivalOrder()
    {
        var owner = Player("p1");
        var production = HostResourceOp.CreditLong(
            HostResourceOpType.ProductionIncome, owner, LedgerResourceKind.InventoryItem, 5001, 10, sequence: 3);
        var transfer = HostResourceOp.TransferLong(
            owner, LedgerResourceKind.InventoryItem, 5001, 4, expectedRevision: 0, sequence: 1);
        var spend = HostResourceOp.SpendLong(
            owner, LedgerResourceKind.InventoryItem, 5001, 6, expectedRevision: 0, sequence: 2);

        var arrivalA = new List<HostResourceOp> { spend, production, transfer };
        var arrivalB = new List<HostResourceOp> { transfer, spend, production };
        var arrivalC = new List<HostResourceOp> { production, transfer, spend };

        var orderedA = HostResourceBatch.Order(arrivalA);
        var orderedB = HostResourceBatch.Order(arrivalB);
        var orderedC = HostResourceBatch.Order(arrivalC);

        TestAssert.HasCount(orderedA.Count, orderedB);
        for (var i = 0; i < orderedA.Count; i++)
        {
            TestAssert.AreEqual(orderedA[i], orderedB[i], "Shuffle must not change the plan at index " + i);
            TestAssert.AreEqual(orderedA[i], orderedC[i], "Shuffle must not change the plan at index " + i);
        }

        // Production income first, then transfer, then spend: the design's stated order.
        TestAssert.AreEqual(HostResourceOpType.ProductionIncome, orderedA[0].OpType);
        TestAssert.AreEqual(HostResourceOpType.Transfer, orderedA[1].OpType);
        TestAssert.AreEqual(HostResourceOpType.Spend, orderedA[2].OpType);
    }

    [TestMethod]
    public void ProductionBeforeSpendLetsOneFrameEarnAndSpend()
    {
        // Same frame earns 10 (factory share) and spends 6. Unordered, the spend reads rev0 with
        // balance 0 and fails; ordered (credit first), it reads the post-credit revision and lands.
        var ledger = new HostResourceLedger(Epoch);
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 6006, 0);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 6006, out _, out var rev0);

        var spendFirst = Begin(ledger, ConnA, 1);
        TestAssert.AreEqual(LedgerReserveCode.Insufficient,
            ledger.TryReserveLong(spendFirst, owner, LedgerResourceKind.InventoryItem, 6006, 6, rev0, out _, out _));
        ledger.AbortTransaction(spendFirst);

        var earn = Begin(ledger, ConnB, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.CreditLong(earn, owner, LedgerResourceKind.InventoryItem, 6006, 10, out var rev1));
        Commit(ledger, earn);

        var spend = Begin(ledger, ConnA, 2);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(spend, owner, LedgerResourceKind.InventoryItem, 6006, 6, rev1, out _, out _));
        Commit(ledger, spend);

        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 6006, out var balance, out _);
        TestAssert.AreEqual(4L, balance, "0 + 10 - 6 in frame order.");
        TestAssert.AreEqual(rev1 + 1, ledger.RevisionOf(new LedgerResourceKey(owner, LedgerResourceKind.InventoryItem, 6006)));
    }

    [TestMethod]
    public void TotalsConserveAcrossEnergyAmmoAndInventory()
    {
        var ledger = new HostResourceLedger(Epoch);
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 100);
        ledger.SeedLong(owner, LedgerResourceKind.AmmoBullet, 7007, 30);
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 7007, 20);

        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var energyRev);
        ledger.TryGetLong(owner, LedgerResourceKind.AmmoBullet, 7007, out _, out var ammoRev);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 7007, out _, out var invRev);

        // One shot groups energy + ammo (A09 shape); a pickup credits inventory in the same frame.
        var shot = Begin(ledger, ConnA, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(shot, owner, LedgerResourceKind.CoreEnergy, 25, energyRev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(shot, owner, LedgerResourceKind.AmmoBullet, 7007, 3, ammoRev, out _, out _));
        Commit(ledger, shot);

        var pickup = Begin(ledger, ConnB, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.CreditLong(pickup, owner, LedgerResourceKind.InventoryItem, 7007, 5, out _));
        Commit(ledger, pickup);

        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var energy, out _);
        ledger.TryGetLong(owner, LedgerResourceKind.AmmoBullet, 7007, out var ammo, out _);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 7007, out var inv, out _);
        TestAssert.AreEqual(75, energy, 1e-9);
        TestAssert.AreEqual(27L, ammo);
        TestAssert.AreEqual(25L, inv);

        // Conservation restated as arithmetic the test owns: start + income - spend == end.
        TestAssert.AreEqual(100 - 25, energy, 1e-9);
        TestAssert.AreEqual(30 - 3, ammo);
        TestAssert.AreEqual(20 + 5, inv);

        // A duplicate retry of either command changes nothing.
        TestAssert.AreEqual(LedgerBeginResult.Duplicate, ledger.BeginTransaction(Key(ConnA, 1), out _, out _));
        TestAssert.AreEqual(LedgerBeginResult.Duplicate, ledger.BeginTransaction(Key(ConnB, 1), out _, out _));
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var energyAgain, out _);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 7007, out var invAgain, out _);
        TestAssert.AreEqual(energy, energyAgain, 1e-9);
        TestAssert.AreEqual(inv, invAgain);
    }

    [TestMethod]
    public void ShuffledPlansAlwaysConverge()
    {
        var random = new Random(0xA10);
        var owner = Player("fuzz");
        var template = new List<HostResourceOp>
        {
            HostResourceOp.CreditLong(HostResourceOpType.ProductionIncome, owner, LedgerResourceKind.InventoryItem, 9001, 7, 1),
            HostResourceOp.CreditLong(HostResourceOpType.SupplyIncome, owner, LedgerResourceKind.InventoryItem, 9001, 5, 2),
            HostResourceOp.TransferLong(owner, LedgerResourceKind.InventoryItem, 9001, 3, 0, 3),
            HostResourceOp.SpendLong(owner, LedgerResourceKind.InventoryItem, 9001, 4, 0, 4),
            HostResourceOp.CreditDouble(HostResourceOpType.ProductionIncome, owner, LedgerResourceKind.CoreEnergy, 11, 5),
            HostResourceOp.SpendDouble(owner, LedgerResourceKind.CoreEnergy, 6, 0, 6)
        };
        var canonical = HostResourceBatch.Order(template);
        for (var trial = 0; trial < 50; trial++)
        {
            var shuffled = new List<HostResourceOp>(template);
            for (var i = shuffled.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                var swap = shuffled[i];
                shuffled[i] = shuffled[j];
                shuffled[j] = swap;
            }
            var ordered = HostResourceBatch.Order(shuffled);
            TestAssert.HasCount(canonical.Count, ordered);
            for (var i = 0; i < canonical.Count; i++)
                TestAssert.AreEqual(canonical[i], ordered[i], "Trial " + trial + " diverged at " + i);
        }
    }
}
