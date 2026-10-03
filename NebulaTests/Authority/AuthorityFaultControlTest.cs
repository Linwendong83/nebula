#region

using NebulaModel.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// A22 closing: the runtime fault rules can be re-armed mid-run from a launch-spec string, with
/// the same refuse-the-whole-spec semantics as launch — so the matrix driver can inject a fault,
/// clear it, and never mistake a typo for a clean run.
/// </summary>
[TestClass]
public class AuthorityFaultControlTest
{
    [TestCleanup]
    public void Disarm()
    {
        AuthorityFaultControl.Configure(null);
    }

    [TestMethod]
    public void AGoodSpecArmsTheRulesWhenOff()
    {
        TestAssert.IsTrue(AuthorityFaultControl.TryApplySpec("delay=2;copies=1", out var error));
        TestAssert.IsNull(error);
        TestAssert.IsTrue(AuthorityFaultControl.IsEnabled);
        TestAssert.AreEqual(2, AuthorityFaultControl.Rules.DelayPumps);
        TestAssert.AreEqual(1, AuthorityFaultControl.Rules.ExtraCopies);
    }

    [TestMethod]
    public void AnUnknownKeyRefusesAndLeavesTheArmedRulesUntouched()
    {
        TestAssert.IsTrue(AuthorityFaultControl.TryApplySpec("delay=2", out _));
        TestAssert.IsFalse(AuthorityFaultControl.TryApplySpec("delay=9;speed=9", out var error));
        TestAssert.IsNotNull(error);
        TestAssert.AreEqual(2, AuthorityFaultControl.Rules.DelayPumps);
    }

    [TestMethod]
    public void AMalformedOrEmptySpecRefuses()
    {
        TestAssert.IsFalse(AuthorityFaultControl.TryApplySpec("delay=abc", out var e1));
        TestAssert.IsNotNull(e1);
        TestAssert.IsFalse(AuthorityFaultControl.TryApplySpec("", out var e2));
        TestAssert.IsNotNull(e2);
        TestAssert.IsFalse(AuthorityFaultControl.TryApplySpec("delay=-1", out var e3));
        TestAssert.IsNotNull(e3);
        TestAssert.IsFalse(AuthorityFaultControl.IsEnabled);
    }

    [TestMethod]
    public void RearmingMutatesTheLiveObjectTheLinkReads()
    {
        var live = new FaultInjectionRules { DelayPumps = 1 };
        AuthorityFaultControl.Configure(live);
        TestAssert.IsTrue(AuthorityFaultControl.TryApplySpec("delay=5;pause", out _));
        TestAssert.IsTrue(ReferenceEquals(live, AuthorityFaultControl.Rules),
            "The live link keeps reading the same object, so re-arm must mutate in place.");
        TestAssert.AreEqual(5, live.DelayPumps);
        TestAssert.IsTrue(live.Paused);
    }

    [TestMethod]
    public void ClearKeepsTheLinkWiredButZeroesItsRulesInPlace()
    {
        // Clear must not detach the live link: after a clean case the driver re-arms the same
        // object, and nulling it would make every later fault case silently run clean.
        var live = new FaultInjectionRules { DelayPumps = 3, Paused = true };
        AuthorityFaultControl.Configure(live);
        AuthorityFaultControl.Clear();
        TestAssert.IsTrue(AuthorityFaultControl.IsEnabled, "The wired link stays wired after clear.");
        TestAssert.IsFalse(AuthorityFaultControl.IsArmed, "A cleared link is not armed.");
        TestAssert.IsTrue(ReferenceEquals(live, AuthorityFaultControl.Rules),
            "Clear zeroes in place so re-arm still reaches the live link.");
        TestAssert.AreEqual(0, live.DelayPumps);
        TestAssert.IsFalse(live.Paused);
    }

    [TestMethod]
    public void ReArmingAfterClearStillReachesTheWiredObject()
    {
        // Regression pin for the A22 matrix bug: faultclear then fault <spec> must mutate the same
        // object the link holds, not replace it.
        var live = new FaultInjectionRules { DelayPumps = 1 };
        AuthorityFaultControl.Configure(live);
        AuthorityFaultControl.Clear();
        TestAssert.IsTrue(AuthorityFaultControl.TryApplySpec("copies=1;reorder=1", out _));
        TestAssert.IsTrue(ReferenceEquals(live, AuthorityFaultControl.Rules));
        TestAssert.AreEqual(1, live.ExtraCopies);
        TestAssert.AreEqual(1, live.ReorderDepth);
        TestAssert.IsTrue(AuthorityFaultControl.IsArmed);
    }

    [TestMethod]
    public void DescribeReportsTheArmedRules()
    {
        AuthorityFaultControl.TryApplySpec("delay=2;pause;droplifecycle", out _);
        var text = AuthorityFaultControl.Describe();
        TestAssert.IsTrue(text.Contains("delay=2"), text);
        TestAssert.IsTrue(text.Contains("paused=1"), text);
        TestAssert.IsTrue(text.Contains("droplifecycle=1"), text);
    }
}
