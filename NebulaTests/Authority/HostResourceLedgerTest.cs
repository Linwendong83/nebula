using System;
using System.Collections.Generic;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A09: the host's resource truth.
/// </summary>
/// <remarks>
/// TASKS.md A09 acceptance: two operations racing on one balance never overdraw; a cancel and a
/// repeated cancel never mint; a reconnect keeps its balance; the host and remote paths are the
/// same. DESIGN 7.2 adds the shape: validate, reserve, execute once, commit, publish — with the
/// retry reading the cached outcome instead of running again.
/// </remarks>
[TestClass]
public class HostResourceLedgerTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA09A09A09A09A09A, 0xBEEFBEEFBEEFBEEF);
    private static readonly ConnectionEpoch ConnA = new(7);
    private static readonly ConnectionEpoch ConnB = new(8);

    private static CommandKey Key(long sequence, ConnectionEpoch? connection = null) =>
        new(Epoch, connection ?? ConnA, sequence);

    private static LedgerOwner Player(string persistentId) => LedgerOwner.ForPlayer(persistentId);

    private static LedgerOwner BaseFor(ObjectKey key) => LedgerOwner.ForBase(key);

    private static ObjectKey BaseKey(int nativeId = 5, long generation = 1) =>
        ObjectKey.Create(Epoch, PoolKind.Base, 101, nativeId, generation);

    private static HostResourceLedger NewLedger(int resultCapacity = 1024) => new(Epoch, resultCapacity);

    private static HostTransactionId Begin(HostResourceLedger ledger, long sequence,
        ConnectionEpoch? connection = null)
    {
        var result = ledger.BeginTransaction(Key(sequence, connection), out var tx, out _);
        TestAssert.AreEqual(LedgerBeginResult.Begun, result, "Sequence " + sequence + " should begin.");
        return tx;
    }

    private static void Commit(HostResourceLedger ledger, HostTransactionId tx,
        CommandResultCode code = CommandResultCode.Applied)
    {
        TestAssert.IsTrue(ledger.CommitTransaction(tx, code, 100, out var outcome));
        TestAssert.AreEqual(code, outcome.Code);
    }

    [TestMethod]
    public void AReserveCommitCycleMovesTheBalanceOnce()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 10);

        TestAssert.IsTrue(ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev0));
        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 6, rev0, out _, out var rev1));
        TestAssert.IsGreaterThan(rev0, rev1);
        Commit(ledger, tx);

        TestAssert.IsTrue(ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var balance, out _));
        TestAssert.AreEqual(4, balance, 1e-9);
        TestAssert.AreEqual(0, ledger.PendingTransactionCount);
        TestAssert.AreEqual(1L, ledger.CommitsTotal);
    }

    [TestMethod]
    public void TwoCompetitorsCannotOverdrawTheSameBalance()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 1001, 10);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out _, out var rev0);

        var first = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(first, owner, LedgerResourceKind.InventoryItem, 1001, 6, rev0, out _, out _));

        // The second command read the same revision before the first committed: stale, not applied.
        var second = Begin(ledger, 2);
        TestAssert.AreEqual(LedgerReserveCode.StaleRevision,
            ledger.TryReserveLong(second, owner, LedgerResourceKind.InventoryItem, 1001, 6, rev0, out _, out _));
        ledger.AbortTransaction(second);

        // Even with a fresh read, 6 no longer fits into 4.
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out _, out var rev1);
        var third = Begin(ledger, 3);
        TestAssert.AreEqual(LedgerReserveCode.Insufficient,
            ledger.TryReserveLong(third, owner, LedgerResourceKind.InventoryItem, 1001, 6, rev1, out _, out _));
        ledger.AbortTransaction(third);

        Commit(ledger, first);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1001, out var balance, out _);
        TestAssert.AreEqual(4L, balance, "10 - 6 exactly once; the losers moved nothing.");
    }

    [TestMethod]
    public void ACancelRefundsOnceAndASecondCancelMintsNothing()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 2002, 10);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 2002, out _, out var rev0);

        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 2002, 4, rev0, out var reservation, out _));
        TestAssert.IsTrue(ledger.ReleaseReservation(reservation));
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 2002, out var refunded, out _);
        TestAssert.AreEqual(10L, refunded);

        TestAssert.IsFalse(ledger.ReleaseReservation(reservation), "A repeated cancel must not mint.");
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 2002, out var after, out _);
        TestAssert.AreEqual(10L, after);

        TestAssert.IsFalse(ledger.ReleaseReservation(999999), "Releasing an unknown reservation mints nothing.");
        // The transaction still seals (with no net movement) so its key cannot be reused as work.
        Commit(ledger, tx);
    }

    [TestMethod]
    public void ACommittedSpendIsFinal()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 3003, 10);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 3003, out _, out var rev0);

        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 3003, 4, rev0, out var reservation, out _));
        Commit(ledger, tx);
        TestAssert.IsFalse(ledger.ReleaseReservation(reservation), "A committed spend cannot be refunded.");
        TestAssert.IsFalse(ledger.AbortTransaction(tx), "A committed transaction cannot be aborted.");
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 3003, out var balance, out _);
        TestAssert.AreEqual(6L, balance);
    }

    [TestMethod]
    public void AnAbortRefundsEveryReserveAndReopensTheKey()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 10);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev0);

        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 3, rev0, out _, out var rev1));
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 2, rev1, out _, out _));
        TestAssert.IsTrue(ledger.AbortTransaction(tx));
        TestAssert.IsFalse(ledger.AbortTransaction(tx), "A second abort refunds nothing.");

        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var balance, out _);
        TestAssert.AreEqual(10, balance, 1e-9, "Nothing was committed, so everything returns.");

        // Abandon reopens the key: the same command may be tried again, running once.
        TestAssert.AreEqual(LedgerBeginResult.Begun, ledger.BeginTransaction(Key(1), out var retry, out _));
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var fresh);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(retry, owner, LedgerResourceKind.CoreEnergy, 3, fresh, out _, out _));
        Commit(ledger, retry);
    }

    [TestMethod]
    public void ADuplicateCommandReadsTheCachedAnswer()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 4004, 10);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 4004, out _, out var rev0);

        var tx = Begin(ledger, 1);
        ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 4004, 4, rev0, out _, out _);
        Commit(ledger, tx, CommandResultCode.Applied);

        TestAssert.AreEqual(LedgerBeginResult.Duplicate,
            ledger.BeginTransaction(Key(1), out var duplicateTx, out var cached));
        TestAssert.AreEqual(tx, duplicateTx);
        TestAssert.AreEqual(CommandResultCode.Applied, cached.Code);
        TestAssert.AreEqual(1L, ledger.DuplicateHits);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 4004, out var balance, out _);
        TestAssert.AreEqual(6L, balance, "A retry must never deduct twice.");
    }

    [TestMethod]
    public void AnEvictedRetryIsRefusedRatherThanExecutedAgain()
    {
        var ledger = NewLedger(resultCapacity: 2);
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 5005, 100);
        for (long sequence = 1; sequence <= 4; sequence++)
        {
            var tx = Begin(ledger, sequence);
            ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 5005, out _, out var rev);
            ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 5005, 1, rev, out _, out _);
            Commit(ledger, tx);
        }
        TestAssert.AreEqual(LedgerBeginResult.TooOld,
            ledger.BeginTransaction(Key(1), out _, out _),
            "An evicted result stays below the high-water mark; it must be refused, not rerun.");
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 5005, out var balance, out _);
        TestAssert.AreEqual(96L, balance);
    }

    [TestMethod]
    public void CommandsFromAnotherEpochAreRejected()
    {
        var ledger = NewLedger();
        var foreign = new CommandKey(new AuthorityEpoch(1, 1), ConnA, 1);
        TestAssert.AreEqual(LedgerBeginResult.WrongEpoch, ledger.BeginTransaction(foreign, out _, out _));
        TestAssert.AreEqual(LedgerBeginResult.Invalid, ledger.BeginTransaction(default, out _, out _));
    }

    [TestMethod]
    public void StaleExpectedRevisionIsRejected()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 10);
        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.StaleRevision,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 1, 999, out _, out _));
        ledger.AbortTransaction(tx);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var balance, out _);
        TestAssert.AreEqual(10, balance, 1e-9);
    }

    [TestMethod]
    public void InvalidAmountsOwnersAndKindsAreRejected()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 10);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev);
        var tx = Begin(ledger, 1);

        TestAssert.AreEqual(LedgerReserveCode.InvalidAmount,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 0, rev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.InvalidAmount,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, -5, rev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.InvalidAmount,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, double.NaN, rev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.InvalidAmount,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, double.PositiveInfinity, rev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.InvalidOwner,
            ledger.TryReserveDouble(tx, default, LedgerResourceKind.CoreEnergy, 1, rev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.WrongKind,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.InventoryItem, 1, rev, out _, out _));
        ledger.AbortTransaction(tx);
    }

    [TestMethod]
    public void ACreditDoesNotDoubleOnRetry()
    {
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 6006, 10);
        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.CreditLong(tx, owner, LedgerResourceKind.InventoryItem, 6006, 5, out _));
        Commit(ledger, tx);

        TestAssert.AreEqual(LedgerBeginResult.Duplicate, ledger.BeginTransaction(Key(1), out _, out var cached));
        TestAssert.IsTrue(cached.Accepted);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 6006, out var balance, out _);
        TestAssert.AreEqual(15L, balance, "Pickup income lands exactly once.");
    }

    [TestMethod]
    public void OneTransactionGroupsEnergyAndAmmo()
    {
        // A single shot spends two pools at once; the retry reads one cached answer for both.
        var ledger = NewLedger();
        var owner = Player("p1");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 100);
        ledger.SeedLong(owner, LedgerResourceKind.AmmoBullet, 5001, 30);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var energyRev);
        ledger.TryGetLong(owner, LedgerResourceKind.AmmoBullet, 5001, out _, out var ammoRev);

        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(tx, owner, LedgerResourceKind.CoreEnergy, 25, energyRev, out _, out _));
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(tx, owner, LedgerResourceKind.AmmoBullet, 5001, 3, ammoRev, out _, out _));
        Commit(ledger, tx);

        TestAssert.AreEqual(LedgerBeginResult.Duplicate, ledger.BeginTransaction(Key(1), out _, out _));
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var energy, out _);
        ledger.TryGetLong(owner, LedgerResourceKind.AmmoBullet, 5001, out var ammo, out _);
        TestAssert.AreEqual(75, energy, 1e-9);
        TestAssert.AreEqual(27L, ammo);
    }

    [TestMethod]
    public void ReconnectKeepsEveryBalance()
    {
        var ledger = NewLedger();
        var owner = Player("same-persistent");
        ledger.SeedDouble(owner, LedgerResourceKind.CoreEnergy, 50);

        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out _, out var rev0);
        var first = Begin(ledger, 1, ConnA);
        ledger.TryReserveDouble(first, owner, LedgerResourceKind.CoreEnergy, 10, rev0, out _, out _);
        Commit(ledger, first);

        // The seat changed (ConnA -> ConnB) but the owner did not: balances travel with the
        // persistent id, while the new connection gets a fresh dedup space.
        TestAssert.IsFalse(ledger.ForgetConnection(new ConnectionEpoch(999)));
        TestAssert.IsTrue(ledger.ForgetConnection(ConnA));
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var kept, out var rev1);
        TestAssert.AreEqual(40, kept, 1e-9, "Forgetting a connection drops its window, never its stock.");

        var second = Begin(ledger, 1, ConnB);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(second, owner, LedgerResourceKind.CoreEnergy, 5, rev1, out _, out _));
        Commit(ledger, second);
        ledger.TryGetDouble(owner, LedgerResourceKind.CoreEnergy, out var after, out _);
        TestAssert.AreEqual(35, after, 1e-9);
    }

    [TestMethod]
    public void HostAndRemoteTakeTheSameLedgerPath()
    {
        var ledger = NewLedger();
        var host = Player("host-persistent");
        var remote = Player("remote-persistent");
        ledger.SeedDouble(host, LedgerResourceKind.CoreEnergy, 20);
        ledger.SeedDouble(remote, LedgerResourceKind.CoreEnergy, 20);
        ledger.TryGetDouble(host, LedgerResourceKind.CoreEnergy, out _, out var hostRev);
        ledger.TryGetDouble(remote, LedgerResourceKind.CoreEnergy, out _, out var remoteRev);

        var hostTx = Begin(ledger, 1, ConnA);
        var remoteTx = Begin(ledger, 1, ConnB);
        var hostCode = ledger.TryReserveDouble(hostTx, host, LedgerResourceKind.CoreEnergy, 7, hostRev, out _, out _);
        var remoteCode = ledger.TryReserveDouble(remoteTx, remote, LedgerResourceKind.CoreEnergy, 7, remoteRev, out _, out _);
        TestAssert.AreEqual(hostCode, remoteCode);
        TestAssert.AreEqual(LedgerReserveCode.Ok, remoteCode);
        Commit(ledger, hostTx);
        Commit(ledger, remoteTx);

        ledger.TryGetDouble(host, LedgerResourceKind.CoreEnergy, out var hostAfter, out _);
        ledger.TryGetDouble(remote, LedgerResourceKind.CoreEnergy, out var remoteAfter, out _);
        TestAssert.AreEqual(hostAfter, remoteAfter, 1e-9);
    }

    [TestMethod]
    public void BaseAndPlayerAccountsNeverShareStock()
    {
        var ledger = NewLedger();
        var player = Player("p1");
        var baseOwner = BaseFor(BaseKey());
        ledger.SeedDouble(baseOwner, LedgerResourceKind.BaseEnergy, 100);
        ledger.SeedDouble(player, LedgerResourceKind.CoreEnergy, 100);

        ledger.TryGetDouble(baseOwner, LedgerResourceKind.BaseEnergy, out _, out var baseRev);
        var tx = Begin(ledger, 1);
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveDouble(tx, baseOwner, LedgerResourceKind.BaseEnergy, 40, baseRev, out _, out _));
        Commit(ledger, tx);

        ledger.TryGetDouble(player, LedgerResourceKind.CoreEnergy, out var mech, out _);
        ledger.TryGetDouble(baseOwner, LedgerResourceKind.BaseEnergy, out var baseBalance, out _);
        TestAssert.AreEqual(100, mech, 1e-9);
        TestAssert.AreEqual(60, baseBalance, 1e-9);
    }

    [TestMethod]
    public void RegistryAndLedgerShareThePersistentOwner()
    {
        // The A09 acceptance "重连不重置余额" across both halves: presence rebinds, stock stays.
        var registry = new HostPlayerRegistry();
        var ledger = NewLedger();
        registry.RegisterOrUpdate("p1", 2, HostPlayerRole.Remote, ConnA);
        var owner = Player("p1");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 7007, 12);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 7007, out _, out var rev0);
        var tx = Begin(ledger, 1, ConnA);
        ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 7007, 2, rev0, out _, out _);
        Commit(ledger, tx);

        TestAssert.IsTrue(registry.MarkOfflineBySession(2));
        registry.RegisterOrUpdate("p1", 9, HostPlayerRole.Remote, ConnB);
        ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 7007, out var kept, out _);
        TestAssert.AreEqual(10L, kept, "Presence went offline and back; the ledger never moved.");
    }

    [TestMethod]
    public void RandomRetrySequenceNeverSpendsTwice()
    {
        var random = new Random(0xA09);
        var ledger = new HostResourceLedger(Epoch, resultCapacity: 8);
        var owner = Player("fuzz");
        ledger.SeedLong(owner, LedgerResourceKind.InventoryItem, 8008, 100000);
        var spent = new Dictionary<long, int>();

        for (var step = 0; step < 2000; step++)
        {
            var sequence = random.Next(1, 60);
            var begin = ledger.BeginTransaction(Key(sequence), out var tx, out _);
            switch (begin)
            {
                case LedgerBeginResult.Begun:
                    ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 8008, out _, out var rev);
                    if (ledger.TryReserveLong(tx, owner, LedgerResourceKind.InventoryItem, 8008, 1, rev, out _, out _) ==
                        LedgerReserveCode.Ok)
                    {
                        spent[sequence] = spent.TryGetValue(sequence, out var count) ? count + 1 : 1;
                    }
                    ledger.CommitTransaction(tx, CommandResultCode.Applied, step, out _);
                    break;
                case LedgerBeginResult.Duplicate:
                case LedgerBeginResult.TooOld:
                    break;
                default:
                    TestAssert.Fail("Unexpected begin " + begin + " at step " + step);
                    break;
            }
        }

        var repeated = new List<long>();
        foreach (var entry in spent)
        {
            if (entry.Value != 1) repeated.Add(entry.Key);
        }
        TestAssert.IsEmpty(repeated, "Retried sequences spent more than once: " + string.Join(",", repeated));
    }

    [TestMethod]
    public void TheFieldTableCoversEveryLedgerKindAndStaysOpen()
    {
        foreach (LedgerResourceKind kind in Enum.GetValues(typeof(LedgerResourceKind)))
        {
            if (kind == LedgerResourceKind.Unknown) continue;
            TestAssert.IsTrue(HostResourceFields.TryGet(kind, out var record), "Missing writer row for " + kind);
            TestAssert.IsFalse(record.IsClosed, kind + " must stay open until A10 migrates it.");
            TestAssert.IsFalse(string.IsNullOrEmpty(record.Blocker), kind + " must name its owning card.");
            TestAssert.IsFalse(HostResourceFields.IsClosed(kind));
        }

        // Owner-kind column agrees with the key: a player energy key is valid, a base energy key
        // under a player owner is not.
        var player = Player("p");
        var baseOwner = BaseFor(BaseKey());
        TestAssert.IsTrue(new LedgerResourceKey(player, LedgerResourceKind.CoreEnergy, 0).IsValid);
        TestAssert.IsFalse(new LedgerResourceKey(player, LedgerResourceKind.BaseEnergy, 0).IsValid);
        TestAssert.IsTrue(new LedgerResourceKey(baseOwner, LedgerResourceKind.BaseEnergy, 0).IsValid);
        TestAssert.IsFalse(new LedgerResourceKey(baseOwner, LedgerResourceKind.CoreEnergy, 0).IsValid);
        TestAssert.IsTrue(new LedgerResourceKey(player, LedgerResourceKind.DroneSlot, 0).IsValid);
        TestAssert.IsTrue(new LedgerResourceKey(baseOwner, LedgerResourceKind.DroneSlot, 0).IsValid);
        TestAssert.IsFalse(new LedgerResourceKey(player, LedgerResourceKind.InventoryItem, 0).IsValid);
    }

    [TestMethod]
    public void TheLedgerModelReferencesNoEngineTypes()
    {
        // Mirrors A02's model-purity gate for the three new types: a pure-model file that starts
        // referencing game or Unity types fails here instead of leaking into tests later.
        var assembly = typeof(LedgerOwner).Assembly;
        var offenders = new List<string>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.Namespace != "NebulaModel.Authority") continue;
            if (type.Name != nameof(HostPlayerRegistry) && type.Name != nameof(HostPlayerState) &&
                type.Name != nameof(HostResourceLedger) && type.Name != "HostResourceFields" &&
                type.Name != nameof(LedgerOwner) && type.Name != "LedgerResourceKey" &&
                type.Name != "HostTransactionId" && type.Name != "HostPlayerPose")
            {
                continue;
            }
            foreach (var member in type.GetMembers())
            {
                string name = string.Empty;
                if (member is System.Reflection.FieldInfo field) name = field.FieldType.FullName ?? string.Empty;
                else if (member is System.Reflection.PropertyInfo property) name = property.PropertyType.FullName ?? string.Empty;
                else if (member is System.Reflection.MethodInfo method)
                {
                    foreach (var parameter in method.GetParameters())
                    {
                        var full = parameter.ParameterType.FullName ?? "";
                        if (full.Contains("UnityEngine") || full.Contains("GameMain") || full.Contains("NebulaWorld") ||
                            full.Contains("NebulaNetwork"))
                        {
                            offenders.Add(type.Name + "." + method.Name + "(" + full + ")");
                        }
                    }
                    continue;
                }
                else continue;
                if (name.Contains("UnityEngine") || name.Contains("GameMain") ||
                    name.Contains("NebulaWorld") || name.Contains("NebulaNetwork"))
                {
                    offenders.Add(type.Name + "." + member.Name + ": " + name);
                }
            }
        }
        TestAssert.IsEmpty(offenders, "Engine references in the A09 model: " + string.Join("; ", offenders));
    }
}
