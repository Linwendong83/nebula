using System.Runtime.Serialization;
using HarmonyLib;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
[DoNotParallelize]
public class HeadlessAdvisorTest
{
    [TestMethod]
    public void AdvisorKeepsItsManagedLifecycleWithoutOpeningGraphicsOrAudio()
    {
        var harmony = new Harmony("nebula.tests.headless-advisor");
        var patches = typeof(NebulaPatcher.NebulaPlugin).Assembly.GetType(
            "NebulaPatcher.Patches.Misc.Dedicated_Server_Patches", true)!;
        var create = AccessTools.Method(typeof(UIAdvisorTip), "_OnCreate");
        var request = AccessTools.Method(typeof(UIAdvisorTip), "RequestAdvisorTip");
        var run = AccessTools.Method(typeof(UIAdvisorTip), "RunAdvisorTip");
        try
        {
            harmony.Patch(create, prefix: new HarmonyMethod(AccessTools.Method(patches, "UIAdvisorTipCreate_Prefix")));
            var suppress = new HarmonyMethod(AccessTools.Method(patches, "UIAdvisorTipPlay_Prefix"));
            harmony.Patch(request, prefix: suppress);
            harmony.Patch(run, prefix: suppress);
            var advisor = (UIAdvisorTip)FormatterServices.GetUninitializedObject(typeof(UIAdvisorTip));
            create.Invoke(advisor, null);
            // The real _OnFree used to throw when the whole constructor was skipped.
            AccessTools.Method(typeof(UIAdvisorTip), "_OnFree").Invoke(advisor, null);
            request.Invoke(advisor, [1]);
            run.Invoke(advisor, [1]);
            TestAssert.IsNotNull(advisor.requests);
            TestAssert.AreEqual(0, advisor.requests.Count);
            TestAssert.IsNull(advisor.playingTip);
            TestAssert.IsNull(AccessTools.Field(typeof(UIAdvisorTip), "audioSampleBuffer").GetValue(advisor));
        }
        finally
        {
            harmony.UnpatchSelf();
        }
    }
}
