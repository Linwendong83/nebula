using System;
using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A11: combat intent codec, per-weapon cost mapping and the legacy-fact policy.
/// </summary>
/// <remarks>
/// <para>
/// The wire rule under test: clients send versioned intents (aim/weapon/tick hint), never final
/// damage or balances. Each trigger pull maps to exactly one ledger spend, charged from host costs.
/// </para>
/// </remarks>
[TestClass]
public class PlayerCombatCommandTest
{
    private static PlayerCombatRequest Fire(int ammoItemId = 7001) =>
        new(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss, ammoItemId,
            inputTick: 100, expectedRevision: 3, protoId: 0, nearStarId: 0);

    [TestMethod]
    public void EveryActionRoundTripsThroughTheCodec()
    {
        var cases = new PlayerCombatRequest[]
        {
            new(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Cannon, 7001, 100, 1, 0, 0),
            new(PlayerCombatAction.LaserFire, PlayerWeaponKind.Laser, 0, 100, 1, 0, 0),
            new(PlayerCombatAction.BombDrop, PlayerWeaponKind.Bomb, 0, 100, 1, 8001, 5),
            new(PlayerCombatAction.ShieldBurst, PlayerWeaponKind.Shield, 0, 100, 1, 0, 0),
            new(PlayerCombatAction.StartContinuous, PlayerWeaponKind.Plasma, 7002, 100, 1, 0, 0),
            new(PlayerCombatAction.StopContinuous, PlayerWeaponKind.Plasma, 7002, 100, 1, 0, 0),
            new(PlayerCombatAction.Respawn, PlayerWeaponKind.Shield, 0, 100, 0, 0, 0),
        };
        foreach (var request in cases)
        {
            TestAssert.IsTrue(PlayerCombatCommand.TryEncode(request, out var payload),
                "Action " + request.Action + " should encode.");
            TestAssert.IsTrue(PlayerCombatCommand.TryDecode(payload, out var decoded, out _),
                "Action " + request.Action + " should decode.");
            TestAssert.AreEqual(request.Action, decoded.Action);
            TestAssert.AreEqual(request.Weapon, decoded.Weapon);
            TestAssert.AreEqual(request.AmmoItemId, decoded.AmmoItemId);
            TestAssert.AreEqual(request.InputTick, decoded.InputTick);
            TestAssert.AreEqual(request.ExpectedRevision, decoded.ExpectedRevision);
            TestAssert.AreEqual(request.ProtoId, decoded.ProtoId);
            TestAssert.AreEqual(request.NearStarId, decoded.NearStarId);
        }
    }

    [TestMethod]
    public void FireActionsNeedATargetAndTheRestMustNot()
    {
        TestAssert.IsTrue(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.PrimaryFire));
        TestAssert.IsTrue(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.LaserFire));
        TestAssert.IsTrue(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.StartContinuous));
        TestAssert.IsFalse(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.BombDrop));
        TestAssert.IsFalse(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.ShieldBurst));
        TestAssert.IsFalse(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.StopContinuous));
        TestAssert.IsFalse(PlayerCombatCommand.RequiresTarget(PlayerCombatAction.Respawn));
    }

    [TestMethod]
    public void CorruptPayloadsAreRefusedRatherThanDecoded()
    {
        TestAssert.IsTrue(PlayerCombatCommand.TryEncode(Fire(), out var good));

        // Truncated.
        var truncated = new byte[good.Length - 1];
        Buffer.BlockCopy(good, 0, truncated, 0, truncated.Length);
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(truncated, out _, out _));

        // Trailing bytes.
        var trailed = new byte[good.Length + 1];
        Buffer.BlockCopy(good, 0, trailed, 0, good.Length);
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(trailed, out _, out _));

        // Unknown version.
        var badVersion = (byte[])good.Clone();
        badVersion[0] = 99;
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(badVersion, out _, out _));

        // Unknown action / weapon.
        var badAction = (byte[])good.Clone();
        badAction[1] = 99;
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(badAction, out _, out _));
        var badWeapon = (byte[])good.Clone();
        badWeapon[2] = 99;
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(badWeapon, out _, out _));

        // Oversize.
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(
            new byte[AuthorityLimits.CommandPayloadMaxBytes + 1], out _, out _));

        // Null.
        TestAssert.IsFalse(PlayerCombatCommand.TryDecode(null, out _, out _));
    }

    [TestMethod]
    public void ActionWeaponMismatchIsRefused()
    {
        // Ammo weapon firing a laser action, laser carrying an ammo id, bomb without a proto id,
        // shield carrying ids: none of these may encode.
        var mismatches = new PlayerCombatRequest[]
        {
            new(PlayerCombatAction.LaserFire, PlayerWeaponKind.Gauss, 7001, 100, 1, 0, 0),
            new(PlayerCombatAction.LaserFire, PlayerWeaponKind.Laser, 7001, 100, 1, 0, 0),
            new(PlayerCombatAction.BombDrop, PlayerWeaponKind.Bomb, 0, 100, 1, 0, 0),
            new(PlayerCombatAction.ShieldBurst, PlayerWeaponKind.Shield, 7001, 100, 1, 0, 0),
            new(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Laser, 0, 100, 1, 0, 0),
            new(PlayerCombatAction.Unknown, PlayerWeaponKind.Gauss, 7001, 100, 1, 0, 0),
        };
        foreach (var request in mismatches)
        {
            TestAssert.IsFalse(PlayerCombatCommand.TryEncode(request, out _),
                $"Action {request.Action} with weapon {request.Weapon} must not encode.");
        }
    }

    [TestMethod]
    public void EachTriggerPullMapsToExactlyOneLedgerSpend()
    {
        var costs = PlayerCombatCosts.Default;
        var owner = LedgerOwner.ForPlayer("pilot");

        var gun = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.PrimaryFire, PlayerWeaponKind.Gauss, 7001, 100, 4, 0, 0),
            4, sequence: 1, costs);
        TestAssert.HasCount(1, gun);
        TestAssert.AreEqual(LedgerResourceKind.AmmoBullet, gun[0].Kind);
        TestAssert.AreEqual(7001, gun[0].ItemId);
        TestAssert.AreEqual(1L, gun[0].LongAmount);

        var laser = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.LaserFire, PlayerWeaponKind.Laser, 0, 100, 4, 0, 0),
            4, sequence: 2, costs);
        TestAssert.HasCount(1, laser);
        TestAssert.AreEqual(LedgerResourceKind.CoreEnergy, laser[0].Kind);
        TestAssert.AreEqual(costs.LaserEnergyPerShot, laser[0].DoubleAmount, 1e-9);

        var bomb = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.BombDrop, PlayerWeaponKind.Bomb, 0, 100, 4, 8001, 5),
            4, sequence: 3, costs);
        TestAssert.HasCount(1, bomb);
        TestAssert.AreEqual(LedgerResourceKind.BombStorageItem, bomb[0].Kind);
        TestAssert.AreEqual(8001, bomb[0].ItemId);
        TestAssert.AreEqual(1L, bomb[0].LongAmount);

        var burst = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.ShieldBurst, PlayerWeaponKind.Shield, 0, 100, 4, 0, 0),
            4, sequence: 4, costs);
        TestAssert.HasCount(1, burst);
        TestAssert.AreEqual(LedgerResourceKind.CoreEnergy, burst[0].Kind);
        TestAssert.AreEqual(costs.ShieldEnergyPerBurst, burst[0].DoubleAmount, 1e-9);

        // Stop and respawn spend nothing: they change state, not balances.
        var stop = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.StopContinuous, PlayerWeaponKind.Gauss, 7001, 100, 4, 0, 0),
            4, sequence: 5, costs);
        TestAssert.IsEmpty(stop);
        var respawn = HostPlayerSimulation.BuildOps(owner,
            new PlayerCombatRequest(PlayerCombatAction.Respawn, PlayerWeaponKind.Shield, 0, 100, 0, 0, 0),
            0, sequence: 6, costs);
        TestAssert.IsEmpty(respawn);
    }

    [TestMethod]
    public void LegacyCombatFactsAreRefusedOnlyInAuthorityMode()
    {
        TestAssert.IsTrue(NebulaWorld.Authority.HostCombatPolicy.ShouldRefuseLegacyCombatFact(isHostAuthority: true));
        TestAssert.IsFalse(NebulaWorld.Authority.HostCombatPolicy.ShouldRefuseLegacyCombatFact(isHostAuthority: false));
        TestAssert.IsTrue(NebulaWorld.Authority.HostCombatPolicy.MustUseMeteredEnergy(isHostAuthority: true));
        TestAssert.IsFalse(NebulaWorld.Authority.HostCombatPolicy.MustUseMeteredEnergy(isHostAuthority: false));
    }
}
