using System;
using System.Text;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A21: the versioned save sidecar, its load-time decision, the one-time reclaim rule, and the
/// session lifecycle rules that keep an unconfirmed command from re-executing in a new epoch.
/// </summary>
/// <remarks>
/// TASKS.md A21 acceptance, as exercised here without a game process: a save→restart round trip
/// restores balances and capacities without copying them, no <c>repairerCount</c> ghost survives a
/// reload, an unknown sidecar schema is refused with an explanation, and old-epoch packets and
/// queued commands are dead after a reload.
/// </remarks>
[TestClass]
public class AuthoritySaveTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA21BEEF123456789, 0x0011223344556677);
    private static readonly AuthorityEpoch NextEpoch = new(0x7766554433221100, 0xAABBCCDDEEFF0011);

    private const string WorldId = "0123456789abcdef0123456789abcdef";

    private static AuthoritySaveState SampleState()
    {
        var state = new AuthoritySaveState
        {
            Schema = AuthoritySidecarSchema.Current,
            WorldId = WorldId,
            SavedEpoch = Epoch,
            SavedHostTick = 4200
        };
        state.Players.Add(new AuthoritySavePlayer
        {
            PersistentId = "host-hash",
            Role = HostPlayerRole.LocalHost,
            PlanetId = 101,
            StarId = 1,
            IsAlive = true,
            RepairEnabled = true,
            BuildEnabled = true,
            DroneTotal = 10
        });
        state.Players.Add(new AuthoritySavePlayer
        {
            PersistentId = "remote-hash",
            Role = HostPlayerRole.Remote,
            PlanetId = 102,
            StarId = 2,
            IsAlive = false,
            DroneTotal = 4
        });
        state.Accounts.Add(new AuthoritySaveAccount
        {
            OwnerKind = LedgerOwnerKind.Player,
            PersistentId = "host-hash",
            ResourceKind = LedgerResourceKind.InventoryItem,
            ItemId = 1207,
            IsDouble = false,
            LongBalance = 42
        });
        state.Accounts.Add(new AuthoritySaveAccount
        {
            OwnerKind = LedgerOwnerKind.BattleBase,
            BaseScope = 101,
            BaseNativeId = 9,
            BaseGeneration = 1,
            ResourceKind = LedgerResourceKind.BaseStockItem,
            ItemId = 1803,
            IsDouble = false,
            LongBalance = 7
        });
        state.Accounts.Add(new AuthoritySaveAccount
        {
            OwnerKind = LedgerOwnerKind.Player,
            PersistentId = "host-hash",
            ResourceKind = LedgerResourceKind.CoreEnergy,
            IsDouble = true,
            DoubleBalance = 123.5
        });
        state.Budgets.Add(new AuthoritySaveBudget
        {
            OwnerKind = ConstructionOwnerKind.Player,
            PersistentId = "host-hash",
            Total = 10
        });
        state.Budgets.Add(new AuthoritySaveBudget
        {
            OwnerKind = ConstructionOwnerKind.BattleBase,
            BaseScope = 101,
            BaseNativeId = 9,
            BaseGeneration = 1,
            Total = 6
        });
        state.Tasks.Add(new AuthoritySaveTask
        {
            Sequence = 3,
            OwnerKind = ConstructionOwnerKind.Player,
            PersistentId = "host-hash",
            TargetKind = PoolKind.Entity,
            TargetScope = 101,
            TargetNativeId = 55,
            TargetGeneration = 2,
            TaskKind = ConstructionTaskKind.Repair,
            Stage = ConstructionTaskStage.Working,
            Revision = 4,
            LastHostTick = 4100
        });
        // A base-owned task whose budget is seeded: a naive restore could resurrect it, which the
        // reclaim rule must refuse.
        state.Tasks.Add(new AuthoritySaveTask
        {
            Sequence = 4,
            OwnerKind = ConstructionOwnerKind.BattleBase,
            BaseScope = 101,
            BaseNativeId = 9,
            BaseGeneration = 1,
            TargetKind = PoolKind.Entity,
            TargetScope = 101,
            TargetNativeId = 77,
            TargetGeneration = 1,
            TaskKind = ConstructionTaskKind.Repair,
            Stage = ConstructionTaskStage.Travelling,
            Revision = 2,
            LastHostTick = 4150
        });
        return state;
    }

    // ---------------------------------------------------------------- codec

    [TestMethod]
    public void AnEncodedSidecarRoundTripsItsRecords()
    {
        var state = SampleState();
        var bytes = AuthoritySaveCodec.Encode(state);
        TestAssert.IsNotNull(bytes);

        var decoded = AuthoritySaveCodec.TryDecode(bytes, out var back, out var reason);
        TestAssert.IsTrue(decoded, reason);
        TestAssert.AreEqual(AuthoritySidecarSchema.Current, back.Schema);
        TestAssert.AreEqual(WorldId, back.WorldId);
        TestAssert.AreEqual(Epoch, back.SavedEpoch);
        TestAssert.AreEqual(4200L, back.SavedHostTick);
        TestAssert.HasCount(2, back.Players);
        TestAssert.HasCount(3, back.Accounts);
        TestAssert.HasCount(2, back.Budgets);
        TestAssert.HasCount(2, back.Tasks);

        TestAssert.AreEqual("host-hash", back.Players[0].PersistentId);
        TestAssert.AreEqual(HostPlayerRole.LocalHost, back.Players[0].Role);
        TestAssert.AreEqual(10, back.Players[0].DroneTotal);
        TestAssert.IsFalse(back.Players[1].IsAlive);

        TestAssert.AreEqual(LedgerOwnerKind.Player, back.Accounts[0].OwnerKind);
        TestAssert.AreEqual(42L, back.Accounts[0].LongBalance);
        TestAssert.AreEqual(1803, back.Accounts[1].ItemId);
        TestAssert.AreEqual(7L, back.Accounts[1].LongBalance);
        TestAssert.IsTrue(back.Accounts[2].IsDouble);
        TestAssert.AreEqual(123.5, back.Accounts[2].DoubleBalance, 1e-9);
        TestAssert.AreEqual(9, back.Accounts[1].BaseNativeId);

        TestAssert.AreEqual(ConstructionOwnerKind.BattleBase, back.Budgets[1].OwnerKind);
        TestAssert.AreEqual(6, back.Budgets[1].Total);

        TestAssert.AreEqual(3L, back.Tasks[0].Sequence);
        TestAssert.AreEqual(ConstructionTaskStage.Working, back.Tasks[0].Stage);
        TestAssert.AreEqual(PoolKind.Entity, back.Tasks[0].TargetKind);
        TestAssert.AreEqual(2, back.LiveTaskCount);
    }

    [TestMethod]
    public void ADecodeRejectsAWrongMagic()
    {
        var bytes = AuthoritySaveCodec.Encode(SampleState());
        bytes[0] = (byte)'X';
        var decoded = AuthoritySaveCodec.TryDecode(bytes, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsTrue(reason.Contains("magic"), reason);
    }

    [TestMethod]
    public void ADecodeRefusesAnUnknownSchemaWithAnExplanation()
    {
        // A future build writes schema 2. This build must refuse it and say why, never guess.
        var bytes = AuthoritySaveCodec.Encode(SampleState());
        Array.Copy(BitConverter.GetBytes(2), 0, bytes, 4, 4);
        var decoded = AuthoritySaveCodec.TryDecode(bytes, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsTrue(reason.Contains("schema 2") && reason.Contains("refusing"), reason);
    }

    [TestMethod]
    public void ADecodeRefusesACountAboveItsCeiling()
    {
        var writer = new AuthorityPayloadWriter();
        writer.TryWriteString(WorldId, AuthoritySidecarLimits.WorldIdMaxBytes);
        writer.WriteRaw(Epoch.ToBytes());
        writer.WriteLong(0);
        writer.WriteInt(0); // players
        writer.WriteInt(AuthoritySidecarLimits.AccountsMax + 1); // accounts: above the ceiling
        var file = BuildSidecarFile(writer.ToArray());
        var decoded = AuthoritySaveCodec.TryDecode(file, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsTrue(reason.Contains("ceiling"), reason);
    }

    [TestMethod]
    public void ADecodeRefusesAnUndefinedEnumByte()
    {
        var writer = new AuthorityPayloadWriter();
        writer.TryWriteString(WorldId, AuthoritySidecarLimits.WorldIdMaxBytes);
        writer.WriteRaw(Epoch.ToBytes());
        writer.WriteLong(0);
        writer.WriteInt(1); // players
        writer.TryWriteString("p", AuthoritySidecarLimits.PersistentIdMaxBytes);
        writer.WriteByte(200); // role: not a defined HostPlayerRole
        writer.WriteInt(0);
        writer.WriteInt(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteByte(0);
        writer.WriteInt(0);
        writer.WriteInt(0); // accounts
        writer.WriteInt(0); // budgets
        writer.WriteInt(0); // tasks
        var file = BuildSidecarFile(writer.ToArray());
        var decoded = AuthoritySaveCodec.TryDecode(file, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsTrue(reason.Contains("player record 0"), reason);
    }

    [TestMethod]
    public void ADecodeRefusesATruncatedRecordBlock()
    {
        var bytes = AuthoritySaveCodec.Encode(SampleState());
        var truncated = new byte[bytes.Length - 5];
        Array.Copy(bytes, truncated, truncated.Length);
        var decoded = AuthoritySaveCodec.TryDecode(truncated, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsNotNull(reason);
    }

    [TestMethod]
    public void ADecodeRefusesTrailingBytes()
    {
        var bytes = AuthoritySaveCodec.Encode(SampleState());
        var padded = new byte[bytes.Length + 3];
        Array.Copy(bytes, padded, bytes.Length);
        var decoded = AuthoritySaveCodec.TryDecode(padded, out _, out var reason);
        TestAssert.IsFalse(decoded);
        TestAssert.IsTrue(reason.Contains("trailing"), reason);
    }

    private static byte[] BuildSidecarFile(byte[] payload)
    {
        var file = new byte[8 + payload.Length];
        Encoding.ASCII.GetBytes(AuthoritySaveCodec.Magic, 0, 4, file, 0);
        Array.Copy(BitConverter.GetBytes(AuthoritySidecarSchema.Current), 0, file, 4, 4);
        Array.Copy(payload, 0, file, 8, payload.Length);
        return file;
    }

    // ---------------------------------------------------------------- policy

    [TestMethod]
    public void LegacyModeIgnoresTheSidecar()
    {
        var decision = AuthoritySavePolicy.Decide(authorityMode: false, sidecarExists: true, decoded: true,
            decodeReason: null, sidecarWorldId: WorldId, saveWorldId: WorldId, out var reason);
        TestAssert.AreEqual(AuthoritySidecarDecision.None, decision);
        TestAssert.IsNotNull(reason);
    }

    [TestMethod]
    public void AuthorityModeWithoutASidecarIsALegacyMigration()
    {
        var decision = AuthoritySavePolicy.Decide(authorityMode: true, sidecarExists: false, decoded: false,
            decodeReason: null, sidecarWorldId: null, saveWorldId: WorldId, out var reason);
        TestAssert.AreEqual(AuthoritySidecarDecision.LegacyMigration, decision);
        TestAssert.IsTrue(reason.Contains("migration"), reason);
    }

    [TestMethod]
    public void AnUnreadableSidecarIsRefusedWithTheCodecReason()
    {
        var decision = AuthoritySavePolicy.Decide(authorityMode: true, sidecarExists: true, decoded: false,
            decodeReason: "unknown sidecar schema 99 (this build supports schema 1)",
            sidecarWorldId: null, saveWorldId: WorldId, out var reason);
        TestAssert.AreEqual(AuthoritySidecarDecision.Refused, decision);
        TestAssert.IsTrue(reason.Contains("schema 99"), reason);
    }

    [TestMethod]
    public void ASidecarFromAnotherWorldIsRefused()
    {
        var decision = AuthoritySavePolicy.Decide(authorityMode: true, sidecarExists: true, decoded: true,
            decodeReason: null, sidecarWorldId: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            saveWorldId: WorldId, out var reason);
        TestAssert.AreEqual(AuthoritySidecarDecision.Refused, decision);
        TestAssert.IsTrue(reason.Contains("belongs to world"), reason);
    }

    // ---------------------------------------------------------------- restore

    [TestMethod]
    public void RestoreSeedsBalancesUnderTheNewEpoch()
    {
        var state = SampleState();
        var ledger = new HostResourceLedger(NextEpoch);
        var tasks = new ConstructionTaskLedger(NextEpoch);
        var players = new HostPlayerRegistry();

        var report = AuthoritySaveRestore.Apply(state, NextEpoch, ledger, tasks, players);
        TestAssert.IsTrue(report.Succeeded, report.Error ?? "");
        TestAssert.AreEqual(3, report.SeededAccounts);
        TestAssert.AreEqual(2, report.SeededPlayers);

        // Player balances re-key by their durable id under the new epoch.
        var hostOwner = LedgerOwner.ForPlayer("host-hash");
        TestAssert.IsTrue(ledger.TryGetLong(hostOwner, LedgerResourceKind.InventoryItem, 1207,
            out var items, out _));
        TestAssert.AreEqual(42L, items);
        TestAssert.IsTrue(ledger.TryGetDouble(hostOwner, LedgerResourceKind.CoreEnergy,
            out var energy, out _));
        TestAssert.AreEqual(123.5, energy, 1e-9);

        // Base balances re-key under the new epoch at the saved slot identity.
        var baseKey = ObjectKey.Create(NextEpoch, PoolKind.Base, 101, 9, 1);
        TestAssert.IsTrue(ledger.TryGetLong(LedgerOwner.ForBase(baseKey), LedgerResourceKind.BaseStockItem,
            1803, out var stock, out _));
        TestAssert.AreEqual(7L, stock);
    }

    [TestMethod]
    public void RestoreLeavesNoGhostOccupancyAndReclaimsTasksOnce()
    {
        var state = SampleState();
        var ledger = new HostResourceLedger(NextEpoch);
        var tasks = new ConstructionTaskLedger(NextEpoch);
        var players = new HostPlayerRegistry();

        var report = AuthoritySaveRestore.Apply(state, NextEpoch, ledger, tasks, players);
        TestAssert.IsTrue(report.Succeeded, report.Error ?? "");
        TestAssert.AreEqual(2, report.ReclaimedTasks, "both saved live tasks are reclaimed, not restored");
        TestAssert.AreEqual(2, report.ReclaimedSlots);
        TestAssert.AreEqual(0, tasks.ActiveTaskCount, "no task survives the reload as live work");

        // The base budget restores at capacity with every slot idle: no repairerCount ghost.
        var baseOwner = ConstructionOwnerKey.ForBase(ObjectKey.Create(NextEpoch, PoolKind.Base, 101, 9, 1));
        TestAssert.IsTrue(tasks.TryGetBudget(baseOwner, out var budget));
        TestAssert.AreEqual(6, budget.Total);
        TestAssert.AreEqual(6, budget.Idle);
        TestAssert.AreEqual(0, budget.Occupied);
        TestAssert.IsTrue(budget.CheckInvariant());

        // The base budget seeds at capacity, all idle; the player budget is deferred to rejoin
        // (no seat exists at load), counted, not lost.
        TestAssert.AreEqual(1, report.SeededBudgets);
        TestAssert.AreEqual(1, report.SkippedPlayerBudgets);
    }

    [TestMethod]
    public void RestoreSeedsSavedPlayersOffline()
    {
        var state = SampleState();
        var ledger = new HostResourceLedger(NextEpoch);
        var tasks = new ConstructionTaskLedger(NextEpoch);
        var players = new HostPlayerRegistry();

        var report = AuthoritySaveRestore.Apply(state, NextEpoch, ledger, tasks, players);
        TestAssert.IsTrue(report.Succeeded, report.Error ?? "");

        TestAssert.IsTrue(players.TryGetByPersistent("remote-hash", out var remote));
        TestAssert.IsFalse(remote.IsOnline, "a restored player is offline until they rejoin");
        TestAssert.IsFalse(remote.CanOwnDroneTask);
        TestAssert.IsFalse(remote.CanOwnCombat);
        TestAssert.AreEqual(4, remote.DroneTotal, "the saved capacity waits for the rejoin");
        TestAssert.AreEqual(102, remote.PlanetId);
    }

    [TestMethod]
    public void ARestoreWithOneBadRecordSeedsNothing()
    {
        var state = SampleState();
        state.Accounts.Add(new AuthoritySaveAccount
        {
            OwnerKind = LedgerOwnerKind.Player,
            PersistentId = "host-hash",
            ResourceKind = LedgerResourceKind.InventoryItem,
            ItemId = 1207,
            IsDouble = false,
            LongBalance = -5 // a negative balance must abort the whole restore
        });
        var ledger = new HostResourceLedger(NextEpoch);
        var tasks = new ConstructionTaskLedger(NextEpoch);
        var players = new HostPlayerRegistry();

        var report = AuthoritySaveRestore.Apply(state, NextEpoch, ledger, tasks, players);
        TestAssert.IsFalse(report.Succeeded);
        TestAssert.AreEqual(0, report.SeededAccounts);
        TestAssert.AreEqual(0, report.SeededPlayers);
        TestAssert.AreEqual(0, ledger.LedgerRevision, "a refused sidecar must not half-populate the world");
        TestAssert.IsFalse(ledger.TryGetLong(LedgerOwner.ForPlayer("host-hash"),
            LedgerResourceKind.InventoryItem, 1207, out _, out _));
    }

    [TestMethod]
    public void ARestoredBalanceSpendsExactlyOnceUnderTheNewEpoch()
    {
        var state = SampleState();
        var ledger = new HostResourceLedger(NextEpoch);
        var tasks = new ConstructionTaskLedger(NextEpoch);
        var players = new HostPlayerRegistry();
        TestAssert.IsTrue(AuthoritySaveRestore.Apply(state, NextEpoch, ledger, tasks, players).Succeeded);

        var connection = new ConnectionEpoch(1);
        var key = new CommandKey(NextEpoch, connection, 1);
        var owner = LedgerOwner.ForPlayer("host-hash");
        TestAssert.AreEqual(LedgerBeginResult.Begun,
            ledger.BeginTransaction(key, out var transaction, out _));
        TestAssert.AreEqual(LedgerReserveCode.Ok,
            ledger.TryReserveLong(transaction, owner, LedgerResourceKind.InventoryItem, 1207, 10,
                expectedRevision: 1, out _, out var newRevision));
        TestAssert.IsTrue(ledger.CommitTransaction(transaction, CommandResultCode.Applied, 10, out var outcome));

        // The client retries the same command after the reload; the restored ledger answers from
        // its window instead of spending a second time.
        TestAssert.AreEqual(LedgerBeginResult.Duplicate,
            ledger.BeginTransaction(key, out _, out var cached));
        TestAssert.AreEqual(outcome.TransactionId, cached.TransactionId);
        TestAssert.IsTrue(ledger.TryGetLong(owner, LedgerResourceKind.InventoryItem, 1207,
            out var remaining, out var finalRevision));
        TestAssert.AreEqual(32L, remaining);
        TestAssert.AreEqual(newRevision, finalRevision);
    }

    // ---------------------------------------------------------------- capture

    [TestMethod]
    public void AHostCaptureEncodesAndDecodesBackToTheSameFacts()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var connection = session.AssignConnectionEpoch(2);
        session.HostPlayers.RegisterOrUpdate("host-hash", 2, HostPlayerRole.LocalHost, connection);
        var owner = LedgerOwner.ForPlayer("host-hash");
        session.HostLedger.SeedLong(owner, LedgerResourceKind.InventoryItem, 1207, 42);
        var baseOwner = ConstructionOwnerKey.ForBase(ObjectKey.Create(Epoch, PoolKind.Base, 101, 9, 1));
        session.HostConstructionLedger.EnsureBudget(baseOwner, 6);
        var repairOwner = ConstructionOwnerKey.ForPlayer("host-hash", 2);
        session.HostConstructionLedger.EnsureBudget(repairOwner, 10);
        TestAssert.AreEqual(ConstructionTaskError.None,
            session.HostConstructionLedger.TryAddTask(repairOwner,
                ObjectKey.Create(Epoch, PoolKind.Entity, 101, 55, 2), ConstructionTaskKind.Repair,
                hostTick: 100, transaction: default, out _));

        var state = AuthoritySaveAdapter.Capture(session, WorldId);
        TestAssert.IsNotNull(state);
        TestAssert.AreEqual(Epoch, state.SavedEpoch);
        TestAssert.HasCount(1, state.Players);
        TestAssert.HasCount(1, state.Accounts);
        TestAssert.HasCount(2, state.Budgets);
        TestAssert.HasCount(1, state.Tasks);

        var decoded = AuthoritySaveCodec.TryDecode(AuthoritySaveCodec.Encode(state), out var back, out var reason);
        TestAssert.IsTrue(decoded, reason);
        TestAssert.AreEqual(Epoch, back.SavedEpoch);
        TestAssert.HasCount(1, back.Tasks);
        TestAssert.AreEqual(42L, back.Accounts[0].LongBalance);
        session.Dispose();
    }

    [TestMethod]
    public void CaptureProducesNothingForAClientOrAWorldlessSession()
    {
        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(AuthoritySaveAdapter.Capture(client, WorldId));
        client.Dispose();

        var idle = new AuthoritySession(new AuthoritySessionState());
        TestAssert.IsNull(AuthoritySaveAdapter.Capture(idle, WorldId));
        idle.Dispose();
    }

    // ---------------------------------------------------------------- session lifecycle

    [TestMethod]
    public void AQueuedCommandFromTheOldEpochIsNeverExecutedAfterAWorldReload()
    {
        // The host saved mid-combat and loaded a different save with a command still queued and
        // unconfirmed. The reload must drop it, not execute it against the new world.
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var connection = session.AssignConnectionEpoch(2);
        var key = new CommandKey(Epoch, connection, 1);
        var header = new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
            connection, 1, hostTick: 10, claimedPlayerId: 2, payloadLength: 0);
        var packet = AuthorityCommandPacket.Create(header,
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1), category: 1, payload: Array.Empty<byte>());
        TestAssert.IsTrue(session.TryEnqueueHostCommand(key, packet, 2, connectionId: 1, enqueuedTick: 10));
        TestAssert.AreEqual(1, session.Commands.Count);

        session.BeginAuthorityWorld(NextEpoch, isHost: true);
        var executor = new CountingExecutor();
        session.Commands.Drain(executor, null);
        TestAssert.HasCount(0, executor.Executed,
            "an unconfirmed command from the previous world must not re-execute in the new one");
        session.Dispose();
    }

    [TestMethod]
    public void ACommandKeyFromTheOldEpochIsRejectedInTheNewWorld()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        var oldConnection = session.AssignConnectionEpoch(2);
        var owner = LedgerOwner.ForPlayer("p1");
        session.HostLedger.SeedLong(owner, LedgerResourceKind.InventoryItem, 1207, 5);

        session.BeginAuthorityWorld(NextEpoch, isHost: true);
        var oldKey = new CommandKey(Epoch, oldConnection, 1);
        TestAssert.AreEqual(LedgerBeginResult.WrongEpoch,
            session.HostLedger.BeginTransaction(oldKey, out _, out _),
            "a saved-epoch command key must never open a transaction in the reloaded world");
        session.Dispose();
    }

    [TestMethod]
    public void AReloadedHostMintsFreshConnectionEpochsOnRestoredBalances()
    {
        var first = new AuthoritySession(new AuthoritySessionState());
        first.BeginAuthorityWorld(Epoch, isHost: true);
        var firstConnection = first.AssignConnectionEpoch(2);
        first.HostPlayers.RegisterOrUpdate("host-hash", 2, HostPlayerRole.LocalHost, firstConnection);
        first.HostLedger.SeedLong(LedgerOwner.ForPlayer("host-hash"), LedgerResourceKind.InventoryItem,
            1207, 42);
        var captured = AuthoritySaveAdapter.Capture(first, WorldId);
        first.Dispose();

        // The host restarts and restores the sidecar under the new epoch.
        var second = new AuthoritySession(new AuthoritySessionState());
        second.BeginAuthorityWorld(NextEpoch, isHost: true);
        TestAssert.IsTrue(AuthoritySaveAdapter.ApplyRestore(second, captured, out var report));
        TestAssert.IsTrue(report.Succeeded, report.Error ?? "");
        TestAssert.AreEqual(1, report.SeededAccounts);

        // Connection epochs restart in the new world; the dedup space is fresh per epoch.
        var reconnection = second.AssignConnectionEpoch(2);
        TestAssert.AreEqual(1UL, reconnection.Value);
        TestAssert.IsTrue(second.HostPlayers.RegisterOrUpdate("host-hash", 2, HostPlayerRole.LocalHost,
            reconnection).IsOnline);

        // The rejoining player keeps the saved stock instead of being reset.
        TestAssert.IsTrue(second.HostLedger.TryGetLong(LedgerOwner.ForPlayer("host-hash"),
            LedgerResourceKind.InventoryItem, 1207, out var balance, out _));
        TestAssert.AreEqual(42L, balance);

        // The saved epoch's keys are dead in this world even though slot numbers repeat.
        var savedEpochKey = new CommandKey(Epoch, reconnection, 1);
        TestAssert.AreEqual(LedgerBeginResult.WrongEpoch,
            second.HostLedger.BeginTransaction(savedEpochKey, out _, out _));
        second.Dispose();
    }

    private sealed class CountingExecutor : IHostCommandExecutor
    {
        public readonly System.Collections.Generic.List<CommandKey> Executed = [];

        public CommandOutcome Execute(in QueuedHostCommand command)
        {
            Executed.Add(command.Key);
            return new CommandOutcome(CommandResultCode.Applied, appliedHostTick: 1, transactionId: 1);
        }
    }
}
