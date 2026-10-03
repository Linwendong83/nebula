#region

using NebulaModel.Authority;
using NebulaPatcher.Patches.Authority;
using NebulaWorld.Authority;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

#endregion

namespace NebulaTests.Authority;

/// <summary>
/// Host-authority is the only multiplayer mode. There are no launch flags: fault specs are driven
/// by the harness verbs through <see cref="AuthorityLaunchOptions.TryParseFaultSpec"/> directly,
/// and startup is fail-closed on hook verification.
/// </summary>
[TestClass]
public class AuthorityLaunchOptionsTest
{
    [TestCleanup]
    public void RestoreDefaults()
    {
        AuthorityLocalOptions.ResetToDefaults();
        AuthorityFaultControl.Configure(null);
    }

    [TestMethod]
    public void TheInstallationAlwaysDeclaresHostAuthority()
    {
        AuthorityLocalOptions.ResetToDefaults();
        TestAssert.AreEqual(AuthorityMode.HostAuthority, AuthorityLocalOptions.Mode);
        TestAssert.AreEqual(AuthoritySchema.V1, AuthorityLocalOptions.Schema);
    }

    [TestMethod]
    public void AFullFaultSpecMapsEveryRule()
    {
        TestAssert.IsTrue(AuthorityLaunchOptions.TryParseFaultSpec(
            "delay=2;copies=1;reorder=1;pause;droplifecycle;dropstate=1;dropdigest=1;dropchunk=0;dupchunk=1;truncatechunk;corruptchunk;corrupthash",
            out var rules, out var error), error);
        TestAssert.IsNotNull(rules);
        TestAssert.AreEqual(2, rules.DelayPumps);
        TestAssert.AreEqual(1, rules.ExtraCopies);
        TestAssert.AreEqual(1, rules.ReorderDepth);
        TestAssert.IsTrue(rules.Paused);
        TestAssert.IsTrue(rules.DropNextLifecycle);
        TestAssert.IsTrue(rules.DropNextWorldState);
        TestAssert.IsTrue(rules.DropNextDigest);
        TestAssert.AreEqual(0, rules.DropChunkIndex);
        TestAssert.AreEqual(1, rules.DuplicateChunkIndex);
        TestAssert.IsTrue(rules.TruncateChunk);
        TestAssert.IsTrue(rules.CorruptChunkBytes);
        TestAssert.IsTrue(rules.CorruptCommitHash);
    }

    [TestMethod]
    public void AnUnknownFaultKeyRefusesTheWholeSpec()
    {
        TestAssert.IsFalse(AuthorityLaunchOptions.TryParseFaultSpec("delay=2;speed=9",
            out var rules, out var error));
        TestAssert.IsNull(rules, "A typo must not turn into a clean run.");
        TestAssert.AreEqual("unknown fault key 'speed'", error);
    }

    [TestMethod]
    public void AMalformedValueRefusesTheWholeSpec()
    {
        TestAssert.IsFalse(AuthorityLaunchOptions.TryParseFaultSpec("delay=abc",
            out _, out var error1));
        TestAssert.IsNotNull(error1);
        TestAssert.IsFalse(AuthorityLaunchOptions.TryParseFaultSpec("delay=-1",
            out var negative, out var error2));
        TestAssert.IsNull(negative);
        TestAssert.IsNotNull(error2);
        TestAssert.IsFalse(AuthorityLaunchOptions.TryParseFaultSpec("",
            out _, out var error3));
        TestAssert.IsNotNull(error3);
    }

    [TestMethod]
    public void ApplySucceedsAfterVerificationPasses()
    {
        // DESIGN 1.8: startup must not throw when the hooks resolve.
        AuthorityStartup.Apply(verifyLoadOnce: () => true);
        TestAssert.AreEqual(AuthorityMode.HostAuthority, AuthorityLocalOptions.Mode);
    }

    [TestMethod]
    public void ApplyRefusesToEnterWhenVerificationFails()
    {
        // DESIGN 1.8: a required hook that cannot resolve stops the mode loudly. The session
        // gate (BeginAuthorityWorld) re-verifies, so startup itself just logs and returns.
        AuthorityStartup.Apply(verifyLoadOnce: () => false);
        TestAssert.AreEqual(AuthorityMode.HostAuthority, AuthorityLocalOptions.Mode);
        TestAssert.IsNull(AuthorityFaultControl.Rules, "No fault link is armed for a refused mode.");
    }

    [TestMethod]
    public void ARefusedHarnessSpecLeavesTheWiredRulesUntouched()
    {
        AuthorityFaultControl.Configure(new FaultInjectionRules());
        TestAssert.IsFalse(AuthorityFaultControl.TryApplySpec("typo=1", out var error));
        TestAssert.IsNotNull(error);
    }
}
