using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A14: host effect records have an explicit kind, a fixed layout and no unanchored event.
/// </summary>
[TestClass]
public class EffectStateTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA14E771CA14E001, 0x1234567890ABCDEF);

    private static ObjectKey Enemy(int nativeId = 7, long generation = 1) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, generation);

    private static EffectState PlayerShot(long effectId = 1, long tx = 9, ulong conn = 41, long seq = 3) =>
        new(effectId, AuthorityEffectKind.MechaProjectile, 7001, tx, 50000, 600, conn, seq,
            default, Enemy());

    private static EffectState TurretShot(long effectId = 2) =>
        new(effectId, AuthorityEffectKind.TurretShot, 0, 0, 50000, 600, 0, 0, Enemy(9), default);

    [TestMethod]
    public void EveryKindRoundTrips()
    {
        var kinds = new[]
        {
            AuthorityEffectKind.MechaMuzzle, AuthorityEffectKind.MechaProjectile,
            AuthorityEffectKind.MechaBeam, AuthorityEffectKind.BombFall,
            AuthorityEffectKind.ShieldBurst, AuthorityEffectKind.TurretShot,
            AuthorityEffectKind.CraftShot, AuthorityEffectKind.HitFlash,
            AuthorityEffectKind.ShieldHit, AuthorityEffectKind.BeamSweep
        };
        var id = 1L;
        foreach (var kind in kinds)
        {
            var state = kind == AuthorityEffectKind.TurretShot || kind == AuthorityEffectKind.CraftShot ||
                kind == AuthorityEffectKind.BeamSweep
                ? new EffectState(id, kind, 3, 0, 50000, AuthorityEffectDefaults.LifeFor(kind), 0, 0,
                    Enemy(9), default)
                : new EffectState(id, kind, 1, 7, 50000, AuthorityEffectDefaults.LifeFor(kind), 41, 3,
                    default, Enemy());
            TestAssert.IsTrue(EffectStateCodec.TryEncode(state, out var data), "kind=" + kind);
            TestAssert.IsTrue(EffectStateCodec.TryDecode(data, 0, data.Length, out var back, out _),
                "kind=" + kind);
            TestAssert.AreEqual(state.EffectId, back.EffectId);
            TestAssert.AreEqual(state.Kind, back.Kind);
            TestAssert.AreEqual(state.Style, back.Style);
            TestAssert.AreEqual(state.TransactionId, back.TransactionId);
            TestAssert.AreEqual(state.StartTick, back.StartTick);
            TestAssert.AreEqual(state.Life, back.Life);
            TestAssert.AreEqual(state.CauseConnection, back.CauseConnection);
            TestAssert.AreEqual(state.CauseSequence, back.CauseSequence);
            TestAssert.AreEqual(state.Caster, back.Caster);
            TestAssert.AreEqual(state.Target, back.Target);
            id++;
        }
    }

    [TestMethod]
    public void UnknownKindNeverEncodes()
    {
        var state = new EffectState(1, AuthorityEffectKind.Unknown, 0, 7, 50000, 30, 41, 3,
            default, Enemy());
        TestAssert.IsFalse(EffectStateCodec.TryEncode(state, out _));
    }

    [TestMethod]
    public void ACorruptBlobIsRefused()
    {
        TestAssert.IsTrue(EffectStateCodec.TryEncode(PlayerShot(), out var data));
        // Truncated.
        TestAssert.IsFalse(EffectStateCodec.TryDecode(data, 0, data.Length - 2, out _, out _));
        // Trailing byte.
        var padded = new byte[data.Length + 1];
        System.Buffer.BlockCopy(data, 0, padded, 0, data.Length);
        TestAssert.IsFalse(EffectStateCodec.TryDecode(padded, 0, padded.Length, out _, out _));
        // Unknown kind byte (offset 1).
        var patched = (byte[])data.Clone();
        patched[1] = 0;
        TestAssert.IsFalse(EffectStateCodec.TryDecode(patched, 0, patched.Length, out _, out _));
        // Unknown version byte (offset 0).
        patched = (byte[])data.Clone();
        patched[0] = 9;
        TestAssert.IsFalse(EffectStateCodec.TryDecode(patched, 0, patched.Length, out _, out _));
    }

    [TestMethod]
    public void OutOfRangeLivesAreRefused()
    {
        foreach (var life in new[] { 0, -5, AuthorityEffectDefaults.MaxLifeTicks + 1 })
        {
            var state = new EffectState(1, AuthorityEffectKind.MechaBeam, 0, 7, 50000, life, 41, 3,
                default, Enemy());
            TestAssert.IsFalse(EffectStateCodec.TryEncode(state, out _), "life=" + life);
        }
    }

    [TestMethod]
    public void AnEventWithNoAnchorIsRefused()
    {
        var state = new EffectState(1, AuthorityEffectKind.HitFlash, 0, 0, 50000, 30, 0, 0,
            default, default);
        TestAssert.IsFalse(EffectStateCodec.TryEncode(state, out _));
    }

    [TestMethod]
    public void AHalfCauseIsRefused()
    {
        var connOnly = new EffectState(1, AuthorityEffectKind.MechaProjectile, 1, 7, 50000, 600, 41, 0,
            default, Enemy());
        TestAssert.IsFalse(EffectStateCodec.TryEncode(connOnly, out _));
    }

    [TestMethod]
    public void NegativeIdsAndStylesAreRefused()
    {
        TestAssert.IsFalse(EffectStateCodec.TryEncode(
            new EffectState(0, AuthorityEffectKind.MechaBeam, 0, 7, 50000, 30, 41, 3, default, Enemy()),
            out _));
        TestAssert.IsFalse(EffectStateCodec.TryEncode(
            new EffectState(1, AuthorityEffectKind.MechaBeam, -1, 7, 50000, 30, 41, 3, default, Enemy()),
            out _));
        TestAssert.IsFalse(EffectStateCodec.TryEncode(
            new EffectState(1, AuthorityEffectKind.MechaBeam, 0, -2, 50000, 30, 41, 3, default, Enemy()),
            out _));
    }

    [TestMethod]
    public void AutonomousWorldEffectsNeedNoTransaction()
    {
        TestAssert.IsTrue(EffectStateCodec.TryEncode(TurretShot(), out var data));
        TestAssert.IsTrue(EffectStateCodec.TryDecode(data, 0, data.Length, out var back, out _));
        TestAssert.AreEqual(0L, back.TransactionId);
        TestAssert.IsFalse(back.HasCause);
        TestAssert.IsTrue(back.Caster.IsValid);
    }

    [TestMethod]
    public void EncodingIsDeterministic()
    {
        TestAssert.IsTrue(EffectStateCodec.TryEncode(PlayerShot(), out var first));
        TestAssert.IsTrue(EffectStateCodec.TryEncode(PlayerShot(), out var second));
        TestAssert.HasCount(first.Length, second);
        for (var i = 0; i < first.Length; i++)
        {
            TestAssert.AreEqual(first[i], second[i], "byte " + i);
        }
    }
}
