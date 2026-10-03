using NebulaModel.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.Authority;

/// <summary>
/// A14: the display pose interpolates between authority samples, freezes past the window,
/// and never carries a rule number.
/// </summary>
[TestClass]
public class PresentationPoseTest
{
    private static AuthorityPose At(double x, long tick) =>
        new(x, 0, 0, 0, 0, 0, 1, 60f, 0, 0, tick);

    [TestMethod]
    public void ASingleSampleDisplaysAsIs()
    {
        var buffer = new RenderPoseBuffer();
        TestAssert.IsFalse(buffer.TrySample(100, out _), "Nothing pushed yet.");
        buffer.Push(At(10, 100));
        TestAssert.IsTrue(buffer.TrySample(100, out var display));
        TestAssert.AreEqual(10.0, display.PosX);
    }

    [TestMethod]
    public void AMidpointSampleInterpolates()
    {
        var buffer = new RenderPoseBuffer();
        buffer.Push(At(0, 100));
        buffer.Push(At(60, 160));
        TestAssert.IsTrue(buffer.TrySample(130, out var display));
        TestAssert.AreEqual(30.0, display.PosX, 1e-6);
    }

    [TestMethod]
    public void OldTicksClampToThePreviousSample()
    {
        var buffer = new RenderPoseBuffer();
        buffer.Push(At(0, 100));
        buffer.Push(At(60, 160));
        TestAssert.IsTrue(buffer.TrySample(50, out var display));
        TestAssert.AreEqual(0.0, display.PosX);
    }

    [TestMethod]
    public void ShortGapsExtrapolateThenFreeze()
    {
        var buffer = new RenderPoseBuffer();
        buffer.Push(At(0, 100));
        buffer.Push(At(60, 160));
        TestAssert.IsTrue(buffer.TrySample(160 + 6, out var moving));
        TestAssert.IsGreaterThan(60.0, moving.PosX, "Within 200 ms the display keeps moving.");
        TestAssert.IsTrue(buffer.TrySample(160 + RenderPoseBuffer.MaxExtrapolateTicks, out var edge));
        TestAssert.IsTrue(buffer.TrySample(160 + RenderPoseBuffer.MaxExtrapolateTicks + 120, out var frozen));
        TestAssert.AreEqual(edge.PosX, frozen.PosX, 1e-9, "Past 200 ms the display freezes.");
        TestAssert.AreEqual(72.0, frozen.PosX, 1e-9, "Frozen means the 200 ms extrapolation endpoint.");
    }

    [TestMethod]
    public void PosesMustArriveInOrder()
    {
        var buffer = new RenderPoseBuffer();
        buffer.Push(At(0, 100));
        try
        {
            buffer.Push(At(60, 99));
            TestAssert.Fail("An out-of-order pose must be rejected.");
        }
        catch (System.ArgumentException)
        {
        }
    }

    [TestMethod]
    public void TheBufferKeepsPreviousWhileNewArrives()
    {
        var buffer = new RenderPoseBuffer();
        buffer.Push(At(0, 100));
        buffer.Push(At(60, 160));
        buffer.Push(At(120, 220));
        TestAssert.IsTrue(buffer.TrySample(190, out var display));
        TestAssert.AreEqual(90.0, display.PosX, 1e-6);
        TestAssert.AreEqual(220L, buffer.NewestTick);
    }

    [TestMethod]
    public void APoseCarriesNoRuleNumber()
    {
        // HP, death and repair must never be extrapolated: the pose type has no such field,
        // so a presentation tick over these values cannot move them. The reflection check
        // locks that shape in place.
        foreach (var name in new[] { "Hp", "Shield", "Energy", "Damage", "Kill", "Repair", "Inventory" })
        {
            TestAssert.IsNull(typeof(AuthorityPose).GetProperty(name), "pose." + name);
            TestAssert.IsNull(typeof(AuthorityPose).GetField(name), "pose." + name);
        }
    }
}
