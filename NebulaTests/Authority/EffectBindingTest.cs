using NebulaModel.Authority;
using NebulaModel.Packets.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A14: predicted muzzles merge with the host event by cause, duplicates never stack,
/// ended events leave no ghost, and rendering moves no protected number.
/// </summary>
[TestClass]
public class EffectBindingTest
{
    private static readonly AuthorityEpoch Epoch = new(0xA14B10DA14B10D0, 0x1234567890ABCDEF);
    private static readonly AuthorityEpoch OtherEpoch = new(0xDEADBEEFDEADBEEF, 0x0011223344556677);
    private static readonly ConnectionEpoch ConnA = new(41);

    private static CommandKey Key(long sequence, ConnectionEpoch? connection = null) =>
        new(Epoch, connection ?? ConnA, sequence);

    private static ObjectKey Enemy(int nativeId = 7) =>
        ObjectKey.Create(Epoch, PoolKind.GroundEnemy, 101, nativeId, 1);

    private static EffectState Shot(long effectId, long tx, long causeSeq, long start = 50000,
        int life = 600) =>
        new(effectId, AuthorityEffectKind.MechaProjectile, 7001, tx, start, life,
            ConnA.Value, causeSeq, default, Enemy());

    [TestMethod]
    public void APredictionMergesWithItsHostEvent()
    {
        var binding = new EffectBinding(Epoch);
        TestAssert.IsTrue(binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990));
        TestAssert.AreEqual(1, binding.PredictedCount);

        TestAssert.IsTrue(binding.ApplyHostEffect(Shot(1, 9, 3)));
        TestAssert.AreEqual(0, binding.PredictedCount, "The confirmed muzzle must replace the prediction.");
        TestAssert.AreEqual(1, binding.ActiveCount);
        TestAssert.AreEqual(1L, binding.PredictionsMerged);
        TestAssert.AreEqual(1L, binding.EffectsApplied);
    }

    [TestMethod]
    public void AnEffectBeforeItsResultStillMergesByCause()
    {
        var binding = new EffectBinding(Epoch);
        binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990);

        // The host event arrives before the command result: the cause fields still name the
        // prediction, so the merge is immediate and no second muzzle ever shows.
        binding.ApplyHostEffect(Shot(1, 9, 3));
        TestAssert.IsTrue(binding.NoteCommandResult(Key(3), 9, 50000));
        TestAssert.AreEqual(0, binding.PredictedCount);
        TestAssert.AreEqual(1, binding.ActiveCount);
        TestAssert.AreEqual(1L, binding.PredictionsMerged);
    }

    [TestMethod]
    public void ARejectedCommandRevokesItsPrediction()
    {
        var binding = new EffectBinding(Epoch);
        binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990);
        TestAssert.IsTrue(binding.NoteCommandResult(Key(3), 0, 0));
        TestAssert.AreEqual(0, binding.PredictedCount);
        TestAssert.AreEqual(0, binding.ActiveCount);
        TestAssert.AreEqual(1L, binding.PredictionsRevoked);
    }

    [TestMethod]
    public void ARetriedInputNeverStacksASecondMuzzle()
    {
        var binding = new EffectBinding(Epoch);
        TestAssert.IsTrue(binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990));
        TestAssert.IsFalse(binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49991));
        TestAssert.AreEqual(1, binding.PredictedCount);
    }

    [TestMethod]
    public void ADuplicateDeliveryIsIdempotent()
    {
        var binding = new EffectBinding(Epoch);
        binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990);
        var effect = Shot(1, 9, 3);
        TestAssert.IsTrue(binding.ApplyHostEffect(effect));
        TestAssert.IsTrue(binding.ApplyHostEffect(effect), "A repeated delivery must not stack.");
        TestAssert.AreEqual(1, binding.ActiveCount);
        TestAssert.AreEqual(1L, binding.EffectsDuplicated);
    }

    [TestMethod]
    public void AReusedIdForADifferentEventIsRefused()
    {
        var binding = new EffectBinding(Epoch);
        TestAssert.IsTrue(binding.ApplyHostEffect(Shot(1, 9, 3)));
        var other = new EffectState(1, AuthorityEffectKind.MechaBeam, 0, 10, 50010, 30,
            ConnA.Value, 4, default, Enemy(8));
        TestAssert.IsFalse(binding.ApplyHostEffect(other));
        TestAssert.AreEqual(1, binding.ActiveCount);
    }

    [TestMethod]
    public void AnEndedEventLeavesNoGhostAndNeverReturns()
    {
        var binding = new EffectBinding(Epoch);
        var effect = Shot(1, 9, 3, start: 50000, life: 30);
        binding.ApplyHostEffect(effect);
        TestAssert.AreEqual(0, binding.Tick(50029), "Start + life has not ended yet.");
        TestAssert.AreEqual(1, binding.ActiveCount);
        TestAssert.AreEqual(1, binding.Tick(50030), "Start + life ends the shell.");
        TestAssert.AreEqual(0, binding.ActiveCount);
        TestAssert.AreEqual(1L, binding.EffectsExpired);
        TestAssert.IsFalse(binding.ApplyHostEffect(effect), "An ended event must not resurrect.");
        TestAssert.AreEqual(0, binding.ActiveCount);
    }

    [TestMethod]
    public void AnUnconfirmedPredictionExpires()
    {
        var binding = new EffectBinding(Epoch);
        binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990);
        TestAssert.AreEqual(0, binding.Tick(49990 + EffectBinding.PredictionTtlTicks - 1));
        TestAssert.AreEqual(1, binding.Tick(49990 + EffectBinding.PredictionTtlTicks));
        TestAssert.AreEqual(0, binding.PredictedCount);
        TestAssert.AreEqual(1L, binding.PredictionsExpired);
    }

    [TestMethod]
    public void OtherEpochsHaveNoSideEffect()
    {
        var binding = new EffectBinding(Epoch);
        var foreignKey = new CommandKey(OtherEpoch, ConnA, 3);
        TestAssert.IsFalse(binding.Predict(foreignKey, AuthorityEffectKind.MechaProjectile, 7001, 1));
        var foreignEnemy = ObjectKey.Create(OtherEpoch, PoolKind.GroundEnemy, 101, 7, 1);
        var foreign = new EffectState(1, AuthorityEffectKind.HitFlash, 0, 0, 50000, 30, 0, 0,
            foreignEnemy, default);
        TestAssert.IsFalse(binding.ApplyHostEffect(foreign));
        TestAssert.AreEqual(0, binding.PredictedCount);
        TestAssert.AreEqual(0, binding.ActiveCount);
    }

    [TestMethod]
    public void TenSecondsOfRenderingMovesNoProtectedNumber()
    {
        // The table holds events only: no HP, shield, energy, inventory or kill reference
        // exists for rendering to move. Six hundred presentation ticks with no host traffic
        // must therefore change nothing but expiry bookkeeping.
        var binding = new EffectBinding(Epoch);
        binding.Predict(Key(3), AuthorityEffectKind.MechaProjectile, 7001, 49990);
        binding.ApplyHostEffect(new EffectState(1, AuthorityEffectKind.MechaProjectile, 7001, 9, 50000,
            AuthorityEffectDefaults.MaxLifeTicks, ConnA.Value, 3, default, Enemy()));
        var refusedBefore = binding.EffectsRefused;
        for (long tick = 50000; tick < 50600; tick++)
        {
            binding.Tick(tick);
        }
        TestAssert.AreEqual(1, binding.ActiveCount, "A long-lived host effect survives rendering.");
        TestAssert.AreEqual(0, binding.PredictedCount, "The merged prediction stays merged.");
        TestAssert.AreEqual(refusedBefore, binding.EffectsRefused, "Rendering refuses nothing.");
    }

    [TestMethod]
    public void AutonomousWorldEffectsNeedNoPrediction()
    {
        var binding = new EffectBinding(Epoch);
        var turret = new EffectState(1, AuthorityEffectKind.TurretShot, 0, 0, 50000, 600, 0, 0,
            Enemy(9), default);
        TestAssert.IsTrue(binding.ApplyHostEffect(turret));
        TestAssert.AreEqual(1, binding.ActiveCount);
        TestAssert.AreEqual(0L, binding.PredictionsMerged);
    }
}
