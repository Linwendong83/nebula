using System.IO;
using System.Reflection;
using NebulaModel.DataStructures;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A14: every client-side visual projectile decodes disarmed. The presentation pools are
/// detached from the rule pools, so a rendered shot can never carry damage, an incoming
/// HP change, or a target mask into the shared world.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NebulaWorld.Combat.BattleVisualRenderer.Decode"/> is the single funnel every
/// legacy visual effect passes before it reaches a detached pool. Each case below arms a
/// native payload with live damage values and asserts the decoded copy carries none. Shield
/// absorb (<c>MechaEnergyShieldResist</c>), <c>hpIncoming</c> (<c>CombatStat</c>) and
/// continuous-removal paths need no per-kind disarm because the renderer never calls them:
/// it owns a detached <c>SkillSystem</c> built with <c>GetUninitializedObject</c> and only
/// ever calls renderer update/draw on it, never a damage, heal, resist or ledger entry.
/// </para>
/// </remarks>
[TestClass]
public class EffectPresentationTest
{
    private static object Decode(BattleEffectKind kind, System.Action<BinaryWriter> export)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            export(writer);
        }
        var method = typeof(NebulaWorld.Combat.BattleVisualRenderer)
            .GetMethod("Decode", BindingFlags.Static | BindingFlags.NonPublic)!;
        return method.Invoke(null, [new BattleEffectData { Kind = kind, Payload = stream.ToArray() }])!;
    }

    [TestMethod]
    public void EveryVisualProjectileDecodesDisarmed()
    {
        var laser = (LocalLaserOneShot)Decode(BattleEffectKind.GroundLaser,
            w => new LocalLaserOneShot { id = 1, life = 20, damage = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
        TestAssert.AreEqual(0, laser.damage);
        TestAssert.AreEqual((ETargetTypeMask)0, laser.mask);

        foreach (var kind in new[] { BattleEffectKind.GroundPlasma, BattleEffectKind.GroundShieldPlasma })
        {
            var local = (LocalGeneralProjectile)Decode(kind,
                w => new LocalGeneralProjectile { id = 1, life = 20, damage = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
            TestAssert.AreEqual(0, local.damage, "kind=" + kind);
            TestAssert.AreEqual((ETargetTypeMask)0, local.mask, "kind=" + kind);
        }

        var spaceLaser = (SpaceLaserOneShot)Decode(BattleEffectKind.SpaceLaser,
            w => new SpaceLaserOneShot { id = 1, life = 20, damage = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
        TestAssert.AreEqual(0, spaceLaser.damage);
        TestAssert.AreEqual((ETargetTypeMask)0, spaceLaser.mask);

        var missile = (GeneralMissile)Decode(BattleEffectKind.TurretMissile,
            w => new GeneralMissile { id = 1, life = 20, damage = 12345, damageIncoming = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
        TestAssert.AreEqual(0, missile.damage);
        TestAssert.AreEqual(0, missile.damageIncoming);
        TestAssert.AreEqual((ETargetTypeMask)0, missile.mask);

        foreach (var kind in new[] { BattleEffectKind.SpacePlasmaF, BattleEffectKind.SpacePlasmaA, BattleEffectKind.TurretPlasma })
        {
            var projectile = (GeneralProjectile)Decode(kind,
                w => new GeneralProjectile { id = 1, life = 20, damage = 12345, damageIncoming = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
            TestAssert.AreEqual(0, projectile.damage, "kind=" + kind);
            TestAssert.AreEqual(0, projectile.damageIncoming, "kind=" + kind);
            TestAssert.AreEqual((ETargetTypeMask)0, projectile.mask, "kind=" + kind);
        }

        var sweep = (SpaceLaserSweep)Decode(BattleEffectKind.LancerSweep,
            w =>
            {
                new SpaceLaserSweep { id = 1, life = 20, damage = 12345, mask = ETargetTypeMask.Enemy }.Export(w);
                w.Write(1f);
                w.Write(2f);
                w.Write(3f);
            });
        TestAssert.AreEqual(0, sweep.damage);
        TestAssert.AreEqual((ETargetTypeMask)0, sweep.mask);

        var bomb = (GeneralExpImpProjectile)Decode(BattleEffectKind.BomberProjectile,
            w => new GeneralExpImpProjectile { id = 1, life = 20, damage = 12345, mask = ETargetTypeMask.Enemy }.Export(w));
        TestAssert.AreEqual(0, bomb.damage);
        TestAssert.AreEqual((ETargetTypeMask)0, bomb.mask);
    }

    [TestMethod]
    public void ImpactParticlesCarryNoDamageField()
    {
        var particle = (ParticleData)Decode(BattleEffectKind.Impact,
            w => new ParticleData { id = 1, time = 0, duration = 100 }.Export(w));
        TestAssert.AreEqual(1, particle.id);
    }
}
