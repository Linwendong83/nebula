using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A11: per-player host combat validation, cooldowns, continuous fire, death and respawn.
/// </summary>
/// <remarks>
/// <para>
/// Every test drives <see cref="HostPlayerSimulation"/> with a registry and a ledger, the same two
/// objects the executor wires in production. Target truth comes from a fake
/// <see cref="IPlayerCombatTargetRules"/>; the game adapter replaces it without changing any
/// assertion here.
/// </para>
/// </remarks>
[TestClass]
public class HostPlayerSimulationTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA11A11A11A11A11A, 0xC0FFEE11C0FFEE11);
    private static readonly ConnectionEpoch ConnA = new(31);
    private static readonly ConnectionEpoch ConnB = new(32);

    private sealed class AllowAllTargets : IPlayerCombatTargetRules
    {
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

    private sealed class OutOfRangeTargets : IPlayerCombatTargetRules
    {
        public bool IsTargetKnown(in ObjectKey target, out string reason)
        {
            reason = null;
            return true;
        }

        public bool IsInRange(string persistentId, in ObjectKey target, PlayerWeaponKind weapon,
            out string reason)
        {
            reason = "too-far";
            return false;
        }
    }

    private sealed class Harness
    {
        public Harness()
        {
            Registry = new HostPlayerRegistry();
            Ledger = new HostResourceLedger(Epoch);
            Simulation = new HostPlayerSimulation();
            Rules = new AllowAllTargets();
            Costs = PlayerCombatCosts.Default;
        }

        public HostPlayerRegistry Registry { get; }

        public HostResourceLedger Ledger { get; }

        public HostPlayerSimulation Simulation { get; }

        public IPlayerCombatTargetRules Rules { get; set; }

        public PlayerCombatCosts Costs { get; }

        public const long HostTick = 10000;

        public void AddPlayer(string persistentId, ushort sessionId, ConnectionEpoch connection,
            HostPlayerRole role = HostPlayerRole.Remote)
        {
            Registry.RegisterOrUpdate(persistentId, sessionId, role, connection);
            Simulation.EnsureOwner(persistentId, HostPlayerSimulation.DefaultMaxHp);
        }

        public void SeedAmmo(string persistentId, int itemId, long count)
        {
            Ledger.SeedLong(LedgerOwner.ForPlayer(persistentId), LedgerResourceKind.AmmoBullet, itemId, count);
        }

        public void SeedEnergy(string persistentId, double amount)
        {
            Ledger.SeedDouble(LedgerOwner.ForPlayer(persistentId), LedgerResourceKind.CoreEnergy, amount);
        }

        public void SeedBombs(string persistentId, int protoId, long count)
        {
            Ledger.SeedLong(LedgerOwner.ForPlayer(persistentId), LedgerResourceKind.BombStorageItem, protoId, count);
        }

        public ObjectKey Target(int nativeId = 7, long generation = 1) =>
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

        public long RevisionOf(string persistentId, LedgerResourceKind kind, int itemId = 0)
        {
            var key = new LedgerResourceKey(LedgerOwner.ForPlayer(persistentId), kind, itemId);
            return Ledger.RevisionOf(key);
        }

        public PlayerCombatPlan Fire(string persistentId, ushort sessionId, int ammoItemId,
            long hostTick = HostTick, long? revision = null)
        {
            var rev = revision ?? RevisionOf(persistentId, LedgerResourceKind.AmmoBullet, ammoItemId);
            var request = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
                ammoItemId, hostTick, rev, 0, 0);
            return Simulation.Validate(Registry, Ledger, sessionId, request, Target(), true,
                hostTick, Costs, Rules, commandSequence: hostTick);
        }
    }

    [TestMethod]
    public void AGunShotValidatesAgainstHostTruth()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);

        var plan = harness.Fire("p1", 2, 7001);
        TestAssert.IsTrue(plan.Accepted, "Reason: " + plan.Reason);
        TestAssert.AreEqual(CommandResultCode.Applied, plan.Code);
        TestAssert.HasCount(1, plan.Ops);
    }

    [TestMethod]
    public void TwoOwnersFireAtDifferentTargetsWithoutCrosstalk()
    {
        // The acceptance case: two players aim at different objects on the same tick. Cooldowns,
        // balances and targets are per-owner; neither intent sees the other's state.
        var harness = new Harness();
        harness.AddPlayer("host", 1, ConnA, HostPlayerRole.LocalHost);
        harness.AddPlayer("remote", 2, ConnB, HostPlayerRole.Remote);
        harness.SeedAmmo("host", 7001, 10);
        harness.SeedAmmo("remote", 7001, 10);

        var hostTarget = harness.Target(nativeId: 7);
        var remoteTarget = harness.Target(nativeId: 9);
        var hostRequest = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, harness.RevisionOf("host", LedgerResourceKind.AmmoBullet, 7001), 0, 0);
        var remoteRequest = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, harness.RevisionOf("remote", LedgerResourceKind.AmmoBullet, 7001), 0, 0);

        var hostPlan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 1, hostRequest,
            hostTarget, true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        var remotePlan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, remoteRequest,
            remoteTarget, true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        TestAssert.IsTrue(hostPlan.Accepted, "Host reason: " + hostPlan.Reason);
        TestAssert.IsTrue(remotePlan.Accepted, "Remote reason: " + remotePlan.Reason);
        TestAssert.AreNotEqual(hostPlan.Target, remotePlan.Target);

        // Applying one owner's cooldown leaves the other ready on the same tick.
        harness.Simulation.Apply(harness.Simulation.EnsureOwner("host", 1000), hostRequest,
            hostTarget, true, Harness.HostTick, harness.Costs);
        var remoteAgain = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, remoteRequest,
            remoteTarget, true, Harness.HostTick, harness.Costs, harness.Rules, 2);
        TestAssert.IsTrue(remoteAgain.Accepted, "Remote must not inherit the host cooldown.");
    }

    [TestMethod]
    public void ASecondPullDuringCooldownIsRefused()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);

        var plan = harness.Fire("p1", 2, 7001);
        TestAssert.IsTrue(plan.Accepted);
        harness.Simulation.Apply(harness.Simulation.EnsureOwner("p1", 1000),
            new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss, 7001,
                Harness.HostTick, 0, 0, 0),
            harness.Target(), true, Harness.HostTick, harness.Costs);

        var retry = harness.Fire("p1", 2, 7001, hostTick: Harness.HostTick + 1);
        TestAssert.IsFalse(retry.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedStale, retry.Code);
        TestAssert.AreEqual(PlayerCombatRejectReason.Cooldown, retry.Reason);

        var after = harness.Fire("p1", 2, 7001,
            hostTick: Harness.HostTick + harness.Costs.CooldownTicksPerShot);
        TestAssert.IsTrue(after.Accepted, "Cooldown expiry must re-arm the trigger.");
    }

    [TestMethod]
    public void ExpiredAndFutureInputsAreRefused()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);
        var live = harness.RevisionOf("p1", LedgerResourceKind.AmmoBullet, 7001);

        // The client saw tick T but the host is now past T + max age: the hint expired.
        var ancient = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, live, 0, 0);
        var ancientPlan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, ancient,
            harness.Target(), true, Harness.HostTick + harness.Costs.MaxInputAgeTicks + 1,
            harness.Costs, harness.Rules, 1);
        TestAssert.IsFalse(ancientPlan.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedStale, ancientPlan.Code);
        TestAssert.AreEqual(PlayerCombatRejectReason.ExpiredInput, ancientPlan.Reason);

        // The client claims a tick ahead of the host: invalid, never ordered by it.
        var future = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick + harness.Costs.FutureToleranceTicks + 1, live, 0, 0);
        var futurePlan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, future,
            harness.Target(), true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        TestAssert.IsFalse(futurePlan.Accepted);
        TestAssert.AreEqual(PlayerCombatRejectReason.FutureInput, futurePlan.Reason);
    }

    [TestMethod]
    public void ADeadOwnerCannotFireButMayRespawn()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);

        var kill = harness.Simulation.ApplyHostDamage("p1", HostPlayerSimulation.DefaultMaxHp, Harness.HostTick);
        TestAssert.IsTrue(kill.Applied);
        TestAssert.IsTrue(kill.Died);

        var fire = harness.Fire("p1", 2, 7001);
        TestAssert.IsFalse(fire.Accepted);
        TestAssert.AreEqual(PlayerCombatRejectReason.NotAlive, fire.Reason);

        var respawn = new PlayerCombatRequest(PlayerCombatAction.Respawn, PlayerWeaponKind.Shield,
            0, Harness.HostTick, 0, 0, 0);
        var plan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, respawn,
            default, false, Harness.HostTick, harness.Costs, harness.Rules, 2);
        TestAssert.IsTrue(plan.Accepted, "Reason: " + plan.Reason);
        harness.Simulation.Apply(harness.Simulation.EnsureOwner("p1", 1000), respawn,
            default, false, Harness.HostTick, harness.Costs);
        TestAssert.IsTrue(harness.Simulation.EnsureOwner("p1", 1000).IsAlive);

        var aliveRespawn = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, respawn,
            default, false, Harness.HostTick, harness.Costs, harness.Rules, 3);
        TestAssert.AreEqual(PlayerCombatRejectReason.AlreadyAlive, aliveRespawn.Reason);
    }

    [TestMethod]
    public void HostDeathDoesNotBlockARemote()
    {
        // The acceptance case: the host's own death (or a headless host with no mecha at all)
        // must not stop a remote from attacking or being attacked.
        var harness = new Harness();
        harness.AddPlayer("host", 1, ConnA, HostPlayerRole.LocalHost);
        harness.AddPlayer("remote", 2, ConnB, HostPlayerRole.Remote);
        harness.SeedAmmo("host", 7001, 10);
        harness.SeedAmmo("remote", 7001, 10);

        harness.Simulation.ApplyHostDamage("host", HostPlayerSimulation.DefaultMaxHp, Harness.HostTick);
        var remote = harness.Fire("remote", 2, 7001);
        TestAssert.IsTrue(remote.Accepted, "A dead host must not disarm a remote: " + remote.Reason);
    }

    [TestMethod]
    public void AHeadlessServerNeverOwnsCombat()
    {
        var harness = new Harness();
        harness.Registry.RegisterOrUpdate("server", 7, HostPlayerRole.HeadlessDedicated, new ConnectionEpoch(99));
        harness.Simulation.EnsureOwner("server", 1000);
        var request = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, 0, 0, 0);
        var plan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 7, request,
            harness.Target(), true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        TestAssert.IsFalse(plan.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedUnauthorized, plan.Code);
    }

    [TestMethod]
    public void AnOfflineOwnerStopsFiring()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);
        harness.Registry.MarkOfflineBySession(2);

        var plan = harness.Fire("p1", 2, 7001);
        TestAssert.IsFalse(plan.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedUnauthorized, plan.Code);
    }

    [TestMethod]
    public void OutOfRangeTargetsAreRefusedWithAReason()
    {
        var harness = new Harness { Rules = new OutOfRangeTargets() };
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);

        var plan = harness.Fire("p1", 2, 7001);
        TestAssert.IsFalse(plan.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedTarget, plan.Code);
        TestAssert.AreEqual(PlayerCombatRejectReason.OutOfRange, plan.Reason);
    }

    [TestMethod]
    public void FireWithoutTargetRulesIsNotReady()
    {
        var harness = new Harness { Rules = null };
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 10);

        var plan = harness.Fire("p1", 2, 7001);
        TestAssert.IsFalse(plan.Accepted);
        TestAssert.AreEqual(CommandResultCode.RejectedNotReady, plan.Code);
        TestAssert.AreEqual(PlayerCombatRejectReason.NoTargetRules, plan.Reason);
    }

    [TestMethod]
    public void StaleRevisionAndEmptyMagazineAreRefused()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 1);

        var stale = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, expectedRevision: 999, 0, 0);
        var stalePlan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, stale,
            harness.Target(), true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        TestAssert.AreEqual(PlayerCombatRejectReason.StaleRevision, stalePlan.Reason);

        // Spend the single round through the ledger, then the same fresh read is dry.
        var live = harness.RevisionOf("p1", LedgerResourceKind.AmmoBullet, 7001);
        var spend = harness.Ledger.BeginHostTransaction();
        TestAssert.AreEqual(LedgerReserveCode.Ok, harness.Ledger.TryReserveLong(spend,
            LedgerOwner.ForPlayer("p1"), LedgerResourceKind.AmmoBullet, 7001, 1, live,
            out _, out _));
        harness.Ledger.CommitHostTransaction(spend, CommandResultCode.Applied, Harness.HostTick, out _);

        var dry = harness.Fire("p1", 2, 7001);
        TestAssert.AreEqual(PlayerCombatRejectReason.Insufficient, dry.Reason);
    }

    [TestMethod]
    public void ContinuousFireTicksOnTheHostClockAndStopsWhenDry()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);
        harness.SeedAmmo("p1", 7001, 3);

        var start = new PlayerCombatRequest(PlayerCombatAction.StartContinuous, PlayerWeaponKind.Gauss,
            7001, Harness.HostTick, harness.RevisionOf("p1", LedgerResourceKind.AmmoBullet, 7001), 0, 0);
        var plan = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, start,
            harness.Target(), true, Harness.HostTick, harness.Costs, harness.Rules, 1);
        TestAssert.IsTrue(plan.Accepted, "Reason: " + plan.Reason);
        harness.Simulation.Apply(harness.Simulation.EnsureOwner("p1", 1000), start,
            harness.Target(), true, Harness.HostTick, harness.Costs);

        var again = harness.Simulation.Validate(harness.Registry, harness.Ledger, 2, start,
            harness.Target(), true, Harness.HostTick, harness.Costs, harness.Rules, 2);
        TestAssert.AreEqual(PlayerCombatRejectReason.AlreadyFiring, again.Reason);

        // Not due before the interval: no shot, no stop.
        var early = harness.Simulation.PlanContinuousTick(harness.Registry, harness.Ledger, "p1",
            Harness.HostTick + 1, harness.Costs, harness.Rules, 10, out var earlyStop);
        TestAssert.IsNull(early);
        TestAssert.AreEqual(PlayerCombatRejectReason.None, earlyStop);
    }

    [TestMethod]
    public void HostDamageKillsOnceAndTheSecondHitLandsOnACorpse()
    {
        var harness = new Harness();
        harness.AddPlayer("p1", 2, ConnA);

        var scratch = harness.Simulation.ApplyHostDamage("p1", 100, Harness.HostTick);
        TestAssert.IsTrue(scratch.Applied);
        TestAssert.IsFalse(scratch.Died);

        var kill = harness.Simulation.ApplyHostDamage("p1", HostPlayerSimulation.DefaultMaxHp, Harness.HostTick);
        TestAssert.IsTrue(kill.Died);
        TestAssert.AreEqual(0, kill.HpAfter);

        var after = harness.Simulation.ApplyHostDamage("p1", 10, Harness.HostTick);
        TestAssert.IsFalse(after.Applied, "A corpse takes no second death.");
    }
}
