using NebulaModel.DataStructures;
using NebulaWorld.Combat;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests;

[TestClass]
public class RemoteRespawnVisualStateTest
{
    [TestMethod]
    public void VisualTicksDoNotModifyAuthoritativeLifeData()
    {
        var visual = new RemoteRespawnVisualState();
        var previous = new PlayerLifeData { IsAlive = false, DeathCount = 1 };
        var sharedWithHost = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 40 };

        TestAssert.IsTrue(visual.Observe(previous, sharedWithHost));
        TestAssert.AreEqual(40, visual.Advance());
        TestAssert.AreEqual(41, visual.Advance());
        TestAssert.AreEqual(40, sharedWithHost.RespawnTick);

        var next = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 41 };
        TestAssert.IsFalse(visual.Observe(sharedWithHost, next));
        TestAssert.AreEqual(42, visual.Advance());
        TestAssert.AreEqual(41, next.RespawnTick);
    }

    [TestMethod]
    public void OnlyADeadPlayersNewRespawnCycleStartsTheVisual()
    {
        var visual = new RemoteRespawnVisualState();
        var alive = new PlayerLifeData { IsAlive = true, DeathCount = 1 };
        var first = new PlayerLifeData
        { IsAlive = false, DeathCount = 1, RespawnMode = 2, RespawnTick = 7 };
        TestAssert.IsTrue(visual.Observe(alive, first));

        var finished = new PlayerLifeData
        { IsAlive = true, DeathCount = 1, RespawnMode = 2, RespawnTick = 70 };
        TestAssert.IsFalse(visual.Observe(first, finished));
        TestAssert.AreEqual(0, visual.Tick);

        // A missed alive packet must not suppress a later death with a new count.
        var second = new PlayerLifeData
        { IsAlive = false, DeathCount = 2, RespawnMode = 2, RespawnTick = 3 };
        TestAssert.IsTrue(visual.Observe(first, second));
        TestAssert.AreEqual(3, visual.Tick);
    }
}
