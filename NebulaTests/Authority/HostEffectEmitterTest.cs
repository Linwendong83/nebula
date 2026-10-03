using System.Collections.Generic;
using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A14: one decided skill publishes one visual event, legacy visual facts retire by policy,
/// and the session owns both ends of the path.
/// </summary>
[TestClass]
public class HostEffectEmitterTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA14E7717A14E771, 0x1234567890ABCDEF);
    private static readonly ConnectionEpoch ConnA = new(41);

    private sealed class AllowAllRules : IHostCombatRules
    {
        public PlayerCombatCosts Costs => PlayerCombatCosts.Default;

        public bool IsTargetKnown(in ObjectKey target, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public bool IsInRange(string persistentId, in ObjectKey target, PlayerWeaponKind weapon,
            out string reason)
        {
            reason = string.Empty;
            return true;
        }
    }

    private static PendingVanillaSkill Skill(PlayerCombatAction action, PlayerWeaponKind weapon,
        int ammo = 0, long tx = 9, long tick = 50000, bool targeted = true,
        CommandKey? cause = null) =>
        new("p1", action, weapon, ammo,
            targeted ? ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1) : default,
            targeted, tick, tx, cause ?? new CommandKey(Epoch, ConnA, 3));

    [TestMethod]
    public void EachWeaponPublishesItsOwnEvent()
    {
        var emitter = new HostEffectEmitter();
        var cases = new[]
        {
            (PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss, 7001, AuthorityEffectKind.MechaProjectile, 7001),
            (PlayerCombatAction.LaserFire, PlayerWeaponKind.Laser, 0, AuthorityEffectKind.MechaBeam, 0),
            (PlayerCombatAction.BombDrop, PlayerWeaponKind.Bomb, 0, AuthorityEffectKind.BombFall, 0),
            (PlayerCombatAction.ShieldBurst, PlayerWeaponKind.Shield, 0, AuthorityEffectKind.ShieldBurst, 0)
        };
        foreach (var (action, weapon, ammo, kind, style) in cases)
        {
            TestAssert.IsTrue(emitter.TryEmit(Skill(action, weapon, ammo), out var effect),
                "action=" + action);
            TestAssert.AreEqual(kind, effect.Kind);
            TestAssert.AreEqual(style, effect.Style);
        }
        TestAssert.AreEqual(4L, emitter.EmittedTotal);
        TestAssert.AreEqual(4, emitter.PendingCount);
    }

    [TestMethod]
    public void ContinuousFireFollowsItsWeapon()
    {
        var emitter = new HostEffectEmitter();
        TestAssert.IsTrue(emitter.TryEmit(
            Skill(PlayerCombatAction.StartContinuous, PlayerWeaponKind.Laser), out var beam));
        TestAssert.AreEqual(AuthorityEffectKind.MechaBeam, beam.Kind);
        TestAssert.IsTrue(emitter.TryEmit(
            Skill(PlayerCombatAction.StartContinuous, PlayerWeaponKind.Gauss, 7001), out var shot));
        TestAssert.AreEqual(AuthorityEffectKind.MechaProjectile, shot.Kind);
        TestAssert.AreEqual(7001, shot.Style);
    }

    [TestMethod]
    public void StopAndRespawnPublishNothing()
    {
        var emitter = new HostEffectEmitter();
        TestAssert.IsFalse(emitter.TryEmit(
            Skill(PlayerCombatAction.StopContinuous, PlayerWeaponKind.Gauss, 7001), out _));
        TestAssert.IsFalse(emitter.TryEmit(
            Skill(PlayerCombatAction.Respawn, PlayerWeaponKind.Shield, 0, targeted: false), out _));
        TestAssert.AreEqual(0, emitter.PendingCount);
        TestAssert.AreEqual(2L, emitter.SkippedTotal);
    }

    [TestMethod]
    public void OneSkillIsOneEventWithCauseAndTransaction()
    {
        var emitter = new HostEffectEmitter();
        var cause = new CommandKey(Epoch, ConnA, 3);
        TestAssert.IsTrue(emitter.TryEmit(Skill(PlayerCombatAction.PrimaryFire,
            PlayerWeaponKind.Cannon, 7002, tx: 11, tick: 50100, cause: cause), out var first));
        TestAssert.IsTrue(emitter.TryEmit(Skill(PlayerCombatAction.PrimaryFire,
            PlayerWeaponKind.Cannon, 7002, tx: 12, tick: 50130, cause: new CommandKey(Epoch, ConnA, 4)),
            out var second));
        TestAssert.AreEqual(1L, first.EffectId);
        TestAssert.AreEqual(2L, second.EffectId);
        TestAssert.AreEqual(11L, first.TransactionId);
        TestAssert.AreEqual(12L, second.TransactionId);
        TestAssert.AreEqual(ConnA.Value, first.CauseConnection);
        TestAssert.AreEqual(3L, first.CauseSequence);
        TestAssert.AreEqual(50100L, first.StartTick);
        TestAssert.AreEqual(AuthorityEffectDefaults.LifeFor(AuthorityEffectKind.MechaProjectile), first.Life);
        TestAssert.IsTrue(first.Target.IsValid);
        TestAssert.IsTrue(emitter.TryTake(out var taken));
        TestAssert.AreEqual(1L, taken.EffectId);
        TestAssert.AreEqual(1L, emitter.TakenTotal);
    }

    [TestMethod]
    public void HostInternalTicksCarryNoPredictionCause()
    {
        var emitter = new HostEffectEmitter();
        var continuous = new PendingVanillaSkill("p1", PlayerCombatAction.PrimaryFire,
            PlayerWeaponKind.Gauss, 7001,
            ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1), true, 50200, 13, default);
        TestAssert.IsTrue(emitter.TryEmit(continuous, out var effect));
        TestAssert.AreEqual(0UL, effect.CauseConnection);
        TestAssert.AreEqual(0L, effect.CauseSequence);
        TestAssert.AreEqual(13L, effect.TransactionId);
    }

    [TestMethod]
    public void LegacyBattleVisualRetiresByPolicy()
    {
        TestAssert.IsTrue(HostEffectPolicy.ShouldSuppressLegacyBattleVisual(isHostAuthority: true));
        TestAssert.IsFalse(HostEffectPolicy.ShouldSuppressLegacyBattleVisual(isHostAuthority: false));
        TestAssert.IsNotEmpty(HostEffectPolicy.SuppressionReason());
    }

    [TestMethod]
    public void ASessionOwnsTheEmitterOnTheHostAndTheTableOnTheClient()
    {
        var host = new AuthoritySession(new AuthoritySessionState());
        host.BeginAuthorityWorld(Epoch, isHost: true);
        TestAssert.IsNotNull(host.HostEffects);
        TestAssert.IsNull(host.ClientEffects);

        var client = new AuthoritySession(new AuthoritySessionState());
        client.BeginAuthorityWorld(Epoch, isHost: false);
        TestAssert.IsNull(client.HostEffects);
        TestAssert.IsNotNull(client.ClientEffects);
        TestAssert.AreEqual(Epoch, client.ClientEffects.Epoch);

        host.Reset();
        TestAssert.IsNull(host.HostEffects);
        client.Reset();
        TestAssert.IsNull(client.ClientEffects);
        host.Dispose();
        client.Dispose();
    }

    [TestMethod]
    public void AFrameBoundaryPublishesOneEventPerDecidedSkill()
    {
        var session = new AuthoritySession(new AuthoritySessionState());
        session.BeginAuthorityWorld(Epoch, isHost: true);
        session.HostCombatExecutor.Rules = new AllowAllRules();
        var connection = session.AssignConnectionEpoch(2);
        session.HostPlayers.RegisterOrUpdate("p1", 2, HostPlayerRole.Remote, connection);
        session.HostCombat.EnsureOwner("p1", 1000);
        var owner = LedgerOwner.ForPlayer("p1");
        session.HostLedger.SeedLong(owner, LedgerResourceKind.AmmoBullet, 7001, 10);

        var revision = session.HostLedger.RevisionOf(
            new LedgerResourceKey(owner, LedgerResourceKind.AmmoBullet, 7001));
        var request = new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss,
            7001, 50000, revision, 0, 0);
        PlayerCombatCommand.TryEncode(request, out var payload);
        var target = ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, 7, 1);
        var packet = AuthorityCommandPacket.Create(
            new AuthorityEnvelopeHeader(AuthoritySchema.V1, AuthorityFamily.Command, Epoch,
                connection, 1, 50000, 2, payload.Length),
            target, PlayerCombatCommand.Category, payload);
        TestAssert.IsTrue(session.TryEnqueueHostCommand(new CommandKey(Epoch, connection, 1),
            packet, 2, 99, 50000));

        session.OnFrameBoundary(50000);
        TestAssert.HasCount(0, session.HostCombatExecutor.PendingSkills,
            "Decided skills must be published in the same frame work, never stranded.");
        TestAssert.AreEqual(1, session.HostEffects.PendingCount);
        TestAssert.IsTrue(session.HostEffects.TryTake(out var effect));
        TestAssert.AreEqual(AuthorityEffectKind.MechaProjectile, effect.Kind);
        TestAssert.AreEqual(7001, effect.Style);
        TestAssert.AreEqual(connection.Value, effect.CauseConnection);
        TestAssert.AreEqual(1L, effect.CauseSequence);
        session.Dispose();
    }
}
